using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coding.Dependencies;
using Ali.Modules.Coding.Engineering;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Execution;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.Coding.Execution;

/// <summary>
/// Durable ownership for one exact ordinary coding/process tool. Each instance has one complete
/// tool/capability/reconciler tuple and one fixed operation recipe. It cannot execute or select
/// another tool.
/// </summary>
internal sealed class AliCodingProcessExecutionAdapter : IAliExecutionEffectAdapter
{
    private readonly AliCodingInvocationKind _kind;
    private readonly AliCodingInvocationBindingResolver _bindings;
    private readonly AliDurableInvocationStore _store;
    private readonly EvidenceLedger _evidence;
    private readonly Func<object?, bool> _resultSucceeded;
    private readonly AliExactExecutionAdapterIdentity _exactIdentity;

    internal AliCodingProcessExecutionAdapter(
        AliCodingInvocationKind kind,
        AliCodingInvocationBindingResolver bindings,
        AliDurableInvocationStore store,
        EvidenceLedger evidence,
        Func<object?, bool> resultSucceeded)
    {
        _kind = kind;
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _resultSucceeded = resultSucceeded ?? throw new ArgumentNullException(nameof(resultSucceeded));
        ToolName = AliCodingInvocationCatalog.ToolName(kind);
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

    internal AliCodingInvocationKind Kind => _kind;

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
                "The coding/process adapter received a mismatched execution identity.");
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
                "The exact coding/process invocation could not be prepared safely.",
                exception);
        }
    }

    internal async ValueTask<AliCodingInvocationBinding> BeginInvocationAsync(
        JsonElement actualArguments,
        CancellationToken cancellationToken)
    {
        var started = await AliDurableInvocationGrantConsumer
            .ConsumeCurrentAndStartAsync(_store, _exactIdentity, cancellationToken)
            .ConfigureAwait(false);

        AliCodingInvocationBinding current;
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
                    "coding-binding-revalidation-failed",
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "The exact coding/process target changed before execution began.",
                exception);
        }

        var participant = new AliCodingInvocationCompletionParticipant(
            _store,
            started.Plan.Id,
            OutcomePrefix(ToolName),
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
                    "coding-completion-participant-unavailable",
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "The coding/process invocation could not register its exact terminal receipt participant.");
        }
        return current;
    }

    internal void RequireStableInvocation(
        AliCodingInvocationBinding expected,
        JsonElement actualArguments)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var current = _bindings.Resolve(_kind, actualArguments);
        if (expected.Kind != current.Kind
            || !string.Equals(expected.ToolName, current.ToolName, StringComparison.Ordinal)
            || !string.Equals(expected.CommandIdentity, current.CommandIdentity, StringComparison.Ordinal)
            || !string.Equals(expected.ExecutorIdentity, current.ExecutorIdentity, StringComparison.Ordinal)
            || !string.Equals(expected.TargetRoot, current.TargetRoot, StringComparison.Ordinal)
            || !FixedTimeDigestEquals(expected.RootBinding, current.RootBinding)
            || !FixedTimeDigestEquals(
                expected.TargetRootIdentity.Identity,
                current.TargetRootIdentity.Identity)
            || !FixedTimeDigestEquals(
                expected.DomainPreparationDigest,
                current.DomainPreparationDigest)
            || !FixedTimeDigestEquals(
                AliCodingInvocationBindingResolver.TargetVersionDigest(expected.TargetState),
                AliCodingInvocationBindingResolver.TargetVersionDigest(current.TargetState)))
        {
            throw new InvalidOperationException(
                "The exact coding/process binding changed at the delegate boundary.");
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
            return ActionReconciliationResult.Unknown(
                "coding-adapter-identity-mismatch");
        }
        if (!AliExecutionAuthorizationDigest.TryCompute(
                AliDurableInvocationStore.AuthorizationDomain,
                intent,
                out var authorizationDigest))
        {
            return ActionReconciliationResult.Unknown(
                "coding-authorization-identity-missing");
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
                        "coding-invocation-failed-state-unproven"),
                _ => ActionReconciliationResult.Unknown(recovered.OutcomeCode)
            };
        }
        catch (FileNotFoundException)
        {
            return ActionReconciliationResult.Unknown(
                "coding-invocation-artifact-missing");
        }
        catch (Exception exception) when (IsRecoverableReconciliationFailure(exception))
        {
            return ActionReconciliationResult.Unknown(
                "coding-reconcile-" + StableExceptionCode(exception));
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
        AliCodingInvocationBinding binding,
        string expectedTargetVersionDigest)
    {
        var current = AliCodingInvocationBindingResolver.TargetVersionDigest(
            binding.TargetState);
        if (!FixedTimeDigestEquals(current, expectedTargetVersionDigest))
        {
            throw new AliExecutionPreparationException(
                "The coding/process target changed after the accepted decision.");
        }
    }

    private static void RequireExactStartedBinding(
        AliDurableInvocationPlan plan,
        AliCodingInvocationBinding current)
    {
        var currentTargetVersion = AliCodingInvocationBindingResolver.TargetVersionDigest(
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
                "The started durable plan does not match the exact live coding invocation binding.");
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
                "The coding invocation completion receipt is not authoritative.");
        }

        var resultBytes = Encoding.UTF8.GetBytes(outcomeCode);
        try
        {
            var reportedSuccess = string.Equals(
                outcomeCode,
                OutcomePrefix(ToolName) + "-returned-success",
                StringComparison.Ordinal);
            var draft = new EvidenceDraft
            {
                EvidenceId = HashText(string.Join(
                    "\0",
                    "ali-coding-invocation-reconciliation-evidence-v1",
                    identity.StorageKey,
                    intent.IdempotencyKey,
                    outcomeCode,
                    receipt.ResultDigest)),
                CallId = intent.AcceptedCallId ?? intent.IdempotencyKey,
                WorkItemId = intent.WorkItemId,
                ToolName = ToolName,
                CapabilityGroup = AliCodingInvocationCatalog.CapabilityGroup(_kind),
                ProviderId = "ali-core",
                RegistryRevision = intent.RegistryRevisionDigest,
                EffectKind = AliCodingInvocationCatalog.EffectKind(_kind),
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
                    "file",
                    "ali-core",
                    "trusted-local",
                    FreshAtUtc: null,
                    intent.RegistryRevisionDigest),
                ProtectedProvenance = JsonSerializer.SerializeToElement(new
                {
                    reconciler = ReconcilerId,
                    planId = snapshot.Plan.Id,
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

    private static string OutcomePrefix(string toolName) =>
        toolName.Replace('_', '-');

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

internal sealed class AliCodingInvocationCompletionParticipant(
    AliDurableInvocationStore store,
    string planId,
    string outcomePrefix,
    Func<object?, bool> resultSucceeded) : IAliInvocationCompletionParticipant
{
    private int _terminal;

    public async ValueTask CompleteAsync(
        object? result,
        CancellationToken cancellationToken)
    {
        RequireFirstTerminal();
        if (!resultSucceeded(result))
        {
            await store.MarkInDoubtAsync(
                    planId,
                    expectedRevision: 1,
                    outcomePrefix + "-returned-failure",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var resultDigest = DigestResult(result);
        var outcomeCode = outcomePrefix + "-returned-success";
        await store.CompleteAsync(
                planId,
                expectedRevision: 1,
                outcomeCode,
                resultDigest,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask FailAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        RequireFirstTerminal();
        var failureCode = exception switch
        {
            TimeoutException => "coding-invocation-timeout",
            OperationCanceledException => "coding-invocation-canceled",
            UnauthorizedAccessException => "coding-invocation-unauthorized",
            IOException => "coding-invocation-io-failure",
            _ => "coding-invocation-failed"
        };
        await store.FailAsync(
                planId,
                expectedRevision: 1,
                failureCode,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask MarkInDoubtAsync(
        string reasonCode,
        CancellationToken cancellationToken)
    {
        RequireFirstTerminal();
        await store.MarkInDoubtAsync(
                planId,
                expectedRevision: 1,
                reasonCode,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void RequireFirstTerminal()
    {
        if (Interlocked.Exchange(ref _terminal, 1) != 0)
        {
            throw new InvalidOperationException(
                "The coding invocation participant already recorded a terminal state.");
        }
    }

    private static string DigestResult(object? result)
    {
        var element = result is null
            ? JsonSerializer.SerializeToElement<object?>(null)
            : JsonSerializer.SerializeToElement(result, result.GetType());
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(element);
        try
        {
            return TurnStateIntegrity.Digest(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

/// <summary>
/// Explicit invocation entrypoints used by the 14 production function registrations. Shared
/// code below only performs the mechanical begin/timeout operation for the adapter supplied by
/// that fixed entrypoint.
/// </summary>
internal sealed class AliCodingProcessExecutionCoordinator
{
    private readonly AliCodingProcessExecutionAdapter _providerAnalyze;
    private readonly AliCodingProcessExecutionAdapter _providerFormat;
    private readonly AliCodingProcessExecutionAdapter _providerBuild;
    private readonly AliCodingProcessExecutionAdapter _providerTest;
    private readonly AliCodingProcessExecutionAdapter _providerRun;
    private readonly AliCodingProcessExecutionAdapter _dotNetCreate;
    private readonly AliCodingProcessExecutionAdapter _roslynFormat;
    private readonly AliCodingProcessExecutionAdapter _dotNetBuild;
    private readonly AliCodingProcessExecutionAdapter _dotNetTest;
    private readonly AliCodingProcessExecutionAdapter _dotNetVerify;
    private readonly AliCodingProcessExecutionAdapter _dotNetRun;
    private readonly AliCodingProcessExecutionAdapter _dotNetStop;
    private readonly AliCodingProcessExecutionAdapter _dependencyInspect;
    private readonly AliCodingProcessExecutionAdapter _dependencyApply;
    private readonly Action? _beforeDelegate;

    internal AliCodingProcessExecutionCoordinator(
        AliCodingInvocationBindingResolver bindings,
        AliDurableInvocationStore store,
        EvidenceLedger evidence,
        Action? beforeDelegate = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(evidence);
        _beforeDelegate = beforeDelegate;
        _providerAnalyze = Adapter(
            AliCodingInvocationKind.ProviderAnalyze,
            result => result is AliLanguageOperationResult { Success: true });
        _providerFormat = Adapter(
            AliCodingInvocationKind.ProviderFormat,
            result => result is AliLanguageOperationResult { Success: true });
        _providerBuild = Adapter(
            AliCodingInvocationKind.ProviderBuild,
            result => result is AliLanguageOperationResult { Success: true });
        _providerTest = Adapter(
            AliCodingInvocationKind.ProviderTest,
            result => result is AliLanguageOperationResult { Success: true });
        _providerRun = Adapter(
            AliCodingInvocationKind.ProviderRun,
            result => result is AliLanguageOperationResult { Success: true });
        _dotNetCreate = Adapter(
            AliCodingInvocationKind.DotNetCreate,
            result => result is DotNetCreateProjectResult { Success: true });
        _roslynFormat = Adapter(
            AliCodingInvocationKind.RoslynFormat,
            result => result is RoslynFormatResult { Success: true });
        _dotNetBuild = Adapter(
            AliCodingInvocationKind.DotNetBuild,
            result => result is DotNetBuildResult { Success: true });
        _dotNetTest = Adapter(
            AliCodingInvocationKind.DotNetTest,
            result => result is DotNetTestResult { Success: true });
        _dotNetVerify = Adapter(
            AliCodingInvocationKind.DotNetVerify,
            result => result is DotNetVerificationResult { Success: true });
        _dotNetRun = Adapter(
            AliCodingInvocationKind.DotNetRun,
            result => result is DotNetRunResult { Success: true });
        _dotNetStop = Adapter(
            AliCodingInvocationKind.DotNetStop,
            result => result is DotNetStopProjectResult { Success: true });
        _dependencyInspect = Adapter(
            AliCodingInvocationKind.DependencyInspect,
            result => result is DependencyInspectionResult { Success: true });
        _dependencyApply = Adapter(
            AliCodingInvocationKind.DependencyApply,
            result => result is DependencyChangeResult { Success: true, Applied: true });
        TargetStates = new AliCodingProcessTargetStateAdapter(bindings);
        // These four legacy entrypoints still mutate canonical source in place. Until they
        // publish through the shared source transaction engine, do not advertise an exact
        // reconciler for them: capability resolution will fail closed as ReconcilerUnavailable.
        Adapters = Array.AsReadOnly(new IAliExecutionEffectAdapter[]
        {
            _providerAnalyze,
            _providerBuild,
            _providerTest,
            _providerRun,
            _dotNetBuild,
            _dotNetTest,
            _dotNetVerify,
            _dotNetRun,
            _dotNetStop,
            _dependencyInspect
        });

        AliCodingProcessExecutionAdapter Adapter(
            AliCodingInvocationKind kind,
            Func<object?, bool> resultSucceeded) =>
            new(kind, bindings, store, evidence, resultSucceeded);
    }

    internal IReadOnlyList<IAliExecutionEffectAdapter> Adapters { get; }

    internal AliCodingProcessTargetStateAdapter TargetStates { get; }

    internal Task<AliLanguageOperationResult> ExecuteProviderAnalyzeAsync(
        string targetPath,
        Func<CancellationToken, Task<AliLanguageOperationResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _providerAnalyze,
            JsonSerializer.SerializeToElement(new { targetPath }),
            execute,
            cancellationToken);

    internal Task<AliLanguageOperationResult> ExecuteProviderFormatAsync(
        string targetPath,
        Func<CancellationToken, Task<AliLanguageOperationResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _providerFormat,
            JsonSerializer.SerializeToElement(new { targetPath }),
            execute,
            cancellationToken);

    internal Task<AliLanguageOperationResult> ExecuteProviderBuildAsync(
        string targetPath,
        string? configuration,
        Func<CancellationToken, Task<AliLanguageOperationResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _providerBuild,
            JsonSerializer.SerializeToElement(new { targetPath, configuration }),
            execute,
            cancellationToken);

    internal Task<AliLanguageOperationResult> ExecuteProviderTestAsync(
        string targetPath,
        string? configuration,
        Func<CancellationToken, Task<AliLanguageOperationResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _providerTest,
            JsonSerializer.SerializeToElement(new { targetPath, configuration }),
            execute,
            cancellationToken);

    internal Task<AliLanguageOperationResult> ExecuteProviderRunAsync(
        string targetPath,
        string? configuration,
        Func<CancellationToken, Task<AliLanguageOperationResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _providerRun,
            JsonSerializer.SerializeToElement(new { targetPath, configuration }),
            execute,
            cancellationToken);

    internal Task<DotNetCreateProjectResult> ExecuteDotNetCreateAsync(
        string projectPath,
        string template,
        Func<CancellationToken, Task<DotNetCreateProjectResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _dotNetCreate,
            JsonSerializer.SerializeToElement(new { projectPath, template }),
            execute,
            cancellationToken);

    internal Task<RoslynFormatResult> ExecuteRoslynFormatAsync(
        string projectPath,
        Func<CancellationToken, Task<RoslynFormatResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _roslynFormat,
            JsonSerializer.SerializeToElement(new { projectPath }),
            execute,
            cancellationToken);

    internal Task<DotNetBuildResult> ExecuteDotNetBuildAsync(
        string projectPath,
        string? configuration,
        Func<CancellationToken, Task<DotNetBuildResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _dotNetBuild,
            JsonSerializer.SerializeToElement(new { projectPath, configuration }),
            execute,
            cancellationToken);

    internal Task<DotNetTestResult> ExecuteDotNetTestAsync(
        string targetPath,
        string? configuration,
        Func<CancellationToken, Task<DotNetTestResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _dotNetTest,
            JsonSerializer.SerializeToElement(new { targetPath, configuration }),
            execute,
            cancellationToken);

    internal Task<DotNetVerificationResult> ExecuteDotNetVerifyAsync(
        string targetPath,
        string? configuration,
        Func<CancellationToken, Task<DotNetVerificationResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _dotNetVerify,
            JsonSerializer.SerializeToElement(new { targetPath, configuration }),
            execute,
            cancellationToken);

    internal Task<DotNetRunResult> ExecuteDotNetRunAsync(
        string projectPath,
        string? configuration,
        Func<CancellationToken, Task<DotNetRunResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _dotNetRun,
            JsonSerializer.SerializeToElement(new { projectPath, configuration }),
            execute,
            cancellationToken);

    internal Task<DotNetStopProjectResult> ExecuteDotNetStopAsync(
        string projectPath,
        string? configuration,
        Func<CancellationToken, Task<DotNetStopProjectResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _dotNetStop,
            JsonSerializer.SerializeToElement(new { projectPath, configuration }),
            execute,
            cancellationToken);

    internal Task<DependencyInspectionResult> ExecuteDependencyInspectAsync(
        string projectPath,
        Func<CancellationToken, Task<DependencyInspectionResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _dependencyInspect,
            JsonSerializer.SerializeToElement(new { projectPath }),
            execute,
            cancellationToken);

    internal Task<DependencyChangeResult> ExecuteDependencyApplyAsync(
        string projectPath,
        string action,
        string packageId,
        string? version,
        Func<CancellationToken, Task<DependencyChangeResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _dependencyApply,
            JsonSerializer.SerializeToElement(new
            {
                projectPath,
                action,
                packageId,
                version
            }),
            execute,
            cancellationToken);

    private async Task<TResult> ExecuteAsync<TResult>(
        AliCodingProcessExecutionAdapter adapter,
        JsonElement arguments,
        Func<CancellationToken, Task<TResult>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(execute);
        var binding = await adapter.BeginInvocationAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        using var targetRoot = binding.TargetRootIdentity.Acquire(
            "The exact selected coding source root spine");
        adapter.RequireStableInvocation(binding, arguments);
        _beforeDelegate?.Invoke();
        targetRoot.RequireStable();
        adapter.RequireStableInvocation(binding, arguments);
        using var executionBinding = AliCodingInvocationExecutionContext.Enter(binding);
        using var exactProcessBinding = AliExactProcessExecutionContext.Enter(
            new AliExactProcessExecutionBinding(
                binding.RuntimeBinding.DotNetHost,
                binding.RuntimeBinding.DotNetRun?.Artifact,
                binding.RuntimeBinding.DotNetRun?.LaunchClosure));
        using var timeout = new CancellationTokenSource(
            AliCodingInvocationCatalog.Timeout(adapter.Kind));
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
                "The bounded coding/process invocation exceeded its fixed operation limit.",
                exception);
        }
    }
}
