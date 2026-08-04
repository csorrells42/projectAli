namespace Ali.Modules.Runtime;

public sealed record OpenAiCompatibleRuntimeOptionsSnapshot(
    long Version,
    OpenAiCompatibleRuntimeOptions Settings);

public sealed class OpenAiCompatibleRuntimeOptionsSnapshotOwner
{
    private readonly string _dataRoot;
    private readonly object _writerGate = new();
    private OpenAiCompatibleRuntimeOptionsSnapshot _published;

    public OpenAiCompatibleRuntimeOptionsSnapshotOwner(string dataRoot)
    {
        _dataRoot = Path.GetFullPath(
            dataRoot ?? throw new ArgumentNullException(nameof(dataRoot)));
        _published = new OpenAiCompatibleRuntimeOptionsSnapshot(
            1,
            Freeze(RuntimeSettingsStore.LoadOrDefault(_dataRoot)));
    }

    public OpenAiCompatibleRuntimeOptionsSnapshot Capture() =>
        Volatile.Read(ref _published);

    public OpenAiCompatibleRuntimeOptionsSnapshot Save(OpenAiCompatibleRuntimeOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_writerGate)
        {
            var frozen = Freeze(settings);
            RuntimeSettingsStore.Save(_dataRoot, frozen);
            return Publish(frozen);
        }
    }

    public OpenAiCompatibleRuntimeOptionsSnapshot Reload()
    {
        lock (_writerGate)
        {
            return Publish(Freeze(RuntimeSettingsStore.LoadOrDefault(_dataRoot)));
        }
    }

    private OpenAiCompatibleRuntimeOptionsSnapshot Publish(
        OpenAiCompatibleRuntimeOptions settings)
    {
        var current = Volatile.Read(ref _published);
        var next = new OpenAiCompatibleRuntimeOptionsSnapshot(
            current.Version == long.MaxValue ? 1 : current.Version + 1,
            Freeze(settings));
        Volatile.Write(ref _published, next);
        return next;
    }

    private static OpenAiCompatibleRuntimeOptions Freeze(
        OpenAiCompatibleRuntimeOptions settings) =>
        OllamaRuntimeSafetyPolicy.Normalize(settings) with { };
}
