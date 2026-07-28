using System.Text.Json;
using Ali.Modules.UserMemory;
using Microsoft.Agents.AI;

#pragma warning disable MAAI001 // Agent Framework file-store API is intentionally adopted behind this module boundary.

namespace Ali.Modules.WorkstationFiles;

public sealed record AgentFileActionAuditEntry(
    DateTimeOffset TimestampUtc,
    string UserStableId,
    string Operation,
    string Path,
    bool Succeeded,
    string Outcome);

public sealed class AgentFileActionAuditStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _path;
    private readonly IActiveUserSession? _activeUsers;

    public AgentFileActionAuditStore(string userDataRoot, IActiveUserSession? activeUsers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataRoot);
        _path = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(userDataRoot),
            "Logs",
            "agent-file-actions.jsonl");
        _activeUsers = activeUsers;
    }

    public string Path => _path;

    public async Task AppendAsync(
        string operation,
        string path,
        bool succeeded,
        string outcome,
        CancellationToken cancellationToken = default)
    {
        var userId = _activeUsers is null || _activeUsers.RequiresSelection
            ? "unselected"
            : _activeUsers.Current.StableId;
        var entry = new AgentFileActionAuditEntry(
            DateTimeOffset.UtcNow,
            userId,
            operation,
            path,
            succeeded,
            outcome);
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            await File.AppendAllTextAsync(_path, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}

/// <summary>Audits file metadata and outcomes without ever recording file content.</summary>
public sealed class AuditedAgentFileStore(
    AgentFileStore inner,
    AgentFileActionAuditStore audit) : AgentFileStore
{
    public override Task WriteAsync(string path, string content, CancellationToken cancellationToken = default) =>
        AuditAsync("write", path, async () =>
        {
            await inner.WriteAsync(path, content, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        AuditAsync("read", path, () => inner.ReadAsync(path, cancellationToken), cancellationToken);

    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        AuditAsync("delete", path, () => inner.DeleteAsync(path, cancellationToken), cancellationToken);

    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken = default) =>
        AuditAsync("list", directory, () => inner.ListChildrenAsync(directory, cancellationToken), cancellationToken);

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
        AuditAsync("exists", path, () => inner.FileExistsAsync(path, cancellationToken), cancellationToken);

    public override Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        CancellationToken cancellationToken = default) =>
        AuditAsync(
            "search",
            directory,
            () => inner.SearchAsync(directory, regexPattern, globPattern, recursive, cancellationToken),
            cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        AuditAsync("create-directory", path, async () =>
        {
            await inner.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private async Task<T> AuditAsync<T>(
        string operation,
        string path,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await action().ConfigureAwait(false);
            await audit.AppendAsync(
                operation,
                path ?? string.Empty,
                succeeded: true,
                Describe(result),
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            try
            {
                await audit.AppendAsync(
                    operation,
                    path ?? string.Empty,
                    succeeded: false,
                    ex.GetType().Name,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }

            throw;
        }
    }

    private static string Describe<T>(T result) => result switch
    {
        null => "not-found",
        bool value => value ? "true" : "false",
        string text => $"text-length:{text.Length}",
        IReadOnlyCollection<FileStoreEntry> entries => $"entries:{entries.Count}",
        IReadOnlyCollection<FileSearchResult> matches => $"matches:{matches.Count}",
        _ => "completed"
    };
}
