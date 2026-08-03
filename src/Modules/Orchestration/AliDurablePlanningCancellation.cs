using Ali.Modules.Orchestration.State;

namespace Ali.Modules.Orchestration;

internal sealed partial class AliDurablePlanningTurn
{
    /// <summary>
    /// Records cancellation with a token independent of the caller's already-cancelled token.
    /// A turn with uncertain effects/publication remains CancelRequested until recovery observes
    /// those boundaries; a clean turn advances immediately to Cancelled.
    /// </summary>
    internal async Task<TurnControlState> RequestCancellationAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var latest = await _writer.ReadAsync(_identity, CancellationToken.None)
                .ConfigureAwait(false);
            if (latest is not null)
            {
                _state = latest;
            }

            if (_state.Control is TurnControlState.Completed or TurnControlState.Cancelled)
            {
                return _state.Control;
            }

            if (_state.Control != TurnControlState.CancelRequested)
            {
                var requested = await _writer.ChangeControlAsync(
                    _identity,
                    _state.Revision,
                    TurnControlState.CancelRequested,
                    "caller-cancelled",
                    AliPlanningStateCoordinator.Correlation(
                        _identity,
                        "cancel-requested",
                        _state.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        _state.Revision),
                    CancellationToken.None).ConfigureAwait(false);
                _state = RequireCommitted(requested, "cancellation request");
            }

            if (_state.PendingActions.Length == 0
                && _state.PendingAcceptedCall is null
                && _state.InterimPublication is null
                && _state.FinalPublication is null or { Status: FinalPublicationStatus.Committed })
            {
                var cancelled = await _writer.ChangeControlAsync(
                    _identity,
                    _state.Revision,
                    TurnControlState.Cancelled,
                    "clean-cancellation-boundary",
                    AliPlanningStateCoordinator.Correlation(
                        _identity,
                        "turn-cancelled",
                        _state.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        _state.Revision),
                    CancellationToken.None).ConfigureAwait(false);
                _state = RequireCommitted(cancelled, "turn cancellation");
            }

            return _state.Control;
        }
        finally
        {
            _gate.Release();
        }
    }
}
