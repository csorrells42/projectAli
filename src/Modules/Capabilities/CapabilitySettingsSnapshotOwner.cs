using System.Globalization;
using System.Security;

namespace Ali.Modules.Capabilities;

internal interface ICapabilityAvailabilitySettingsPersistence
{
    CapabilityAvailabilityLoadResult Load();

    CapabilityAvailabilitySaveResult Save(
        string expectedRevision,
        CapabilityAvailabilitySettings settings);
}

internal sealed class FileCapabilityAvailabilitySettingsPersistence :
    ICapabilityAvailabilitySettingsPersistence
{
    private readonly string _dataRoot;

    public FileCapabilityAvailabilitySettingsPersistence(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = dataRoot;
    }

    public CapabilityAvailabilityLoadResult Load() =>
        CapabilityAvailabilitySettingsStore.Load(_dataRoot);

    public CapabilityAvailabilitySaveResult Save(
        string expectedRevision,
        CapabilityAvailabilitySettings settings) =>
        CapabilityAvailabilitySettingsStore.Save(_dataRoot, expectedRevision, settings);
}

internal sealed class CapabilityPlanningPublication
{
    internal CapabilityPlanningPublication(
        CapabilityAvailabilitySettings settings,
        CapabilityRuntimeAvailability runtime,
        CapabilityRegistryRevisionSnapshot registry,
        CapabilityResolutionSnapshot resolution)
    {
        if (!string.Equals(settings.Revision, registry.SettingsRevision, StringComparison.Ordinal)
            || !string.Equals(registry.RegistryRevision, resolution.RegistryRevision, StringComparison.Ordinal)
            || !string.Equals(registry.SettingsRevision, resolution.SettingsRevision, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Capability planning publication revisions do not match.",
                nameof(resolution));
        }

        Settings = settings;
        Runtime = runtime;
        Registry = registry;
        Resolution = resolution;
    }

    public CapabilityAvailabilitySettings Settings { get; }

    public CapabilityRuntimeAvailability Runtime { get; }

    public CapabilityRegistryRevisionSnapshot Registry { get; }

    public CapabilityResolutionSnapshot Resolution { get; }
}

public sealed class CapabilitySettingsSnapshotOwner
{
    private readonly CanonicalCapabilityRegistry _canonicalRegistry;
    private readonly CapabilityResolver _resolver;
    private readonly ICapabilityAvailabilitySettingsPersistence _persistence;
    private readonly object _writerGate = new();
    private long _publicationSequence;
    private PublishedState _published;

    internal CapabilitySettingsSnapshotOwner(
        CanonicalCapabilityRegistry canonicalRegistry,
        CapabilityResolver resolver,
        CapabilityRuntimeAvailability initialRuntime,
        ICapabilityAvailabilitySettingsPersistence persistence)
    {
        _canonicalRegistry = canonicalRegistry ?? throw new ArgumentNullException(nameof(canonicalRegistry));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        ArgumentNullException.ThrowIfNull(initialRuntime);
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));

        var load = _persistence.Load();
        var settings = load.Status == CapabilityAvailabilityLoadStatus.FailedClosed
            ? CapabilityAvailabilitySettings.CreateFailClosed()
            : load.Settings;
        _published = BuildPublishedState(
            settings,
            initialRuntime,
            load.Status,
            load.Error);
    }

    public static CapabilitySettingsSnapshotOwner Open(
        string dataRoot,
        CanonicalCapabilityRegistry canonicalRegistry,
        CapabilityRuntimeAvailability initialRuntime) =>
        new(
            canonicalRegistry,
            new CapabilityResolver(),
            initialRuntime,
            new FileCapabilityAvailabilitySettingsPersistence(dataRoot));

    public CapabilitySettingsEnvelope CaptureSettings() =>
        Volatile.Read(ref _published).Envelope;

    public CapabilitySettingsEnvelope Reload()
    {
        lock (_writerGate)
        {
            var current = Volatile.Read(ref _published);
            var load = _persistence.Load();
            var settings = load.Status == CapabilityAvailabilityLoadStatus.FailedClosed
                ? CapabilityAvailabilitySettings.CreateFailClosed()
                : load.Settings;
            var candidate = BuildPublishedState(
                settings,
                current.Planning.Runtime,
                load.Status,
                load.Error);
            Publish(current, candidate);
            return candidate.Envelope;
        }
    }

    internal CapabilityPlanningPublication CapturePlanning() =>
        Volatile.Read(ref _published).Planning;

    public CapabilitySettingsMutationResult TrySaveRows(
        CapabilitySettingsStamp expected,
        IReadOnlyDictionary<string, bool> displayedSelections)
    {
        ArgumentNullException.ThrowIfNull(displayedSelections);
        lock (_writerGate)
        {
            var current = Volatile.Read(ref _published);
            if (!Matches(current, expected))
            {
                return Result(
                    CapabilitySettingsMutationStatus.Conflict,
                    current,
                    "Capability settings changed after the edit began.");
            }

            if (!TryOverlayDisplayedSelections(
                    current,
                    displayedSelections,
                    out var selections,
                    out var validationError))
            {
                return Result(
                    CapabilitySettingsMutationStatus.InvalidRequest,
                    current,
                    validationError);
            }

            var nextSettings = new CapabilityAvailabilitySettings(selections);
            var noChange = string.Equals(
                nextSettings.Revision,
                current.Planning.Settings.Revision,
                StringComparison.Ordinal);
            return PersistAndPublish(current, nextSettings, noChange);
        }
    }

    public CapabilitySettingsMutationResult TryApplyPreset(
        CapabilitySettingsStamp expected,
        string presetId,
        IReadOnlyDictionary<string, bool> displayedSelections)
    {
        ArgumentNullException.ThrowIfNull(displayedSelections);
        lock (_writerGate)
        {
            var current = Volatile.Read(ref _published);
            if (!Matches(current, expected))
            {
                return Result(
                    CapabilitySettingsMutationStatus.Conflict,
                    current,
                    "Capability settings changed after the edit began.");
            }

            if (string.IsNullOrWhiteSpace(presetId))
            {
                return Result(
                    CapabilitySettingsMutationStatus.InvalidRequest,
                    current,
                    "A capability preset ID is required.");
            }
            var preset = current.Planning.Registry.Presets.FirstOrDefault(
                candidate => string.Equals(candidate.Id, presetId, StringComparison.Ordinal));
            if (preset is null)
            {
                return Result(
                    CapabilitySettingsMutationStatus.InvalidRequest,
                    current,
                    $"Unknown capability preset '{presetId}'.");
            }
            if (!TryOverlayDisplayedSelections(
                    current,
                    displayedSelections,
                    out var selections,
                    out var validationError))
            {
                return Result(
                    CapabilitySettingsMutationStatus.InvalidRequest,
                    current,
                    validationError);
            }

            foreach (var groupId in preset.GroupIds)
            {
                selections[groupId] = true;
            }

            var nextSettings = new CapabilityAvailabilitySettings(selections);
            var noChange = string.Equals(
                nextSettings.Revision,
                current.Planning.Settings.Revision,
                StringComparison.Ordinal);
            return PersistAndPublish(current, nextSettings, noChange);
        }
    }

    internal bool TryPublishRuntime(
        CapabilitySettingsStamp expected,
        CapabilityRuntimeAvailability runtime,
        out CapabilitySettingsEnvelope currentEnvelope)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_writerGate)
        {
            var current = Volatile.Read(ref _published);
            if (!Matches(current, expected))
            {
                currentEnvelope = current.Envelope;
                return false;
            }

            var candidate = BuildPublishedState(
                current.Planning.Settings,
                runtime,
                current.Envelope.LoadStatus,
                current.Envelope.LoadError);
            Publish(current, candidate);
            currentEnvelope = candidate.Envelope;
            return true;
        }
    }

    private CapabilitySettingsMutationResult PersistAndPublish(
        PublishedState current,
        CapabilityAvailabilitySettings nextSettings,
        bool noChange)
    {
        CapabilityAvailabilitySaveResult save;
        try
        {
            save = _persistence.Save(current.Planning.Settings.Revision, nextSettings);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or SecurityException)
        {
            return Result(
                CapabilitySettingsMutationStatus.WriteFailed,
                current,
                FormatError(ex));
        }

        switch (save.Status)
        {
            case CapabilityAvailabilitySaveStatus.Saved:
                {
                    var persisted = save.Settings
                        ?? throw new InvalidOperationException(
                            "A successful capability settings save returned no settings.");
                    var candidate = BuildPublishedState(
                        persisted,
                        current.Planning.Runtime,
                        CapabilityAvailabilityLoadStatus.Loaded,
                        null);
                    Publish(current, candidate);
                    return Result(
                        noChange
                            ? CapabilitySettingsMutationStatus.NoChange
                            : CapabilitySettingsMutationStatus.Saved,
                        candidate,
                        null);
                }
            case CapabilityAvailabilitySaveStatus.Conflict:
                {
                    var authoritative = save.Settings
                        ?? throw new InvalidOperationException(
                            "A capability settings conflict returned no authoritative settings.");
                    var candidate = BuildPublishedState(
                        authoritative,
                        current.Planning.Runtime,
                        CapabilityAvailabilityLoadStatus.Loaded,
                        null);
                    Publish(current, candidate);
                    return Result(
                        CapabilitySettingsMutationStatus.Conflict,
                        candidate,
                        save.Error);
                }
            case CapabilityAvailabilitySaveStatus.Busy:
                return Result(CapabilitySettingsMutationStatus.Busy, current, save.Error);
            case CapabilityAvailabilitySaveStatus.FailedClosed:
                {
                    var candidate = BuildPublishedState(
                        CapabilityAvailabilitySettings.CreateFailClosed(),
                        current.Planning.Runtime,
                        CapabilityAvailabilityLoadStatus.FailedClosed,
                        save.Error ?? "Capability availability could not be loaded safely.");
                    Publish(current, candidate);
                    return Result(
                        CapabilitySettingsMutationStatus.FailedClosed,
                        candidate,
                        candidate.Envelope.LoadError);
                }
            default:
                throw new InvalidOperationException(
                    $"Unknown capability settings save status '{save.Status}'.");
        }
    }

    private PublishedState BuildPublishedState(
        CapabilityAvailabilitySettings settings,
        CapabilityRuntimeAvailability runtime,
        CapabilityAvailabilityLoadStatus loadStatus,
        string? loadError)
    {
        var registry = _canonicalRegistry.Freeze(settings);
        var resolution = _resolver.ResolvePlanning(registry, runtime);
        var planning = new CapabilityPlanningPublication(settings, runtime, registry, resolution);
        var envelope = CapabilitySettingsProjection.Create(
            registry,
            resolution,
            NextPublicationRevision(),
            loadStatus,
            loadError);
        return new PublishedState(planning, envelope);
    }

    private static bool TryOverlayDisplayedSelections(
        PublishedState current,
        IReadOnlyDictionary<string, bool> displayedSelections,
        out SortedDictionary<string, bool> selections,
        out string? error)
    {
        var knownGroupIds = current.Planning.Registry.Groups
            .Select(group => group.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (displayedSelections.Count != knownGroupIds.Count
            || displayedSelections.Keys.Any(groupId => !knownGroupIds.Contains(groupId))
            || knownGroupIds.Any(groupId => !displayedSelections.ContainsKey(groupId)))
        {
            selections = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            error = "Displayed capability selections do not exactly match the current registry rows.";
            return false;
        }

        selections = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (groupId, enabled) in current.Planning.Settings.GroupSelections)
        {
            selections.Add(groupId, enabled);
        }
        foreach (var group in current.Planning.Registry.Groups)
        {
            selections[group.Id] = displayedSelections[group.Id];
        }

        error = null;
        return true;
    }

    private static bool Matches(PublishedState current, CapabilitySettingsStamp expected) =>
        string.Equals(
            current.Envelope.PublicationRevision,
            expected.PublicationRevision,
            StringComparison.Ordinal)
        && string.Equals(
            current.Envelope.RegistryRevision,
            expected.RegistryRevision,
            StringComparison.Ordinal)
        && string.Equals(
            current.Envelope.SettingsRevision,
            expected.SettingsRevision,
            StringComparison.Ordinal)
        && string.Equals(
            current.Envelope.ResolutionRevision,
            expected.ResolutionRevision,
            StringComparison.Ordinal);

    private string NextPublicationRevision() =>
        Interlocked.Increment(ref _publicationSequence).ToString(CultureInfo.InvariantCulture);

    private void Publish(PublishedState current, PublishedState candidate)
    {
        var observed = Interlocked.CompareExchange(ref _published, candidate, current);
        if (!ReferenceEquals(observed, current))
        {
            throw new InvalidOperationException(
                "Capability settings publication changed outside the serialized writer boundary.");
        }
    }

    private static CapabilitySettingsMutationResult Result(
        CapabilitySettingsMutationStatus status,
        PublishedState current,
        string? error) =>
        new(status, current.Envelope, error);

    private static string FormatError(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message.ReplaceLineEndings(" ").Trim()}";

    private sealed class PublishedState
    {
        public PublishedState(
            CapabilityPlanningPublication planning,
            CapabilitySettingsEnvelope envelope)
        {
            Planning = planning;
            Envelope = envelope;
        }

        public CapabilityPlanningPublication Planning { get; }

        public CapabilitySettingsEnvelope Envelope { get; }
    }
}
