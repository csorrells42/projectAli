using Microsoft.Extensions.AI;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;

namespace Ali.Modules.Orchestration.Planning;

public sealed record TemporaryCompletionRequest
{
    public TemporaryCompletionRequest(
        string immutableOriginalRequest,
        AliPlanningTurnInput authoritativeInput,
        OrchestrationDecision acceptedDecision,
        ChatResponse plannerResponse,
        IEnumerable<WorkNode> requiredOutcomes,
        IEnumerable<OrchestrationMaterialClaim> requiredClaims,
        IEnumerable<AcceptedEvidenceProjection> citedEvidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(immutableOriginalRequest);
        ImmutableOriginalRequest = immutableOriginalRequest;
        AuthoritativeInput = authoritativeInput
            ?? throw new ArgumentNullException(nameof(authoritativeInput));
        AcceptedDecision = acceptedDecision
            ?? throw new ArgumentNullException(nameof(acceptedDecision));
        PlannerResponse = plannerResponse
            ?? throw new ArgumentNullException(nameof(plannerResponse));
        RequiredOutcomes = Array.AsReadOnly(
            (requiredOutcomes ?? throw new ArgumentNullException(nameof(requiredOutcomes)))
            .ToArray());
        RequiredClaims = Array.AsReadOnly(
            (requiredClaims ?? throw new ArgumentNullException(nameof(requiredClaims)))
            .ToArray());
        CitedEvidence = Array.AsReadOnly(
            (citedEvidence ?? throw new ArgumentNullException(nameof(citedEvidence)))
            .ToArray());
    }

    public string ImmutableOriginalRequest { get; }

    public AliPlanningTurnInput AuthoritativeInput { get; }

    public OrchestrationDecision AcceptedDecision { get; }

    public ChatResponse PlannerResponse { get; }

    public IReadOnlyList<WorkNode> RequiredOutcomes { get; }

    public IReadOnlyList<OrchestrationMaterialClaim> RequiredClaims { get; }

    public IReadOnlyList<AcceptedEvidenceProjection> CitedEvidence { get; }
}

internal enum TemporaryCompletionFailureKind
{
    CompletionInputNotAdmitted,
    CompletionDispatchBindingsChanged,
    CompletionOutputIncomplete
}

internal sealed record TemporaryCompletionFailure(
    TemporaryCompletionFailureKind Kind,
    string UserVisibleMessage,
    IReadOnlyList<string>? ChangedBindings = null);

internal sealed record TemporaryCompletionAttempt(
    ChatResponse? Response,
    TemporaryCompletionFailure? Failure,
    AliCommittedAnswerDraft? CommittedDraft = null)
{
    internal bool IsSuccessful => Response is not null && Failure is null;

    internal static TemporaryCompletionAttempt Candidate(
        ChatResponse response,
        AliCommittedAnswerDraft? committedDraft = null) =>
        new(
            response ?? throw new ArgumentNullException(nameof(response)),
            Failure: null,
            committedDraft);

    internal static TemporaryCompletionAttempt Failed(TemporaryCompletionFailure failure) =>
        new(
            Response: null,
            Failure: failure ?? throw new ArgumentNullException(nameof(failure)),
            CommittedDraft: null);
}

internal delegate ValueTask<TemporaryCompletionAttempt> TemporaryCompletionComposerDelegate(
    TemporaryCompletionRequest request,
    CancellationToken cancellationToken);

/// <summary>
/// Explicit migration seam for the existing evidence-aware answer composer. It is temporary by
/// name and cannot be confused with planner output: BeginCompletion carries proof bindings, not
/// user-facing answer prose.
/// </summary>
public sealed class TemporaryCompletionBridge
{
    private readonly TemporaryCompletionComposerDelegate _compose;

    public TemporaryCompletionBridge(
        Func<TemporaryCompletionRequest, CancellationToken, ValueTask<ChatResponse>> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        _compose = async (request, cancellationToken) => TemporaryCompletionAttempt.Candidate(
            await compose(request, cancellationToken).ConfigureAwait(false));
    }

    private TemporaryCompletionBridge(TemporaryCompletionComposerDelegate compose)
    {
        _compose = compose ?? throw new ArgumentNullException(nameof(compose));
    }

    internal static TemporaryCompletionBridge FromComposer(
        TemporaryCompletionComposerDelegate compose) =>
        new(compose);

    internal async ValueTask<TemporaryCompletionAttempt> CompleteAsync(
        TemporaryCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var attempt = await _compose(request, cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            throw new InvalidOperationException("The completion composer returned no attempt.");
        }

        if (attempt.Failure is not null)
        {
            if (attempt.Response is not null
                || attempt.CommittedDraft is not null
                || string.IsNullOrWhiteSpace(attempt.Failure.UserVisibleMessage))
            {
                throw new InvalidOperationException(
                    "A failed completion attempt must contain one safe message and no response.");
            }

            return attempt;
        }

        var response = attempt.Response
            ?? throw new InvalidOperationException(
                "A successful completion attempt returned no response.");
        var explicitlyComplete = response.FinishReason == ChatFinishReason.Stop;
        if (explicitlyComplete && !string.IsNullOrWhiteSpace(response.Text))
        {
            if (attempt.CommittedDraft is { } committed
                && (!string.Equals(response.Text, committed.Text, StringComparison.Ordinal)
                    || !string.Equals(
                        committed.AnswerDigest,
                        TurnStateIntegrity.Digest(committed.Text),
                        StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "The completion response does not match its exact committed answer draft.");
            }

            return attempt;
        }

        return TemporaryCompletionAttempt.Failed(new TemporaryCompletionFailure(
            TemporaryCompletionFailureKind.CompletionOutputIncomplete,
            "Ali paused because the answer composer did not return a complete, nonempty final "
            + "answer. The immutable request, accepted work graph, and accepted evidence remain "
            + "durably preserved. No final answer was prepared or published; any partial composer "
            + "output was discarded and was not journaled."));
    }
}
