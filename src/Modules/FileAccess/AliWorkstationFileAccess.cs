using System.Text.Json;
using Ali.Modules.Coding.Changesets;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.Permissions;
using Ali.Modules.UserMemory;
using Microsoft.Agents.AI;

#pragma warning disable MAAI001 // Agent Framework file-store API is intentionally adopted behind this module boundary.

namespace Ali.Modules.WorkstationFiles;

public sealed record WorkstationFileMoveResult(
    bool Success,
    string SourcePath,
    string DestinationPath,
    string Message);

public sealed class AliWorkstationFileAccess
{
    private static readonly HashSet<string> ReadOnlyToolNames =
    [
        AliCapabilityCatalog.FileReadName,
        AliCapabilityCatalog.FileListName,
        AliCapabilityCatalog.FileSearchName,
        AliCapabilityCatalog.LoadAgentSkillName,
        AliCapabilityCatalog.ReadAgentSkillResourceName
    ];

    private readonly AliWorkstationFileStore _rawStore;
    private readonly AuditedAgentFileStore _auditedStore;
    private readonly AgentToolPermissionStore _permissions;
    private readonly AliFileTreeMutationCoordinator? _treeMutations;

    internal AliWorkstationFileAccess(
        AliWorkstationFileStore store,
        AgentFileActionAuditStore audit,
        AgentToolPermissionStore permissions)
    {
        _rawStore = store;
        Audit = audit;
        _permissions = permissions;
        _auditedStore = new AuditedAgentFileStore(store, audit);
        Store = _auditedStore;
        FrameworkStore = Store;
        ExecutionEffectAdapters = [];
        TargetStateAdapters = [];
    }

    internal AliWorkstationFileAccess(
        AliWorkstationFileStore store,
        AgentFileActionAuditStore audit,
        AgentToolPermissionStore permissions,
        string durableOrchestrationRoot,
        string assistantProfileBinding,
        EvidenceLedger? evidence = null,
        Action<AliSourceTransactionFault>? faultInjector = null,
        Action<AliFileTreeExecutionCheckpoint>? treeExecutionFaultHook = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(permissions);
        _rawStore = store;
        Audit = audit;
        _permissions = permissions;
        var transaction = new AliFrameworkFileMutationTransaction(
            store,
            durableOrchestrationRoot,
            assistantProfileBinding,
            evidence,
            faultInjector);
        _treeMutations = new AliFileTreeMutationCoordinator(
            store,
            durableOrchestrationRoot,
            assistantProfileBinding,
            evidence,
            executionFaultHook: treeExecutionFaultHook);
        _auditedStore = new AuditedAgentFileStore(
            new AliBrokeredFrameworkFileStore(store, transaction, _treeMutations),
            audit);
        Store = _auditedStore;
        FrameworkStore = Store;
        ExecutionEffectAdapters = AliFrameworkFileExecutionAdapter.CreateAll(transaction)
            .Concat(_treeMutations.ExecutionEffectAdapters)
            .ToArray();
        TargetStateAdapters = _treeMutations.TargetStateAdapters;
    }

    /// <summary>
    /// Audited workstation store. Production composition exposes the same durable brokered
    /// facade as <see cref="FrameworkStore"/>, so every canonical mutation requires its exact
    /// one-use execution grant while reads retain their normal behavior.
    /// </summary>
    public AgentFileStore Store { get; }

    /// <summary>
    /// Store used by the production Agent Framework provider. Its mutations require a broker
    /// grant and it is the same facade exposed by <see cref="Store"/> in production composition.
    /// </summary>
    internal AgentFileStore FrameworkStore { get; }

    internal IReadOnlyList<IAliExecutionEffectAdapter> ExecutionEffectAdapters { get; }

    internal IReadOnlyList<IActionTargetStateAdapter> TargetStateAdapters { get; }

    public AgentFileActionAuditStore Audit { get; }

    public IReadOnlyList<AliWorkstationFileMount> Mounts => _rawStore.Mounts;

    public string RecoverableTrashPath => _rawStore.TrashRoot;

    internal AliResolvedWorkstationPath ResolvePhysicalFilePath(string path) =>
        _rawStore.ResolvePhysicalFilePath(path);

    internal AliResolvedWorkstationPath ResolvePhysicalDirectoryPath(string path) =>
        _rawStore.ResolvePhysicalDirectoryPath(path);

    internal void ConfigureOutcomeReporting(AliFrameworkToolOutcomeSidecar outcomes)
    {
        _auditedStore.ConfigureOutcomeReporting(outcomes);
    }

    internal Dictionary<string, object?> NormalizeProviderToolArguments(
        string toolName,
        Dictionary<string, object?> arguments)
    {
        var (argumentName, allowMountRoot) = toolName switch
        {
            AliCapabilityCatalog.FileWriteName
                or AliCapabilityCatalog.FileReadName
                or AliCapabilityCatalog.FileDeleteName
                or AliCapabilityCatalog.FileReplaceName
                or AliCapabilityCatalog.FileReplaceLinesName => ("fileName", false),
            AliCapabilityCatalog.FileListName
                or AliCapabilityCatalog.FileSearchName => ("directory", true),
            _ => (string.Empty, false)
        };
        if (argumentName.Length == 0
            || !TryRead(arguments, argumentName, out var value)
            || ReadText(value) is not { Length: > 0 } path
            || !_rawStore.TryNormalizeApprovedAbsolutePath(path, allowMountRoot, out var virtualPath))
        {
            return arguments;
        }

        arguments[argumentName] = virtualPath;
        return arguments;
    }

    public async Task<WorkstationFileMoveResult> MoveAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (_treeMutations is not null)
        {
            var result = await _treeMutations
                .MoveAsync(sourcePath, destinationPath, cancellationToken)
                .ConfigureAwait(false);
            await Audit.AppendAsync(
                    "move",
                    $"{sourcePath} -> {destinationPath}",
                    result.Success,
                    result.Success ? "completed" : "failed",
                    result.Success ? cancellationToken : CancellationToken.None)
                .ConfigureAwait(false);
            return result;
        }

        try
        {
            await _rawStore.MoveItemAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
            await Audit.AppendAsync(
                    "move",
                    $"{sourcePath} -> {destinationPath}",
                    succeeded: true,
                    "completed",
                    cancellationToken)
                .ConfigureAwait(false);
            return new WorkstationFileMoveResult(
                true,
                sourcePath,
                destinationPath,
                "The file or folder was moved successfully.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try
            {
                await Audit.AppendAsync(
                        "move",
                        $"{sourcePath} -> {destinationPath}",
                        succeeded: false,
                        ex.GetType().Name,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
            }

            return new WorkstationFileMoveResult(false, sourcePath, destinationPath, ex.Message);
        }
    }

    internal async Task<WorkstationFileOperationResult> CopyDurablyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (_treeMutations is null)
        {
            throw new InvalidOperationException(
                "The exact durable file-copy adapter is not configured.");
        }
        var result = await _treeMutations
            .CopyAsync(sourcePath, destinationPath, cancellationToken)
            .ConfigureAwait(false);
        await Audit.AppendAsync(
                "copy",
                $"{sourcePath} -> {destinationPath}",
                result.Success,
                result.Success ? "completed" : "failed",
                result.Success ? cancellationToken : CancellationToken.None)
            .ConfigureAwait(false);
        return result;
    }

    internal async Task<WorkstationFileOperationResult> CreateDirectoryDurablyAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (_treeMutations is null)
        {
            throw new InvalidOperationException(
                "The exact durable directory-creation adapter is not configured.");
        }
        var result = await _treeMutations.CreateDirectoryAsync(path, cancellationToken)
            .ConfigureAwait(false);
        await Audit.AppendAsync(
                "create-directory",
                path,
                result.Success,
                result.Success ? "completed" : "failed",
                result.Success ? cancellationToken : CancellationToken.None)
            .ConfigureAwait(false);
        return result;
    }

    internal bool HasDurableTreeMutationBoundary => _treeMutations is not null;

    public string Instructions =>
        "Use only relative paths beneath Workspace/. "
        + "Never ask the user for an absolute path; if a path call fails, correct it to Workspace/<path> and retry. "
        + "Read, list, and search files when useful. Delete can move either a file or a complete folder tree into recoverable trash. "
        + "Ali can also copy files or folders, create folders, inspect metadata and SHA-256 hashes, and create, list, or extract archives. "
        + "Copy and move require the destination parent folder to already exist; create that folder with the folder-creation tool first when needed. "
        + "ZIP is the default archive format. Use TAR, GZip, or TAR.GZ when the user requests one of those formats, and use 7-Zip only when the user explicitly requests 7z/7-Zip. "
        + "Create every new artifact beneath Workspace/. "
        + "Write with overwrite=false when creating a new file. Existing-file overwrite, replace, line edits, and delete require approval. "
        + "A delete moves the selected file or folder into Ali's recoverable trash rather than erasing it permanently.";

    public static AliWorkstationFileAccess CreateDefault(
        string userDataRoot,
        string profileDataRoot,
        AgentToolPermissionStore permissions,
        IActiveUserSession? activeUsers,
        string? durableOrchestrationRoot = null,
        string? assistantProfileBinding = null,
        string? workspaceRootOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDataRoot);
        var workspace = string.IsNullOrWhiteSpace(workspaceRootOverride)
            ? Path.Combine(userDataRoot, "Workspace")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(workspaceRootOverride));
        var mounts = new[]
        {
            new AliWorkstationFileMount("Workspace", workspace)
        };
        var store = new AliWorkstationFileStore(mounts, Path.Combine(userDataRoot, "RecoverableTrash"));
        var audit = new AgentFileActionAuditStore(userDataRoot, activeUsers);
        return new AliWorkstationFileAccess(
            store,
            audit,
            permissions);
    }

    public ValueTask<bool> ShouldAutoApproveAsync(ToolAutoApprovalRuleContext context) =>
        ShouldAutoApproveAsync(
            context.FunctionCallContent.Name,
            context.FunctionCallContent.Arguments,
            CancellationToken.None);

    internal async ValueTask<bool> ShouldAutoApproveAsync(
        string toolName,
        IDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        if (_permissions.CurrentProfile != AgentPermissionProfile.TrustedWorkstation)
        {
            return false;
        }

        if (ReadOnlyToolNames.Contains(toolName))
        {
            return true;
        }

        if (!string.Equals(toolName, AliCapabilityCatalog.FileWriteName, StringComparison.Ordinal)
            || ReadBoolean(arguments, "overwrite"))
        {
            return false;
        }

        var path = ReadString(arguments, "fileName");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return !await _rawStore.FileExistsAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? ReadString(IDictionary<string, object?>? arguments, string name)
    {
        if (!TryRead(arguments, name, out var value) || value is null)
        {
            return null;
        }

        return ReadText(value);
    }

    private static string? ReadText(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
        null => null,
        _ => value.ToString()
    };

    private static bool ReadBoolean(IDictionary<string, object?>? arguments, string name)
    {
        if (!TryRead(arguments, name, out var value) || value is null)
        {
            return false;
        }

        return value switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } json => bool.TryParse(json.GetString(), out var parsed) && parsed,
            _ => bool.TryParse(value.ToString(), out var parsed) && parsed
        };
    }

    private static bool TryRead(
        IDictionary<string, object?>? arguments,
        string name,
        out object? value)
    {
        if (arguments is not null)
        {
            foreach (var pair in arguments)
            {
                if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
        }

        value = null;
        return false;
    }
}
