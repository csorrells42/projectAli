using Ali.Modules.Coding.Changesets;

namespace Ali.Modules.Coding.RoslynActions;

internal enum AliRoslynActionPublicationRecoveryDisposition
{
    AppliedAndPostverified,
    AppliedNeedsReview,
    Absent,
    Unknown
}

internal sealed record AliRoslynActionPublicationRecoveryResult(
    AliRoslynActionPublicationRecoveryDisposition Disposition,
    string OutcomeCode,
    string EvidenceVersion);

/// <summary>
/// Closes the crash window between the durable source transaction and the protected Roslyn
/// handle. It observes and transitions durable state only; it never invokes the source publisher.
/// </summary>
internal sealed class AliRoslynActionPublicationRecovery(
    AliRoslynActionHandleStore handles,
    AliSourceChangeSetStore changeSets,
    AliSourceChangeSetPublisher sourcePublisher,
    AliRoslynWorkspaceLoader workspaceLoader,
    AliRoslynSolutionFingerprint fingerprint)
{
    internal async Task<AliRoslynActionPublicationRecoveryResult> ReconcileAsync(
        string changeSetId,
        AliSourcePublicationState sourceState,
        string? expectedAuthorizationBindingDigest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changeSetId);
        var handle = await handles.FindByChangeSetIdAsync(changeSetId, cancellationToken)
            .ConfigureAwait(false);
        if (handle is null)
        {
            return Unknown("roslyn-publication-handle-missing", changeSetId);
        }

        var changeSet = await changeSets.LoadAsync(changeSetId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(handle.ChangeSetManifestDigest, changeSet.ManifestDigest, StringComparison.Ordinal)
            || !Path.GetFullPath(handle.SourceRoot).Equals(
                Path.GetFullPath(changeSet.CanonicalSourceRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return Unknown(
                "roslyn-publication-handle-binding-mismatch",
                changeSet.ManifestDigest);
        }

        return sourceState switch
        {
            AliSourcePublicationState.Committed =>
                string.IsNullOrWhiteSpace(expectedAuthorizationBindingDigest)
                    ? Unknown(
                        "roslyn-publication-authorization-identity-missing",
                        changeSet.ManifestDigest)
                    : await ReconcileCommittedAsync(
                            handle,
                            changeSet,
                            expectedAuthorizationBindingDigest,
                            cancellationToken)
                    .ConfigureAwait(false),
            AliSourcePublicationState.RolledBack =>
                await ReconcileRolledBackAsync(handle, changeSet, cancellationToken)
                    .ConfigureAwait(false),
            _ => Unknown("roslyn-publication-in-doubt", changeSet.ManifestDigest)
        };
    }

    private async Task<AliRoslynActionPublicationRecoveryResult> ReconcileCommittedAsync(
        AliRoslynActionHandle handle,
        AliSourceChangeSet changeSet,
        string expectedAuthorizationBindingDigest,
        CancellationToken cancellationToken)
    {
        var receipt = await sourcePublisher.ReadReceiptAsync(changeSet.Id, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null
            || receipt.State != AliSourcePublicationState.Committed
            || !string.Equals(receipt.ManifestDigest, changeSet.ManifestDigest, StringComparison.Ordinal))
        {
            return Unknown("roslyn-publication-receipt-mismatch", changeSet.ManifestDigest);
        }
        if (!string.Equals(
                receipt.AuthorizationBindingDigest,
                expectedAuthorizationBindingDigest,
                StringComparison.Ordinal))
        {
            return Unknown(
                "roslyn-publication-authorization-mismatch",
                changeSet.ManifestDigest);
        }

        if (handle.State == AliRoslynActionHandleState.Applied)
        {
            return string.Equals(
                    handle.PublicationTransactionId,
                    receipt.GrantId,
                    StringComparison.Ordinal)
                ? AppliedAndPostverified(handle)
                : Unknown("roslyn-publication-transaction-mismatch", changeSet.ManifestDigest);
        }

        if (handle.State == AliRoslynActionHandleState.Failed)
        {
            return AppliedNeedsReview(
                handle,
                "roslyn-publication-committed-postverify-unconfirmed");
        }

        if (handle.State != AliRoslynActionHandleState.Applying
            || handle.Verification?.Success != true)
        {
            return Unknown("roslyn-publication-handle-state-mismatch", changeSet.ManifestDigest);
        }

        try
        {
            using var canonical = await workspaceLoader.LoadAsync(handle.TargetPath, cancellationToken)
                .ConfigureAwait(false);
            var postFingerprint = await fingerprint.CaptureAsync(
                    canonical.Solution,
                    cancellationToken)
                .ConfigureAwait(false);
            if (canonical.Warnings.Count == 0
                && string.Equals(
                    postFingerprint.Sha256,
                    handle.Verification.StagedSolutionFingerprint,
                    StringComparison.Ordinal))
            {
                var applied = await handles.CommitAppliedAsync(
                        handle.Id,
                        handle.Revision,
                        receipt.GrantId,
                        cancellationToken)
                    .ConfigureAwait(false);
                return AppliedAndPostverified(applied);
            }

            await MarkCommittedFailureAsync(
                    handle,
                    "canonical-postverify-failed",
                    cancellationToken)
                .ConfigureAwait(false);
            return AppliedNeedsReview(
                handle,
                "roslyn-publication-committed-postverify-failed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            await MarkCommittedFailureAsync(
                    handle,
                    "canonical-postverify-" + StableExceptionCode(exception),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return AppliedNeedsReview(
                handle,
                "roslyn-publication-committed-postverify-unavailable");
        }
    }

    private async Task<AliRoslynActionPublicationRecoveryResult> ReconcileRolledBackAsync(
        AliRoslynActionHandle handle,
        AliSourceChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        if (handle.State == AliRoslynActionHandleState.Applied)
        {
            return Unknown("roslyn-publication-rollback-state-mismatch", changeSet.ManifestDigest);
        }

        if (handle.State is AliRoslynActionHandleState.Verified
            or AliRoslynActionHandleState.Applying)
        {
            try
            {
                handle = await handles.MarkFailedAsync(
                        handle.Id,
                        handle.Revision,
                        "publication-rolled-back",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                handle = await handles.LoadAsync(handle.Id, cancellationToken).ConfigureAwait(false);
                if (handle.State == AliRoslynActionHandleState.Applied)
                {
                    return Unknown(
                        "roslyn-publication-rollback-state-raced",
                        changeSet.ManifestDigest);
                }
            }
        }

        return handle.State is AliRoslynActionHandleState.Failed
            or AliRoslynActionHandleState.Expired
            ? new(
                AliRoslynActionPublicationRecoveryDisposition.Absent,
                "roslyn-publication-proved-rolled-back",
                changeSet.ManifestDigest)
            : Unknown("roslyn-publication-rollback-handle-mismatch", changeSet.ManifestDigest);
    }

    private async Task MarkCommittedFailureAsync(
        AliRoslynActionHandle handle,
        string failureCode,
        CancellationToken cancellationToken)
    {
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
            var current = await handles.LoadAsync(handle.Id, cancellationToken).ConfigureAwait(false);
            if (current.State is not (AliRoslynActionHandleState.Failed
                or AliRoslynActionHandleState.Applied))
            {
                throw;
            }
        }
    }

    private static AliRoslynActionPublicationRecoveryResult AppliedAndPostverified(
        AliRoslynActionHandle handle) =>
        new(
            AliRoslynActionPublicationRecoveryDisposition.AppliedAndPostverified,
            "roslyn-publication-committed-and-postverified",
            handle.Verification?.VerificationDigest ?? handle.ChangeSetManifestDigest);

    private static AliRoslynActionPublicationRecoveryResult AppliedNeedsReview(
        AliRoslynActionHandle handle,
        string outcomeCode) =>
        new(
            AliRoslynActionPublicationRecoveryDisposition.AppliedNeedsReview,
            outcomeCode,
            handle.ChangeSetManifestDigest);

    private static AliRoslynActionPublicationRecoveryResult Unknown(
        string outcomeCode,
        string evidenceVersion) =>
        new(
            AliRoslynActionPublicationRecoveryDisposition.Unknown,
            outcomeCode,
            evidenceVersion);

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
            : filtered[..Math.Min(filtered.Length, 72)];
    }
}
