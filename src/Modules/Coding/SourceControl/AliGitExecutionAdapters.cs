using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Execution;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.Coding.SourceControl;

/// <summary>
/// Durable owner for one exact Git schema and provider operation. Started invocations have no
/// domain reconciler: without a protected intended poststate, a lost status result or a possibly
/// applied branch, commit, or push must remain unknown and is never automatically repeated.
/// </summary>
internal sealed class AliGitExecutionAdapter : IAliExecutionEffectAdapter
{
    private readonly AliGitInvocationKind _kind;
    private readonly AliGitInvocationBindingResolver _bindings;
    private readonly AliDurableInvocationStore _store;
    private readonly EvidenceLedger _evidence;
    private readonly Func<object?, bool> _resultSucceeded;
    private readonly AliExactExecutionAdapterIdentity _exactIdentity;

    internal AliGitExecutionAdapter(
        AliGitInvocationKind kind,
        AliGitInvocationBindingResolver bindings,
        AliDurableInvocationStore store,
        EvidenceLedger evidence,
        Func<object?, bool> resultSucceeded)
    {
        _kind = kind;
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _resultSucceeded = resultSucceeded ?? throw new ArgumentNullException(nameof(resultSucceeded));
        ToolName = AliGitInvocationCatalog.ToolName(kind);
        CapabilityId = CapabilityIdFor(ToolName);
        ReconcilerId = ReconcilerIdFor(ToolName);
        _exactIdentity = new AliExactExecutionAdapterIdentity(
            ToolName,
            CapabilityId,
            ReconcilerId);
    }

    public string ToolName { get; }

    public string CapabilityId { get; }

    public string ReconcilerId { get; }

    internal AliGitInvocationKind Kind => _kind;

    public async ValueTask<AliExecutionPreparation> PrepareAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.ToolName, ToolName, StringComparison.Ordinal)
            || !string.Equals(request.CapabilityId, CapabilityId, StringComparison.Ordinal)
            || !string.Equals(request.ReconcilerId, ReconcilerId, StringComparison.Ordinal))
        {
            throw new AliExecutionPreparationException(
                "The Git adapter received a mismatched exact execution identity.");
        }

        try
        {
            request.Validate();
            var binding = _bindings.Resolve(_kind, request.Arguments);
            RequireTargetVersion(binding, request.TargetVersionDigest);
            var plan = AliDurableInvocationPlan.Create(
                request,
                binding.RootBinding,
                binding.CommandIdentity,
                binding.DomainPreparationDigest);
            await _store.PrepareAsync(plan, cancellationToken).ConfigureAwait(false);
            return new AliExecutionPreparation(
                plan.Id,
                plan.RootBinding,
                plan.TargetVersionDigest);
        }
        catch (AliExecutionPreparationException)
        {
            throw;
        }
        catch (Exception exception) when (IsPreparationFailure(exception))
        {
            throw new AliExecutionPreparationException(
                "The exact Git invocation could not be prepared safely.",
                exception);
        }
    }

    internal async ValueTask<AliGitInvocationBinding> BeginInvocationAsync(
        JsonElement actualArguments,
        CancellationToken cancellationToken)
    {
        var started = await AliDurableInvocationGrantConsumer
            .ConsumeCurrentAndStartAsync(_store, _exactIdentity, cancellationToken)
            .ConfigureAwait(false);
        AliGitInvocationBinding current;
        try
        {
            current = _bindings.Resolve(_kind, actualArguments);
            RequireExactStartedBinding(started.Plan, current);
        }
        catch (Exception exception) when (IsBindingRevalidationFailure(exception))
        {
            await _store.FailAsync(
                    started.Plan.Id,
                    expectedRevision: 1,
                    "git-binding-revalidation-failed",
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "The exact Git repository state changed before execution began.",
                exception);
        }

        var participant = new AliCodingInvocationCompletionParticipant(
            _store,
            started.Plan.Id,
            OutcomePrefix(_kind),
            _resultSucceeded);
        if (!AliExecutionGrantContext.TryRegisterCurrentCompletionParticipant(
                ToolName,
                CapabilityId,
                ReconcilerId,
                started.Plan.Id,
                started.Plan.RootBinding,
                participant))
        {
            await _store.MarkInDoubtAsync(
                    started.Plan.Id,
                    expectedRevision: 1,
                    "git-completion-participant-unavailable",
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "The Git invocation could not register its exact terminal receipt participant.");
        }
        return current;
    }

    internal void RequireStableInvocation(
        AliGitInvocationBinding expected,
        JsonElement actualArguments)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var current = _bindings.Resolve(_kind, actualArguments);
        if (expected.Kind != current.Kind
            || !string.Equals(expected.ToolName, current.ToolName, StringComparison.Ordinal)
            || !string.Equals(expected.CommandIdentity, current.CommandIdentity, StringComparison.Ordinal)
            || !string.Equals(expected.ProviderOperation, current.ProviderOperation, StringComparison.Ordinal)
            || !FixedTimeDigestEquals(expected.ProviderIdentity, current.ProviderIdentity)
            || !string.Equals(
                Path.GetFullPath(expected.RepositoryRoot),
                Path.GetFullPath(current.RepositoryRoot),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
            || !FixedTimeDigestEquals(expected.RootBinding, current.RootBinding)
            || !FixedTimeDigestEquals(
                expected.RepositoryRootIdentity.Identity,
                current.RepositoryRootIdentity.Identity)
            || !FixedTimeDigestEquals(
                expected.DomainPreparationDigest,
                current.DomainPreparationDigest)
            || !FixedTimeDigestEquals(
                AliGitInvocationBindingResolver.TargetVersionDigest(expected.TargetState),
                AliGitInvocationBindingResolver.TargetVersionDigest(current.TargetState)))
        {
            throw new InvalidOperationException(
                "The exact Git binding changed at the delegate boundary.");
        }
    }

    public async ValueTask<ActionReconciliationResult> ReconcileAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(intent);
        if (!string.Equals(intent.ToolName, ToolName, StringComparison.Ordinal)
            || !string.Equals(intent.CapabilityId, CapabilityId, StringComparison.Ordinal)
            || !string.Equals(intent.ReconcilerId, ReconcilerId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(intent.PreparationIdentity))
        {
            return ActionReconciliationResult.Unknown("git-adapter-identity-mismatch");
        }
        if (!AliExecutionAuthorizationDigest.TryCompute(
                AliDurableInvocationStore.AuthorizationDomain,
                intent,
                out var authorizationDigest))
        {
            return ActionReconciliationResult.Unknown(
                "git-authorization-identity-missing");
        }

        try
        {
            var recovered = await new AliDurableInvocationReconciler(
                    _store,
                    _exactIdentity)
                .ReconcileAsync(
                    intent.PreparationIdentity,
                    authorizationDigest,
                    cancellationToken)
                .ConfigureAwait(false);
            return recovered.Disposition switch
            {
                AliDurableInvocationRecoveryDisposition.Absent =>
                    ActionReconciliationResult.Absent(recovered.OutcomeCode),
                AliDurableInvocationRecoveryDisposition.Applied =>
                    ActionReconciliationResult.Applied(
                        recovered.OutcomeCode,
                        await AppendReconciliationEvidenceAsync(
                                identity,
                                intent,
                                recovered.OutcomeCode,
                                cancellationToken)
                            .ConfigureAwait(false)),
                AliDurableInvocationRecoveryDisposition.Failed =>
                    ActionReconciliationResult.Unknown(
                        "git-invocation-failed-state-unproven"),
                _ => ActionReconciliationResult.Unknown(recovered.OutcomeCode)
            };
        }
        catch (FileNotFoundException)
        {
            return ActionReconciliationResult.Unknown("git-invocation-artifact-missing");
        }
        catch (Exception exception) when (IsRecoverableReconciliationFailure(exception))
        {
            return ActionReconciliationResult.Unknown(
                "git-reconcile-" + StableExceptionCode(exception));
        }
    }

    internal static string CapabilityIdFor(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return "ali.tool." + toolName;
    }

    internal static string ReconcilerIdFor(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return "ali.reconcile." + toolName;
    }

    private static void RequireTargetVersion(
        AliGitInvocationBinding binding,
        string expectedTargetVersionDigest)
    {
        var current = AliGitInvocationBindingResolver.TargetVersionDigest(
            binding.TargetState);
        if (!FixedTimeDigestEquals(current, expectedTargetVersionDigest))
        {
            throw new AliExecutionPreparationException(
                "The Git repository changed after the accepted decision.");
        }
    }

    private static void RequireExactStartedBinding(
        AliDurableInvocationPlan plan,
        AliGitInvocationBinding current)
    {
        var currentTargetVersion = AliGitInvocationBindingResolver.TargetVersionDigest(
            current.TargetState);
        if (!string.Equals(plan.ToolName, current.ToolName, StringComparison.Ordinal)
            || !string.Equals(
                plan.DomainPreparationIdentity,
                current.CommandIdentity,
                StringComparison.Ordinal)
            || !FixedTimeDigestEquals(plan.RootBinding, current.RootBinding)
            || !FixedTimeDigestEquals(
                plan.DomainPreparationDigest,
                current.DomainPreparationDigest)
            || !FixedTimeDigestEquals(plan.TargetVersionDigest, currentTargetVersion))
        {
            throw new InvalidOperationException(
                "The started durable plan does not match the exact live Git binding.");
        }
    }

    private async Task<CommittedEvidenceReference> AppendReconciliationEvidenceAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        string outcomeCode,
        CancellationToken cancellationToken)
    {
        var snapshot = await _store.LoadAsync(
                intent.PreparationIdentity!,
                cancellationToken)
            .ConfigureAwait(false);
        var receipt = snapshot.Receipt;
        if (snapshot.State != AliDurableInvocationState.Completed
            || receipt is null
            || receipt.ResultDigest is null
            || receipt.StableOutcomeCode is null
            || !string.Equals(receipt.StableOutcomeCode, outcomeCode, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Git completion receipt is not authoritative.");
        }

        var resultBytes = Encoding.UTF8.GetBytes(outcomeCode);
        try
        {
            var reportedSuccess = string.Equals(
                outcomeCode,
                OutcomePrefix(_kind) + "-returned-success",
                StringComparison.Ordinal);
            var draft = new EvidenceDraft
            {
                EvidenceId = HashText(string.Join(
                    "\0",
                    "ali-git-reconciliation-evidence-v1",
                    identity.StorageKey,
                    intent.IdempotencyKey,
                    outcomeCode,
                    receipt.ResultDigest)),
                CallId = intent.AcceptedCallId ?? intent.IdempotencyKey,
                WorkItemId = intent.WorkItemId,
                ToolName = ToolName,
                CapabilityGroup = "source-control-delivery",
                ProviderId = "ali-git",
                RegistryRevision = intent.RegistryRevisionDigest,
                EffectKind = AliGitInvocationCatalog.EffectKind(_kind),
                Arguments = JsonSerializer.SerializeToElement(new
                {
                    intent.CanonicalArgumentsDigest,
                    intent.PreparationIdentity
                }),
                Result = JsonSerializer.SerializeToElement(new
                {
                    outcomeCode,
                    receipt.ResultDigest
                }),
                NormalizedTarget = JsonSerializer.SerializeToElement(new
                {
                    intent.RootBinding,
                    intent.TargetVersionDigest
                }),
                NormalizedEffectResult = JsonSerializer.SerializeToElement(new
                {
                    outcomeCode,
                    receipt.ResultDigest
                }),
                Outcome = ToolInvocationOutcome.Returned(resultBytes, reportedSuccess),
                StableOutcomeCode = outcomeCode,
                StartedAtUtc = receipt.StartedAtUtc,
                CompletedAtUtc = receipt.TerminalAtUtc!.Value,
                Artifacts = [],
                Permission = new EvidencePermissionMetadata("unknown", "unknown"),
                ProtectedPermissionReceipt = JsonSerializer.SerializeToElement(new
                {
                    intent.PermissionReceiptDigest,
                    intent.RequiresApproval
                }),
                Source = new EvidenceSourceMetadata(
                    "process",
                    "ali-git",
                    "trusted-local",
                    FreshAtUtc: null,
                    intent.RegistryRevisionDigest),
                ProtectedProvenance = JsonSerializer.SerializeToElement(new
                {
                    reconciler = ReconcilerId,
                    planId = snapshot.Plan.Id,
                    providerOperation = AliGitInvocationCatalog.ProviderOperation(_kind),
                    commandIdentity = snapshot.Plan.DomainPreparationIdentity,
                    snapshot.Plan.DomainPreparationDigest,
                    receipt.AuthorizationDigest,
                    replayedEffect = false
                })
            };
            await _evidence.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            var committed = await _evidence.AppendAsync(identity, draft, cancellationToken)
                .ConfigureAwait(false);
            return new CommittedEvidenceReference(
                committed.Evidence.EvidenceId,
                committed.Cursor,
                committed.Checksum);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(resultBytes);
        }
    }

    private static string OutcomePrefix(AliGitInvocationKind kind) => kind switch
    {
        AliGitInvocationKind.Status => "git-status",
        AliGitInvocationKind.Diff => "git-diff",
        AliGitInvocationKind.CreateBranch => "git-create-branch",
        AliGitInvocationKind.Commit => "git-commit",
        AliGitInvocationKind.Push => "git-push",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static bool FixedTimeDigestEquals(string actual, string expected)
    {
        byte[] actualBytes;
        byte[] expectedBytes;
        try
        {
            actualBytes = Convert.FromHexString(actual);
            expectedBytes = Convert.FromHexString(expected);
        }
        catch (FormatException)
        {
            return false;
        }
        try
        {
            return actualBytes.Length == expectedBytes.Length
                && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualBytes);
            CryptographicOperations.ZeroMemory(expectedBytes);
        }
    }

    private static bool IsPreparationFailure(Exception exception) =>
        exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException;

    private static bool IsBindingRevalidationFailure(Exception exception) =>
        exception is not OperationCanceledException
            and not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    private static bool IsRecoverableReconciliationFailure(Exception exception) =>
        exception is not OperationCanceledException
            and not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    private static string StableExceptionCode(Exception exception)
    {
        var name = exception.GetType().Name;
        var filtered = new string(name.Where(char.IsAsciiLetterOrDigit).ToArray())
            .ToLowerInvariant();
        return string.IsNullOrWhiteSpace(filtered)
            ? "failed"
            : filtered[..Math.Min(filtered.Length, 72)];
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}

/// <summary>
/// Five explicit production entrypoints. Shared code performs only grant start, fixed timeout,
/// and delegate invocation for the adapter supplied by that closed entrypoint.
/// </summary>
internal sealed class AliGitExecutionCoordinator
{
    private readonly AliGitExecutionAdapter _status;
    private readonly AliGitExecutionAdapter _diff;
    private readonly AliGitExecutionAdapter _createBranch;
    private readonly AliGitExecutionAdapter _commit;
    private readonly AliGitExecutionAdapter _push;
    private readonly Action? _beforeDelegate;

    internal AliGitExecutionCoordinator(
        AliGitInvocationBindingResolver bindings,
        AliDurableInvocationStore store,
        EvidenceLedger evidence,
        Action? beforeDelegate = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(evidence);
        _beforeDelegate = beforeDelegate;
        _status = Adapter(AliGitInvocationKind.Status, "status");
        _diff = Adapter(AliGitInvocationKind.Diff, "diff");
        _createBranch = Adapter(AliGitInvocationKind.CreateBranch, "create-branch");
        _commit = Adapter(AliGitInvocationKind.Commit, "commit");
        _push = Adapter(AliGitInvocationKind.Push, "push");
        TargetStates = new AliGitTargetStateAdapter(bindings);
        Adapters = Array.AsReadOnly(new IAliExecutionEffectAdapter[]
        {
            _status,
            _diff,
            _createBranch,
            _commit,
            _push
        });

        AliGitExecutionAdapter Adapter(AliGitInvocationKind kind, string operation) =>
            new(
                kind,
                bindings,
                store,
                evidence,
                result => result is SourceControlResult { Success: true } sourceResult
                    && string.Equals(
                        sourceResult.Operation,
                        operation,
                        StringComparison.Ordinal));
    }

    internal IReadOnlyList<IAliExecutionEffectAdapter> Adapters { get; }

    internal AliGitTargetStateAdapter TargetStates { get; }

    internal Task<SourceControlResult> ExecuteStatusAsync(
        string targetPath,
        Func<CancellationToken, Task<SourceControlResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _status,
            JsonSerializer.SerializeToElement(new { targetPath }),
            execute,
            cancellationToken);

    internal Task<SourceControlResult> ExecuteDiffAsync(
        string targetPath,
        bool staged,
        Func<CancellationToken, Task<SourceControlResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _diff,
            JsonSerializer.SerializeToElement(new { targetPath, staged }),
            execute,
            cancellationToken);

    internal Task<SourceControlResult> ExecuteCreateBranchAsync(
        string targetPath,
        string branchName,
        Func<CancellationToken, Task<SourceControlResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _createBranch,
            JsonSerializer.SerializeToElement(new { targetPath, branchName }),
            execute,
            cancellationToken);

    internal Task<SourceControlResult> ExecuteCommitAsync(
        string targetPath,
        string message,
        Func<CancellationToken, Task<SourceControlResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _commit,
            JsonSerializer.SerializeToElement(new { targetPath, message }),
            execute,
            cancellationToken);

    internal Task<SourceControlResult> ExecutePushAsync(
        string targetPath,
        string remote,
        string branchName,
        Func<CancellationToken, Task<SourceControlResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _push,
            JsonSerializer.SerializeToElement(new { targetPath, remote, branchName }),
            execute,
            cancellationToken);

    private async Task<SourceControlResult> ExecuteAsync(
        AliGitExecutionAdapter adapter,
        JsonElement arguments,
        Func<CancellationToken, Task<SourceControlResult>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(execute);
        var binding = await adapter.BeginInvocationAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        using var repositoryRoot = binding.RepositoryRootIdentity.Acquire(
            "The exact selected Git repository root spine");
        using var executionFiles = AliGitExecutionFileLeaseGroup.Acquire(
            binding.ExecutionFiles);
        adapter.RequireStableInvocation(binding, arguments);
        _beforeDelegate?.Invoke();
        repositoryRoot.RequireStable();
        executionFiles.RequireStable();
        adapter.RequireStableInvocation(binding, arguments);
        using var timeout = new CancellationTokenSource(
            AliGitInvocationCatalog.ExecutionTimeout(adapter.Kind));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            return await execute(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The exact Git operation exceeded its fixed execution limit.",
                exception);
        }
    }
}
