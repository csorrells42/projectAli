using Ali.Modules.Coding.Changesets;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;

namespace Ali.Modules.Coding.RoslynActions;

/// <summary>
/// The sole Action Deck component that owns the canonical source publisher. It accepts only a
/// preverified protected handle and an exact one-use broker grant; it never recomputes an action.
/// </summary>
internal sealed class AliRoslynActionPublisher(
    AliRoslynActionHandleStore handles,
    AliSourceChangeSetStore changeSets,
    AliSourceChangeSetPublisher publisher,
    AliRoslynWorkspaceLoader workspaceLoader,
    AliRoslynSolutionFingerprint fingerprint)
{
    internal async Task<AliRoslynActionApplication> ApplyAsync(
        string handleId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handleId);
        var grant = ConsumeGrant();
        AliRoslynActionHandle? handle = null;
        AliRoslynActionHandle? applying = null;
        var publicationAttempted = false;
        var sourceCommitted = false;
        try
        {
            handle = await handles.LoadAsync(handleId, cancellationToken).ConfigureAwait(false);
            RequireGrantBinding(grant, handle);
            if (handle.State != AliRoslynActionHandleState.Verified
                || handle.Verification?.Success != true)
            {
                return Failed(handle, "handle-not-verified", "The protected action handle is not in its exact verified state.");
            }
            var verification = handle.Verification!;

            var changeSet = await changeSets.LoadAsync(handle.ChangeSetId, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(changeSet.ManifestDigest, handle.ChangeSetManifestDigest, StringComparison.Ordinal)
                || !Path.GetFullPath(changeSet.CanonicalSourceRoot).Equals(
                    Path.GetFullPath(handle.SourceRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                await TryMarkFailedAsync(handle, "manifest-binding-mismatch", cancellationToken)
                    .ConfigureAwait(false);
                return Failed(handle, "manifest-binding-mismatch", "The protected handle no longer matches its authenticated source manifest.");
            }

            AliRoslynVerifiedInputBinding inputBinding;
            try
            {
                inputBinding = await AliRoslynVerifiedInputBindingStore.LoadAsync(
                        changeSets,
                        verification,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && IsRecoverableFailure(exception))
            {
                await TryMarkFailedAsync(handle, "verification-input-binding-invalid", cancellationToken)
                    .ConfigureAwait(false);
                return Failed(
                    handle,
                    "verification-input-binding-invalid",
                    "The authenticated exact-file evidence for staged verification is missing or invalid; nothing was published.");
            }

            using (var canonical = await workspaceLoader.LoadAsync(handle.TargetPath, cancellationToken)
                       .ConfigureAwait(false))
            {
                if (canonical.Warnings.Count != 0)
                {
                    await TryMarkFailedAsync(handle, "canonical-workspace-warning", cancellationToken)
                        .ConfigureAwait(false);
                    return Failed(handle, "canonical-workspace-warning", "Canonical Roslyn loading produced workspace warnings; publication was refused.");
                }

                var current = await fingerprint.CaptureAsync(canonical.Solution, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(
                        current.Sha256,
                        handle.CanonicalSolutionFingerprint,
                        StringComparison.Ordinal))
                {
                    await TryMarkFailedAsync(handle, "stale-canonical-fingerprint", cancellationToken)
                        .ConfigureAwait(false);
                    return Failed(handle, "stale-canonical-fingerprint", "Canonical source or semantic inputs changed after preview; nothing was published.");
                }
            }

            if (!CanonicalInputsMatch(handle.SourceRoot, inputBinding, cancellationToken))
            {
                await TryMarkFailedAsync(handle, "stale-canonical-input-manifest", cancellationToken)
                    .ConfigureAwait(false);
                return Failed(
                    handle,
                    "stale-canonical-input-manifest",
                    "A canonical build or test input changed after exact staged verification; nothing was published.");
            }

            applying = await handles.BeginApplyAsync(handle.Id, handle.Revision, cancellationToken)
                .ConfigureAwait(false);
            if (applying.State == AliRoslynActionHandleState.Expired)
            {
                return Failed(applying, "verification-expired", "The staged verification expired before publication began.");
            }
            if (applying.State != AliRoslynActionHandleState.Applying)
            {
                return Failed(applying, "apply-transition-refused", "The protected handle could not enter its one-use publication state.");
            }

            if (!CanonicalInputsMatch(handle.SourceRoot, inputBinding, cancellationToken))
            {
                await TryMarkFailedAsync(applying, "stale-canonical-input-manifest", cancellationToken)
                    .ConfigureAwait(false);
                return Failed(
                    applying,
                    "stale-canonical-input-manifest",
                    "A canonical build or test input changed immediately before publication; nothing was published.");
            }

            var sourceGrant = AliSourcePublicationGrant.Issue(
                changeSet,
                AliRoslynActionExecutionAdapter.AuthorizationBindingDigest(grant));
            publicationAttempted = true;
            var receipt = await publisher.PublishAsync(changeSet, sourceGrant, cancellationToken)
                .ConfigureAwait(false);
            if (receipt.State == AliSourcePublicationState.RolledBack)
            {
                await TryMarkFailedAsync(applying, "publication-rolled-back", CancellationToken.None)
                    .ConfigureAwait(false);
                return Failed(applying, "publication-rolled-back", receipt.Summary);
            }
            if (receipt.State != AliSourcePublicationState.Committed)
            {
                return Failed(
                    applying,
                    "publication-in-doubt",
                    "The source transaction could not prove commit or rollback; recovery must reconcile it before another action.");
            }
            sourceCommitted = true;

            using (var canonical = await workspaceLoader.LoadAsync(handle.TargetPath, cancellationToken)
                       .ConfigureAwait(false))
            {
                var postFingerprint = await fingerprint.CaptureAsync(
                        canonical.Solution,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (canonical.Warnings.Count != 0
                    || !string.Equals(
                        postFingerprint.Sha256,
                        handle.Verification.StagedSolutionFingerprint,
                        StringComparison.Ordinal))
                {
                    await TryMarkFailedAsync(applying, "canonical-postverify-failed", CancellationToken.None)
                        .ConfigureAwait(false);
                    return Failed(
                        applying,
                        "canonical-postverify-failed",
                        "The transaction committed exact postimages, but canonical Roslyn post-verification diverged; the source remains durably recorded for recovery and review.",
                        applied: true);
                }
            }

            var applied = await handles.CommitAppliedAsync(
                    applying.Id,
                    applying.Revision,
                    receipt.GrantId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new(
                true,
                applied.Id,
                applied.State.ToString(),
                true,
                "publication-committed-and-postverified",
                "The exact preverified changeset was published and canonical Roslyn state matches the staged semantic fingerprint.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!publicationAttempted && applying is not null)
            {
                await TryMarkFailedAsync(applying, "cancelled-before-publication", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            throw;
        }
        catch (AliSourcePublicationException exception)
        {
            if (exception.Receipt.State == AliSourcePublicationState.RolledBack
                && applying is not null)
            {
                await TryMarkFailedAsync(applying, "publication-rolled-back", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            return Failed(
                applying ?? handle,
                exception.Receipt.State == AliSourcePublicationState.InDoubt
                    ? "publication-in-doubt"
                    : "publication-failed",
                exception.Receipt.Summary,
                applied: exception.Receipt.State == AliSourcePublicationState.Committed);
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            if (!publicationAttempted && applying is not null)
            {
                await TryMarkFailedAsync(applying, "publication-not-started", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            return Failed(
                applying ?? handle,
                "apply-" + StableExceptionCode(exception),
                sourceCommitted
                    ? "The exact source transaction committed, but final Roslyn post-verification or handle sealing did not complete; recovery will not publish it twice."
                    : "The exact action could not complete publication; no success was recorded.",
                applied: sourceCommitted);
        }
    }

    private static AliExecutionGrant ConsumeGrant()
    {
        var toolName = AliCapabilityCatalog.RoslynApplyActionName;
        if (!AliExecutionGrantContext.TryConsumeCurrent(
                toolName,
                AliRoslynActionExecutionAdapter.CapabilityIdFor(toolName),
                AliRoslynActionExecutionAdapter.ReconcilerIdFor(toolName),
                out var grant)
            || grant is null)
        {
            throw new InvalidOperationException(
                "Roslyn publication requires an exact one-use durable execution grant.");
        }

        return grant;
    }

    private static bool CanonicalInputsMatch(
        string sourceRoot,
        AliRoslynVerifiedInputBinding inputBinding,
        CancellationToken cancellationToken) =>
        AliRoslynStagedInputBinding.Capture(sourceRoot, cancellationToken)
            .Matches(inputBinding.CanonicalPreimage);

    private static void RequireGrantBinding(
        AliExecutionGrant grant,
        AliRoslynActionHandle handle)
    {
        if (!string.Equals(grant.PreparationIdentity, handle.ChangeSetId, StringComparison.Ordinal)
            || !string.Equals(
                grant.RootBinding,
                AliRoslynActionExecutionAdapter.RootBinding(handle.SourceRoot),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The publication grant does not match the exact manifest and source root.");
        }
    }

    private async Task TryMarkFailedAsync(
        AliRoslynActionHandle handle,
        string failureCode,
        CancellationToken cancellationToken)
    {
        if (handle.State is not (AliRoslynActionHandleState.Verified
            or AliRoslynActionHandleState.Applying))
        {
            return;
        }

        try
        {
            await handles.MarkFailedAsync(
                    handle.Id,
                    handle.Revision,
                    failureCode,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // A concurrent consume/recovery transition owns the current state. Never overwrite it.
        }
    }

    private static AliRoslynActionApplication Failed(
        AliRoslynActionHandle? handle,
        string outcomeCode,
        string summary,
        bool applied = false) =>
        new(
            false,
            handle?.Id ?? "unavailable",
            handle?.State.ToString() ?? "Unavailable",
            applied,
            outcomeCode,
            summary);

    private static bool IsRecoverableFailure(Exception exception) =>
        exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    private static string StableExceptionCode(Exception exception)
    {
        var filtered = new string(exception.GetType().Name
            .Where(char.IsAsciiLetterOrDigit)
            .ToArray())
            .ToLowerInvariant();
        return string.IsNullOrWhiteSpace(filtered)
            ? "failed"
            : filtered[..Math.Min(filtered.Length, 96)];
    }
}
