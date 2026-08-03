using System.Security.Cryptography;
using System.Text;
using Ali.Modules.Coding.Changesets;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Text;

namespace Ali.Modules.Coding.RoslynActions;

/// <summary>
/// Exact-identity Roslyn action discovery, isolated preview, and staged preverification.
/// This type never receives a canonical publication primitive.
/// </summary>
internal sealed class AliRoslynActionDeck
{
    internal static readonly TimeSpan PreviewLifetime = TimeSpan.FromHours(2);
    internal static readonly TimeSpan VerificationLifetime = TimeSpan.FromMinutes(30);

    private readonly AliRoslynWorkspaceLoader _workspaceLoader;
    private readonly AliRoslynPreviewWorkspaceManager _previewWorkspaces;
    private readonly AliRoslynActionDiscovery _discovery;
    private readonly AliRoslynChangeSetStore _roslynChangeSets;
    private readonly AliSourceChangeSetStore _sourceChangeSets;
    private readonly AliRoslynActionHandleStore _handles;
    private readonly AliRoslynActionTargetStateAdapter _targetStates;
    private readonly AliRoslynSolutionFingerprint _fingerprint;
    private readonly AliRoslynChangeSetVerifier _diagnostics;
    private readonly AliRoslynStagedBuildVerifier _buildVerifier;
    private readonly TimeProvider _timeProvider;

    internal AliRoslynActionDeck(
        AliRoslynWorkspaceLoader workspaceLoader,
        AliRoslynPreviewWorkspaceManager previewWorkspaces,
        AliRoslynActionDiscovery discovery,
        AliRoslynChangeSetStore roslynChangeSets,
        AliSourceChangeSetStore sourceChangeSets,
        AliRoslynActionHandleStore handles,
        AliRoslynActionTargetStateAdapter targetStates,
        AliRoslynSolutionFingerprint fingerprint,
        AliRoslynChangeSetVerifier diagnostics,
        AliRoslynStagedBuildVerifier buildVerifier,
        TimeProvider? timeProvider = null)
    {
        _workspaceLoader = workspaceLoader ?? throw new ArgumentNullException(nameof(workspaceLoader));
        _previewWorkspaces = previewWorkspaces ?? throw new ArgumentNullException(nameof(previewWorkspaces));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _roslynChangeSets = roslynChangeSets ?? throw new ArgumentNullException(nameof(roslynChangeSets));
        _sourceChangeSets = sourceChangeSets ?? throw new ArgumentNullException(nameof(sourceChangeSets));
        _handles = handles ?? throw new ArgumentNullException(nameof(handles));
        _targetStates = targetStates ?? throw new ArgumentNullException(nameof(targetStates));
        _fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _buildVerifier = buildVerifier ?? throw new ArgumentNullException(nameof(buildVerifier));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<AliRoslynTargetInspection> InspectTargetAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var grant = ConsumeGrant(AliCapabilityCatalog.RoslynInspectTargetName);
        try
        {
            using var canonical = await AuthorizeAndLoadTargetAsync(
                    AliCapabilityCatalog.RoslynInspectTargetName,
                    grant,
                    targetPath,
                    documentPath: null,
                    cancellationToken)
                .ConfigureAwait(false);
            using var preview = await _previewWorkspaces.CreateAsync(canonical, cancellationToken)
                .ConfigureAwait(false);
            var diagnosticSet = await _diagnostics.CaptureAsync(preview.Solution, cancellationToken)
                .ConfigureAwait(false);
            var errorCount = diagnosticSet.Diagnostics.Count(item =>
                string.Equals(item.Severity, DiagnosticSeverity.Error.ToString(), StringComparison.Ordinal));
            var warningCount = diagnosticSet.Diagnostics.Count(item =>
                string.Equals(item.Severity, DiagnosticSeverity.Warning.ToString(), StringComparison.Ordinal));
            return new(
                true,
                Path.GetFileName(canonical.Target.PhysicalPath),
                preview.CanonicalFingerprint.Sha256,
                preview.CanonicalFingerprint.ProjectCount,
                preview.CanonicalFingerprint.DocumentCount,
                errorCount,
                warningCount,
                canonical.Warnings.Count,
                "Roslyn loaded the exact target and reproduced its semantic inputs in an isolated workspace.",
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return AliRoslynTargetInspection.Failed(
                SafeFileName(targetPath),
                FailureCode(exception),
                "Roslyn could not establish an exact isolated semantic target.");
        }
    }

    internal async Task<AliRoslynActionListResult> ListActionsAsync(
        string targetPath,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        var grant = ConsumeGrant(AliCapabilityCatalog.RoslynListActionsName);
        try
        {
            using var canonical = await AuthorizeAndLoadTargetAsync(
                    AliCapabilityCatalog.RoslynListActionsName,
                    grant,
                    targetPath,
                    documentPath,
                    cancellationToken)
                .ConfigureAwait(false);
            var (canonicalDocument, position) = await _workspaceLoader.ResolvePositionAsync(
                    canonical,
                    documentPath,
                    line,
                    column,
                    cancellationToken)
                .ConfigureAwait(false);
            using var preview = await _previewWorkspaces.CreateAsync(canonical, cancellationToken)
                .ConfigureAwait(false);
            var result = await _discovery.DiscoverAsync(
                    preview.Solution,
                    canonicalDocument.Id,
                    new TextSpan(position, 0),
                    preview.CanonicalFingerprint.Sha256,
                    cancellationToken)
                .ConfigureAwait(false);
            var relativeDocument = RelativePath(canonical.Target.RootDirectory, canonicalDocument.FilePath);
            var actions = result.Actions
                .Select(action => new AliRoslynActionDescriptor(
                    action.IdentitySha256,
                    action.SolutionFingerprintSha256,
                    action.DocumentTextSha256,
                    action.ProviderIdentity,
                    action.ProviderVersion,
                    action.ProviderAssemblySha256,
                    action.EquivalenceKey,
                    action.NestedActionPath,
                    action.Title,
                    action.DiagnosticIds.ToArray(),
                    relativeDocument,
                    line,
                    column))
                .ToArray();
            var failures = result.ProviderFailures
                .Select(failure => new AliRoslynActionProviderFailureReceipt(
                    failure.ProviderIdentity,
                    failure.ProviderVersion,
                    failure.ProviderAssemblySha256,
                    failure.ExceptionType,
                    failure.MessageSha256))
                .ToArray();
            return new(
                true,
                Path.GetFileName(canonical.Target.PhysicalPath),
                relativeDocument,
                line,
                column,
                preview.CanonicalFingerprint.Sha256,
                actions,
                failures,
                result.Truncated,
                actions.Length == 0
                    ? "Roslyn found no applicable action at the exact source position."
                    : $"Roslyn found {actions.Length} exact action(s) at the source position.",
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return AliRoslynActionListResult.Failed(
                SafeFileName(targetPath),
                SafeFileName(documentPath),
                line,
                column,
                FailureCode(exception),
                "Roslyn could not discover actions at the exact source position.");
        }
    }

    internal async Task<AliRoslynActionPreview> PreviewActionAsync(
        string targetPath,
        string documentPath,
        int line,
        int column,
        string actionIdentitySha256,
        string requestedValue,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        ValidateDigest(actionIdentitySha256, nameof(actionIdentitySha256));
        ArgumentNullException.ThrowIfNull(requestedValue);
        if (requestedValue.Length > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedValue),
                "A Roslyn action value cannot exceed 4,096 characters.");
        }
        var grant = ConsumeGrant(AliCapabilityCatalog.RoslynPreviewActionName);
        try
        {
            using var canonical = await AuthorizeAndLoadTargetAsync(
                    AliCapabilityCatalog.RoslynPreviewActionName,
                    grant,
                    targetPath,
                    documentPath,
                    cancellationToken)
                .ConfigureAwait(false);
            var (canonicalDocument, position) = await _workspaceLoader.ResolvePositionAsync(
                    canonical,
                    documentPath,
                    line,
                    column,
                    cancellationToken)
                .ConfigureAwait(false);
            using var preview = await _previewWorkspaces.CreateAsync(canonical, cancellationToken)
                .ConfigureAwait(false);
            var discovery = await _discovery.DiscoverAsync(
                    preview.Solution,
                    canonicalDocument.Id,
                    new TextSpan(position, 0),
                    preview.CanonicalFingerprint.Sha256,
                    cancellationToken)
                .ConfigureAwait(false);
            var selected = discovery.Actions.SingleOrDefault(action =>
                string.Equals(action.IdentitySha256, actionIdentitySha256, StringComparison.Ordinal));
            if (selected is null)
            {
                return AliRoslynActionPreview.Failed(
                    actionIdentitySha256,
                    "selected-action-not-applicable",
                    "The exact action identity is no longer applicable at this source position.");
            }

            System.Collections.Immutable.ImmutableArray<CodeActionOperation> operations;
            try
            {
                operations = await selected.ExecuteAsync(requestedValue, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AliRoslynProviderExecutionException exception)
            {
                return AliRoslynActionPreview.Failed(
                    selected.IdentitySha256,
                    exception.FailureCode,
                    exception.Message);
            }
            if (operations.IsDefault
                || operations.Length != 1
                || operations[0] is not ApplyChangesOperation applyChanges)
            {
                return AliRoslynActionPreview.Failed(
                    selected.IdentitySha256,
                    "provider-operation-set-rejected",
                    "The exact provider action must return one ApplyChangesOperation and no other operation.");
            }
            var prepared = await _roslynChangeSets.CreateAsync(
                    preview.Solution,
                    applyChanges.ChangedSolution,
                    canonical.Target,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    prepared.CanonicalFingerprint.Sha256,
                    preview.CanonicalFingerprint.Sha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The canonical solution changed while the exact preview manifest was being created.");
            }

            var now = _timeProvider.GetUtcNow();
            var handle = new AliRoslynActionHandle(
                grant.PreparationIdentity,
                selected.IdentitySha256,
                selected.ProviderIdentity,
                selected.ProviderVersion,
                selected.EquivalenceKey,
                selected.Title,
                selected.DiagnosticIds.ToArray(),
                canonical.Target.PhysicalPath,
                canonical.Target.RootDirectory,
                selected.ProjectIdentity,
                selected.DocumentIdentity,
                canonicalDocument.FilePath
                    ?? throw new InvalidOperationException("The exact Roslyn document has no physical path."),
                selected.SpanStart,
                selected.SpanLength,
                requestedValue,
                prepared.CanonicalFingerprint.Sha256,
                prepared.SourceChangeSet.Id,
                prepared.SourceChangeSet.ManifestDigest,
                now,
                now + PreviewLifetime,
                AliRoslynActionHandleState.Previewed,
                1,
                DocumentChanges: prepared.DocumentChanges.ToArray());
            await _handles.CreateAsync(
                    handle,
                    prepared.StagedFingerprint.Sha256,
                    cancellationToken)
                .ConfigureAwait(false);

            var comparison = _diagnostics.Compare(
                prepared.BaselineDiagnostics,
                prepared.StagedDiagnostics);
            return new(
                true,
                handle.Id,
                selected.IdentitySha256,
                selected.Title,
                prepared.SourceChangeSet.Id,
                prepared.SourceChangeSet.ManifestDigest,
                prepared.CanonicalFingerprint.Sha256,
                prepared.StagedFingerprint.Sha256,
                prepared.ChangedFiles.Take(AliSourceChangeSetStore.MaximumOperations).ToArray(),
                comparison.NoRegressions,
                comparison.Added.Count,
                "Roslyn created an exact protected preview handle and authenticated staged source manifest; canonical source was not changed.",
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return AliRoslynActionPreview.Failed(
                actionIdentitySha256,
                FailureCode(exception),
                "Roslyn could not create an exact isolated action preview.");
        }
    }

    internal async Task<AliRoslynActionVerification> VerifyAsync(
        string handleId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handleId);
        var grant = ConsumeGrant(AliCapabilityCatalog.RoslynVerifyChangesetName);
        AliRoslynActionHandle handle;
        try
        {
            handle = await _handles.LoadAsync(handleId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return AliRoslynActionVerification.Failed(
                handleId,
                "unavailable",
                FailureCode(exception),
                "The protected Roslyn action handle could not be loaded.");
        }

        var now = _timeProvider.GetUtcNow();
        if (!string.Equals(grant.PreparationIdentity, handle.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Roslyn staged-verification grant does not match the exact prepared handle.");
        }
        var dotNetHost = AliRoslynActionExecutionAdapter.RequireStagedDotNetHostBinding(
            grant,
            handle.SourceRoot);
        if (handle.ExpiresAtUtc <= now)
        {
            return AliRoslynActionVerification.Failed(
                handle.Id,
                AliRoslynActionHandleState.Expired.ToString(),
                "preview-expired",
                "The exact preview expired before staged verification.");
        }
        if (handle.State == AliRoslynActionHandleState.Verified
            && handle.Verification?.Success == true
            && handle.Verification.ExpiresAtUtc > now)
        {
            return AliRoslynActionVerification.FromVerifiedHandle(handle);
        }
        if (handle.State != AliRoslynActionHandleState.Previewed)
        {
            return AliRoslynActionVerification.Failed(
                handle.Id,
                handle.State.ToString(),
                "invalid-handle-state",
                "Only a fresh preview handle can enter staged preverification.");
        }

        AliRoslynStagedDirectory? stagedDirectory = null;
        try
        {
            var sourceChangeSet = await _sourceChangeSets.LoadAsync(handle.ChangeSetId, cancellationToken)
                .ConfigureAwait(false);
            RequireHandleManifest(handle, sourceChangeSet);
            var authorizedTarget = _targetStates.ResolveTarget(
                handle.TargetPath,
                documentPath: null);
            RequireHandleTarget(handle, authorizedTarget.Target);
            using var canonical = await _workspaceLoader.LoadAsync(
                    authorizedTarget.Target,
                    cancellationToken)
                .ConfigureAwait(false);
            await _targetStates.BindLoadedAsync(
                    canonical,
                    authorizedTarget,
                    cancellationToken)
                .ConfigureAwait(false);
            using var preview = await _previewWorkspaces.CreateAsync(canonical, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    preview.CanonicalFingerprint.Sha256,
                    handle.CanonicalSolutionFingerprint,
                    StringComparison.Ordinal))
            {
                return AliRoslynActionVerification.Failed(
                    handle.Id,
                    handle.State.ToString(),
                    "stale-canonical-fingerprint",
                    "Verification refused the preview because the canonical semantic fingerprint changed.");
            }

            var baselineDiagnostics = await _diagnostics.CaptureAsync(
                    preview.Solution,
                    cancellationToken)
                .ConfigureAwait(false);
            var stagedSolution = await ReconstructStagedSolutionAsync(
                    preview.Solution,
                    canonical.Target,
                    sourceChangeSet,
                    handle.DocumentChanges
                        ?? throw new InvalidDataException(
                            "The protected action handle has no exact Roslyn document delta."),
                    cancellationToken)
                .ConfigureAwait(false);
            var stagedFingerprint = await _fingerprint.CaptureAsync(stagedSolution, cancellationToken)
                .ConfigureAwait(false);
            var previewedStagedFingerprint = await _handles
                .LoadPreviewedStagedSolutionFingerprintAsync(handle.Id, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    stagedFingerprint.Sha256,
                    previewedStagedFingerprint,
                    StringComparison.Ordinal))
            {
                return AliRoslynActionVerification.Failed(
                    handle.Id,
                    handle.State.ToString(),
                    "staged-fingerprint-mismatch",
                    "Verification refused publication because the reconstructed solution no longer matches the authenticated previewed staged fingerprint.");
            }
            var stagedDiagnostics = await _diagnostics.CaptureAsync(stagedSolution, cancellationToken)
                .ConfigureAwait(false);
            var diagnosticComparison = _diagnostics.Compare(baselineDiagnostics, stagedDiagnostics);
            if (!diagnosticComparison.NoRegressions)
            {
                return AliRoslynActionVerification.Failed(
                    handle.Id,
                    handle.State.ToString(),
                    "diagnostic-regression",
                    $"Staged verification stopped because the exact action introduced {diagnosticComparison.Added.Count} diagnostic regression(s).");
            }

            var canonicalInputs = AliRoslynStagedInputBinding.Capture(
                sourceChangeSet.CanonicalSourceRoot,
                cancellationToken);
            stagedDirectory = AliRoslynStagedDirectory.Create();
            var materializationReceipt = await _sourceChangeSets.MaterializeStagedTreeAsync(
                    sourceChangeSet,
                    stagedDirectory.Path,
                    cancellationToken)
                .ConfigureAwait(false);
            var stagedInputs = AliRoslynStagedInputBinding.Capture(
                stagedDirectory.Path,
                cancellationToken);
            var affectedProjects = ResolveAffectedProjects(
                preview.Solution,
                canonical.Target,
                handle.DocumentChanges);
            var targetRelativePath = RelativePath(
                canonical.Target.RootDirectory,
                canonical.Target.PhysicalPath);
            var buildReceipt = await _buildVerifier.VerifyAsync(
                    stagedDirectory.Path,
                    targetRelativePath,
                    affectedProjects,
                    "Release",
                    dotNetHost,
                    stagedInputs,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!buildReceipt.Success)
            {
                return AliRoslynActionVerification.Failed(
                    handle.Id,
                    handle.State.ToString(),
                    buildReceipt.OutcomeCode,
                    buildReceipt.Summary,
                    roslynSucceeded: true,
                    buildSucceeded: false,
                    buildReceipt.TotalTests,
                    testsSucceeded: false);
            }
            stagedInputs.RequireStable(stagedDirectory.Path, cancellationToken);
            canonicalInputs.RequireStable(sourceChangeSet.CanonicalSourceRoot, cancellationToken);

            var receiptCreated = _timeProvider.GetUtcNow();
            var receiptExpires = Min(
                handle.ExpiresAtUtc,
                receiptCreated + VerificationLifetime);
            var receiptId = Guid.NewGuid().ToString("N");
            var inputBinding = AliRoslynVerifiedInputBinding.Create(
                receiptId,
                sourceChangeSet,
                materializationReceipt,
                canonicalInputs.Manifest,
                stagedInputs.Manifest);
            await AliRoslynVerifiedInputBindingStore.SaveAsync(
                    _sourceChangeSets,
                    inputBinding,
                    cancellationToken)
                .ConfigureAwait(false);
            var verificationDigest = ComputeStableVerificationDigest(
                handle,
                stagedFingerprint,
                baselineDiagnostics,
                stagedDiagnostics,
                materializationReceipt,
                inputBinding,
                buildReceipt);
            var receipt = new AliRoslynPreverificationReceipt(
                receiptId,
                sourceChangeSet.Id,
                sourceChangeSet.ManifestDigest,
                preview.CanonicalFingerprint.Sha256,
                stagedFingerprint.Sha256,
                baselineDiagnostics.Sha256,
                stagedDiagnostics.Sha256,
                materializationReceipt.ReceiptId,
                inputBinding.BindingDigest,
                inputBinding.CanonicalPreimage.PolicyDigest,
                inputBinding.CanonicalPreimage.ManifestSha256,
                inputBinding.StagedPostimage.ManifestSha256,
                RoslynSucceeded: true,
                BuildSucceeded: true,
                buildReceipt.TotalTests,
                TestsSucceeded: true,
                verificationDigest,
                receiptCreated,
                receiptExpires);
            stagedInputs.RequireStable(stagedDirectory.Path, cancellationToken);
            canonicalInputs.RequireStable(sourceChangeSet.CanonicalSourceRoot, cancellationToken);
            var verified = await _handles.RecordVerificationAsync(
                    handle.Id,
                    handle.Revision,
                    receipt,
                    cancellationToken)
                .ConfigureAwait(false);
            return new(
                true,
                verified.Id,
                verified.State.ToString(),
                receipt.Id,
                receipt.VerificationDigest,
                true,
                true,
                receipt.TestsRun,
                true,
                buildReceipt.OutcomeCode,
                "The authenticated staged source passed exact Roslyn diagnostics, affected builds, and dependency-selected tests; canonical source was not changed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return AliRoslynActionVerification.Failed(
                handle.Id,
                handle.State.ToString(),
                FailureCode(exception),
                "Roslyn staged verification failed closed before publication could be authorized.");
        }
        finally
        {
            stagedDirectory?.Dispose();
        }
    }

    internal async Task<AliRoslynActionApplication> ApplyAsync(
        string handleId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handleId);
        var grant = ConsumeGrant(AliCapabilityCatalog.RoslynApplyActionName);
        try
        {
            var handle = await _handles.LoadAsync(handleId, cancellationToken).ConfigureAwait(false);
            RequireGrantBinding(grant, handle.SourceRoot, handle.ChangeSetId);
            return new(
                false,
                handle.Id,
                handle.State.ToString(),
                false,
                "broker-publication-required",
                "Canonical publication is available only through the execution broker's exact one-use grant.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return new(
                false,
                handleId,
                "unavailable",
                false,
                FailureCode(exception),
                "The protected Roslyn action handle could not be prepared for broker publication.");
        }
    }

    private static AliExecutionGrant ConsumeGrant(string toolName)
    {
        if (!AliExecutionGrantContext.TryConsumeCurrent(
                toolName,
                AliRoslynActionExecutionAdapter.CapabilityIdFor(toolName),
                AliRoslynActionExecutionAdapter.ReconcilerIdFor(toolName),
                out var grant)
            || grant is null)
        {
            throw new InvalidOperationException(
                "The Roslyn Action Deck requires an exact one-use durable execution grant.");
        }

        return grant;
    }

    private async Task<AliRoslynWorkspaceSession> AuthorizeAndLoadTargetAsync(
        string toolName,
        AliExecutionGrant grant,
        string targetPath,
        string? documentPath,
        CancellationToken cancellationToken)
    {
        var authorizedTarget = _targetStates.ResolveTarget(targetPath, documentPath);
        RequireGrantBinding(
            grant,
            authorizedTarget.Target.RootDirectory,
            expectedPreparationIdentity: null);
        _targetStates.RequireStaticGrantVersion(toolName, grant, authorizedTarget);

        AliRoslynWorkspaceSession? session = null;
        try
        {
            session = await _workspaceLoader.LoadAsync(
                    authorizedTarget.Target,
                    cancellationToken)
                .ConfigureAwait(false);
            await _targetStates.BindLoadedAsync(
                    session,
                    authorizedTarget,
                    cancellationToken)
                .ConfigureAwait(false);
            _targetStates.RequireStaticGrantVersion(toolName, grant, authorizedTarget);
            return session;
        }
        catch
        {
            session?.Dispose();
            throw;
        }
    }

    private static void RequireGrantBinding(
        AliExecutionGrant grant,
        string sourceRoot,
        string? expectedPreparationIdentity)
    {
        if (!string.Equals(
                grant.RootBinding,
                AliRoslynActionExecutionAdapter.RootBinding(sourceRoot),
                StringComparison.Ordinal)
            || expectedPreparationIdentity is not null
                && !string.Equals(
                    grant.PreparationIdentity,
                    expectedPreparationIdentity,
                    StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Roslyn Action Deck grant does not match the exact prepared artifact and source root.");
        }
    }

    internal async Task<Solution> ReconstructStagedSolutionAsync(
        Solution canonicalClone,
        AliResolvedCodingTarget target,
        AliSourceChangeSet changeSet,
        IReadOnlyList<AliRoslynDocumentChange> documentChanges,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documentChanges);
        if (documentChanges.Count == 0
            || documentChanges.Count > AliSourceChangeSetStore.MaximumOperations)
        {
            throw new InvalidDataException("The protected Roslyn document delta is empty or unbounded.");
        }
        var operations = changeSet.Operations.ToDictionary(operation => operation.Sequence);
        var claimed = documentChanges.SelectMany(change => change.SourceOperationSequences).ToHashSet();
        if (claimed.Count == 0
            || changeSet.Operations.Any(operation => !claimed.Contains(operation.Sequence)))
        {
            throw new InvalidDataException(
                "The protected Roslyn document delta does not cover the durable source manifest.");
        }

        var solution = canonicalClone;
        foreach (var change in documentChanges
                     .OrderBy(item => item.SourceOperationSequences.Min())
                     .ThenBy(item => item.ProjectRelativePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.DocumentKind))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireBoundOperations(change, operations);
            var project = FindExactProject(solution, target, change.ProjectRelativePath);
            TextDocument? canonicalDocument = null;
            if (change.Kind != AliRoslynDocumentChangeKind.Add)
            {
                canonicalDocument = FindExactDocument(project, target, change);
            }

            SourceText? stagedText = null;
            if (change.Kind is AliRoslynDocumentChangeKind.Add
                or AliRoslynDocumentChangeKind.Replace
                or AliRoslynDocumentChangeKind.RenameAndReplace)
            {
                var postimage = change.SourceOperationSequences
                    .Select(sequence => operations[sequence])
                    .Single(operation => operation.Kind is AliSourceChangeKind.Add or AliSourceChangeKind.Replace);
                stagedText = await ReadSourceTextAsync(
                        changeSet,
                        postimage,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (change.Kind == AliRoslynDocumentChangeKind.Rename)
            {
                stagedText = await canonicalDocument!.GetTextAsync(cancellationToken).ConfigureAwait(false);
            }

            if (canonicalDocument is not null)
            {
                solution = RemoveDocument(solution, canonicalDocument.Id, change.DocumentKind);
            }
            if (change.Kind == AliRoslynDocumentChangeKind.Delete)
            {
                continue;
            }

            project = FindExactProject(solution, target, change.ProjectRelativePath);
            var destinationPath = ResolveContainedDocumentPath(
                target.RootDirectory,
                change.DestinationRelativePath
                ?? throw new InvalidDataException("A staged Roslyn document has no destination path."));
            var documentId = canonicalDocument?.Id
                ?? DocumentId.CreateNewId(
                    project.Id,
                    change.StagedName
                    ?? throw new InvalidDataException("An added Roslyn document has no name."));
            solution = AddDocument(
                solution,
                documentId,
                change,
                destinationPath,
                stagedText
                ?? throw new InvalidDataException("A staged Roslyn document has no authenticated text."));
        }

        return solution;
    }

    private static IReadOnlyList<AliRoslynAffectedProject> ResolveAffectedProjects(
        Solution solution,
        AliResolvedCodingTarget target,
        IReadOnlyList<AliRoslynDocumentChange> documentChanges)
    {
        ArgumentNullException.ThrowIfNull(documentChanges);
        var affectedPaths = documentChanges
            .Select(change => change.ProjectRelativePath)
            .Distinct(PathComparer)
            .ToHashSet(PathComparer);
        var affected = solution.Projects
            .Where(project => project.FilePath is not null
                && affectedPaths.Contains(RelativePath(target.RootDirectory, project.FilePath)))
            .Select(project => new AliRoslynAffectedProject(
                project.Id.Id.ToString("N"),
                RelativePath(
                    target.RootDirectory,
                    project.FilePath
                        ?? throw new InvalidOperationException("An affected Roslyn project has no physical path."))))
            .OrderBy(project => project.CanonicalRelativeProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (affected.Length == 0)
        {
            throw new InvalidOperationException(
                "The authenticated source manifest does not affect an exact Roslyn project.");
        }
        return affected;
    }

    private async Task<SourceText> ReadSourceTextAsync(
        AliSourceChangeSet changeSet,
        AliSourceChangeOperation operation,
        CancellationToken cancellationToken)
    {
        var bytes = await _sourceChangeSets.ReadPostImageAsync(
                changeSet,
                operation.Sequence,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var metadata = operation.Encoding ?? AliSourceTextEncoding.Detect(bytes);
            var content = AliSourceTextEncoding.Decode(bytes, metadata);
            return SourceText.From(content, AliSourceTextEncoding.Resolve(metadata.WebName));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void RequireBoundOperations(
        AliRoslynDocumentChange change,
        IReadOnlyDictionary<int, AliSourceChangeOperation> operations)
    {
        if (change.SourceOperationSequences is null
            || change.SourceOperationSequences.Length is < 1 or > 2
            || change.SourceOperationSequences.Distinct().Count() != change.SourceOperationSequences.Length
            || change.SourceOperationSequences.Any(sequence => !operations.ContainsKey(sequence)))
        {
            throw new InvalidDataException(
                "A protected Roslyn document delta has invalid source operation bindings.");
        }
        var bound = change.SourceOperationSequences.Select(sequence => operations[sequence]).ToArray();
        var valid = change.Kind switch
        {
            AliRoslynDocumentChangeKind.Add => bound.Length == 1
                && bound[0].Kind == AliSourceChangeKind.Add
                && PathComparer.Equals(bound[0].SourceRelativePath, change.DestinationRelativePath),
            AliRoslynDocumentChangeKind.Replace => bound.Length == 1
                && bound[0].Kind == AliSourceChangeKind.Replace
                && PathComparer.Equals(bound[0].SourceRelativePath, change.SourceRelativePath)
                && PathComparer.Equals(change.SourceRelativePath, change.DestinationRelativePath),
            AliRoslynDocumentChangeKind.Delete => bound.Length == 1
                && bound[0].Kind == AliSourceChangeKind.Delete
                && PathComparer.Equals(bound[0].SourceRelativePath, change.SourceRelativePath),
            AliRoslynDocumentChangeKind.Rename => bound.Length == 1
                && bound[0].Kind == AliSourceChangeKind.Rename
                && PathComparer.Equals(bound[0].SourceRelativePath, change.SourceRelativePath)
                && PathComparer.Equals(bound[0].DestinationRelativePath, change.DestinationRelativePath),
            AliRoslynDocumentChangeKind.RenameAndReplace => bound.Length == 2
                && bound.Any(operation => operation.Kind == AliSourceChangeKind.Delete
                    && PathComparer.Equals(operation.SourceRelativePath, change.SourceRelativePath))
                && bound.Any(operation => operation.Kind == AliSourceChangeKind.Add
                    && PathComparer.Equals(operation.SourceRelativePath, change.DestinationRelativePath)),
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException(
                "A protected Roslyn document delta does not match its source operations.");
        }
    }

    private static Project FindExactProject(
        Solution solution,
        AliResolvedCodingTarget target,
        string projectRelativePath)
    {
        var matches = solution.Projects.Where(project =>
                project.FilePath is not null
                && PathComparer.Equals(
                    RelativePath(target.RootDirectory, project.FilePath),
                    projectRelativePath))
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                "A protected Roslyn document delta does not identify one exact project.");
    }

    private static TextDocument FindExactDocument(
        Project project,
        AliResolvedCodingTarget target,
        AliRoslynDocumentChange change)
    {
        var matches = Documents(project, change.DocumentKind)
            .Where(document =>
                document.FilePath is not null
                && PathComparer.Equals(
                    RelativePath(target.RootDirectory, document.FilePath),
                    change.SourceRelativePath)
                && string.Equals(document.Name, change.CanonicalName, StringComparison.Ordinal)
                && document.Folders.SequenceEqual(change.CanonicalFolders, StringComparer.Ordinal))
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                "A protected Roslyn document delta does not identify one exact canonical document.");
    }

    private static IEnumerable<TextDocument> Documents(Project project, AliRoslynDocumentKind kind) =>
        kind switch
        {
            AliRoslynDocumentKind.Regular => project.Documents,
            AliRoslynDocumentKind.Additional => project.AdditionalDocuments,
            AliRoslynDocumentKind.AnalyzerConfig => project.AnalyzerConfigDocuments,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static Solution RemoveDocument(
        Solution solution,
        DocumentId documentId,
        AliRoslynDocumentKind kind) => kind switch
    {
        AliRoslynDocumentKind.Regular => solution.RemoveDocument(documentId),
        AliRoslynDocumentKind.Additional => solution.RemoveAdditionalDocument(documentId),
        AliRoslynDocumentKind.AnalyzerConfig => solution.RemoveAnalyzerConfigDocument(documentId),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static Solution AddDocument(
        Solution solution,
        DocumentId documentId,
        AliRoslynDocumentChange change,
        string filePath,
        SourceText text)
    {
        var name = change.StagedName
            ?? throw new InvalidDataException("A staged Roslyn document has no name.");
        return change.DocumentKind switch
        {
            AliRoslynDocumentKind.Regular => solution.AddDocument(DocumentInfo.Create(
                documentId,
                name,
                change.StagedFolders,
                change.SourceCodeKind,
                TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), filePath)),
                filePath,
                isGenerated: false)),
            AliRoslynDocumentKind.Additional => solution.AddAdditionalDocument(
                documentId,
                name,
                text,
                change.StagedFolders,
                filePath),
            AliRoslynDocumentKind.AnalyzerConfig => solution.AddAnalyzerConfigDocument(
                documentId,
                name,
                text,
                change.StagedFolders,
                filePath),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
    }

    private static string ResolveContainedDocumentPath(string root, string relativePath)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(
            canonicalRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidDataException("A staged Roslyn document path escaped the approved source root.");
        }
        return fullPath;
    }

    private static void RequireHandleManifest(
        AliRoslynActionHandle handle,
        AliSourceChangeSet changeSet)
    {
        if (!string.Equals(handle.ChangeSetId, changeSet.Id, StringComparison.Ordinal)
            || !string.Equals(
                handle.ChangeSetManifestDigest,
                changeSet.ManifestDigest,
                StringComparison.Ordinal)
            || !PathComparer.Equals(
                Path.GetFullPath(handle.SourceRoot),
                Path.GetFullPath(changeSet.CanonicalSourceRoot)))
        {
            throw new InvalidDataException(
                "The authenticated source manifest does not match the protected action handle.");
        }
    }

    private static void RequireHandleTarget(
        AliRoslynActionHandle handle,
        AliResolvedCodingTarget target)
    {
        if (!PathComparer.Equals(Path.GetFullPath(handle.TargetPath), target.PhysicalPath)
            || !PathComparer.Equals(Path.GetFullPath(handle.SourceRoot), target.RootDirectory))
        {
            throw new InvalidDataException(
                "The resolved canonical target does not match the protected action handle.");
        }
    }

    internal static string ComputeStableVerificationDigest(
        AliRoslynActionHandle handle,
        AliRoslynSolutionFingerprintSnapshot stagedFingerprint,
        AliRoslynDiagnosticSet baselineDiagnostics,
        AliRoslynDiagnosticSet stagedDiagnostics,
        AliSourceTreeMaterializationReceipt materialization,
        AliRoslynVerifiedInputBinding inputBinding,
        AliRoslynStagedBuildVerificationReceipt build)
    {
        var canonical = string.Join(
            "\n",
            "ali-roslyn-preverification-v3",
            handle.ActionIdentitySha256,
            handle.ChangeSetManifestDigest,
            handle.CanonicalSolutionFingerprint,
            stagedFingerprint.Sha256,
            baselineDiagnostics.Sha256,
            stagedDiagnostics.Sha256,
            materialization.ReceiptId,
            materialization.ManifestDigest,
            materialization.PolicyDigest,
            materialization.CopiedEntries.ToString(System.Globalization.CultureInfo.InvariantCulture),
            materialization.CopiedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            inputBinding.BindingDigest,
            inputBinding.CanonicalPreimage.PolicyDigest,
            inputBinding.CanonicalPreimage.ManifestSha256,
            inputBinding.CanonicalPreimage.FileCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            inputBinding.CanonicalPreimage.TotalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            inputBinding.StagedPostimage.ManifestSha256,
            inputBinding.StagedPostimage.FileCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            inputBinding.StagedPostimage.TotalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            build.Success.ToString(System.Globalization.CultureInfo.InvariantCulture),
            build.TargetRelativePath,
            build.TargetSha256,
            build.Configuration,
            build.Toolset.Name,
            build.Toolset.Version,
            build.Toolset.LocationSha256,
            build.EvaluatedProjectCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            build.AffectedProjectCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            build.PlannedBuildTargetCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            build.CompletedBuildTargetCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            build.SelectedTestProjectCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            build.CompletedTestProjectCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            build.OutcomeCode,
            build.TotalTests.ToString(System.Globalization.CultureInfo.InvariantCulture),
            build.PassedTests.ToString(System.Globalization.CultureInfo.InvariantCulture),
            build.FailedTests.ToString(System.Globalization.CultureInfo.InvariantCulture),
            build.SkippedTests.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(
                "|",
                build.Steps.Select(step => string.Join(
                    "\u001f",
                    step.Operation.ToString(),
                    step.TargetRelativePath,
                    step.TargetSha256,
                    step.Success.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    step.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    step.TimedOut.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    step.TotalTests.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    step.PassedTests.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    step.FailedTests.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    step.SkippedTests.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private static string RelativePath(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Roslyn returned a document without a physical path.");
        }
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(
                canonicalRoot + Path.DirectorySeparatorChar,
                PathComparison))
        {
            throw new InvalidOperationException("Roslyn returned a path outside the approved source root.");
        }
        return Path.GetRelativePath(canonicalRoot, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string SafeFileName(string value)
    {
        try
        {
            return Path.GetFileName(value);
        }
        catch
        {
            return "<invalid>";
        }
    }

    private static string FailureCode(Exception exception) => exception switch
    {
        FileNotFoundException => "target-not-found",
        DirectoryNotFoundException => "target-not-found",
        ArgumentException => "invalid-request",
        InvalidDataException => "artifact-integrity-failed",
        UnauthorizedAccessException => "access-denied",
        IOException => "io-failure",
        _ => "verification-failed-closed"
    };

    private static void ValidateDigest(string value, string parameterName)
    {
        if (value?.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("An exact Roslyn action identity must be a SHA-256 digest.", parameterName);
        }
    }

    private static bool IsRecoverableFailure(Exception exception) =>
        exception is not OperationCanceledException
            and not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
