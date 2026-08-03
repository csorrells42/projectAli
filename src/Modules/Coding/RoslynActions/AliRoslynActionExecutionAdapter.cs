using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coding.Changesets;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;

namespace Ali.Modules.Coding.RoslynActions;

/// <summary>
/// Exact durable-effect ownership for one Action Deck schema. Instances are registered by
/// complete tool/capability/reconciler identity; no name pattern grants execution authority.
/// </summary>
internal sealed class AliRoslynActionExecutionAdapter : IAliExecutionEffectAdapter
{
    private const string StagedDotNetBindingPrefix = "ali-roslyn-staged-dotnet-v1";
    private readonly AliCodingProjectResolver _resolver;
    private readonly AliRoslynActionHandleStore _handles;
    private readonly AliSourceChangeSetStore _changeSets;
    private readonly AliSourceChangeSetReconciler _sourceReconciler;
    private readonly AliRoslynActionPublicationRecovery _publicationRecovery;
    private readonly AliRoslynActionTargetStateAdapter _targetStates;
    private readonly EvidenceLedger _evidence;

    private AliRoslynActionExecutionAdapter(
        string toolName,
        AliCodingProjectResolver resolver,
        AliRoslynActionHandleStore handles,
        AliSourceChangeSetStore changeSets,
        AliSourceChangeSetReconciler sourceReconciler,
        AliRoslynActionPublicationRecovery publicationRecovery,
        AliRoslynActionTargetStateAdapter targetStates,
        EvidenceLedger evidence)
    {
        ToolName = toolName;
        CapabilityId = CapabilityIdFor(toolName);
        ReconcilerId = ReconcilerIdFor(toolName);
        _resolver = resolver;
        _handles = handles;
        _changeSets = changeSets;
        _sourceReconciler = sourceReconciler;
        _publicationRecovery = publicationRecovery;
        _targetStates = targetStates;
        _evidence = evidence;
    }

    public string ToolName { get; }

    public string CapabilityId { get; }

    public string ReconcilerId { get; }

    internal static IReadOnlyList<IAliExecutionEffectAdapter> CreateAll(
        AliCodingProjectResolver resolver,
        AliRoslynActionHandleStore handles,
        AliSourceChangeSetStore changeSets,
        AliSourceChangeSetReconciler sourceReconciler,
        AliRoslynActionPublicationRecovery publicationRecovery,
        AliRoslynActionTargetStateAdapter targetStates,
        EvidenceLedger evidence)
    {
        AliRoslynActionExecutionAdapter Create(string toolName) => new(
            toolName,
            resolver,
            handles,
            changeSets,
            sourceReconciler,
            publicationRecovery,
            targetStates,
            evidence);

        return
        [
            Create(AliCapabilityCatalog.RoslynInspectTargetName),
            Create(AliCapabilityCatalog.RoslynListActionsName),
            Create(AliCapabilityCatalog.RoslynPreviewActionName),
            Create(AliCapabilityCatalog.RoslynVerifyChangesetName),
            Create(AliCapabilityCatalog.RoslynApplyActionName)
        ];
    }

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
                "The Roslyn Action Deck adapter received a mismatched execution identity.");
        }

        var current = _targetStates.Capture(ToolName, request.Arguments);
        var currentDigest = WorkIdentityCanonicalizer.MapDigest(
            "action-target-versions-v1",
            current.TargetVersions);
        if (!string.Equals(currentDigest, request.TargetVersionDigest, StringComparison.Ordinal))
        {
            throw new AliExecutionPreparationException(
                "The Roslyn Action Deck target changed after the accepted decision.");
        }

        return ToolName switch
        {
            AliCapabilityCatalog.RoslynInspectTargetName =>
                PrepareTarget(request.Arguments, Guid.NewGuid().ToString("N")),
            AliCapabilityCatalog.RoslynListActionsName =>
                PrepareTarget(request.Arguments, Guid.NewGuid().ToString("N")),
            AliCapabilityCatalog.RoslynPreviewActionName =>
                PrepareTarget(request.Arguments, Guid.NewGuid().ToString("N")),
            AliCapabilityCatalog.RoslynVerifyChangesetName =>
                await PrepareHandleAsync(request.Arguments, apply: false, cancellationToken)
                    .ConfigureAwait(false),
            AliCapabilityCatalog.RoslynApplyActionName =>
                await PrepareHandleAsync(request.Arguments, apply: true, cancellationToken)
                    .ConfigureAwait(false),
            _ => throw new AliExecutionPreparationException(
                "The Roslyn Action Deck adapter tool identity is not supported.")
        } with { TargetVersionDigest = request.TargetVersionDigest };
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
            return ActionReconciliationResult.Unknown("roslyn-adapter-identity-mismatch");
        }

        try
        {
            return ToolName switch
            {
                AliCapabilityCatalog.RoslynInspectTargetName =>
                    ActionReconciliationResult.Absent("roslyn-inspection-safe-to-repeat"),
                AliCapabilityCatalog.RoslynListActionsName =>
                    ActionReconciliationResult.Absent("roslyn-list-safe-to-repeat"),
                AliCapabilityCatalog.RoslynPreviewActionName =>
                    await ReconcilePreviewAsync(identity, intent, cancellationToken)
                        .ConfigureAwait(false),
                AliCapabilityCatalog.RoslynVerifyChangesetName =>
                    await ReconcileVerificationAsync(identity, intent, cancellationToken)
                        .ConfigureAwait(false),
                AliCapabilityCatalog.RoslynApplyActionName =>
                    await ReconcilePublicationAsync(identity, intent, cancellationToken)
                        .ConfigureAwait(false),
                _ => ActionReconciliationResult.Unknown("roslyn-adapter-tool-unknown")
            };
        }
        catch (FileNotFoundException)
        {
            return ToolName == AliCapabilityCatalog.RoslynApplyActionName
                ? ActionReconciliationResult.Unknown("roslyn-publication-artifact-missing")
                : ActionReconciliationResult.Absent("roslyn-prepared-artifact-absent");
        }
        catch (Exception exception) when (IsRecoverableReconciliationFailure(exception))
        {
            return ActionReconciliationResult.Unknown(
                "roslyn-reconcile-" + StableExceptionCode(exception));
        }
    }

    internal static string CapabilityIdFor(string toolName) => "ali.tool." + toolName;

    internal static string ReconcilerIdFor(string toolName) => "ali.reconcile." + toolName;

    internal static string RootBinding(string sourceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }
        return HashText("ali-source-root-v1\0" + normalized);
    }

    internal static string StagedRootBinding(
        string sourceRoot,
        AliBoundExecutionFile dotNetHost)
    {
        ArgumentNullException.ThrowIfNull(dotNetHost);
        var exactPath = Path.GetFullPath(dotNetHost.PhysicalPath);
        if (!Path.IsPathFullyQualified(exactPath)
            || !IsExactFileIdentity(dotNetHost.Identity))
        {
            throw new InvalidDataException(
                "The staged Roslyn .NET host binding is invalid.");
        }

        return string.Join(
            '|',
            StagedDotNetBindingPrefix,
            RootBinding(sourceRoot),
            dotNetHost.Identity,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(exactPath)));
    }

    internal static AliBoundExecutionFile RequireStagedDotNetHostBinding(
        AliExecutionGrant grant,
        string sourceRoot)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        var parts = grant.RootBinding.Split('|');
        if (parts.Length != 4
            || !string.Equals(parts[0], StagedDotNetBindingPrefix, StringComparison.Ordinal)
            || !string.Equals(parts[1], RootBinding(sourceRoot), StringComparison.Ordinal)
            || !IsExactFileIdentity(parts[2]))
        {
            throw new InvalidOperationException(
                "The Roslyn staged-verification grant does not bind an exact .NET host and source root.");
        }

        string path;
        try
        {
            path = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]));
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "The Roslyn staged-verification .NET host binding is malformed.",
                exception);
        }
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > 1_024
            || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException(
                "The Roslyn staged-verification .NET host path is invalid.");
        }

        var boundHost = new AliBoundExecutionFile(Path.GetFullPath(path), parts[2]);
        _ = AliExactDotNetHost.Revalidate(boundHost);
        return boundHost;
    }

    private static bool IsExactFileIdentity(string? identity) =>
        identity is not null
        && identity.StartsWith("file:sha256:", StringComparison.Ordinal)
        && identity.Length == "file:sha256:".Length + 64
        && identity["file:sha256:".Length..].All(char.IsAsciiHexDigit);

    internal static string AuthorizationBindingDigest(AliExecutionGrant grant)
        => AliExecutionAuthorizationDigest.Compute(
            AliExecutionAuthorizationDigest.SourcePublicationDomain,
            grant);

    private AliExecutionPreparation PrepareTarget(JsonElement arguments, string preparationIdentity)
    {
        var target = _resolver.ResolveExistingTarget(
            AliRoslynActionTargetStateAdapter.RequireString(arguments, "targetPath"));
        return new(
            preparationIdentity,
            RootBinding(target.RootDirectory),
            new string('0', 64));
    }

    private async Task<AliExecutionPreparation> PrepareHandleAsync(
        JsonElement arguments,
        bool apply,
        CancellationToken cancellationToken)
    {
        var handle = await _handles.LoadAsync(
                AliRoslynActionTargetStateAdapter.RequireString(arguments, "handleId"),
                cancellationToken)
            .ConfigureAwait(false);
        if (handle.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new AliExecutionPreparationException("The Roslyn action handle expired.");
        }
        if (apply)
        {
            if (handle.State != AliRoslynActionHandleState.Verified
                || handle.Verification?.Success != true
                || handle.Verification.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                throw new AliExecutionPreparationException(
                    "Only an exact, unexpired, preverified Roslyn action can publish.");
            }
        }
        else if (handle.State != AliRoslynActionHandleState.Previewed)
        {
            throw new AliExecutionPreparationException(
                "Only an exact previewed Roslyn action can enter staged verification.");
        }

        var changeSet = await _changeSets.LoadAsync(handle.ChangeSetId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(changeSet.ManifestDigest, handle.ChangeSetManifestDigest, StringComparison.Ordinal)
            || !Path.GetFullPath(changeSet.CanonicalSourceRoot).Equals(
                Path.GetFullPath(handle.SourceRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new AliExecutionPreparationException(
                "The protected Roslyn handle no longer matches its authenticated source manifest.");
        }

        var rootBinding = apply
            ? RootBinding(handle.SourceRoot)
            : StagedRootBinding(handle.SourceRoot, AliExactDotNetHost.CaptureCurrent());
        return new(
            apply ? changeSet.Id : handle.Id,
            rootBinding,
            new string('0', 64));
    }

    private async Task<ActionReconciliationResult> ReconcilePreviewAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        CancellationToken cancellationToken)
    {
        var handle = await _handles.LoadAsync(intent.PreparationIdentity!, cancellationToken)
            .ConfigureAwait(false);
        return handle.State == AliRoslynActionHandleState.Previewed
                || handle.State == AliRoslynActionHandleState.Verified
                || handle.State == AliRoslynActionHandleState.Applying
                || handle.State == AliRoslynActionHandleState.Applied
            ? ActionReconciliationResult.Applied(
                "roslyn-preview-proved",
                await AppendReconciliationEvidenceAsync(
                        identity,
                        intent,
                        "roslyn-preview-proved",
                        handle.ChangeSetManifestDigest,
                        reportedSuccess: true,
                        cancellationToken)
                    .ConfigureAwait(false))
            : ActionReconciliationResult.Absent("roslyn-preview-not-usable");
    }

    private async Task<ActionReconciliationResult> ReconcileVerificationAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        CancellationToken cancellationToken)
    {
        var handle = await _handles.LoadAsync(intent.PreparationIdentity!, cancellationToken)
            .ConfigureAwait(false);
        return handle.State is AliRoslynActionHandleState.Verified
            or AliRoslynActionHandleState.Applying
            or AliRoslynActionHandleState.Applied
            ? ActionReconciliationResult.Applied(
                "roslyn-verification-proved",
                await AppendReconciliationEvidenceAsync(
                        identity,
                        intent,
                        "roslyn-verification-proved",
                        handle.Verification?.VerificationDigest ?? handle.ChangeSetManifestDigest,
                        reportedSuccess: true,
                        cancellationToken)
                    .ConfigureAwait(false))
            : ActionReconciliationResult.Absent("roslyn-verification-not-committed");
    }

    private async Task<ActionReconciliationResult> ReconcilePublicationAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        CancellationToken cancellationToken)
    {
        var hasAuthorizationIdentity = AliExecutionAuthorizationDigest.TryCompute(
            AliExecutionAuthorizationDigest.SourcePublicationDomain,
            intent,
            out var expectedAuthorizationBindingDigest);
        var source = await _sourceReconciler
            .ReconcileAsync(intent.PreparationIdentity!, cancellationToken)
            .ConfigureAwait(false);
        var recovered = await _publicationRecovery.ReconcileAsync(
                intent.PreparationIdentity!,
                source.State,
                hasAuthorizationIdentity ? expectedAuthorizationBindingDigest : null,
                cancellationToken)
            .ConfigureAwait(false);
        return recovered.Disposition switch
        {
            AliRoslynActionPublicationRecoveryDisposition.AppliedAndPostverified =>
                ActionReconciliationResult.Applied(
                recovered.OutcomeCode,
                await AppendReconciliationEvidenceAsync(
                        identity,
                        intent,
                        recovered.OutcomeCode,
                        recovered.EvidenceVersion,
                        reportedSuccess: true,
                        cancellationToken)
                    .ConfigureAwait(false)),
            AliRoslynActionPublicationRecoveryDisposition.AppliedNeedsReview =>
                ActionReconciliationResult.Applied(
                    recovered.OutcomeCode,
                    await AppendReconciliationEvidenceAsync(
                            identity,
                            intent,
                            recovered.OutcomeCode,
                            recovered.EvidenceVersion,
                            reportedSuccess: false,
                            cancellationToken)
                        .ConfigureAwait(false)),
            AliRoslynActionPublicationRecoveryDisposition.Absent =>
                ActionReconciliationResult.Absent(recovered.OutcomeCode),
            _ => ActionReconciliationResult.Unknown(recovered.OutcomeCode)
        };
    }

    private async Task<CommittedEvidenceReference> AppendReconciliationEvidenceAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        string outcomeCode,
        string afterVersion,
        bool reportedSuccess,
        CancellationToken cancellationToken)
    {
        var resultBytes = Encoding.UTF8.GetBytes(outcomeCode);
        try
        {
            var fixedTime = DateTimeOffset.UnixEpoch;
            var draft = new EvidenceDraft
            {
                EvidenceId = HashText(
                    "ali-roslyn-reconciliation-evidence-v1\0"
                    + identity.StorageKey + "\0" + intent.IdempotencyKey + "\0" + outcomeCode),
                CallId = intent.AcceptedCallId ?? intent.IdempotencyKey,
                WorkItemId = intent.WorkItemId,
                ToolName = intent.ToolName,
                CapabilityGroup = "csharp-dotnet-roslyn",
                ProviderId = "ali-core",
                RegistryRevision = intent.RegistryRevisionDigest,
                EffectKind = intent.ToolName == AliCapabilityCatalog.RoslynApplyActionName
                    ? "update"
                    : "create",
                Arguments = JsonSerializer.SerializeToElement(new
                {
                    intent.CanonicalArgumentsDigest,
                    intent.PreparationIdentity
                }),
                Result = JsonSerializer.SerializeToElement(new { outcomeCode }),
                NormalizedTarget = JsonSerializer.SerializeToElement(new
                {
                    intent.PreparationIdentity,
                    intent.TargetVersionDigest
                }),
                NormalizedEffectResult = JsonSerializer.SerializeToElement(new { outcomeCode }),
                Outcome = ToolInvocationOutcome.Returned(resultBytes, reportedSuccess),
                StableOutcomeCode = outcomeCode,
                StartedAtUtc = fixedTime,
                CompletedAtUtc = fixedTime,
                Artifacts =
                [
                    new EvidenceArtifactDraft(
                        intent.PreparationIdentity!,
                        "file",
                        BeforeVersion: null,
                        AfterVersion: afterVersion)
                ],
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
                    reconciler = intent.ReconcilerId,
                    replayedEffect = false
                })
            };
            await _evidence.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            var committed = await _evidence.AppendAsync(identity, draft, cancellationToken)
                .ConfigureAwait(false);
            return new(
                committed.Evidence.EvidenceId,
                committed.Cursor,
                committed.Checksum);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(resultBytes);
        }
    }

    private static bool IsRecoverableReconciliationFailure(Exception exception) =>
        exception is not OperationCanceledException
            and not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    private static string StableExceptionCode(Exception exception)
    {
        var name = exception.GetType().Name;
        var filtered = new string(name.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(filtered) ? "failed" : filtered[..Math.Min(filtered.Length, 72)];
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
