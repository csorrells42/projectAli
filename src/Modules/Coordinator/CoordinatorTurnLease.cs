using System.Collections.Concurrent;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Publishes only the flow-bound foreground turn. Losing ExecutionContext flow also
/// loses turn authority; process-wide active-turn state is never an identity fallback.
/// </summary>
internal sealed class CoordinatorTurnLease
{
    private readonly AsyncLocal<CoordinatorTurnContext?> _ambient = new();
    private readonly ConcurrentDictionary<string, CoordinatorTurnContext> _active =
        new(StringComparer.Ordinal);

    public CoordinatorTurnContext? Current => _ambient.Value;

    public IDisposable Enter(CoordinatorTurnContext turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var key = Key(turn);
        if (!_active.TryAdd(key, turn))
        {
            throw new InvalidOperationException(
                "This exact coordinator turn is already active.");
        }

        var previous = _ambient.Value;
        _ambient.Value = turn;
        return new Scope(this, key, turn, previous);
    }

    private void Exit(
        string key,
        CoordinatorTurnContext turn,
        CoordinatorTurnContext? previous)
    {
        if (_active.TryGetValue(key, out var current) && ReferenceEquals(current, turn))
        {
            _active.TryRemove(key, out _);
        }
        if (ReferenceEquals(_ambient.Value, turn))
        {
            _ambient.Value = previous;
        }
    }

    private static string Key(CoordinatorTurnContext turn) =>
        turn.ConversationId + "\0" + turn.AssistantMessageId;

    private sealed class Scope(
        CoordinatorTurnLease owner,
        string key,
        CoordinatorTurnContext turn,
        CoordinatorTurnContext? previous) : IDisposable
    {
        private CoordinatorTurnLease? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Exit(key, turn, previous);
    }
}
