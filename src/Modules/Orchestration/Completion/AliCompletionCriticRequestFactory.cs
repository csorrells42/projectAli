using Ali.Modules.Orchestration.Planning;

namespace Ali.Modules.Orchestration.Completion;

/// <summary>
/// Narrow integration seam from the accepted completion dossier to the critic contract. It copies
/// typed state only; unresolved-issue and capability relevance remain explicit caller inputs and
/// are never inferred from user prose.
/// </summary>
internal static class AliCompletionCriticRequestFactory
{
    internal static AliCompletionCriticRequest Create(
        TemporaryCompletionRequest completion,
        AliCommittedAnswerDraft committedDraft,
        IEnumerable<AliCompletionCriticUnresolvedIssue>? unresolvedIssues,
        IEnumerable<AliCompletionCriticCapability>? relevantCapabilities)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(committedDraft);
        var plan = completion.AcceptedDecision.NextAction as BeginCompletionAction
            ?? throw new InvalidDataException(
                "A completion critic request requires an accepted BeginCompletion action.");
        var graph = completion.AuthoritativeInput.AuthoritativeWorkGraph
            ?? throw new InvalidDataException(
                "A state-native completion critic request requires the authoritative work graph.");
        return new AliCompletionCriticRequest(
            completion.AuthoritativeInput.StateRevision,
            completion.ImmutableOriginalRequest,
            completion.AuthoritativeInput.AcceptedPriorConversation
                .Where(static entry => entry.IsSteering)
                .Select(static entry =>
                    new AliCompletionCriticSteeringConstraint(entry.Sequence, entry.Text)),
            plan.Plan,
            committedDraft,
            graph,
            completion.RequiredClaims.Select(static claim =>
                new AliCompletionCriticClaimCoverage(
                    claim.ClaimId,
                    claim.Statement,
                    claim.Kind,
                    claim.EvidenceIds)),
            completion.CitedEvidence,
            unresolvedIssues,
            relevantCapabilities);
    }
}
