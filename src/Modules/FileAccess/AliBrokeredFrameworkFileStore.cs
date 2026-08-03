using Microsoft.Agents.AI;

#pragma warning disable MAAI001 // Agent Framework file-store API is isolated behind this module boundary.

namespace Ali.Modules.WorkstationFiles;

/// <summary>
/// Production-only Agent Framework store facade. Reads retain the normal workstation behavior;
/// writes require and consume a broker grant and publish only through the durable transaction.
/// The same audited facade is exposed to lower-level Ali callers in production composition.
/// </summary>
internal sealed class AliBrokeredFrameworkFileStore(
    AliWorkstationFileStore inner,
    AliFrameworkFileMutationTransaction transaction,
    AliFileTreeMutationCoordinator treeMutations) : AgentFileStore
{
    private readonly AliWorkstationFileStore _inner = inner
        ?? throw new ArgumentNullException(nameof(inner));
    private readonly AliFrameworkFileMutationTransaction _transaction = transaction
        ?? throw new ArgumentNullException(nameof(transaction));
    private readonly AliFileTreeMutationCoordinator _treeMutations = treeMutations
        ?? throw new ArgumentNullException(nameof(treeMutations));

    public override Task WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default) =>
        _transaction.PublishFrameworkWriteAsync(path, content, cancellationToken);

    public override Task<string?> ReadAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(path, cancellationToken);

    public override Task<bool> DeleteAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        _treeMutations.DeleteAsync(path, cancellationToken);

    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken = default) =>
        _inner.ListChildrenAsync(directory, cancellationToken);

    public override Task<bool> FileExistsAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        _inner.FileExistsAsync(path, cancellationToken);

    public override Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        CancellationToken cancellationToken = default) =>
        _inner.SearchAsync(
            directory,
            regexPattern,
            globPattern,
            recursive,
            cancellationToken);

    public override Task CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            "Agent Framework directory creation is unavailable through the brokered file store.");
    }
}
