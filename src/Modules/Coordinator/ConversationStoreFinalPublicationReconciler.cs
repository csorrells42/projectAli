using Ali.Modules.Conversation;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Observation-only recovery adapter for the desktop conversation store. It never writes or
/// republishes an answer; it only proves whether the exact assistant-message ID and digest are
/// already durable, definitely absent, or unsafe to classify.
/// </summary>
internal sealed class ConversationStoreFinalPublicationReconciler(
    IConversationPublicationProbe probe) : ITurnPublicationReconciler
{
    private readonly IConversationPublicationProbe _probe =
        probe ?? throw new ArgumentNullException(nameof(probe));

    public ValueTask<PublicationReconciliationResult> ReconcileAsync(
        TurnIdentity identity,
        FinalPublicationState publication,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(publication);
        cancellationToken.ThrowIfCancellationRequested();
        var observed = _probe.ProbeAssistantPublication(
            identity.ConversationId,
            publication.AssistantMessageId,
            publication.AnswerDigest);
        var disposition = observed.Status switch
        {
            ConversationPublicationProbeStatus.Present =>
                PublicationReconciliationDisposition.Applied,
            ConversationPublicationProbeStatus.Absent =>
                PublicationReconciliationDisposition.Absent,
            ConversationPublicationProbeStatus.Mismatch or
                ConversationPublicationProbeStatus.Unavailable =>
                PublicationReconciliationDisposition.Unknown,
            _ => PublicationReconciliationDisposition.Unknown
        };
        return ValueTask.FromResult(new PublicationReconciliationResult(
            disposition,
            observed.OutcomeCode));
    }
}
