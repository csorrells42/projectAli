using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ali.Modules.UserMemory;

namespace Ali.Modules.Permissions;

public enum AgentToolPermissionScope
{
    ExactArguments,
    Tool
}

public enum AgentPermissionProfile
{
    TrustedWorkstation,
    LockedDown
}

public sealed record AgentToolPermissionGrant(
    string Id,
    string UserStableId,
    string UserDisplayName,
    string ToolName,
    AgentToolPermissionScope Scope,
    string? ArgumentFingerprint,
    string ArgumentSummary,
    DateTimeOffset CreatedUtc);

/// <summary>
/// Persists only explicit Agent Framework standing approvals. Rules are isolated by
/// active-user profile, fail closed, and never store raw tool argument values.
/// </summary>
public sealed class AgentToolPermissionStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly object _sync = new();
    private readonly string _path;
    private List<AgentToolPermissionGrant> _grants;
    private AgentPermissionProfile _profile;

    public AgentToolPermissionStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _path = Path.Combine(Path.GetFullPath(dataRoot), "Permissions", "agent-tool-permissions.json");
        var document = LoadFromDisk();
        _profile = Enum.IsDefined(document.Profile)
            ? document.Profile
            : AgentPermissionProfile.TrustedWorkstation;
        _grants = Normalize(document.Grants);
    }

    public string SettingsPath => _path;

    public AgentPermissionProfile CurrentProfile
    {
        get
        {
            lock (_sync)
            {
                return _profile;
            }
        }
    }

    public void SetProfile(AgentPermissionProfile profile)
    {
        lock (_sync)
        {
            if (_profile == profile)
            {
                return;
            }

            _profile = profile;
            SaveToDisk();
        }
    }

    public IReadOnlyList<AgentToolPermissionGrant> ListForUser(string userStableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userStableId);
        lock (_sync)
        {
            return _grants
                .Where(grant => grant.UserStableId.Equals(userStableId.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(grant => grant.ToolName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(grant => grant.Scope)
                .ToArray();
        }
    }

    public bool TryMatch(
        ActiveUser user,
        string toolName,
        IDictionary<string, object?>? arguments,
        out AgentToolPermissionGrant? grant)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var fingerprint = AgentToolArgumentFingerprint.Create(arguments);
        lock (_sync)
        {
            grant = _grants.FirstOrDefault(candidate =>
                candidate.UserStableId.Equals(user.StableId, StringComparison.OrdinalIgnoreCase)
                && candidate.ToolName.Equals(toolName.Trim(), StringComparison.Ordinal)
                && candidate.Scope == AgentToolPermissionScope.ExactArguments
                && candidate.ArgumentFingerprint == fingerprint)
                ?? _grants.FirstOrDefault(candidate =>
                    candidate.UserStableId.Equals(user.StableId, StringComparison.OrdinalIgnoreCase)
                    && candidate.ToolName.Equals(toolName.Trim(), StringComparison.Ordinal)
                    && candidate.Scope == AgentToolPermissionScope.Tool);
            return grant is not null;
        }
    }

    public AgentToolPermissionGrant Save(
        ActiveUser user,
        string toolName,
        AgentToolPermissionScope scope,
        IDictionary<string, object?>? arguments)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var normalizedUser = user.Normalize();
        var normalizedTool = toolName.Trim();
        var fingerprint = scope == AgentToolPermissionScope.ExactArguments
            ? AgentToolArgumentFingerprint.Create(arguments)
            : null;
        var summary = scope == AgentToolPermissionScope.ExactArguments
            ? AgentToolArgumentFingerprint.Summarize(arguments)
            : "All arguments";

        lock (_sync)
        {
            var existing = _grants.FirstOrDefault(candidate =>
                candidate.UserStableId.Equals(normalizedUser.StableId, StringComparison.OrdinalIgnoreCase)
                && candidate.ToolName.Equals(normalizedTool, StringComparison.Ordinal)
                && candidate.Scope == scope
                && candidate.ArgumentFingerprint == fingerprint);
            if (existing is not null)
            {
                return existing;
            }

            var saved = new AgentToolPermissionGrant(
                Guid.NewGuid().ToString("N"),
                normalizedUser.StableId,
                normalizedUser.DisplayName,
                normalizedTool,
                scope,
                fingerprint,
                summary,
                DateTimeOffset.UtcNow);
            _grants.Add(saved);
            SaveToDisk();
            return saved;
        }
    }

    public bool Revoke(string userStableId, string grantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        lock (_sync)
        {
            var removed = _grants.RemoveAll(grant =>
                grant.UserStableId.Equals(userStableId.Trim(), StringComparison.OrdinalIgnoreCase)
                && grant.Id.Equals(grantId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return false;
            }

            SaveToDisk();
            return true;
        }
    }

    public int RevokeAll(string userStableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userStableId);
        lock (_sync)
        {
            var removed = _grants.RemoveAll(grant =>
                grant.UserStableId.Equals(userStableId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveToDisk();
            }

            return removed;
        }
    }

    private AgentToolPermissionDocument LoadFromDisk()
    {
        if (!File.Exists(_path))
        {
            return new AgentToolPermissionDocument();
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<AgentToolPermissionDocument>(stream, JsonOptions)
                ?? new AgentToolPermissionDocument();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AgentToolPermissionDocument();
        }
    }

    private void SaveToDisk()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        using (var stream = File.Create(temporaryPath))
        {
            JsonSerializer.Serialize(
                stream,
                new AgentToolPermissionDocument
                {
                    Profile = _profile,
                    Grants = Normalize(_grants)
                },
                JsonOptions);
        }

        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static List<AgentToolPermissionGrant> Normalize(IEnumerable<AgentToolPermissionGrant> grants) =>
        grants
            .Where(grant => !string.IsNullOrWhiteSpace(grant.Id)
                && !string.IsNullOrWhiteSpace(grant.UserStableId)
                && !string.IsNullOrWhiteSpace(grant.ToolName)
                && (grant.Scope == AgentToolPermissionScope.Tool
                    || !string.IsNullOrWhiteSpace(grant.ArgumentFingerprint)))
            .Select(grant => grant with
            {
                Id = grant.Id.Trim(),
                UserStableId = grant.UserStableId.Trim(),
                UserDisplayName = string.IsNullOrWhiteSpace(grant.UserDisplayName) ? "Current user" : grant.UserDisplayName.Trim(),
                ToolName = grant.ToolName.Trim(),
                ArgumentFingerprint = grant.ArgumentFingerprint?.Trim(),
                ArgumentSummary = string.IsNullOrWhiteSpace(grant.ArgumentSummary) ? "Arguments hidden" : grant.ArgumentSummary.Trim()
            })
            .DistinctBy(grant => new
            {
                User = grant.UserStableId.ToUpperInvariant(),
                grant.ToolName,
                grant.Scope,
                grant.ArgumentFingerprint
            })
            .ToList();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

internal sealed class AgentToolPermissionDocument
{
    public AgentPermissionProfile Profile { get; set; } = AgentPermissionProfile.TrustedWorkstation;

    public List<AgentToolPermissionGrant> Grants { get; set; } = [];
}

internal static class AgentToolArgumentFingerprint
{
    public static string Create(IDictionary<string, object?>? arguments)
    {
        var element = JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, object?>());
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, element);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
    }

    public static string Summarize(IDictionary<string, object?>? arguments)
    {
        var names = arguments?.Keys
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray() ?? [];
        return names.Length == 0
            ? "Exact call with no arguments"
            : $"Exact arguments; values hidden ({string.Join(", ", names)})";
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
