namespace Ali.Modules.Coordinator;

/// <summary>
/// Marks the temporary journal-free assistant path. Workspace validation remains authoritative;
/// durable execution grants are intentionally bypassed while the core functionality gate is active.
/// </summary>
internal static class AliCoreAssistantExecutionContext
{
    private static readonly AsyncLocal<Scope?> CurrentScope = new();

    internal static bool IsActive => CurrentScope.Value is not null;

    internal static IDisposable Enter()
    {
        var scope = new Scope(CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    private sealed class Scope(Scope? previous) : IDisposable
    {
        private readonly Scope? _previous = previous;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0
                && ReferenceEquals(CurrentScope.Value, this))
            {
                CurrentScope.Value = _previous;
            }
        }
    }
}
