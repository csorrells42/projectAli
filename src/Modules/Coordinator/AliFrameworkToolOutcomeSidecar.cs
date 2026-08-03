using Ali.Modules.Orchestration.Contracts;

namespace Ali.Modules.Coordinator;

/// <summary>
/// A provider-owned terminal signal. These values are emitted at the exact store,
/// mode-state, or skill-source boundary; they are never inferred from model-facing text.
/// </summary>
internal enum AliFrameworkToolOutcomeSignal
{
    Completed,
    Found,
    NoMatches,
    NotFound,
    Rejected,
    Failed,
    Conflicted
}

internal readonly record struct AliFrameworkToolOutcomeKey(
    TurnIdentity TurnIdentity,
    string CallId,
    string ToolName)
{
    public AliFrameworkToolOutcomeKey Validate()
    {
        ArgumentNullException.ThrowIfNull(TurnIdentity);
        if (string.IsNullOrWhiteSpace(CallId)
            || CallId.Length > AliFrameworkToolOutcomeSidecar.MaximumIdentifierCharacters)
        {
            throw new ArgumentException("A framework outcome call ID is missing or too long.", nameof(CallId));
        }

        if (string.IsNullOrWhiteSpace(ToolName)
            || ToolName.Length > AliFrameworkToolOutcomeSidecar.MaximumIdentifierCharacters)
        {
            throw new ArgumentException("A framework outcome tool name is missing or too long.", nameof(ToolName));
        }

        return this;
    }
}

/// <summary>
/// Bounded, consume-once correlation for Agent Framework tools whose model-facing
/// return values can also contain ordinary error strings. One coordinator owns one
/// instance. There is deliberately no process-global or "latest result" fallback.
/// </summary>
internal sealed class AliFrameworkToolOutcomeSidecar
{
    internal const int DefaultCapacity = 4_096;
    internal const int MaximumIdentifierCharacters = 256;

    private readonly int _capacity;
    private readonly Dictionary<AliFrameworkToolOutcomeKey, Entry> _entries = [];
    private readonly LinkedList<AliFrameworkToolOutcomeKey> _oldestFirst = [];
    private readonly AsyncLocal<InvocationFrame?> _activeInvocation = new();
    private readonly object _sync = new();

    internal AliFrameworkToolOutcomeSidecar(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    internal int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Records one exact-key provider signal. Repeating the same signal is idempotent.
    /// A contradictory signal is retained as Conflicted and can never promote work.
    /// Failed terminal evidence dominates every earlier signal for the same call.
    /// </summary>
    internal void Record(
        AliFrameworkToolOutcomeKey key,
        AliFrameworkToolOutcomeSignal signal)
    {
        RecordCore(key, signal, ownerTurn: null);
    }

    private void RecordCore(
        AliFrameworkToolOutcomeKey key,
        AliFrameworkToolOutcomeSignal signal,
        CoordinatorTurnContext? ownerTurn)
    {
        key = key.Validate();
        ValidateSignal(signal);
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                if (existing.OwnerTurn is not null
                    && ownerTurn is not null
                    && !ReferenceEquals(existing.OwnerTurn, ownerTurn))
                {
                    existing.Signal = AliFrameworkToolOutcomeSignal.Conflicted;
                    return;
                }

                existing.OwnerTurn ??= ownerTurn;
                existing.Signal = Merge(existing.Signal, signal);
                return;
            }

            var node = _oldestFirst.AddLast(key);
            _entries.Add(key, new Entry(signal, node, ownerTurn));
            while (_entries.Count > _capacity)
            {
                EvictOldest();
            }
        }
    }

    internal bool TryConsume(
        AliFrameworkToolOutcomeKey key,
        out AliFrameworkToolOutcomeSignal signal)
    {
        key = key.Validate();
        lock (_sync)
        {
            if (!_entries.Remove(key, out var entry))
            {
                signal = default;
                return false;
            }

            _oldestFirst.Remove(entry.Node);
            signal = entry.Signal;
            return true;
        }
    }

    internal bool Discard(AliFrameworkToolOutcomeKey key)
    {
        key = key.Validate();
        lock (_sync)
        {
            if (!_entries.Remove(key, out var entry))
            {
                return false;
            }

            _oldestFirst.Remove(entry.Node);
            return true;
        }
    }

    internal int DiscardTurn(TurnIdentity turnIdentity)
    {
        ArgumentNullException.ThrowIfNull(turnIdentity);
        lock (_sync)
        {
            var removed = 0;
            var node = _oldestFirst.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.TurnIdentity.Equals(turnIdentity))
                {
                    _entries.Remove(node.Value);
                    _oldestFirst.Remove(node);
                    removed++;
                }

                node = next;
            }

            return removed;
        }
    }

    internal int DiscardCoordinatorTurn(CoordinatorTurnContext turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        lock (_sync)
        {
            var removed = 0;
            var node = _oldestFirst.First;
            while (node is not null)
            {
                var next = node.Next;
                if (_entries.TryGetValue(node.Value, out var entry)
                    && ReferenceEquals(entry.OwnerTurn, turn))
                {
                    _entries.Remove(node.Value);
                    _oldestFirst.Remove(node);
                    removed++;
                }

                node = next;
            }

            return removed;
        }
    }

    internal bool TryEnterInvocation(
        CoordinatorTurnContext? turn,
        string callId,
        string toolName,
        out IDisposable? scope)
    {
        scope = null;
        if (turn is null
            || !turn.TryGetActionExecutionAuthority(out var authority)
            || authority is null
            || !turn.TryGetToolPlan(callId, out var plan)
            || plan is null
            || !string.Equals(plan.ToolName, toolName, StringComparison.Ordinal))
        {
            return false;
        }

        AliFrameworkToolOutcomeKey key;
        try
        {
            key = new AliFrameworkToolOutcomeKey(
                    authority.DurableIdentity,
                    callId,
                    toolName)
                .Validate();
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!turn.TryEnterActiveToolInvocation(callId, toolName, out var turnScope)
            || turnScope is null)
        {
            return false;
        }

        var frame = new InvocationFrame(key, turn, _activeInvocation.Value);
        _activeInvocation.Value = frame;
        scope = new InvocationLease(this, frame, turnScope);
        return true;
    }

    internal bool TryRecordActive(
        IReadOnlyList<string> eligibleToolNames,
        AliFrameworkToolOutcomeSignal signal)
    {
        ValidateSignal(signal);
        ArgumentNullException.ThrowIfNull(eligibleToolNames);
        var frame = _activeInvocation.Value;
        if (frame is null || !frame.IsActive)
        {
            return false;
        }

        foreach (var toolName in eligibleToolNames)
        {
            if (string.Equals(frame.Key.ToolName, toolName, StringComparison.Ordinal))
            {
                RecordCore(
                    frame.Key,
                    signal,
                    frame.OwnerTurn);
                return true;
            }
        }

        return false;
    }

    private void ExitInvocation(InvocationFrame frame)
    {
        frame.Deactivate();
        if (ReferenceEquals(_activeInvocation.Value, frame))
        {
            _activeInvocation.Value = frame.Previous;
        }
    }

    private static void ValidateSignal(AliFrameworkToolOutcomeSignal signal)
    {
        if (!Enum.IsDefined(typeof(AliFrameworkToolOutcomeSignal), signal))
        {
            throw new ArgumentOutOfRangeException(nameof(signal));
        }
    }

    private static AliFrameworkToolOutcomeSignal Merge(
        AliFrameworkToolOutcomeSignal current,
        AliFrameworkToolOutcomeSignal next)
    {
        if (current == next || current == AliFrameworkToolOutcomeSignal.Failed)
        {
            return current;
        }

        if (next == AliFrameworkToolOutcomeSignal.Failed)
        {
            return next;
        }

        return AliFrameworkToolOutcomeSignal.Conflicted;
    }

    private void EvictOldest()
    {
        var node = _oldestFirst.First;
        if (node is null)
        {
            return;
        }

        _oldestFirst.RemoveFirst();
        _entries.Remove(node.Value);
    }

    private sealed class Entry(
        AliFrameworkToolOutcomeSignal signal,
        LinkedListNode<AliFrameworkToolOutcomeKey> node,
        CoordinatorTurnContext? ownerTurn)
    {
        public AliFrameworkToolOutcomeSignal Signal { get; set; } = signal;

        public LinkedListNode<AliFrameworkToolOutcomeKey> Node { get; } = node;

        public CoordinatorTurnContext? OwnerTurn { get; set; } = ownerTurn;
    }

    private sealed class InvocationFrame(
        AliFrameworkToolOutcomeKey key,
        CoordinatorTurnContext ownerTurn,
        InvocationFrame? previous)
    {
        private int _active = 1;

        public AliFrameworkToolOutcomeKey Key { get; } = key;

        public CoordinatorTurnContext OwnerTurn { get; } = ownerTurn;

        public InvocationFrame? Previous { get; } = previous;

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public void Deactivate() => Interlocked.Exchange(ref _active, 0);
    }

    private sealed class InvocationLease(
        AliFrameworkToolOutcomeSidecar owner,
        InvocationFrame frame,
        IDisposable turnScope) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    owner.ExitInvocation(frame);
                }
                finally
                {
                    turnScope.Dispose();
                }
            }
        }
    }
}
