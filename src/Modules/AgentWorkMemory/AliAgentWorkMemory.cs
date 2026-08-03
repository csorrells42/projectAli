using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.UserMemory;
using Microsoft.Agents.AI;

#pragma warning disable MAAI001 // Agent Framework file-memory storage is intentionally isolated behind this module.

namespace Ali.Modules.AgentWorkMemory;

public sealed record AgentWorkMemoryAuditEntry(
    DateTimeOffset TimestampUtc,
    string UserStableId,
    string ConversationId,
    string Operation,
    string FileName,
    bool Succeeded,
    string Outcome);

/// <summary>
/// Owns Ali's private Agent Framework working-memory store. The Framework receives one
/// store, while this module dynamically isolates every operation by active user and
/// visible conversation. This deliberately remains separate from Mem0, RAG, and user files.
/// </summary>
public sealed class AliAgentWorkMemory
{
    private readonly AsyncLocal<ScopeFrame?> _scope = new();
    private readonly ScopedAgentWorkMemoryStore _store;

    public AliAgentWorkMemory(string userDataRoot)
        : this(
            userDataRoot,
            durableOrchestrationRoot: null,
            assistantProfileBinding: null,
            evidence: null)
    {
    }

    internal AliAgentWorkMemory(
        string userDataRoot,
        string? durableOrchestrationRoot,
        string? assistantProfileBinding,
        EvidenceLedger? evidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataRoot);
        var fullRoot = Path.GetFullPath(userDataRoot);
        RootPath = Path.Combine(fullRoot, "AgentWorkspaces");
        RecoverableTrashPath = Path.Combine(fullRoot, "RecoverableTrash", "AgentWorkMemory");
        AuditPath = Path.Combine(fullRoot, "Logs", "agent-work-memory-actions.jsonl");
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(RecoverableTrashPath);
        _store = new ScopedAgentWorkMemoryStore(
            RootPath,
            RecoverableTrashPath,
            AuditPath,
            () => _scope.Value?.Scope);
        if (string.IsNullOrWhiteSpace(durableOrchestrationRoot)
            || string.IsNullOrWhiteSpace(assistantProfileBinding))
        {
            Store = _store;
            ExecutionEffectAdapters = [];
            TargetStateAdapters = [];
        }
        else
        {
            var coordinator = new AliAgentWorkMemoryExecutionCoordinator(
                _store,
                RootPath,
                RecoverableTrashPath,
                () => _scope.Value?.Scope,
                durableOrchestrationRoot,
                assistantProfileBinding,
                evidence);
            Store = new AliBrokeredAgentWorkMemoryStore(_store, coordinator);
            ExecutionEffectAdapters = coordinator.ExecutionEffectAdapters;
            TargetStateAdapters = coordinator.TargetStateAdapters;
        }
    }

    public AgentFileStore Store { get; }

    internal IReadOnlyList<IAliExecutionEffectAdapter> ExecutionEffectAdapters { get; }

    internal IReadOnlyList<IActionTargetStateAdapter> TargetStateAdapters { get; }

    public string RootPath { get; }

    public string RecoverableTrashPath { get; }

    public string AuditPath { get; }

    public IDisposable EnterScope(string conversationId, ActiveUser? activeUser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        var previous = _scope.Value;
        var userId = activeUser?.Normalize().StableId ?? "unselected";
        _scope.Value = new ScopeFrame(
            new AgentWorkMemoryScope(
                userId,
                conversationId.Trim(),
                BuildScopedRelativePath(userId, conversationId)),
            previous);
        return new ScopeLease(this, previous);
    }

    internal string GetWorkspacePath(string userStableId, string conversationId) =>
        Path.Combine(RootPath, BuildScopedRelativePath(userStableId, conversationId));

    internal void ConfigureOutcomeReporting(AliFrameworkToolOutcomeSidecar outcomes) =>
        _store.ConfigureOutcomeReporting(outcomes);

    private static string BuildScopedRelativePath(string userStableId, string conversationId) =>
        Path.Combine(
            "Users",
            SafeSegment(userStableId),
            "Conversations",
            SafeSegment(conversationId));

    private static string SafeSegment(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        var slug = new string(normalized
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')
            .ToArray())
            .Trim('-');
        if (slug.Length == 0)
        {
            slug = "scope";
        }
        if (slug.Length > 40)
        {
            slug = slug[..40];
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12]
            .ToLowerInvariant();
        return $"{slug}-{digest}";
    }

    private void Restore(ScopeFrame? previous) => _scope.Value = previous;

    private sealed record ScopeFrame(AgentWorkMemoryScope Scope, ScopeFrame? Previous);

    private sealed class ScopeLease(AliAgentWorkMemory owner, ScopeFrame? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Restore(previous);
            }
        }
    }
}

internal sealed record AgentWorkMemoryScope(
    string UserStableId,
    string ConversationId,
    string RelativePath);

/// <summary>
/// Resolves the current logical work-memory scope before every operation, delegates the
/// actual file semantics to Agent Framework's store, and adds recoverability plus metadata-only auditing.
/// </summary>
internal sealed class ScopedAgentWorkMemoryStore : AgentFileStore
{
    private static readonly string[] WriteToolNames =
    [
        AliCapabilityCatalog.WorkMemoryWriteName,
        AliCapabilityCatalog.WorkMemoryReplaceName,
        AliCapabilityCatalog.WorkMemoryReplaceLinesName
    ];

    private static readonly string[] ReadAndEditToolNames =
    [
        AliCapabilityCatalog.WorkMemoryReadName,
        AliCapabilityCatalog.WorkMemoryReplaceName,
        AliCapabilityCatalog.WorkMemoryReplaceLinesName
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _rootPath;
    private readonly string _trashPath;
    private readonly string _auditPath;
    private readonly Func<AgentWorkMemoryScope?> _scopeAccessor;
    private readonly ConcurrentDictionary<string, ScopedStore> _stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _auditGate = new(1, 1);
    private readonly object _outcomeBindingSync = new();
    private OutcomeBinding? _outcomeBinding;

    public ScopedAgentWorkMemoryStore(
        string rootPath,
        string trashPath,
        string auditPath,
        Func<AgentWorkMemoryScope?> scopeAccessor)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _trashPath = Path.GetFullPath(trashPath);
        _auditPath = Path.GetFullPath(auditPath);
        _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
    }

    internal void ConfigureOutcomeReporting(AliFrameworkToolOutcomeSidecar outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        lock (_outcomeBindingSync)
        {
            if (_outcomeBinding is not null)
            {
                throw new InvalidOperationException(
                    "Agent work-memory outcome reporting was already configured.");
            }

            _outcomeBinding = new OutcomeBinding(outcomes);
        }
    }

    internal void ReportExactDurableOutcome(
        string toolName,
        AliFrameworkToolOutcomeSignal signal)
    {
        if (toolName is not (
                AliCapabilityCatalog.WorkMemoryWriteName
                or AliCapabilityCatalog.WorkMemoryReplaceName
                or AliCapabilityCatalog.WorkMemoryReplaceLinesName
                or AliCapabilityCatalog.WorkMemoryDeleteName))
        {
            throw new ArgumentException(
                "Only an explicitly registered work-memory mutation can report a durable outcome.",
                nameof(toolName));
        }
        Report([toolName], signal);
    }

    internal Task AppendExactDurableAuditAsync(
        AgentWorkMemoryScope scope,
        string operation,
        string path,
        bool succeeded,
        string outcome,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        return AppendAuditAsync(
            scope,
            operation,
            path,
            succeeded,
            outcome,
            cancellationToken);
    }

    public override Task WriteAsync(string path, string content, CancellationToken cancellationToken = default) =>
        AuditAsync("write", path, async store =>
        {
            await store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsFrameworkIndex(path)
                    && await store.Inner.FileExistsAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    await MoveToTrashAsync(store, path, "overwritten", cancellationToken).ConfigureAwait(false);
                }
                await store.Inner.WriteAsync(path, content, cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                store.Gate.Release();
            }
        }, cancellationToken);

    public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        AuditAsync("read", path, store => store.Inner.ReadAsync(path, cancellationToken), cancellationToken);

    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        AuditAsync("delete", path, async store =>
        {
            await store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!await store.Inner.FileExistsAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
                await MoveToTrashAsync(store, path, "deleted", cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                store.Gate.Release();
            }
        }, cancellationToken);

    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken = default) =>
        AuditAsync("list", directory, store => store.Inner.ListChildrenAsync(directory, cancellationToken), cancellationToken);

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
        AuditAsync("exists", path, store => store.Inner.FileExistsAsync(path, cancellationToken), cancellationToken);

    public override Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        CancellationToken cancellationToken = default) =>
        AuditAsync(
            "search",
            directory,
            store => store.Inner.SearchAsync(directory, regexPattern, globPattern, recursive, cancellationToken),
            cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        AuditAsync("create-directory", path, async store =>
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                await store.Inner.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            }
            return true;
        }, cancellationToken);

    private async Task<T> AuditAsync<T>(
        string operation,
        string? path,
        Func<ScopedStore, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var scope = _scopeAccessor()
            ?? throw new InvalidOperationException("Agent work memory was accessed outside an active Ali conversation scope.");
        var store = GetStore(scope);
        try
        {
            var result = await action(store).ConfigureAwait(false);
            await AppendAuditAsync(scope, operation, path, true, Describe(result), cancellationToken).ConfigureAwait(false);
            ReportCompleted(operation, path, result);
            return result;
        }
        catch (Exception ex)
        {
            ReportFailed(operation);
            try
            {
                await AppendAuditAsync(scope, operation, path, false, ex.GetType().Name, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
            }
            throw;
        }
    }

    private ScopedStore GetStore(AgentWorkMemoryScope scope)
    {
        var workspace = ResolveBeneath(_rootPath, scope.RelativePath);
        return _stores.GetOrAdd(workspace, path => new ScopedStore(path));
    }

    private async Task MoveToTrashAsync(
        ScopedStore store,
        string path,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ResolveBeneath(store.RootPath, path);
        var relativeScope = Path.GetRelativePath(_rootPath, store.RootPath);
        var destination = ResolveBeneath(
            _trashPath,
            Path.Combine(
                DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"),
                reason,
                relativeScope,
                path));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task AppendAuditAsync(
        AgentWorkMemoryScope scope,
        string operation,
        string? path,
        bool succeeded,
        string outcome,
        CancellationToken cancellationToken)
    {
        var entry = new AgentWorkMemoryAuditEntry(
            DateTimeOffset.UtcNow,
            scope.UserStableId,
            scope.ConversationId,
            operation,
            path ?? string.Empty,
            succeeded,
            outcome);
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
        await _auditGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_auditPath)!);
            await File.AppendAllTextAsync(_auditPath, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _auditGate.Release();
        }
    }

    private static string ResolveBeneath(string root, string relativePath)
    {
        var fullRoot = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath ?? string.Empty));
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The work-memory path escaped its isolated conversation workspace.", nameof(relativePath));
        }
        return candidate;
    }

    private static bool IsFrameworkIndex(string path) =>
        string.Equals(path?.Replace('\\', '/').Trim('/'), "memories.md", StringComparison.OrdinalIgnoreCase);

    private static bool IsFrameworkDescription(string? path) =>
        path?.Replace('\\', '/').EndsWith("_description.md", StringComparison.OrdinalIgnoreCase) == true;

    private static string Describe<T>(T result) => result switch
    {
        null => "not-found",
        bool value => value ? "true" : "false",
        string text => $"text-length:{text.Length}",
        IReadOnlyCollection<FileStoreEntry> entries => $"entries:{entries.Count}",
        IReadOnlyCollection<FileSearchResult> matches => $"matches:{matches.Count}",
        _ => "completed"
    };

    private void ReportCompleted<T>(string operation, string? path, T result)
    {
        if (IsFrameworkIndex(path ?? string.Empty) || IsFrameworkDescription(path))
        {
            return;
        }

        switch (operation)
        {
            case "write":
                Report(WriteToolNames, AliFrameworkToolOutcomeSignal.Completed);
                break;
            case "read":
                Report(
                    result is null
                        ? ReadAndEditToolNames
                        : [AliCapabilityCatalog.WorkMemoryReadName],
                    result is null
                        ? AliFrameworkToolOutcomeSignal.NotFound
                        : AliFrameworkToolOutcomeSignal.Found);
                break;
            case "delete" when result is bool deleted:
                Report(
                    [AliCapabilityCatalog.WorkMemoryDeleteName],
                    deleted
                        ? AliFrameworkToolOutcomeSignal.Completed
                        : AliFrameworkToolOutcomeSignal.NotFound);
                break;
            case "list" when result is IReadOnlyCollection<FileStoreEntry> entries:
                Report(
                    [AliCapabilityCatalog.WorkMemoryListName],
                    entries.Count == 0
                        ? AliFrameworkToolOutcomeSignal.NoMatches
                        : AliFrameworkToolOutcomeSignal.Completed);
                break;
            case "search" when result is IReadOnlyCollection<FileSearchResult> matches:
                Report(
                    [AliCapabilityCatalog.WorkMemorySearchName],
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
            "delete" => [AliCapabilityCatalog.WorkMemoryDeleteName],
            "list" => [AliCapabilityCatalog.WorkMemoryListName],
            "search" => [AliCapabilityCatalog.WorkMemorySearchName],
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
            // Outcome observation never changes work-memory semantics. Missing
            // exact evidence remains Unreported at the planning boundary.
        }
    }

    private sealed class ScopedStore
    {
        public ScopedStore(string rootPath)
        {
            RootPath = rootPath;
            Directory.CreateDirectory(rootPath);
            Inner = new FileSystemAgentFileStore(rootPath);
        }

        public string RootPath { get; }

        public FileSystemAgentFileStore Inner { get; }

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed record OutcomeBinding(AliFrameworkToolOutcomeSidecar Outcomes);
}
