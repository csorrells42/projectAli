using System.Threading.Channels;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Orchestration.Observation;

internal sealed class ShadowToolObservationService : IShadowToolObserver, IAsyncDisposable
{
    internal const int DefaultCapacity = 256;
    internal const int MaximumRememberedTerminals = 65_536;
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly IShadowObservationSink _sink;
    private readonly Channel<ShadowToolObservation> _channel;
    private readonly CancellationTokenSource _readerLifetime = new();
    private readonly TimeSpan _shutdownTimeout;
    private readonly Task _readerTask;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;
    private int _accepting = 1;
    private int _readerCompleted;
    private long _enqueued;
    private long _pending;
    private long _queueFullDrops;
    private long _missingIdentityDrops;
    private long _invalidObservationDrops;
    private long _stoppedDrops;
    private long _persisted;
    private long _duplicateTerminals;
    private long _persistenceFailures;
    private long _dedupeEvictions;
    private long _shutdownTimeouts;
    private long _shutdownPendingAtTimeout;
    private long _shutdownAbandoned;

    public ShadowToolObservationService(
        EvidenceLedger ledger,
        int capacity = DefaultCapacity,
        TimeSpan? shutdownTimeout = null)
        : this(new ShadowEvidenceLedgerSink(ledger), capacity, shutdownTimeout)
    {
    }

    internal ShadowToolObservationService(
        IShadowObservationSink sink,
        int capacity = DefaultCapacity,
        TimeSpan? shutdownTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        var effectiveTimeout = shutdownTimeout ?? DefaultShutdownTimeout;
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));
        }

        _sink = sink;
        _shutdownTimeout = effectiveTimeout;
        _channel = Channel.CreateBounded<ShadowToolObservation>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        _readerTask = Task.Run(ReadLoopAsync);
    }

    public ShadowObservationHealthSnapshot Health => new(
        Interlocked.Read(ref _enqueued),
        Math.Max(0, Interlocked.Read(ref _pending)),
        Interlocked.Read(ref _queueFullDrops),
        Interlocked.Read(ref _missingIdentityDrops),
        Interlocked.Read(ref _invalidObservationDrops),
        Interlocked.Read(ref _stoppedDrops),
        Interlocked.Read(ref _persisted),
        Interlocked.Read(ref _duplicateTerminals),
        Interlocked.Read(ref _persistenceFailures),
        Interlocked.Read(ref _dedupeEvictions),
        Interlocked.Read(ref _shutdownTimeouts),
        Interlocked.Read(ref _shutdownPendingAtTimeout),
        Interlocked.Read(ref _shutdownAbandoned),
        Volatile.Read(ref _accepting) == 1,
        Volatile.Read(ref _readerCompleted) == 1);

    public bool TryObserveReturned(
        TurnIdentity? identity,
        string callId,
        string toolName,
        object? arguments,
        object? result,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission) =>
        TryCreateAndEnqueue(
            identity,
            value => ShadowToolObservation.Returned(
                value,
                callId,
                toolName,
                startedAtUtc,
                completedAtUtc,
                permission));

    public bool TryObserveDenied(
        TurnIdentity? identity,
        string callId,
        string toolName,
        object? arguments,
        string? failureCode,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission) =>
        TryCreateAndEnqueue(
            identity,
            value => ShadowToolObservation.Denied(
                value,
                callId,
                toolName,
                failureCode,
                startedAtUtc,
                completedAtUtc,
                permission));

    public bool TryObserveThrew(
        TurnIdentity? identity,
        string callId,
        string toolName,
        object? arguments,
        Exception exception,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission) =>
        TryCreateAndEnqueue(
            identity,
            value => ShadowToolObservation.Threw(
                value,
                callId,
                toolName,
                GetExceptionType(exception),
                startedAtUtc,
                completedAtUtc,
                permission));

    public bool TryObserveCancelled(
        TurnIdentity? identity,
        string callId,
        string toolName,
        object? arguments,
        OperationCanceledException exception,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission) =>
        TryCreateAndEnqueue(
            identity,
            value =>
            {
                ArgumentNullException.ThrowIfNull(exception);
                return ShadowToolObservation.Cancelled(
                    value,
                    callId,
                    toolName,
                    startedAtUtc,
                    completedAtUtc,
                    permission);
            });

    internal bool TryEnqueue(ShadowToolObservation? observation)
    {
        var pendingReserved = false;
        try
        {
            if (observation is null)
            {
                IncrementSaturating(ref _invalidObservationDrops);
                return false;
            }

            if (Volatile.Read(ref _accepting) != 1)
            {
                IncrementSaturating(ref _stoppedDrops);
                return false;
            }

            Interlocked.Increment(ref _pending);
            pendingReserved = true;
            if (!_channel.Writer.TryWrite(observation))
            {
                Interlocked.Decrement(ref _pending);
                pendingReserved = false;
                if (Volatile.Read(ref _accepting) == 1)
                {
                    IncrementSaturating(ref _queueFullDrops);
                }
                else
                {
                    IncrementSaturating(ref _stoppedDrops);
                }

                return false;
            }

            IncrementSaturating(ref _enqueued);
            return true;
        }
        catch
        {
            if (pendingReserved)
            {
                Interlocked.Decrement(ref _pending);
            }

            IncrementSaturating(ref _invalidObservationDrops);
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private bool TryCreateAndEnqueue(
        TurnIdentity? identity,
        Func<TurnIdentity, ShadowToolObservation> factory)
    {
        try
        {
            if (identity is null)
            {
                IncrementSaturating(ref _missingIdentityDrops);
                return false;
            }

            if (Volatile.Read(ref _accepting) != 1)
            {
                IncrementSaturating(ref _stoppedDrops);
                return false;
            }

            return TryEnqueue(factory(identity));
        }
        catch
        {
            IncrementSaturating(ref _invalidObservationDrops);
            return false;
        }
    }

    private async Task ReadLoopAsync()
    {
        var terminals = new BoundedTerminalSet(MaximumRememberedTerminals);
        try
        {
            await foreach (var observation in _channel.Reader.ReadAllAsync(_readerLifetime.Token))
            {
                var persisted = false;
                var duplicate = false;
                try
                {
                    if (terminals.Contains(observation))
                    {
                        duplicate = true;
                        IncrementSaturating(ref _duplicateTerminals);
                        continue;
                    }

                    await _sink.PersistAsync(
                        observation,
                        _readerLifetime.Token).ConfigureAwait(false);
                    persisted = true;
                    IncrementSaturating(ref _persisted);

                    var rememberResult = terminals.Remember(observation);
                    if (rememberResult == TerminalRememberResult.AddedAfterEviction)
                    {
                        IncrementSaturating(ref _dedupeEvictions);
                    }
                }
                catch (OperationCanceledException) when (_readerLifetime.IsCancellationRequested)
                {
                    IncrementSaturating(ref _persistenceFailures);
                }
                catch
                {
                    IncrementSaturating(ref _persistenceFailures);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                    if (_readerLifetime.IsCancellationRequested && !persisted && !duplicate)
                    {
                        IncrementSaturating(ref _shutdownAbandoned);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_readerLifetime.IsCancellationRequested)
        {
        }
        catch
        {
            IncrementSaturating(ref _persistenceFailures);
        }
        finally
        {
            if (_readerLifetime.IsCancellationRequested)
            {
                long unread = 0;
                while (_channel.Reader.TryRead(out _))
                {
                    unread++;
                }

                if (unread > 0)
                {
                    Interlocked.Add(ref _pending, -unread);
                    AddSaturating(ref _shutdownAbandoned, unread);
                }
            }

            Volatile.Write(ref _readerCompleted, 1);
            Interlocked.Exchange(ref _accepting, 0);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _accepting, 0);
        _channel.Writer.TryComplete();

        var completed = await Task.WhenAny(
            _readerTask,
            Task.Delay(_shutdownTimeout)).ConfigureAwait(false);
        if (ReferenceEquals(completed, _readerTask))
        {
            await _readerTask.ConfigureAwait(false);
            _readerLifetime.Dispose();
            return;
        }

        IncrementSaturating(ref _shutdownTimeouts);
        var pendingAtTimeout = Math.Max(0, Interlocked.Read(ref _pending));
        AddSaturating(ref _shutdownPendingAtTimeout, pendingAtTimeout);
        try
        {
            _readerLifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _ = _readerTask.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            _readerLifetime,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void IncrementSaturating(ref long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref value);
            if (current == long.MaxValue)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref value, current + 1, current) == current)
            {
                return;
            }
        }
    }

    private static void AddSaturating(ref long value, long increment)
    {
        if (increment <= 0)
        {
            return;
        }

        while (true)
        {
            var current = Interlocked.Read(ref value);
            if (current == long.MaxValue)
            {
                return;
            }

            var next = increment >= long.MaxValue - current
                ? long.MaxValue
                : current + increment;
            if (Interlocked.CompareExchange(ref value, next, current) == current)
            {
                return;
            }
        }
    }

    private static string GetExceptionType(Exception? exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var type = exception.GetType();
        return type.FullName ?? type.Name;
    }

    private enum TerminalRememberResult
    {
        Added,
        AddedAfterEviction,
        Duplicate
    }

    private readonly record struct TerminalKey(TurnIdentity Identity, string CallId);

    private sealed class BoundedTerminalSet
    {
        private readonly int _capacity;
        private readonly Dictionary<TerminalKey, LinkedListNode<TerminalKey>> _nodes = [];
        private readonly LinkedList<TerminalKey> _oldestFirst = [];

        public BoundedTerminalSet(int capacity)
        {
            _capacity = capacity;
        }

        public bool Contains(ShadowToolObservation observation) =>
            _nodes.ContainsKey(new TerminalKey(observation.Identity, observation.CallId));

        public TerminalRememberResult Remember(ShadowToolObservation observation)
        {
            var key = new TerminalKey(observation.Identity, observation.CallId);
            if (_nodes.ContainsKey(key))
            {
                return TerminalRememberResult.Duplicate;
            }

            var evicted = false;
            if (_nodes.Count >= _capacity)
            {
                var oldest = _oldestFirst.First
                    ?? throw new InvalidOperationException("The terminal observation cache is inconsistent.");
                _oldestFirst.RemoveFirst();
                _nodes.Remove(oldest.Value);
                evicted = true;
            }

            var node = _oldestFirst.AddLast(key);
            _nodes.Add(key, node);
            return evicted
                ? TerminalRememberResult.AddedAfterEviction
                : TerminalRememberResult.Added;
        }
    }
}
