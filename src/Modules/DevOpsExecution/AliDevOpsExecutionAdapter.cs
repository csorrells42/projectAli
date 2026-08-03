using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Capabilities;
using Ali.Modules.Coding.Architecture;
using Ali.Modules.Coding.Delivery;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coding.Quality;
using Ali.Modules.Coding.Release;
using Ali.Modules.Coding.Verification;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Execution;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.DevOpsExecution;

/// <summary>
/// Durable ownership for one exact production DevOps operation. Every instance has one complete
/// tool/capability/reconciler tuple and one fixed operation recipe.
/// </summary>
internal sealed class AliDevOpsExecutionAdapter : IAliExecutionEffectAdapter
{
    private readonly AliDevOpsInvocationKind _kind;
    private readonly AliDevOpsInvocationBindingResolver _bindings;
    private readonly AliDurableInvocationStore _store;
    private readonly EvidenceLedger _evidence;
    private readonly AliExactExecutionAdapterIdentity _exactIdentity;

    internal AliDevOpsExecutionAdapter(
        AliDevOpsInvocationKind kind,
        AliDevOpsInvocationBindingResolver bindings,
        AliDurableInvocationStore store,
        EvidenceLedger evidence)
    {
        _kind = kind;
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        ToolName = AliDevOpsInvocationCatalog.ToolName(kind);
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

    internal AliDevOpsInvocationKind Kind => _kind;

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
                "The DevOps adapter received a mismatched execution identity.");
        }

        try
        {
            request.Validate();
            var binding = _bindings.Resolve(_kind, request.Arguments);
            RequireTargetVersion(binding, request.TargetVersionDigest);
            var plan = AliDurableInvocationPlan.Create(
                request,
                binding.RootBinding,
                binding.OperationIdentity,
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
                "The exact DevOps invocation could not be prepared safely.",
                exception);
        }
    }

    internal async ValueTask<AliDevOpsInvocationBinding> BeginInvocationAsync(
        JsonElement actualArguments,
        CancellationToken cancellationToken)
    {
        var started = await AliDurableInvocationGrantConsumer
            .ConsumeCurrentAndStartAsync(_store, _exactIdentity, cancellationToken)
            .ConfigureAwait(false);

        AliDevOpsInvocationBinding current;
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
                    "devops-binding-revalidation-failed",
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "The exact DevOps target changed before execution began.",
                exception);
        }

        var participant = new AliDevOpsInvocationCompletionParticipant(
            _store,
            started.Plan.Id,
            _kind,
            OutcomePrefix(ToolName));
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
                    "devops-completion-participant-unavailable",
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "The DevOps invocation could not register its exact terminal receipt participant.");
        }
        return current;
    }

    internal void RequireStableInvocation(
        AliDevOpsInvocationBinding expected,
        JsonElement actualArguments)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var current = _bindings.Resolve(_kind, actualArguments);
        if (current.Kind != expected.Kind
            || !string.Equals(current.ToolName, expected.ToolName, StringComparison.Ordinal)
            || !string.Equals(
                current.OperationIdentity,
                expected.OperationIdentity,
                StringComparison.Ordinal)
            || !string.Equals(
                current.ExecutorIdentity,
                expected.ExecutorIdentity,
                StringComparison.Ordinal)
            || !FixedTimeDigestEquals(current.RootBinding, expected.RootBinding)
            || !FixedTimeDigestEquals(
                current.DomainPreparationDigest,
                expected.DomainPreparationDigest)
            || !FixedTimeDigestEquals(
                AliDevOpsInvocationBindingResolver.TargetVersionDigest(current.TargetState),
                AliDevOpsInvocationBindingResolver.TargetVersionDigest(expected.TargetState))
            || !SameDirectoryBindings(
                current.TargetRootIdentities,
                expected.TargetRootIdentities))
        {
            throw new InvalidOperationException(
                "The exact DevOps invocation binding changed at the delegate boundary.");
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
            return ActionReconciliationResult.Unknown("devops-adapter-identity-mismatch");
        }
        if (!AliExecutionAuthorizationDigest.TryCompute(
                AliDurableInvocationStore.AuthorizationDomain,
                intent,
                out var authorizationDigest))
        {
            return ActionReconciliationResult.Unknown(
                "devops-authorization-identity-missing");
        }

        try
        {
            var recovered = await new AliDurableInvocationReconciler(_store, _exactIdentity)
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
                        "devops-invocation-failed-state-unproven"),
                _ => ActionReconciliationResult.Unknown(recovered.OutcomeCode)
            };
        }
        catch (FileNotFoundException)
        {
            return ActionReconciliationResult.Unknown("devops-invocation-artifact-missing");
        }
        catch (Exception exception) when (IsRecoverableReconciliationFailure(exception))
        {
            return ActionReconciliationResult.Unknown(
                "devops-reconcile-" + StableExceptionCode(exception));
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
        AliDevOpsInvocationBinding binding,
        string expectedTargetVersionDigest)
    {
        var current = AliDevOpsInvocationBindingResolver.TargetVersionDigest(
            binding.TargetState);
        if (!FixedTimeDigestEquals(current, expectedTargetVersionDigest))
        {
            throw new AliExecutionPreparationException(
                "The DevOps target changed after the accepted decision.");
        }
    }

    private static void RequireExactStartedBinding(
        AliDurableInvocationPlan plan,
        AliDevOpsInvocationBinding current)
    {
        var currentTargetVersion = AliDevOpsInvocationBindingResolver.TargetVersionDigest(
            current.TargetState);
        if (!string.Equals(plan.ToolName, current.ToolName, StringComparison.Ordinal)
            || !string.Equals(
                plan.DomainPreparationIdentity,
                current.OperationIdentity,
                StringComparison.Ordinal)
            || !FixedTimeDigestEquals(plan.RootBinding, current.RootBinding)
            || !FixedTimeDigestEquals(
                plan.DomainPreparationDigest,
                current.DomainPreparationDigest)
            || !FixedTimeDigestEquals(plan.TargetVersionDigest, currentTargetVersion))
        {
            throw new InvalidOperationException(
                "The started durable plan does not match the exact live DevOps invocation binding.");
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
                "The DevOps invocation completion receipt is not authoritative.");
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
                    "ali-devops-invocation-reconciliation-evidence-v1",
                    identity.StorageKey,
                    intent.IdempotencyKey,
                    outcomeCode,
                    receipt.ResultDigest)),
                CallId = intent.AcceptedCallId ?? intent.IdempotencyKey,
                WorkItemId = intent.WorkItemId,
                ToolName = ToolName,
                CapabilityGroup = CapabilityGroupIds.DevOpsArchitectureQuality,
                ProviderId = "ali-core",
                RegistryRevision = intent.RegistryRevisionDigest,
                EffectKind = "execute",
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
                    operationIdentity = snapshot.Plan.DomainPreparationIdentity,
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

    private static string OutcomePrefix(string toolName) => toolName.Replace('_', '-');

    private static bool SameDirectoryBindings(
        IReadOnlyList<AliExecutionDirectoryBinding> current,
        IReadOnlyList<AliExecutionDirectoryBinding> expected)
    {
        if (current.Count != expected.Count)
        {
            return false;
        }
        for (var index = 0; index < current.Count; index++)
        {
            if (!string.Equals(
                    current[index].TargetPath,
                    expected[index].TargetPath,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal)
                || !FixedTimeDigestEquals(
                    current[index].Identity,
                    expected[index].Identity))
            {
                return false;
            }
        }
        return true;
    }

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

internal sealed class AliDevOpsInvocationCompletionParticipant(
    AliDurableInvocationStore store,
    string planId,
    AliDevOpsInvocationKind kind,
    string outcomePrefix) : IAliInvocationCompletionParticipant
{
    private int _terminal;

    public async ValueTask CompleteAsync(
        object? result,
        CancellationToken cancellationToken)
    {
        RequireFirstTerminal();
        AliDevOpsResultDigest digest;
        try
        {
            digest = AliDevOpsResultPolicy.Digest(kind, result);
        }
        catch (AliDevOpsResultContractException)
        {
            await store.MarkInDoubtAsync(
                    planId,
                    expectedRevision: 1,
                    "devops-result-contract-unproven",
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        if (!digest.Succeeded)
        {
            await store.MarkInDoubtAsync(
                    planId,
                    expectedRevision: 1,
                    outcomePrefix + "-returned-failure",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var outcomeCode = outcomePrefix + "-returned-success";
        await store.CompleteAsync(
                planId,
                expectedRevision: 1,
                outcomeCode,
                digest.Digest,
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
            TimeoutException => "devops-invocation-timeout",
            OperationCanceledException => "devops-invocation-canceled",
            UnauthorizedAccessException => "devops-invocation-unauthorized",
            IOException => "devops-invocation-io-failure",
            _ => "devops-invocation-failed"
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
                "The DevOps invocation participant already recorded a terminal state.");
        }
    }
}

internal sealed record AliDevOpsResultDigest(string Digest, bool Succeeded);

internal sealed class AliDevOpsResultContractException(string message)
    : Exception(message);

/// <summary>
/// Exact result contracts with fixed collection, string, and aggregate character bounds. Only
/// bounded typed fields enter the terminal receipt digest; an unknown or oversized result leaves
/// the durable invocation explicitly in doubt.
/// </summary>
internal static class AliDevOpsResultPolicy
{
    private const int MaximumAggregateCharacters = 8_000_000;
    private const int MaximumSingleStringCharacters = 1_000_000;

    internal static AliDevOpsResultDigest Digest(
        AliDevOpsInvocationKind kind,
        object? result)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var budget = new ResultBudget(hash, MaximumAggregateCharacters);
        budget.Append("ali-devops-result-v1");
        budget.Append(kind.ToString());
        var succeeded = kind switch
        {
            AliDevOpsInvocationKind.ArchitectureInspect =>
                AddArchitectureInspection(
                    budget,
                    Require<ArchitectureInspectionResult>(result, kind)),
            AliDevOpsInvocationKind.ArchitectureCheck =>
                AddArchitectureBoundary(
                    budget,
                    Require<ArchitectureBoundaryResult>(result, kind)),
            AliDevOpsInvocationKind.QualityScan =>
                AddQualityScan(budget, Require<QualityScanResult>(result, kind)),
            AliDevOpsInvocationKind.ApplicationVerify =>
                AddApplicationVerification(
                    budget,
                    Require<ApplicationVerificationResult>(result, kind)),
            AliDevOpsInvocationKind.ReleasePublish =>
                AddRelease(budget, Require<DotNetReleaseResult>(result, kind)),
            AliDevOpsInvocationKind.DeliveryVerify =>
                AddDelivery(budget, Require<AutonomousDeliveryResult>(result, kind)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var digest = hash.GetHashAndReset();
        try
        {
            return new AliDevOpsResultDigest(
                Convert.ToHexString(digest).ToLowerInvariant(),
                succeeded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static bool AddArchitectureInspection(
        ResultBudget budget,
        ArchitectureInspectionResult result)
    {
        budget.Append(result.Success);
        budget.Append(result.Summary);
        RequireCount(result.ProjectEdges, 10_000, "architecture project edges");
        budget.Append(result.ProjectEdges.Count);
        foreach (var edge in result.ProjectEdges)
        {
            budget.Append(edge.From);
            budget.Append(edge.To);
        }
        RequireCount(result.CallEdges, 2_000, "architecture call edges");
        budget.Append(result.CallEdges.Count);
        foreach (var edge in result.CallEdges)
        {
            budget.Append(edge.Caller);
            budget.Append(edge.Callee);
            budget.Append(edge.File);
            budget.Append(edge.Line);
        }
        RequireCount(result.ProjectCycles, 10_000, "architecture project cycles");
        budget.Append(result.ProjectCycles.Count);
        foreach (var cycle in result.ProjectCycles)
        {
            RequireCount(cycle, 1_000, "one architecture project cycle");
            budget.Append(cycle.Count);
            foreach (var project in cycle)
            {
                budget.Append(project);
            }
        }
        RequireCount(result.WorkspaceWarnings, 10_000, "architecture workspace warnings");
        budget.Append(result.WorkspaceWarnings.Count);
        foreach (var warning in result.WorkspaceWarnings)
        {
            budget.Append(warning);
        }
        return result.Success;
    }

    private static bool AddArchitectureBoundary(
        ResultBudget budget,
        ArchitectureBoundaryResult result)
    {
        budget.Append(result.Success);
        budget.Append(result.Summary);
        RequireCount(result.Violations, 10_000, "architecture violations");
        budget.Append(result.Violations.Count);
        foreach (var violation in result.Violations)
        {
            budget.Append(violation.Rule);
            budget.Append(violation.FromSymbol);
            budget.Append(violation.ToSymbol);
            budget.Append(violation.File);
            budget.Append(violation.Line);
        }
        return result.Success;
    }

    private static bool AddQualityScan(ResultBudget budget, QualityScanResult result)
    {
        budget.Append(result.Success);
        budget.Append(result.Summary);
        budget.Append(result.SarifPath);
        budget.Append(result.EditorConfigPresent);
        RequireCount(result.Findings, 20_000, "quality findings");
        budget.Append(result.Findings.Count);
        foreach (var finding in result.Findings)
        {
            budget.Append(finding.RuleId);
            budget.Append(finding.Severity);
            budget.Append(finding.Message);
            budget.Append(finding.File);
            budget.Append(finding.Line);
        }
        return result.Success;
    }

    private static bool AddApplicationVerification(
        ResultBudget budget,
        ApplicationVerificationResult result)
    {
        budget.Append(result.Success);
        budget.Append(result.Summary);
        budget.Append(result.ProjectPath);
        budget.Append(result.ApplicationKind);
        budget.Append(result.ExitCode);
        budget.Append(result.ProcessId);
        budget.Append(result.Output);
        budget.Append(result.ScreenshotPath);
        budget.Append(result.HealthCheckPassed);
        budget.Append(result.DurationMilliseconds);
        return result.Success;
    }

    private static bool AddRelease(ResultBudget budget, DotNetReleaseResult result)
    {
        budget.Append(result.Success);
        budget.Append(result.Summary);
        budget.Append(result.ProjectPath);
        budget.Append(result.PublishDirectory);
        budget.Append(result.ManifestPath);
        budget.Append(result.Output);
        RequireCount(result.Files, 25_000, "release files");
        budget.Append(result.Files.Count);
        foreach (var file in result.Files)
        {
            budget.Append(file.RelativePath);
            budget.Append(file.Size);
            budget.Append(file.Sha256);
        }
        return result.Success;
    }

    private static bool AddDelivery(ResultBudget budget, AutonomousDeliveryResult result)
    {
        budget.Append(result.Success);
        budget.Append(result.Summary);
        budget.Append(result.TargetPath);
        budget.Append(result.ReleaseDirectory);
        RequireCount(result.Stages, 8, "delivery stages");
        budget.Append(result.Stages.Count);
        foreach (var stage in result.Stages)
        {
            budget.Append(stage.Name);
            budget.Append(stage.Success);
            budget.Append(stage.Evidence);
            budget.Append(stage.DurationMilliseconds);
        }
        return result.Success;
    }

    private static T Require<T>(object? result, AliDevOpsInvocationKind kind)
        where T : class =>
        result as T
        ?? throw new AliDevOpsResultContractException(
            $"The {kind} invocation returned an unexpected result contract.");

    private static void RequireCount<T>(
        IReadOnlyCollection<T>? values,
        int maximum,
        string description)
    {
        if (values is null || values.Count > maximum)
        {
            throw new AliDevOpsResultContractException(
                $"The {description} result exceeds its fixed bound or is missing.");
        }
    }

    private sealed class ResultBudget(IncrementalHash hash, int remainingCharacters)
    {
        private int _remainingCharacters = remainingCharacters;

        internal void Append(string? value)
        {
            value ??= "<null>";
            if (value.Length > MaximumSingleStringCharacters
                || value.Length > _remainingCharacters)
            {
                throw new AliDevOpsResultContractException(
                    "The DevOps result exceeds its fixed text-output bound.");
            }
            _remainingCharacters -= value.Length;
            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                hash.AppendData(bytes);
                hash.AppendData([0]);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        internal void Append(bool value) =>
            Append(value.ToString(CultureInfo.InvariantCulture));

        internal void Append(int value) =>
            Append(value.ToString(CultureInfo.InvariantCulture));

        internal void Append(int? value) =>
            Append(value?.ToString(CultureInfo.InvariantCulture));

        internal void Append(long value) =>
            Append(value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Six explicit invocation entrypoints used by the six fixed production AIFunction registrations.
/// The shared private helper only performs begin/timeout mechanics for the fixed adapter supplied
/// by that entrypoint and cannot select another tool.
/// </summary>
internal sealed class AliDevOpsExecutionCoordinator
{
    private readonly AliDevOpsExecutionAdapter _architectureInspect;
    private readonly AliDevOpsExecutionAdapter _architectureCheck;
    private readonly AliDevOpsExecutionAdapter _qualityScan;
    private readonly AliDevOpsExecutionAdapter _applicationVerify;
    private readonly AliDevOpsExecutionAdapter _releasePublish;
    private readonly AliDevOpsExecutionAdapter _deliveryVerify;
    private readonly Action? _beforeDelegate;

    internal AliDevOpsExecutionCoordinator(
        AliDevOpsInvocationBindingResolver bindings,
        AliDurableInvocationStore store,
        EvidenceLedger evidence,
        Action? beforeDelegate = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(evidence);
        _beforeDelegate = beforeDelegate;
        _architectureInspect = Adapter(AliDevOpsInvocationKind.ArchitectureInspect);
        _architectureCheck = Adapter(AliDevOpsInvocationKind.ArchitectureCheck);
        _qualityScan = Adapter(AliDevOpsInvocationKind.QualityScan);
        _applicationVerify = Adapter(AliDevOpsInvocationKind.ApplicationVerify);
        _releasePublish = Adapter(AliDevOpsInvocationKind.ReleasePublish);
        _deliveryVerify = Adapter(AliDevOpsInvocationKind.DeliveryVerify);
        TargetStates = new AliDevOpsTargetStateAdapter(bindings);
        Adapters = Array.AsReadOnly(new IAliExecutionEffectAdapter[]
        {
            _architectureInspect,
            _architectureCheck,
            _qualityScan,
            _applicationVerify,
            _releasePublish,
            _deliveryVerify
        });

        AliDevOpsExecutionAdapter Adapter(AliDevOpsInvocationKind kind) =>
            new(kind, bindings, store, evidence);
    }

    internal IReadOnlyList<IAliExecutionEffectAdapter> Adapters { get; }

    internal AliDevOpsTargetStateAdapter TargetStates { get; }

    internal Task<ArchitectureInspectionResult> ExecuteArchitectureInspectAsync(
        string targetPath,
        Func<CancellationToken, Task<ArchitectureInspectionResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _architectureInspect,
            JsonSerializer.SerializeToElement(new { targetPath }),
            execute,
            cancellationToken);

    internal Task<ArchitectureBoundaryResult> ExecuteArchitectureCheckAsync(
        string targetPath,
        ArchitectureBoundaryRule[] rules,
        Func<CancellationToken, Task<ArchitectureBoundaryResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _architectureCheck,
            JsonSerializer.SerializeToElement(new { targetPath, rules }),
            execute,
            cancellationToken);

    internal Task<QualityScanResult> ExecuteQualityScanAsync(
        string projectPath,
        Func<CancellationToken, Task<QualityScanResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _qualityScan,
            JsonSerializer.SerializeToElement(new { projectPath }),
            execute,
            cancellationToken);

    internal Task<ApplicationVerificationResult> ExecuteApplicationVerifyAsync(
        string projectPath,
        string? configuration,
        string? healthUrl,
        Func<CancellationToken, Task<ApplicationVerificationResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _applicationVerify,
            JsonSerializer.SerializeToElement(new
            {
                projectPath,
                configuration,
                healthUrl
            }),
            execute,
            cancellationToken);

    internal Task<DotNetReleaseResult> ExecuteReleasePublishAsync(
        string projectPath,
        string? runtimeIdentifier,
        bool selfContained,
        Func<CancellationToken, Task<DotNetReleaseResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _releasePublish,
            JsonSerializer.SerializeToElement(new
            {
                projectPath,
                runtimeIdentifier,
                selfContained
            }),
            execute,
            cancellationToken);

    internal Task<AutonomousDeliveryResult> ExecuteDeliveryVerifyAsync(
        string projectPath,
        string? testTargetPath,
        string? configuration,
        bool verifyApplication,
        bool publishRelease,
        Func<CancellationToken, Task<AutonomousDeliveryResult>> execute,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            _deliveryVerify,
            JsonSerializer.SerializeToElement(new
            {
                projectPath,
                testTargetPath,
                configuration,
                verifyApplication,
                publishRelease
            }),
            execute,
            cancellationToken);

    private async Task<TResult> ExecuteAsync<TResult>(
        AliDevOpsExecutionAdapter adapter,
        JsonElement arguments,
        Func<CancellationToken, Task<TResult>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(execute);
        var binding = await adapter.BeginInvocationAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        using var targetRoots = AliDevOpsTargetRootLeaseGroup.Acquire(
            binding.TargetRootIdentities);
        adapter.RequireStableInvocation(binding, arguments);
        _beforeDelegate?.Invoke();
        targetRoots.RequireStable();
        adapter.RequireStableInvocation(binding, arguments);
        using var processBinding = AliExactProcessExecutionContext.Enter(
            binding.ProcessBinding);
        using var timeout = new CancellationTokenSource(
            AliDevOpsInvocationCatalog.Timeout(adapter.Kind));
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
                "The bounded DevOps invocation exceeded its fixed operation limit.",
                exception);
        }
    }
}

internal sealed class AliDevOpsTargetRootLeaseGroup : IDisposable
{
    private readonly List<AliExecutionDirectoryLease> _leases;
    private bool _disposed;

    private AliDevOpsTargetRootLeaseGroup(List<AliExecutionDirectoryLease> leases) =>
        _leases = leases;

    internal static AliDevOpsTargetRootLeaseGroup Acquire(
        IReadOnlyList<AliExecutionDirectoryBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var acquiredPaths = new HashSet<string>(comparer);
        var leases = new List<AliExecutionDirectoryLease>();
        try
        {
            foreach (var binding in bindings
                         .OrderBy(item => item.TargetPath, comparer))
            {
                if (!acquiredPaths.Add(binding.TargetPath))
                {
                    continue;
                }
                leases.Add(binding.Acquire("A selected DevOps target root spine"));
            }
            return new AliDevOpsTargetRootLeaseGroup(leases);
        }
        catch
        {
            for (var index = leases.Count - 1; index >= 0; index--)
            {
                leases[index].Dispose();
            }
            throw;
        }
    }

    internal void RequireStable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var lease in _leases)
        {
            lease.RequireStable();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        for (var index = _leases.Count - 1; index >= 0; index--)
        {
            _leases[index].Dispose();
        }
    }
}
