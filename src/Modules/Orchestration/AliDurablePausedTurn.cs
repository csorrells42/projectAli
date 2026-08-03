using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.Orchestration;

internal sealed partial class AliPlanningStateCoordinator
{
    internal async Task RecordRecoverableTurnAsync(
        TurnIdentity identity,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        var state = await _transitionWriter.ReadAsync(identity, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "A paused-turn index cannot reference a missing durable turn.");
        if (state.Control is TurnControlState.Completed or TurnControlState.Cancelled)
        {
            throw new InvalidDataException(
                "A terminal turn cannot enter the active recoverable-turn index.");
        }

        await _pausedTurnCatalog.RecordAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<TurnIdentity?> FindPausedTurnAsync(
        CoordinatorTurnContext visibleTurn,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(visibleTurn);
        var userId = visibleTurn.ObservationIdentity?.UserId ?? UnresolvedLocalUserId;
        var identity = await _pausedTurnCatalog.FindAsync(
                userId,
                visibleTurn.ConversationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
        {
            return null;
        }

        var state = await _transitionWriter.ReadAsync(identity, cancellationToken)
            .ConfigureAwait(false)
            ;
        if (state is null)
        {
            // Crash between catalog admission and the initial state commit. The exact entry is
            // authenticated but contains no work/effect to recover, so remove only that entry.
            await _pausedTurnCatalog.RemoveExactAsync(identity, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        if (state.Control is TurnControlState.Completed or TurnControlState.Cancelled)
        {
            await _pausedTurnCatalog.RemoveExactAsync(identity, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        return identity;
    }

    internal async Task ClearRecoverableTurnAsync(
        TurnIdentity identity,
        CancellationToken cancellationToken)
    {
        var state = await _transitionWriter.ReadAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        if (state is not null
            && state.Control is not (TurnControlState.Completed or TurnControlState.Cancelled))
        {
            throw new InvalidOperationException(
                "An active recoverable-turn entry can be removed only after terminal reconciliation.");
        }

        await _pausedTurnCatalog.RemoveExactAsync(identity, cancellationToken).ConfigureAwait(false);
    }
}
