using Microsoft.Agents.AI;

#pragma warning disable MAAI001

namespace Ali.Modules.Coordinator;

/// <summary>
/// Keeps Agent Framework's native file provider readable for independent verification
/// while structurally rejecting its mutation methods after a coding-agent handoff.
/// </summary>
internal sealed class ExternalOwnershipFileStore(
    AgentFileStore inner,
    Func<bool> externalAgentOwnsTurn) : AgentFileStore
{
    public override Task WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        RejectMutation();
        return inner.WriteAsync(path, content, cancellationToken);
    }

    public override Task<string?> ReadAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        inner.ReadAsync(path, cancellationToken);

    public override Task<bool> DeleteAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        RejectMutation();
        return inner.DeleteAsync(path, cancellationToken);
    }

    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken = default) =>
        inner.ListChildrenAsync(directory, cancellationToken);

    public override Task<bool> FileExistsAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        inner.FileExistsAsync(path, cancellationToken);

    public override Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        CancellationToken cancellationToken = default) =>
        inner.SearchAsync(directory, regexPattern, globPattern, recursive, cancellationToken);

    public override Task CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        RejectMutation();
        return inner.CreateDirectoryAsync(path, cancellationToken);
    }

    private void RejectMutation()
    {
        if (externalAgentOwnsTurn())
        {
            throw new InvalidOperationException(
                "The selected external coding agent owns this programming turn. "
                + "Ali's native file provider remains read-only until the turn ends.");
        }
    }
}
