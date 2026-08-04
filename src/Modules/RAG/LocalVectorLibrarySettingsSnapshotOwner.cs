namespace Ali.Modules.RAG;

public sealed record LocalVectorLibrarySettingsSnapshot(
    long Version,
    LocalVectorLibrarySettings Settings);

public sealed class LocalVectorLibrarySettingsSnapshotOwner
{
    private readonly string _dataRoot;
    private readonly object _writerGate = new();
    private LocalVectorLibrarySettingsSnapshot _published;

    public LocalVectorLibrarySettingsSnapshotOwner(string dataRoot)
    {
        _dataRoot = Path.GetFullPath(
            dataRoot ?? throw new ArgumentNullException(nameof(dataRoot)));
        _published = new LocalVectorLibrarySettingsSnapshot(
            1,
            Freeze(LocalVectorLibrarySettingsStore.LoadOrDefault(_dataRoot)));
    }

    public LocalVectorLibrarySettingsSnapshot Capture() =>
        Volatile.Read(ref _published);

    public LocalVectorLibrarySettingsSnapshot Save(LocalVectorLibrarySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_writerGate)
        {
            var frozen = Freeze(settings);
            LocalVectorLibrarySettingsStore.Save(_dataRoot, frozen);
            return Publish(frozen);
        }
    }

    public LocalVectorLibrarySettingsSnapshot Reload()
    {
        lock (_writerGate)
        {
            var loaded = Freeze(LocalVectorLibrarySettingsStore.LoadOrDefault(_dataRoot));
            return Publish(loaded);
        }
    }

    public LocalVectorLibrarySettingsSnapshot SaveRootDirectory(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var fullPath = Path.GetFullPath(rootDirectory.Trim());
        lock (_writerGate)
        {
            var current = Volatile.Read(ref _published);
            var merged = Freeze(current.Settings with { RootDirectory = fullPath });
            LocalVectorLibrarySettingsStore.Save(_dataRoot, merged);
            return Publish(merged);
        }
    }

    private LocalVectorLibrarySettingsSnapshot Publish(LocalVectorLibrarySettings settings)
    {
        var current = Volatile.Read(ref _published);
        var next = new LocalVectorLibrarySettingsSnapshot(
            current.Version == long.MaxValue ? 1 : current.Version + 1,
            settings);
        Volatile.Write(ref _published, next);
        return next;
    }

    private static LocalVectorLibrarySettings Freeze(LocalVectorLibrarySettings settings) =>
        settings with
        {
            AllowedExtensions = Array.AsReadOnly(settings.AllowedExtensions.ToArray())
        };
}
