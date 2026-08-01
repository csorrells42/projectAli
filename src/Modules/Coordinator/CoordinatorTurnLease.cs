namespace Ali.Modules.Coordinator;

/// <summary>
/// Publishes the one active foreground turn to tool workers even when a framework
/// deliberately suppresses ExecutionContext flow. The UI admits one turn at a time.
/// </summary>
internal sealed class CoordinatorTurnLease
{
    private CoordinatorTurnContext? _current;

    public CoordinatorTurnContext? Current => Volatile.Read(ref _current);

    public IDisposable Enter(CoordinatorTurnContext turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var existing = Interlocked.CompareExchange(ref _current, turn, null);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                "A foreground coordinator turn is already active.");
        }

        return new Scope(this, turn);
    }

    private void Exit(CoordinatorTurnContext turn) =>
        Interlocked.CompareExchange(ref _current, null, turn);

    private sealed class Scope(
        CoordinatorTurnLease owner,
        CoordinatorTurnContext turn) : IDisposable
    {
        private CoordinatorTurnLease? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Exit(turn);
    }
}
