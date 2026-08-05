using System.Text.Json;
using Ali.Modules.Coordinator;
using Ali.Modules.UserMemory;
using Microsoft.Agents.AI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
        if (AliCoreAssistantExecutionContext.IsActive)
        {
            return;
        }

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
    private static readonly string[] WriteToolNames =
    [
        AliCapabilityCatalog.FileWriteName,
        AliCapabilityCatalog.FileReplaceName,
        AliCapabilityCatalog.FileReplaceLinesName
    ];

    private static readonly string[] ReadAndEditToolNames =
    [
        AliCapabilityCatalog.FileReadName,
        AliCapabilityCatalog.FileReplaceName,
        AliCapabilityCatalog.FileReplaceLinesName
    ];

    private readonly object _outcomeBindingSync = new();
    private OutcomeBinding? _outcomeBinding;

    internal void ConfigureOutcomeReporting(AliFrameworkToolOutcomeSidecar outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        lock (_outcomeBindingSync)
        {
            if (_outcomeBinding is not null)
            {
                throw new InvalidOperationException(
                    "Workstation file outcome reporting was already configured.");
            }

            _outcomeBinding = new OutcomeBinding(outcomes);
        }
    }

    public override Task WriteAsync(string path, string content, CancellationToken cancellationToken = default) =>
        AuditAsync("write", path, async () =>
        {
            await ValidateCoreSourceWriteAsync(path, content, cancellationToken).ConfigureAwait(false);
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
            ReportCompleted(operation, result);
            return result;
        }
        catch (Exception ex)
        {
            ReportFailed(operation);
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

    private async Task ValidateCoreSourceWriteAsync(
        string path,
        string postContent,
        CancellationToken cancellationToken)
    {
        if (!AliCoreAssistantExecutionContext.IsActive || !IsProtectedSourcePath(path))
        {
            return;
        }

        var currentContent = await inner.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (currentContent is null)
        {
            return;
        }

        ValidateCoreCSharpSyntaxEdit(path, currentContent, postContent, cancellationToken);
    }

    private static void ValidateCoreCSharpSyntaxEdit(
        string path,
        string currentContent,
        string postContent,
        CancellationToken cancellationToken)
    {
        if (!System.IO.Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var currentErrors = ParseCoreSource(path, currentContent, cancellationToken);
        var postErrors = ParseCoreSource(path, postContent, cancellationToken);
        var remainingCurrentErrors = currentErrors
            .GroupBy(DiagnosticFingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var introduced = new List<Diagnostic>();
        foreach (var diagnostic in postErrors)
        {
            var fingerprint = DiagnosticFingerprint(diagnostic);
            if (remainingCurrentErrors.TryGetValue(fingerprint, out var count) && count > 0)
            {
                remainingCurrentErrors[fingerprint] = count - 1;
            }
            else
            {
                introduced.Add(diagnostic);
            }
        }

        if (introduced.Count == 0)
        {
            return;
        }

        var detail = string.Join(
            " | ",
            introduced.Take(6).Select(diagnostic =>
                $"{diagnostic.Id}: {diagnostic.GetMessage()}"));
        throw new InvalidDataException(
            "Roslyn rejected the proposed source edit before disk mutation because it introduced C# syntax errors. "
            + detail
            + " Re-read the exact affected region and submit a corrected targeted edit.");
    }

    private static IReadOnlyList<Diagnostic> ParseCoreSource(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        return CSharpSyntaxTree.ParseText(
                content,
                path: path,
                cancellationToken: cancellationToken)
            .GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
    }

    private static string DiagnosticFingerprint(Diagnostic diagnostic) =>
        diagnostic.Id + "\0" + diagnostic.GetMessage();

    private static bool IsProtectedSourcePath(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".fs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vb", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cpp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".c", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".h", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".hpp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ts", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".py", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".razor", StringComparison.OrdinalIgnoreCase);
    }

    private void ReportCompleted<T>(string operation, T result)
    {
        switch (operation)
        {
            case "write":
                Report(WriteToolNames, AliFrameworkToolOutcomeSignal.Completed);
                break;
            case "read":
                Report(
                    result is null
                        ? ReadAndEditToolNames
                        : [AliCapabilityCatalog.FileReadName],
                    result is null
                        ? AliFrameworkToolOutcomeSignal.NotFound
                        : AliFrameworkToolOutcomeSignal.Found);
                break;
            case "delete" when result is bool deleted:
                Report(
                    [AliCapabilityCatalog.FileDeleteName],
                    deleted
                        ? AliFrameworkToolOutcomeSignal.Completed
                        : AliFrameworkToolOutcomeSignal.NotFound);
                break;
            case "list" when result is IReadOnlyCollection<FileStoreEntry> entries:
                Report(
                    [AliCapabilityCatalog.FileListName],
                    entries.Count == 0
                        ? AliFrameworkToolOutcomeSignal.NoMatches
                        : AliFrameworkToolOutcomeSignal.Completed);
                break;
            case "search" when result is IReadOnlyCollection<FileSearchResult> matches:
                Report(
                    [AliCapabilityCatalog.FileSearchName],
                    matches.Count == 0
                        ? AliFrameworkToolOutcomeSignal.NoMatches
                        : AliFrameworkToolOutcomeSignal.Completed);
                break;
        }
    }

    private void ReportFailed(string operation)
    {
        var toolNames = operation switch
        {
            "write" or "exists" or "create-directory" => WriteToolNames,
            "read" => ReadAndEditToolNames,
            "delete" => [AliCapabilityCatalog.FileDeleteName],
            "list" => [AliCapabilityCatalog.FileListName],
            "search" => [AliCapabilityCatalog.FileSearchName],
            _ => []
        };
        Report(toolNames, AliFrameworkToolOutcomeSignal.Failed);
    }

    private void Report(
        IReadOnlyList<string> eligibleToolNames,
        AliFrameworkToolOutcomeSignal signal)
    {
        var binding = Volatile.Read(ref _outcomeBinding);
        if (binding is null || eligibleToolNames.Count == 0)
        {
            return;
        }

        try
        {
            binding.Outcomes.TryRecordActive(
                eligibleToolNames,
                signal);
        }
        catch
        {
            // Outcome observation never changes the file operation. A missing signal
            // remains fail-closed as Unreported at the planning boundary.
        }
    }

    private sealed record OutcomeBinding(AliFrameworkToolOutcomeSidecar Outcomes);
}
