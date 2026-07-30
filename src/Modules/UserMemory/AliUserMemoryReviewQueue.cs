namespace Ali.Modules.UserMemory;

/// <summary>
/// Serializes post-response semantic memory reviews without delaying the visible
/// answer. The queue never interprets text; Mem0 and its configured model decide
/// whether each user turn produces ADD, UPDATE, DELETE, or NONE.
/// </summary>
public sealed class AliUserMemoryReviewQueue
{
    internal static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromSeconds(15);

    private readonly IUserMemoryService _memories;
    private readonly TimeSpan _quietPeriod;
    private readonly object _sync = new();
    private Task<MemoryOperationResult> _tail = Task.FromResult(
        new MemoryOperationResult(true, "No memory reviews are pending.", []));
    private TaskCompletionSource<bool> _stateChanged = NewStateSignal();
    private CancellationTokenSource? _runningBackgroundReview;
    private DateTimeOffset _lastForegroundActivityUtc = DateTimeOffset.UtcNow;
    private bool _foregroundTurnActive;
    private int _forcedDrainCount;

    public AliUserMemoryReviewQueue(
        IUserMemoryService memories,
        TimeSpan? quietPeriod = null)
    {
        _memories = memories ?? throw new ArgumentNullException(nameof(memories));
        _quietPeriod = quietPeriod ?? DefaultQuietPeriod;
        if (_quietPeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(quietPeriod));
        }
    }

    /// <summary>
    /// Gives a new user-facing model turn priority over opportunistic memory work.
    /// This is scheduling only; it never examines or classifies the user's text.
    /// </summary>
    public void BeginForegroundTurn()
    {
        CancellationTokenSource? reviewToCancel;
        lock (_sync)
        {
            _foregroundTurnActive = true;
            _lastForegroundActivityUtc = DateTimeOffset.UtcNow;
            reviewToCancel = _forcedDrainCount == 0 ? _runningBackgroundReview : null;
            PulseStateChanged();
        }

        reviewToCancel?.Cancel();
    }

    public void EndForegroundTurn()
    {
        lock (_sync)
        {
            _foregroundTurnActive = false;
            _lastForegroundActivityUtc = DateTimeOffset.UtcNow;
            PulseStateChanged();
        }
    }

    public Task<MemoryOperationResult> Enqueue(ActiveUser user, string userText)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);

        lock (_sync)
        {
            var previous = _tail;
            _tail = Task.Run(() => ReviewWhenConversationIsIdleAsync(
                previous,
                user.Normalize(),
                userText.Trim()));
            return _tail;
        }
    }

    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        Task pending;
        lock (_sync)
        {
            _forcedDrainCount++;
            pending = _tail;
            PulseStateChanged();
        }

        try
        {
            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _forcedDrainCount--;
                PulseStateChanged();
            }
        }
    }

    private async Task<MemoryOperationResult> ReviewWhenConversationIsIdleAsync(
        Task<MemoryOperationResult> previous,
        ActiveUser user,
        string userText)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // A failed review is isolated. Later user turns must still be reviewed.
        }

        while (true)
        {
            await WaitForReviewOpportunityAsync().ConfigureAwait(false);

            using var reviewCancellation = new CancellationTokenSource();
            lock (_sync)
            {
                if (_foregroundTurnActive && _forcedDrainCount == 0)
                {
                    continue;
                }
                _runningBackgroundReview = reviewCancellation;
            }

            try
            {
                return await _memories.RememberAsync(
                    user,
                    userText,
                    "conversation",
                    null,
                    reviewCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (reviewCancellation.IsCancellationRequested)
            {
                // A new foreground turn arrived. Retry only after conversation becomes
                // quiet again, or immediately if recall explicitly drains this review.
            }
            catch (Exception ex)
            {
                return MemoryOperationResult.Failed(
                    $"Mem0 could not review this user turn: {ex.Message}",
                    "background_review_unavailable");
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_runningBackgroundReview, reviewCancellation))
                    {
                        _runningBackgroundReview = null;
                    }
                }
            }
        }
    }

    private async Task WaitForReviewOpportunityAsync()
    {
        while (true)
        {
            Task stateChanged;
            TimeSpan remainingQuietTime;
            lock (_sync)
            {
                if (_forcedDrainCount > 0)
                {
                    return;
                }

                if (_foregroundTurnActive)
                {
                    stateChanged = _stateChanged.Task;
                    remainingQuietTime = Timeout.InfiniteTimeSpan;
                }
                else
                {
                    remainingQuietTime = _quietPeriod - (DateTimeOffset.UtcNow - _lastForegroundActivityUtc);
                    if (remainingQuietTime <= TimeSpan.Zero)
                    {
                        return;
                    }
                    stateChanged = _stateChanged.Task;
                }
            }

            if (remainingQuietTime == Timeout.InfiniteTimeSpan)
            {
                await stateChanged.ConfigureAwait(false);
            }
            else
            {
                await Task.WhenAny(stateChanged, Task.Delay(remainingQuietTime)).ConfigureAwait(false);
            }
        }
    }

    private void PulseStateChanged()
    {
        var completed = _stateChanged;
        _stateChanged = NewStateSignal();
        completed.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewStateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
