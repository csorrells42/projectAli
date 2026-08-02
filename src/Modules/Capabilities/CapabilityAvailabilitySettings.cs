using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ali.Modules.Capabilities;

public sealed class CapabilityAvailabilitySettings
{
    public CapabilityAvailabilitySettings(
        IReadOnlyDictionary<string, bool> groupSelections)
    {
        ArgumentNullException.ThrowIfNull(groupSelections);
        GroupSelections = FreezeSelections(groupSelections);
        Revision = CalculateRevision(GroupSelections);
    }

    /// <summary>
    /// SHA-256 fingerprint of the exact ordinally sorted group selections.
    /// </summary>
    public string Revision { get; }

    public IReadOnlyDictionary<string, bool> GroupSelections { get; }

    public bool IsEnabled(string groupId) =>
        GroupSelections.TryGetValue(groupId, out var enabled) && enabled;

    public CapabilityAvailabilitySettings WithGroupSelection(string groupId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        var selections = CopySelections(GroupSelections);
        selections[groupId] = enabled;
        return new CapabilityAvailabilitySettings(selections);
    }

    public CapabilityAvailabilitySettings ApplyPreset(CapabilityPresetDescriptor preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var canonical = CanonicalCapabilityCatalog.GetPreset(preset.Id);
        if (!string.Equals(preset.DisplayName, canonical.DisplayName, StringComparison.Ordinal)
            || !string.Equals(preset.Description, canonical.Description, StringComparison.Ordinal)
            || !preset.GroupIds.SequenceEqual(canonical.GroupIds, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Preset '{preset.Id}' does not match the canonical capability preset.",
                nameof(preset));
        }

        var selections = CopySelections(GroupSelections);
        foreach (var groupId in canonical.GroupIds)
        {
            selections[groupId] = true;
        }
        return new CapabilityAvailabilitySettings(selections);
    }

    public CapabilityAvailabilitySettings ApplyPreset(string presetId) =>
        ApplyPreset(CanonicalCapabilityCatalog.GetPreset(presetId));

    public static CapabilityAvailabilitySettings CreateDefault() =>
        new(
            CanonicalCapabilityCatalog.Groups.ToDictionary(
                group => group.Id,
                group => group.EnabledByDefault,
                StringComparer.Ordinal));

    public static CapabilityAvailabilitySettings CreateFailClosed() =>
        new(
            CanonicalCapabilityCatalog.Groups.ToDictionary(
                group => group.Id,
                _ => false,
                StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, bool> FreezeSelections(
        IReadOnlyDictionary<string, bool> selections)
    {
        var frozen = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (groupId, enabled) in selections)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
            frozen.Add(groupId, enabled);
        }

        return new ReadOnlyDictionary<string, bool>(frozen);
    }

    private static SortedDictionary<string, bool> CopySelections(
        IReadOnlyDictionary<string, bool> selections)
    {
        var copy = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (groupId, enabled) in selections)
        {
            copy.Add(groupId, enabled);
        }

        return copy;
    }

    private static string CalculateRevision(IReadOnlyDictionary<string, bool> selections)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> integer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(integer, selections.Count);
        hash.AppendData(integer);

        foreach (var (groupId, enabled) in selections.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var groupIdBytes = Encoding.UTF8.GetBytes(groupId);
            BinaryPrimitives.WriteInt32BigEndian(integer, groupIdBytes.Length);
            hash.AppendData(integer);
            hash.AppendData(groupIdBytes);
            hash.AppendData(enabled ? [1] : [0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

public enum CapabilityAvailabilityLoadStatus
{
    Loaded,
    MissingFileDefaults,
    FailedClosed
}

public sealed class CapabilityAvailabilityLoadResult
{
    private CapabilityAvailabilityLoadResult(
        CapabilityAvailabilityLoadStatus status,
        CapabilityAvailabilitySettings settings,
        string? error)
    {
        Status = status;
        Settings = settings;
        Error = error;
    }

    public CapabilityAvailabilityLoadStatus Status { get; }

    public CapabilityAvailabilitySettings Settings { get; }

    public string? Error { get; }

    public bool Success => Status != CapabilityAvailabilityLoadStatus.FailedClosed;

    internal static CapabilityAvailabilityLoadResult Loaded(CapabilityAvailabilitySettings settings) =>
        new(CapabilityAvailabilityLoadStatus.Loaded, settings, null);

    internal static CapabilityAvailabilityLoadResult MissingFileDefaults(
        CapabilityAvailabilitySettings settings) =>
        new(CapabilityAvailabilityLoadStatus.MissingFileDefaults, settings, null);

    internal static CapabilityAvailabilityLoadResult FailedClosed(string error) =>
        new(CapabilityAvailabilityLoadStatus.FailedClosed, CapabilityAvailabilitySettings.CreateFailClosed(), error);
}

public enum CapabilityAvailabilitySaveStatus
{
    Saved,
    Conflict,
    Busy,
    FailedClosed
}

public sealed class CapabilityAvailabilitySaveResult
{
    private CapabilityAvailabilitySaveResult(
        CapabilityAvailabilitySaveStatus status,
        CapabilityAvailabilitySettings? settings,
        string? error)
    {
        Status = status;
        Settings = settings;
        Error = error;
    }

    public CapabilityAvailabilitySaveStatus Status { get; }

    public CapabilityAvailabilitySettings? Settings { get; }

    public string? Error { get; }

    public bool Success => Status == CapabilityAvailabilitySaveStatus.Saved;

    internal static CapabilityAvailabilitySaveResult Saved(CapabilityAvailabilitySettings settings) =>
        new(CapabilityAvailabilitySaveStatus.Saved, settings, null);

    internal static CapabilityAvailabilitySaveResult Conflict(CapabilityAvailabilitySettings settings) =>
        new(
            CapabilityAvailabilitySaveStatus.Conflict,
            settings,
            "Capability availability changed after the edit began.");

    internal static CapabilityAvailabilitySaveResult Busy() =>
        new(
            CapabilityAvailabilitySaveStatus.Busy,
            null,
            "Capability availability is being updated by another writer.");

    internal static CapabilityAvailabilitySaveResult FailedClosed(
        CapabilityAvailabilitySettings settings,
        string error) =>
        new(CapabilityAvailabilitySaveStatus.FailedClosed, settings, error);
}

public static class CapabilityAvailabilitySettingsStore
{
    public static string GetSettingsPath(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(dataRoot, "Capabilities", "capability-availability.json");
    }

    public static CapabilityAvailabilityLoadResult Load(string dataRoot)
    {
        var path = GetSettingsPath(dataRoot);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
            return CapabilityAvailabilityLoadResult.Loaded(ReadCurrentFormat(document.RootElement));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return CapabilityAvailabilityLoadResult.MissingFileDefaults(
                CapabilityAvailabilitySettings.CreateDefault());
        }
        catch (Exception ex) when (ex is JsonException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or SecurityException)
        {
            return CapabilityAvailabilityLoadResult.FailedClosed(
                $"{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ").Trim()}");
        }
    }

    public static CapabilityAvailabilitySaveResult Save(
        string dataRoot,
        string expectedRevision,
        CapabilityAvailabilitySettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        ArgumentNullException.ThrowIfNull(settings);
        var path = GetSettingsPath(dataRoot);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, ".capability-availability.lock");
        FileStream writerLock;
        try
        {
            writerLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return CapabilityAvailabilitySaveResult.Busy();
        }

        using (writerLock)
        {
            var current = Load(dataRoot);
            if (!current.Success)
            {
                return CapabilityAvailabilitySaveResult.FailedClosed(
                    current.Settings,
                    current.Error ?? "Capability availability could not be loaded safely.");
            }
            if (!string.Equals(current.Settings.Revision, expectedRevision, StringComparison.Ordinal))
            {
                return CapabilityAvailabilitySaveResult.Conflict(current.Settings);
            }

            WriteAtomically(path, directory, settings);
        }

        return CapabilityAvailabilitySaveResult.Saved(
            new CapabilityAvailabilitySettings(settings.GroupSelections));
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    private static void WriteAtomically(
        string path,
        string directory,
        CapabilityAvailabilitySettings settings)
    {
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var bytes = WriteCurrentFormat(settings);
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static CapabilityAvailabilitySettings ReadCurrentFormat(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Capability availability must be a JSON object.");
        }

        JsonElement selectionsElement = default;
        var foundSelections = false;
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, "groupSelections", StringComparison.Ordinal)
                || foundSelections)
            {
                throw new JsonException($"Unexpected capability availability field '{property.Name}'.");
            }

            selectionsElement = property.Value;
            foundSelections = true;
        }

        if (!foundSelections || selectionsElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Capability availability requires one groupSelections object.");
        }

        var selections = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var selection in selectionsElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(selection.Name)
                || !selections.TryAdd(
                    selection.Name,
                    selection.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => throw new JsonException(
                            $"Capability group '{selection.Name}' must be true or false.")
                    }))
            {
                throw new JsonException("Capability group names must be non-empty and unique.");
            }
        }

        return new CapabilityAvailabilitySettings(selections);
    }

    private static byte[] WriteCurrentFormat(CapabilityAvailabilitySettings settings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Indented = true,
                       SkipValidation = false
                   }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("groupSelections");
            writer.WriteStartObject();
            foreach (var (groupId, enabled) in settings.GroupSelections)
            {
                writer.WriteBoolean(groupId, enabled);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
