namespace Ali.Modules.UserMemory;

public sealed record UserMemorySettingsSnapshot(
    long Version,
    UserMemorySettings Settings);

public sealed class UserMemorySettingsSnapshotOwner
{
    private readonly string _dataRoot;
    private readonly object _writerGate = new();
    private UserMemorySettingsSnapshot _published;

    public UserMemorySettingsSnapshotOwner(string dataRoot)
    {
        _dataRoot = Path.GetFullPath(
            dataRoot ?? throw new ArgumentNullException(nameof(dataRoot)));
        _published = new UserMemorySettingsSnapshot(
            1,
            Freeze(UserMemorySettingsStore.LoadOrDefault(_dataRoot)));
    }

    public UserMemorySettingsSnapshot Capture() =>
        Volatile.Read(ref _published);

    public UserMemorySettingsSnapshot Save(UserMemorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_writerGate)
        {
            var frozen = Freeze(settings);
            UserMemorySettingsStore.Save(_dataRoot, frozen);
            return Publish(frozen);
        }
    }

    public UserMemorySettingsSnapshot Reload()
    {
        lock (_writerGate)
        {
            return Publish(Freeze(UserMemorySettingsStore.LoadOrDefault(_dataRoot)));
        }
    }

    private UserMemorySettingsSnapshot Publish(UserMemorySettings settings)
    {
        var current = Volatile.Read(ref _published);
        var next = new UserMemorySettingsSnapshot(
            current.Version == long.MaxValue ? 1 : current.Version + 1,
            Freeze(settings));
        Volatile.Write(ref _published, next);
        return next;
    }

    private static UserMemorySettings Freeze(UserMemorySettings settings) =>
        settings.Normalize();
}
