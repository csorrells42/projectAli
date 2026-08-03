namespace Ali.Modules.Orchestration.Completion;

internal enum AliCompletionCriticReviewStatus
{
    Prepared,
    Committed
}
internal sealed record AliCompletionCriticReviewState(
    AliCompletionCriticReviewIdentity Identity,
    AliCompletionCriticReviewStatus Status,
    AliCompletionCriticVerdict? Verdict)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Identity);
        Identity.Validate();
        if (!Enum.IsDefined(Status))
        {
            throw new InvalidDataException("The completion-critic review status is invalid.");
        }

        if ((Status == AliCompletionCriticReviewStatus.Prepared) != (Verdict is null))
        {
            throw new InvalidDataException(
                "A prepared critic review has no verdict and a committed review has exactly one.");
        }
    }
}

internal enum AliCompletionCriticReviewDisposition
{
    Start,
    ReuseCommitted,
    IdenticalReviewInProgress
}

internal sealed record AliCompletionCriticReviewLease(
    AliCompletionCriticReviewDisposition Disposition,
    AliCompletionCriticVerdict? CommittedVerdict,
    AliCompletionCriticStoreSnapshot StoreSnapshot);

/// <summary>
/// Durable, atomic critic-review boundary. Production implementations must bind Begin and Commit
/// to the authenticated turn journal. Begin must persist Prepared before returning Start, and an
/// identical prepared or committed identity must survive process restart without a second model
/// call.
/// </summary>
internal interface IAliCompletionCriticReviewStore
{
    ValueTask<AliCompletionCriticReviewLease> BeginAsync(
        AliCompletionCriticReviewIdentity identity,
        CancellationToken cancellationToken);

    ValueTask<AliCompletionCriticStoreSnapshot> CommitAsync(
        AliCompletionCriticReviewIdentity identity,
        AliCompletionCriticVerdict verdict,
        CancellationToken cancellationToken);
}

/// <summary>
/// Pure transition rules shared by the durable turn-journal adapter and focused tests. The rules
/// contain no process-local cache: persistence and compare-and-swap serialization belong to the
/// IAliCompletionCriticReviewStore implementation.
/// </summary>
internal static class AliCompletionCriticReviewStoreRules
{
    internal static AliCompletionCriticReviewDisposition ClassifyBegin(
        AliCompletionCriticReviewState? current,
        AliCompletionCriticReviewIdentity proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        proposed.Validate();
        current?.Validate();
        if (current is null
            || !string.Equals(
                current.Identity.Digest,
                proposed.Digest,
                StringComparison.Ordinal))
        {
            return AliCompletionCriticReviewDisposition.Start;
        }

        return current.Status switch
        {
            AliCompletionCriticReviewStatus.Prepared =>
                AliCompletionCriticReviewDisposition.IdenticalReviewInProgress,
            AliCompletionCriticReviewStatus.Committed =>
                AliCompletionCriticReviewDisposition.ReuseCommitted,
            _ => throw new InvalidDataException("The completion-critic review status is invalid.")
        };
    }

    internal static AliCompletionCriticReviewState Prepare(
        AliCompletionCriticReviewState? current,
        AliCompletionCriticReviewIdentity proposed)
    {
        if (ClassifyBegin(current, proposed) != AliCompletionCriticReviewDisposition.Start)
        {
            throw new InvalidDataException(
                "An identical critic review cannot be prepared more than once.");
        }

        return new AliCompletionCriticReviewState(
            proposed,
            AliCompletionCriticReviewStatus.Prepared,
            Verdict: null);
    }

    internal static AliCompletionCriticReviewState Commit(
        AliCompletionCriticReviewState current,
        AliCompletionCriticReviewIdentity identity,
        AliCompletionCriticVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(verdict);
        current.Validate();
        identity.Validate();
        if (current.Status != AliCompletionCriticReviewStatus.Prepared
            || !string.Equals(
                current.Identity.Digest,
                identity.Digest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A critic verdict can commit only to its exact prepared review identity.");
        }

        return current with
        {
            Status = AliCompletionCriticReviewStatus.Committed,
            Verdict = verdict
        };
    }
}
