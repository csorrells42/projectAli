namespace Ali.Modules.Coding.Changesets;

internal sealed record AliSourceReconciliationResult(
    string ChangeSetId,
    AliSourcePublicationState State,
    bool SafeToContinue,
    string Summary);

internal sealed class AliSourceChangeSetReconciler(
    AliSourceChangeSetStore store,
    AliSourceChangeSetPublisher publisher)
{
    private readonly AliSourceChangeSetStore _store = store
        ?? throw new ArgumentNullException(nameof(store));
    private readonly AliSourceChangeSetPublisher _publisher = publisher
        ?? throw new ArgumentNullException(nameof(publisher));

    internal async Task<AliSourceReconciliationResult> ReconcileAsync(
        string changeSetId,
        CancellationToken cancellationToken)
    {
        var changeSet = await _store.LoadAsync(changeSetId, cancellationToken).ConfigureAwait(false);
        await using var lease = await _publisher.AcquirePublicationLeaseAsync(
            changeSet.CanonicalSourceRoot,
            cancellationToken).ConfigureAwait(false);
        var receipt = await _publisher.ReadReceiptAsync(changeSetId, cancellationToken).ConfigureAwait(false);
        var snapshot = await _publisher.ReadJournalSnapshotAsync(changeSet, cancellationToken)
            .ConfigureAwait(false);
        var head = snapshot.Head;
        if (receipt is not null
            && (receipt.JournalSequence != head.Sequence
                || !string.Equals(
                    receipt.JournalAuthenticationTag,
                    head.AuthenticationTag,
                    StringComparison.Ordinal)))
        {
            if (receipt.State != AliSourcePublicationState.Prepared)
            {
                return await PersistAsync(
                    changeSet,
                    receipt,
                    AliSourcePublicationState.InDoubt,
                    head,
                    "The terminal receipt and durable journal head disagree; user resolution is required.",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        AliSourceRecoveryBinding recovery;
        try
        {
            recovery = await _publisher.BindRecoveryUnderLeaseAsync(
                    changeSet,
                    snapshot,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsNamespaceBindingFailure(exception))
        {
            return await PersistAsync(
                changeSet,
                receipt,
                AliSourcePublicationState.InDoubt,
                head,
                "The authenticated source root or namespace identity is missing, rebound, or ambiguous; recovery refused to mutate canonical source.",
                cancellationToken).ConfigureAwait(false);
        }
        using var recoveryBinding = recovery;
        var states = recovery.States;
        var allPreimage = states.All(state => state == AliSourceOperationState.Preimage);
        var allPostimage = states.All(state => state == AliSourceOperationState.Postimage);
        var recognizedMixed = !allPreimage
            && !allPostimage
            && states.All(state => state is AliSourceOperationState.Preimage or AliSourceOperationState.Postimage);
        if ((allPreimage || allPostimage || recognizedMixed)
            && !await _publisher.ReverifyRecoveryBindingAsync(
                    changeSet,
                    recovery,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return await PersistAsync(
                changeSet,
                receipt,
                AliSourcePublicationState.InDoubt,
                head,
                "Canonical source changed during recovery verification; recovery refused to mutate or certify it.",
                cancellationToken).ConfigureAwait(false);
        }

        if (receipt is null)
        {
            if (head.Sequence == 0 && allPreimage)
            {
                // Explicit reconciliation permanently closes an authenticated but unstarted
                // manifest so a later grant cannot replay it.
                head = await _publisher.AppendTerminalAsync(
                    changeSet,
                    head,
                    AliSourcePublicationState.RolledBack).ConfigureAwait(false);
                return await PersistAsync(
                    changeSet,
                    prior: null,
                    AliSourcePublicationState.RolledBack,
                    head,
                    "Recovery closed an unstarted manifest after proving every canonical source preimage.",
                    cancellationToken).ConfigureAwait(false);
            }

            return await PersistAsync(
                changeSet,
                prior: null,
                AliSourcePublicationState.InDoubt,
                head,
                "Publication evidence is missing while canonical source is not an untouched preimage; user resolution is required.",
                cancellationToken).ConfigureAwait(false);
        }

        if (receipt.State == AliSourcePublicationState.Committed)
        {
            return allPostimage
                ? new(changeSetId, AliSourcePublicationState.Committed, true, receipt.Summary)
                : await PersistAsync(
                    changeSet,
                    receipt,
                    AliSourcePublicationState.InDoubt,
                    head,
                    "Committed source no longer matches the authenticated postimages; user resolution is required.",
                    cancellationToken).ConfigureAwait(false);
        }
        if (receipt.State == AliSourcePublicationState.RolledBack)
        {
            return allPreimage
                ? new(changeSetId, AliSourcePublicationState.RolledBack, true, receipt.Summary)
                : await PersistAsync(
                    changeSet,
                    receipt,
                    AliSourcePublicationState.InDoubt,
                    head,
                    "Rolled-back source no longer matches the authenticated preimages; user resolution is required.",
                    cancellationToken).ConfigureAwait(false);
        }
        if (receipt.State == AliSourcePublicationState.InDoubt)
        {
            if (!allPreimage && !allPostimage && !recognizedMixed)
            {
                return new(changeSetId, AliSourcePublicationState.InDoubt, false, receipt.Summary);
            }
        }

        if (allPostimage)
        {
            head = await _publisher.AppendTerminalAsync(
                changeSet,
                head,
                AliSourcePublicationState.Committed).ConfigureAwait(false);
            return await PersistAsync(
                changeSet,
                receipt,
                AliSourcePublicationState.Committed,
                head,
                "Recovery proved that every canonical source operation matches its authenticated postimage.",
                cancellationToken).ConfigureAwait(false);
        }
        if (allPreimage)
        {
            if (receipt.State is AliSourcePublicationState.Prepared or AliSourcePublicationState.InDoubt)
            {
                head = await _publisher.AppendTerminalAsync(
                    changeSet,
                    head,
                    AliSourcePublicationState.RolledBack).ConfigureAwait(false);
            }
            return await PersistAsync(
                changeSet,
                receipt,
                AliSourcePublicationState.RolledBack,
                head,
                "Recovery proved that every canonical source operation matches its authenticated preimage.",
                cancellationToken).ConfigureAwait(false);
        }
        if (recognizedMixed)
        {
            var rollback = await _publisher.RollbackUnderLeaseAsync(
                changeSet,
                recovery,
                cancellationToken).ConfigureAwait(false);
            head = rollback.Head;
            var state = rollback.Complete
                ? AliSourcePublicationState.RolledBack
                : AliSourcePublicationState.InDoubt;
            head = await _publisher.AppendTerminalAsync(changeSet, head, state).ConfigureAwait(false);
            return await PersistAsync(
                changeSet,
                receipt,
                state,
                head,
                rollback.Complete
                    ? "Recovery restored and hash-verified every canonical source preimage."
                    : "Recovery could not prove complete rollback; user resolution is required.",
                cancellationToken).ConfigureAwait(false);
        }

        return await PersistAsync(
            changeSet,
            receipt,
            AliSourcePublicationState.InDoubt,
            head,
            "Canonical source contains an unrecognized file image; recovery refused to overwrite it.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AliSourceReconciliationResult> PersistAsync(
        AliSourceChangeSet changeSet,
        AliSourcePublicationReceipt? prior,
        AliSourcePublicationState state,
        AliSourceJournalHead head,
        string summary,
        CancellationToken cancellationToken)
    {
        var receipt = _publisher.CreateRecoveredReceipt(changeSet, prior, state, head, summary);
        await _publisher.WriteReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
        await _publisher.CleanupStagedFilesAsync(changeSet).ConfigureAwait(false);
        return new(
            changeSet.Id,
            state,
            state is AliSourcePublicationState.Committed or AliSourcePublicationState.RolledBack,
            receipt.Summary);
    }

    private static bool IsNamespaceBindingFailure(Exception exception) =>
        exception is IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or NotSupportedException;
}
