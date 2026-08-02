using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ali.Modules.Capabilities;
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

public sealed class AgentToolPermissionSnapshot
{
    internal AgentToolPermissionSnapshot(
        AgentPermissionProfile profile,
        IEnumerable<AgentToolPermissionGrant> grants,
        string revision)
    {
        Profile = profile;
        Grants = Array.AsReadOnly(grants.ToArray());
        Revision = revision;
    }

    public AgentPermissionProfile Profile { get; }

    public IReadOnlyList<AgentToolPermissionGrant> Grants { get; }

    public string Revision { get; }
}

/// <summary>
/// Persists only explicit Agent Framework standing approvals. Rules are isolated by
/// active-user profile, fail closed, and never store raw tool argument values.
/// </summary>
public sealed class AgentToolPermissionStore
{
    private const string InitializationMarkerContent =
        "ProjectAli.AgentToolPermissions.Initialized.v1\n";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly byte[] InitializationMarkerBytes =
        Encoding.UTF8.GetBytes(InitializationMarkerContent);
    private static readonly ConcurrentDictionary<string, object> FileGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> MutationEpochs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly string _path;
    private readonly string _initializationMarkerPath;
    private readonly object _fileGate;
    private List<AgentToolPermissionGrant> _grants = [];
    private AgentPermissionProfile _profile = AgentPermissionProfile.LockedDown;
    private string _fileFingerprint = "uninitialized";
    private string _initializationMarkerFingerprint = "uninitialized";
    private bool _initializationMarkerValid;

    public AgentToolPermissionStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var normalizedDataRoot = Path.GetFullPath(dataRoot);
        _path = Path.Combine(normalizedDataRoot, "Permissions", "agent-tool-permissions.json");
        _initializationMarkerPath = Path.Combine(
            normalizedDataRoot,
            ".agent-tool-permissions-initialized");
        _fileGate = FileGates.GetOrAdd(_path, static _ => new object());
        lock (_fileGate)
        {
            var observedFile = ObserveFile();
            var observedMarker = ObserveInitializationMarker();
            if (observedMarker.Kind == InitializationMarkerObservationKind.Missing)
            {
                try
                {
                    if (observedFile.Kind == PermissionFileObservationKind.Missing)
                    {
                        Persist(
                            AgentPermissionProfile.TrustedWorkstation,
                            []);
                    }

                    PersistInitializationMarker();
                    observedFile = ObserveFile();
                    observedMarker = ObserveInitializationMarker();
                }
                catch (Exception ex) when (ex is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException
                                           or SecurityException)
                {
                    observedFile = ObserveFile();
                    observedMarker = ObserveInitializationMarker();
                }
            }

            PublishObservedState(observedFile, observedMarker);
        }
    }

    public string SettingsPath => _path;

    internal string InitializationMarkerPath => _initializationMarkerPath;

    public AgentPermissionProfile CurrentProfile => CaptureSnapshot().Profile;

    public string CurrentRevision => CaptureSnapshot().Revision;

    public AgentToolPermissionSnapshot CaptureSnapshot()
    {
        lock (_fileGate)
        {
            lock (_sync)
            {
                RefreshFromDiskIfChanged();
                return CreateSnapshot();
            }
        }
    }

    public void SetProfile(AgentPermissionProfile profile)
    {
        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        lock (_fileGate)
        {
            lock (_sync)
            {
                RefreshFromDiskIfChanged();
                EnsureInitializationMarkerValid();
                if (_profile == profile)
                {
                    return;
                }

                var grants = _grants.ToList();
                var fingerprint = Persist(profile, grants);
                PublishMutation(
                    profile,
                    grants,
                    fingerprint,
                    CaptureValidInitializationMarkerFingerprint());
            }
        }
    }

    public IReadOnlyList<AgentToolPermissionGrant> ListForUser(string userStableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userStableId);
        return CaptureSnapshot().Grants
            .Where(grant => grant.UserStableId.Equals(userStableId.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(grant => grant.ToolName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(grant => grant.Scope)
            .ToArray();
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
        var snapshot = CaptureSnapshot();
        grant = snapshot.Grants.FirstOrDefault(candidate =>
            candidate.UserStableId.Equals(user.StableId, StringComparison.OrdinalIgnoreCase)
            && candidate.ToolName.Equals(toolName.Trim(), StringComparison.Ordinal)
            && candidate.Scope == AgentToolPermissionScope.ExactArguments
            && candidate.ArgumentFingerprint == fingerprint)
            ?? snapshot.Grants.FirstOrDefault(candidate =>
                candidate.UserStableId.Equals(user.StableId, StringComparison.OrdinalIgnoreCase)
                && candidate.ToolName.Equals(toolName.Trim(), StringComparison.Ordinal)
                && candidate.Scope == AgentToolPermissionScope.Tool);
        return grant is not null;
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

        lock (_fileGate)
        {
            lock (_sync)
            {
                RefreshFromDiskIfChanged();
                EnsureInitializationMarkerValid();
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
                var grants = Normalize(_grants.Append(saved));
                var persistedFingerprint = Persist(_profile, grants);
                PublishMutation(
                    _profile,
                    grants,
                    persistedFingerprint,
                    CaptureValidInitializationMarkerFingerprint());
                return saved;
            }
        }
    }

    public bool Revoke(string userStableId, string grantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userStableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        lock (_fileGate)
        {
            lock (_sync)
            {
                RefreshFromDiskIfChanged();
                EnsureInitializationMarkerValid();
                var grants = _grants.ToList();
                var removed = grants.RemoveAll(grant =>
                    grant.UserStableId.Equals(userStableId.Trim(), StringComparison.OrdinalIgnoreCase)
                    && grant.Id.Equals(grantId.Trim(), StringComparison.OrdinalIgnoreCase));
                if (removed == 0)
                {
                    return false;
                }

                var persistedFingerprint = Persist(_profile, grants);
                PublishMutation(
                    _profile,
                    grants,
                    persistedFingerprint,
                    CaptureValidInitializationMarkerFingerprint());
                return true;
            }
        }
    }

    public int RevokeAll(string userStableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userStableId);
        lock (_fileGate)
        {
            lock (_sync)
            {
                RefreshFromDiskIfChanged();
                EnsureInitializationMarkerValid();
                var grants = _grants.ToList();
                var removed = grants.RemoveAll(grant =>
                    grant.UserStableId.Equals(userStableId.Trim(), StringComparison.OrdinalIgnoreCase));
                if (removed == 0)
                {
                    return 0;
                }

                var persistedFingerprint = Persist(_profile, grants);
                PublishMutation(
                    _profile,
                    grants,
                    persistedFingerprint,
                    CaptureValidInitializationMarkerFingerprint());
                return removed;
            }
        }
    }

    private void RefreshFromDiskIfChanged()
    {
        var observedFile = ObserveFile();
        var observedMarker = ObserveInitializationMarker();
        if (string.Equals(
                observedFile.Fingerprint,
                _fileFingerprint,
                StringComparison.Ordinal)
            && string.Equals(
                observedMarker.Fingerprint,
                _initializationMarkerFingerprint,
                StringComparison.Ordinal))
        {
            return;
        }

        PublishObservedState(observedFile, observedMarker);
        AdvanceMutationEpoch();
    }

    private void PublishObservedState(
        PermissionFileObservation observedFile,
        InitializationMarkerObservation observedMarker)
    {
        _fileFingerprint = observedFile.Fingerprint;
        _initializationMarkerFingerprint = observedMarker.Fingerprint;
        _initializationMarkerValid = observedMarker.Kind == InitializationMarkerObservationKind.Valid;
        if (_initializationMarkerValid
            && observedFile.Kind == PermissionFileObservationKind.Valid)
        {
            _profile = observedFile.Profile;
            _grants = observedFile.Grants;
            return;
        }

        _profile = AgentPermissionProfile.LockedDown;
        _grants = [];
    }

    private void EnsureInitializationMarkerValid()
    {
        if (!_initializationMarkerValid)
        {
            throw new IOException(
                "The permission-store initialization marker is missing or unreadable; permission changes are locked down.");
        }
    }

    private string CaptureValidInitializationMarkerFingerprint()
    {
        var observed = ObserveInitializationMarker();
        if (observed.Kind != InitializationMarkerObservationKind.Valid)
        {
            throw new IOException(
                "The permission-store initialization marker changed or became unreadable before the mutation could be published in memory.");
        }

        return observed.Fingerprint;
    }

    private PermissionFileObservation ObserveFile()
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Missing();
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or SecurityException)
        {
            return new PermissionFileObservation(
                PermissionFileObservationKind.Invalid,
                AgentPermissionProfile.LockedDown,
                [],
                BuildUnreadableFingerprint(_path),
                null);
        }
        try
        {
            using (stream)
            {
                var before = CaptureFileMetadata(_path, stream);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var bytes = buffer.ToArray();
                var after = CaptureFileMetadata(_path, stream);
                var contentHash = Convert.ToHexString(SHA256.HashData(bytes));
                if (before != after
                    || before.PathLength != bytes.LongLength
                    || before.StreamLength != bytes.LongLength)
                {
                    return new PermissionFileObservation(
                        PermissionFileObservationKind.Invalid,
                        AgentPermissionProfile.LockedDown,
                        [],
                        BuildObservedFingerprint("unstable", after, contentHash),
                        contentHash);
                }

                try
                {
                    var document = JsonSerializer.Deserialize<AgentToolPermissionDocument>(bytes, JsonOptions);
                    if (document is null || !Enum.IsDefined(document.Profile))
                    {
                        return Invalid(after, contentHash);
                    }

                    return new PermissionFileObservation(
                        PermissionFileObservationKind.Valid,
                        document.Profile,
                        Normalize(document.Grants ?? []),
                        BuildObservedFingerprint("valid", after, contentHash),
                        contentHash);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    return Invalid(after, contentHash);
                }
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or SecurityException)
        {
            return new PermissionFileObservation(
                PermissionFileObservationKind.Invalid,
                AgentPermissionProfile.LockedDown,
                [],
                BuildUnreadableFingerprint(_path),
                null);
        }

        PermissionFileObservation Missing() => new(
            PermissionFileObservationKind.Missing,
            AgentPermissionProfile.LockedDown,
            [],
            "missing",
            null);

        PermissionFileObservation Invalid(PermissionFileMetadata metadata, string hash) => new(
            PermissionFileObservationKind.Invalid,
            AgentPermissionProfile.LockedDown,
            [],
            BuildObservedFingerprint("invalid", metadata, hash),
            hash);
    }

    private InitializationMarkerObservation ObserveInitializationMarker()
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                _initializationMarkerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new InitializationMarkerObservation(
                InitializationMarkerObservationKind.Missing,
                "marker-missing");
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or SecurityException)
        {
            return new InitializationMarkerObservation(
                InitializationMarkerObservationKind.Invalid,
                $"marker-{BuildUnreadableFingerprint(_initializationMarkerPath)}");
        }

        try
        {
            using (stream)
            {
                var before = CaptureFileMetadata(_initializationMarkerPath, stream);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var bytes = buffer.ToArray();
                var after = CaptureFileMetadata(_initializationMarkerPath, stream);
                var contentHash = Convert.ToHexString(SHA256.HashData(bytes));
                if (before != after
                    || before.PathLength != bytes.LongLength
                    || before.StreamLength != bytes.LongLength)
                {
                    return new InitializationMarkerObservation(
                        InitializationMarkerObservationKind.Invalid,
                        BuildObservedFingerprint("marker-unstable", after, contentHash));
                }

                var valid = bytes.AsSpan().SequenceEqual(InitializationMarkerBytes);
                return new InitializationMarkerObservation(
                    valid
                        ? InitializationMarkerObservationKind.Valid
                        : InitializationMarkerObservationKind.Invalid,
                    BuildObservedFingerprint(
                        valid ? "marker-valid" : "marker-invalid",
                        after,
                        contentHash));
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or SecurityException)
        {
            return new InitializationMarkerObservation(
                InitializationMarkerObservationKind.Invalid,
                $"marker-{BuildUnreadableFingerprint(_initializationMarkerPath)}");
        }
    }

    private string PersistInitializationMarker()
    {
        var directory = Path.GetDirectoryName(_initializationMarkerPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_initializationMarkerPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(InitializationMarkerBytes);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, _initializationMarkerPath, overwrite: false);
            }
            catch (IOException)
            {
                var raced = ObserveInitializationMarker();
                if (raced.Kind == InitializationMarkerObservationKind.Valid)
                {
                    return raced.Fingerprint;
                }

                throw;
            }

            var persisted = ObserveInitializationMarker();
            if (persisted.Kind != InitializationMarkerObservationKind.Valid)
            {
                throw new IOException(
                    "The permission-store initialization marker changed or became unreadable before it could be published in memory.");
            }

            return persisted.Fingerprint;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or SecurityException)
            {
                // A failed cleanup never makes an unverified marker valid in memory.
            }
        }
    }

    private string Persist(
        AgentPermissionProfile profile,
        IEnumerable<AgentToolPermissionGrant> grants)
    {
        var document = new AgentToolPermissionDocument
        {
            Profile = profile,
            Grants = Normalize(grants)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            var expectedContentHash = Convert.ToHexString(SHA256.HashData(bytes));
            var persisted = ObserveFile();
            if (persisted.Kind != PermissionFileObservationKind.Valid
                || !string.Equals(
                    persisted.ContentHash,
                    expectedContentHash,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    "The permission file changed or became unreadable before the durable mutation could be published in memory.");
            }

            return persisted.Fingerprint;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or SecurityException)
            {
                // A failed cleanup never publishes an unpersisted permission mutation.
            }
        }
    }

    private void PublishMutation(
        AgentPermissionProfile profile,
        List<AgentToolPermissionGrant> grants,
        string fingerprint,
        string initializationMarkerFingerprint)
    {
        _profile = profile;
        _grants = Normalize(grants);
        _fileFingerprint = fingerprint;
        _initializationMarkerFingerprint = initializationMarkerFingerprint;
        _initializationMarkerValid = true;
        AdvanceMutationEpoch();
    }

    private AgentToolPermissionSnapshot CreateSnapshot()
    {
        var epoch = MutationEpochs.GetOrAdd(_path, 0);
        using var revision = new CapabilityRevisionBuilder();
        revision.Add("ali-agent-tool-permission-snapshot-v2");
        revision.Add(epoch.ToString(CultureInfo.InvariantCulture));
        revision.Add(_fileFingerprint);
        revision.Add(_initializationMarkerFingerprint);
        revision.Add((int)_profile);
        revision.Add(_grants.Count);
        foreach (var grant in _grants)
        {
            revision.Add(grant.Id);
            revision.Add(grant.UserStableId);
            revision.Add(grant.UserDisplayName);
            revision.Add(grant.ToolName);
            revision.Add((int)grant.Scope);
            revision.Add(grant.ArgumentFingerprint);
            revision.Add(grant.ArgumentSummary);
            revision.Add(grant.CreatedUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture));
        }

        return new AgentToolPermissionSnapshot(_profile, _grants, revision.Finish());
    }

    private void AdvanceMutationEpoch() =>
        MutationEpochs.AddOrUpdate(
            _path,
            1,
            static (_, current) => current == long.MaxValue ? current : current + 1);

    private static string BuildUnreadableFingerprint(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"unreadable:{info.Exists}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or SecurityException)
        {
            return "unreadable";
        }
    }

    private static PermissionFileMetadata CaptureFileMetadata(
        string path,
        FileStream stream)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return new PermissionFileMetadata(
            info.LastWriteTimeUtc.Ticks,
            info.Length,
            stream.Length);
    }

    private static string BuildObservedFingerprint(
        string kind,
        PermissionFileMetadata metadata,
        string contentHash) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{kind}:{metadata.LastWriteUtcTicks}:{metadata.PathLength}:{metadata.StreamLength}:{contentHash}");

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
            .OrderBy(grant => grant.UserStableId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(grant => grant.ToolName, StringComparer.Ordinal)
            .ThenBy(grant => grant.Scope)
            .ThenBy(grant => grant.ArgumentFingerprint, StringComparer.Ordinal)
            .ToList();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private enum PermissionFileObservationKind
    {
        Missing,
        Valid,
        Invalid
    }

    private enum InitializationMarkerObservationKind
    {
        Missing,
        Valid,
        Invalid
    }

    private sealed record PermissionFileObservation(
        PermissionFileObservationKind Kind,
        AgentPermissionProfile Profile,
        List<AgentToolPermissionGrant> Grants,
        string Fingerprint,
        string? ContentHash);

    private sealed record InitializationMarkerObservation(
        InitializationMarkerObservationKind Kind,
        string Fingerprint);

    private readonly record struct PermissionFileMetadata(
        long LastWriteUtcTicks,
        long PathLength,
        long StreamLength);
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
