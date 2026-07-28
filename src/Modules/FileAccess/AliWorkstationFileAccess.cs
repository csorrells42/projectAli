using System.Text.Json;
using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.UserMemory;
using Microsoft.Agents.AI;

#pragma warning disable MAAI001 // Agent Framework file-store API is intentionally adopted behind this module boundary.

namespace Ali.Modules.WorkstationFiles;

public sealed class AliWorkstationFileAccess
{
    private static readonly HashSet<string> ReadOnlyToolNames =
    [
        AliCapabilityCatalog.FileReadName,
        AliCapabilityCatalog.FileListName,
        AliCapabilityCatalog.FileSearchName
    ];

    private readonly AliWorkstationFileStore _rawStore;
    private readonly AgentToolPermissionStore _permissions;

    public AliWorkstationFileAccess(
        AliWorkstationFileStore store,
        AgentFileActionAuditStore audit,
        AgentToolPermissionStore permissions)
    {
        _rawStore = store;
        Audit = audit;
        _permissions = permissions;
        Store = new AuditedAgentFileStore(store, audit);
    }

    public AgentFileStore Store { get; }

    public AgentFileActionAuditStore Audit { get; }

    public IReadOnlyList<AliWorkstationFileMount> Mounts => _rawStore.Mounts;

    public string RecoverableTrashPath => _rawStore.TrashRoot;

    public string Instructions =>
        "Use only relative paths beginning with one of these approved roots: "
        + string.Join(", ", Mounts.Select(mount => mount.Name))
        + ". Translate the user's named location directly: 'desktop' means Desktop/<file>, "
        + "'documents' means Documents/<file>, 'downloads' means Downloads/<file>, and an export means Exports/<file>. "
        + "For example, a request for touch.txt on the desktop must use Desktop/touch.txt. "
        + "Never ask the user for an absolute path; if a path call fails, correct it with one of these virtual roots and retry. "
        + "Read, list, and search files when useful. "
        + "For new artifacts, default to Exports unless the user names another approved root. "
        + "Write with overwrite=false when creating a new file. Existing-file overwrite, replace, line edits, and delete require approval. "
        + "A delete moves the file into Ali's recoverable trash rather than erasing it permanently.";

    public static AliWorkstationFileAccess CreateDefault(
        string userDataRoot,
        string profileDataRoot,
        AgentToolPermissionStore permissions,
        IActiveUserSession? activeUsers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDataRoot);
        var profileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profileRoot))
        {
            profileRoot = AppContext.BaseDirectory;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
        {
            desktop = Path.Combine(profileRoot, "Desktop");
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Path.Combine(profileRoot, "Documents");
        }

        var mounts = new[]
        {
            new AliWorkstationFileMount("Workspace", Path.Combine(userDataRoot, "Workspace")),
            new AliWorkstationFileMount("Desktop", desktop),
            new AliWorkstationFileMount("Documents", documents),
            new AliWorkstationFileMount("Downloads", Path.Combine(profileRoot, "Downloads")),
            new AliWorkstationFileMount("Exports", Path.Combine(profileDataRoot, "Exports"))
        };
        var store = new AliWorkstationFileStore(mounts, Path.Combine(userDataRoot, "RecoverableTrash"));
        var audit = new AgentFileActionAuditStore(userDataRoot, activeUsers);
        return new AliWorkstationFileAccess(store, audit, permissions);
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

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => value.ToString()
        };
    }

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
