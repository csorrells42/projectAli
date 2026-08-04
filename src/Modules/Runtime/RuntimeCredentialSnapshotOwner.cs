namespace Ali.Modules.Runtime;

public sealed class RuntimeCredentialSnapshotOwner
{
    private readonly RuntimeCredentialStore _store;
    private readonly object _writerGate = new();
    private string? _storedApiKey;

    internal RuntimeCredentialSnapshotOwner(string dataRoot)
    {
        _store = new RuntimeCredentialStore(dataRoot);
        _storedApiKey = _store.LoadApiKey();
    }

    internal string? LoadApiKey() => Volatile.Read(ref _storedApiKey);

    internal string? ResolveApiKey(string? environmentVariable)
    {
        var variable = string.IsNullOrWhiteSpace(environmentVariable)
            ? RuntimeCredentialStore.DefaultApiKeyEnvironmentVariable
            : environmentVariable.Trim();
        var fromEnvironment = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(fromEnvironment)
            ? fromEnvironment.Trim()
            : Volatile.Read(ref _storedApiKey);
    }

    internal void SaveApiKey(string? apiKey)
    {
        lock (_writerGate)
        {
            _store.SaveApiKey(apiKey);
            Volatile.Write(
                ref _storedApiKey,
                string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim());
        }
    }
}
