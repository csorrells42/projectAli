using System.Text;
using System.Text.Json;

namespace Ali.Modules.Coding.Changesets;

internal enum AliSourcePublicationState
{
    Prepared,
    Committed,
    RolledBack,
    InDoubt
}

internal sealed record AliSourcePublicationReceipt(
    string ChangeSetId,
    AliSourcePublicationState State,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> PublishedFiles,
    string Summary);

internal sealed class AliSourceChangeSetPublisher(
    AliSourceChangeSetValidator validator)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<AliSourcePublicationReceipt> PublishAsync(
        AliSourceChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        var validation = await validator.ValidateAsync(changeSet, cancellationToken).ConfigureAwait(false);
        if (!validation.Valid)
        {
            return new AliSourcePublicationReceipt(
                changeSet.Id,
                AliSourcePublicationState.RolledBack,
                DateTimeOffset.UtcNow,
                [],
                "The staged source change was not published: " + string.Join(" | ", validation.Errors));
        }

        var workingDirectory = Path.Combine(Path.GetTempPath(), "ProjectAli", "SourceChanges", changeSet.Id);
        Directory.CreateDirectory(workingDirectory);
        var prepared = new List<PreparedFile>(changeSet.Files.Count);
        foreach (var change in changeSet.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encoding = Encoding.GetEncoding(change.EncodingName);
            var stagedPath = Path.Combine(workingDirectory, prepared.Count.ToString("D4") + ".new");
            var backupPath = Path.Combine(workingDirectory, prepared.Count.ToString("D4") + ".bak");
            await File.WriteAllTextAsync(stagedPath, change.NewContent, encoding, cancellationToken).ConfigureAwait(false);
            File.Copy(change.FilePath, backupPath, overwrite: true);
            prepared.Add(new PreparedFile(change.FilePath, stagedPath, backupPath, change.NewSha256));
        }

        var receipt = new AliSourcePublicationReceipt(
            changeSet.Id,
            AliSourcePublicationState.Prepared,
            DateTimeOffset.UtcNow,
            [],
            "The source transaction is prepared and has not modified canonical files.");
        await WriteReceiptAsync(workingDirectory, receipt, cancellationToken).ConfigureAwait(false);

        var published = new List<PreparedFile>();
        try
        {
            foreach (var file in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(file.StagedPath, file.TargetPath, overwrite: true);
                published.Add(file);
            }

            foreach (var file in published)
            {
                var bytes = await File.ReadAllBytesAsync(file.TargetPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(AliSourceChangeSetStore.Hash(bytes), file.NewSha256, StringComparison.Ordinal))
                {
                    throw new IOException($"Published source hash verification failed for {file.TargetPath}.");
                }
            }

            receipt = new AliSourcePublicationReceipt(
                changeSet.Id,
                AliSourcePublicationState.Committed,
                DateTimeOffset.UtcNow,
                published.Select(file => file.TargetPath).ToArray(),
                $"Published and hash-verified {published.Count} source file(s) as one changeset.");
            await WriteReceiptAsync(workingDirectory, receipt, cancellationToken).ConfigureAwait(false);
            return receipt;
        }
        catch
        {
            var rollbackFailures = new List<string>();
            foreach (var file in published.AsEnumerable().Reverse())
            {
                try
                {
                    File.Copy(file.BackupPath, file.TargetPath, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    rollbackFailures.Add($"{file.TargetPath}: {ex.Message}");
                }
            }

            receipt = new AliSourcePublicationReceipt(
                changeSet.Id,
                rollbackFailures.Count == 0 ? AliSourcePublicationState.RolledBack : AliSourcePublicationState.InDoubt,
                DateTimeOffset.UtcNow,
                published.Select(file => file.TargetPath).ToArray(),
                rollbackFailures.Count == 0
                    ? "Publication failed and every changed source file was restored from the prepared transaction."
                    : "Publication failed and rollback could not be proven complete: " + string.Join(" | ", rollbackFailures));
            await WriteReceiptAsync(workingDirectory, receipt, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<AliSourcePublicationReceipt?> ReadReceiptAsync(
        string changeSetId,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), "ProjectAli", "SourceChanges", changeSetId, "receipt.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<AliSourcePublicationReceipt>(json, JsonOptions);
    }

    private static async Task WriteReceiptAsync(
        string directory,
        AliSourcePublicationReceipt receipt,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "receipt.json");
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(
            temp,
            JsonSerializer.Serialize(receipt, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private sealed record PreparedFile(
        string TargetPath,
        string StagedPath,
        string BackupPath,
        string NewSha256);
}
