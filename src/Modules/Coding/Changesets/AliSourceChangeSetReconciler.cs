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
    public async Task<AliSourceReconciliationResult> ReconcileAsync(
        string changeSetId,
        CancellationToken cancellationToken)
    {
        var receipt = await publisher.ReadReceiptAsync(changeSetId, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        {
            return new(changeSetId, AliSourcePublicationState.InDoubt, false, "No durable publication receipt exists.");
        }
        if (receipt.State is AliSourcePublicationState.Committed or AliSourcePublicationState.RolledBack)
        {
            return new(changeSetId, receipt.State, true, receipt.Summary);
        }

        var changeSet = await store.LoadAsync(changeSetId, cancellationToken).ConfigureAwait(false);
        var newMatches = 0;
        var oldMatches = 0;
        foreach (var file in changeSet.Files)
        {
            if (!File.Exists(file.FilePath))
            {
                return new(changeSetId, AliSourcePublicationState.InDoubt, false, $"The target file is missing: {file.FilePath}");
            }
            var bytes = await File.ReadAllBytesAsync(file.FilePath, cancellationToken).ConfigureAwait(false);
            var hash = AliSourceChangeSetStore.Hash(bytes);
            if (string.Equals(hash, file.NewSha256, StringComparison.Ordinal)) newMatches++;
            else if (string.Equals(hash, file.ExpectedSha256, StringComparison.Ordinal)) oldMatches++;
            else return new(changeSetId, AliSourcePublicationState.InDoubt, false, $"The target has an unrecognized version: {file.FilePath}");
        }

        if (newMatches == changeSet.Files.Count)
        {
            return new(changeSetId, AliSourcePublicationState.Committed, true, "Every target matches the staged changeset hash.");
        }
        if (oldMatches == changeSet.Files.Count)
        {
            return new(changeSetId, AliSourcePublicationState.RolledBack, true, "Every target matches the original changeset hash.");
        }
        return new(changeSetId, AliSourcePublicationState.InDoubt, false, "The transaction is partially visible and requires recovery before another source mutation.");
    }
}
