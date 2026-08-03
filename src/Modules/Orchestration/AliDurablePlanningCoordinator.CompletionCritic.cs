using System.Text.Json.Nodes;
using Ali.Modules.Orchestration.Completion;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.Orchestration;

/// <summary>
/// Authenticated turn-journal adapter for completion-critic review identities and verdicts. The
/// shared durable-turn gate makes Begin a write-ahead compare-and-swap boundary: a process restart
/// reconstructs Prepared or Committed from the journal before any caller can request another model
/// review.
/// </summary>
internal sealed partial class AliDurablePlanningTurn : IAliCompletionCriticReviewStore
{
    async ValueTask<AliCompletionCriticReviewLease>
        IAliCompletionCriticReviewStore.BeginAsync(
            AliCompletionCriticReviewIdentity identity,
            CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.CompletionCriticReview is { } existing
                && string.Equals(
                    existing.ReviewIdentityDigest,
                    identity.Digest,
                    StringComparison.Ordinal))
            {
                RequireEquivalentStoredMaterial(existing, identity);
                var disposition = existing.Status switch
                {
                    CompletionCriticReviewPersistenceStatus.Prepared =>
                        AliCompletionCriticReviewDisposition.IdenticalReviewInProgress,
                    CompletionCriticReviewPersistenceStatus.Committed =>
                        AliCompletionCriticReviewDisposition.ReuseCommitted,
                    _ => throw new InvalidDataException(
                        "The durable completion-critic status is invalid.")
                };
                return new AliCompletionCriticReviewLease(
                    disposition,
                    disposition == AliCompletionCriticReviewDisposition.ReuseCommitted
                        ? StoredVerdict(existing)
                        : null,
                    Snapshot());
            }

            if (identity.SourceStateRevision != _state.Revision)
            {
                throw new InvalidDataException(
                    "The completion critic tried to prepare against a stale durable state revision.");
            }

            var prepared = await _writer.WriteAsync(
                    _identity,
                    _state.Revision,
                    new CompletionCriticReviewPreparedTransition(
                        AliPlanningStateCoordinator.Correlation(
                            _identity,
                            "completion-critic-review-prepared",
                            identity.Digest,
                            _state.Revision),
                        identity.Digest,
                        identity.SourceStateRevision,
                        identity.AnswerId,
                        identity.AnswerDigest,
                        identity.RenderedPageHashes.ToArray(),
                        identity.RuntimeDigest,
                        identity.ModelDigest,
                        identity.GenerationSettingsDigest,
                        identity.ReasoningEffort),
                    cancellationToken)
                .ConfigureAwait(false);
            _state = RequireCommitted(prepared, "completion-critic preparation");
            return new AliCompletionCriticReviewLease(
                AliCompletionCriticReviewDisposition.Start,
                CommittedVerdict: null,
                Snapshot());
        }
        finally
        {
            _gate.Release();
        }
    }

    async ValueTask<AliCompletionCriticStoreSnapshot>
        IAliCompletionCriticReviewStore.CommitAsync(
            AliCompletionCriticReviewIdentity identity,
            AliCompletionCriticVerdict verdict,
            CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(verdict);
        identity.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = _state.CompletionCriticReview
                ?? throw new InvalidDataException(
                    "The durable turn has no prepared completion-critic identity.");
            RequireExactStoredIdentity(existing, identity);
            if (existing.Status != CompletionCriticReviewPersistenceStatus.Prepared)
            {
                throw new InvalidDataException(
                    "The durable completion-critic verdict is already committed.");
            }

            var committed = await _writer.WriteAsync(
                    _identity,
                    _state.Revision,
                    new CompletionCriticVerdictCommittedTransition(
                        AliPlanningStateCoordinator.Correlation(
                            _identity,
                            "completion-critic-verdict-committed",
                            identity.Digest,
                            _state.Revision),
                        identity.Digest,
                        verdict.Complete,
                        verdict.Basis,
                        verdict.MaterialUnmetOutcomes.ToArray()),
                    cancellationToken)
                .ConfigureAwait(false);
            _state = RequireCommitted(committed, "completion-critic verdict");
            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    private AliCompletionCriticStoreSnapshot Snapshot() =>
        new(_state.Revision, BuildStateProjection());

    private JsonNode? BuildCompletionCriticStateProjection()
    {
        var review = _state.CompletionCriticReview;
        if (review is null)
        {
            return null;
        }

        var unmet = new JsonArray();
        if (review.Status == CompletionCriticReviewPersistenceStatus.Committed)
        {
            foreach (var outcome in review.MaterialUnmetOutcomes)
            {
                unmet.Add(outcome);
            }
        }

        return new JsonObject
        {
            ["reviewIdentityDigest"] = review.ReviewIdentityDigest,
            ["sourceStateRevision"] = review.SourceStateRevision,
            ["answerId"] = review.AnswerId,
            ["answerDigest"] = review.AnswerDigest,
            ["status"] = review.Status.ToString(),
            ["complete"] = review.Status == CompletionCriticReviewPersistenceStatus.Committed
                ? JsonValue.Create(review.Complete!.Value)
                : null,
            ["materialUnmetOutcomes"] = unmet,
            ["notice"] = "Typed critic outcomes are planner state, not evidence; critic basis prose is not projected."
        };
    }

    private static AliCompletionCriticVerdict StoredVerdict(
        CompletionCriticReviewPersistenceState state)
    {
        if (state.Status != CompletionCriticReviewPersistenceStatus.Committed
            || state.Complete is null
            || state.Basis is null)
        {
            throw new InvalidDataException(
                "The durable completion-critic receipt has no committed verdict.");
        }

        return new AliCompletionCriticVerdict(
            state.Complete.Value,
            state.Basis,
            state.MaterialUnmetOutcomes);
    }

    private static void RequireExactStoredIdentity(
        CompletionCriticReviewPersistenceState stored,
        AliCompletionCriticReviewIdentity identity)
    {
        stored.Validate();
        if (!string.Equals(stored.ReviewIdentityDigest, identity.Digest, StringComparison.Ordinal)
            || stored.SourceStateRevision != identity.SourceStateRevision
            || !HasEquivalentStoredMaterial(stored, identity))
        {
            throw new InvalidDataException(
                "A completion-critic digest cannot be rebound to different state, pages, or runtime settings.");
        }
    }

    private static void RequireEquivalentStoredMaterial(
        CompletionCriticReviewPersistenceState stored,
        AliCompletionCriticReviewIdentity identity)
    {
        stored.Validate();
        if (!string.Equals(stored.ReviewIdentityDigest, identity.Digest, StringComparison.Ordinal)
            || !HasEquivalentStoredMaterial(stored, identity))
        {
            throw new InvalidDataException(
                "A completion-critic material digest cannot be rebound to different answer, pages, or runtime settings.");
        }
    }

    private static bool HasEquivalentStoredMaterial(
        CompletionCriticReviewPersistenceState stored,
        AliCompletionCriticReviewIdentity identity) =>
        string.Equals(stored.AnswerId, identity.AnswerId, StringComparison.Ordinal)
            && string.Equals(stored.AnswerDigest, identity.AnswerDigest, StringComparison.Ordinal)
            && stored.RenderedPageHashes.SequenceEqual(
                identity.RenderedPageHashes,
                StringComparer.Ordinal)
            && string.Equals(stored.RuntimeDigest, identity.RuntimeDigest, StringComparison.Ordinal)
            && string.Equals(stored.ModelDigest, identity.ModelDigest, StringComparison.Ordinal)
            && string.Equals(
                stored.GenerationSettingsDigest,
                identity.GenerationSettingsDigest,
                StringComparison.Ordinal)
            && string.Equals(
                stored.ReasoningEffort,
                identity.ReasoningEffort,
                StringComparison.Ordinal);
}
