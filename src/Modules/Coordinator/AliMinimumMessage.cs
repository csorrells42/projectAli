using System.Text;
using Ali.Modules.Evidence;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Ali's release-candidate execution path.
///
/// One request enters. The model may call an already-loaded tool, receives that
/// exact result, and continues until it returns an answer. No recovery journal,
/// receipt replay, memory mutation, semantic reload, critic, or durable pause is
/// permitted in this class. Attachments are already part of <paramref name="input"/>.
/// Workspace enforcement remains inside the tool implementations.
/// </summary>
internal sealed class AliMinimumMessage
{
    private const int MaximumToolResults = 64;
    private const int MaximumBlockedRetries = 3;
    private const int MaximumEmptyResponseRetries = 2;

    // The gate's unconditional post-mutation run check would force a launch after
    // every single source edit, even when nobody asked for one. A model-chosen run
    // that then fails still blocks via run-failed, and an explicit stop still blocks
    // via the separate run-stopped code; only this specific proactive-run-after-edit
    // demand is skipped here to avoid manufacturing busywork on a personal desktop.
    private const string SkippedBlockerCode = "run-missing-or-stale";

    internal async Task<AgentHarnessRunResult> RunAsync(
        CoordinatorTurnContext turn,
        AIAgent agent,
        IReadOnlyList<ChatMessage> input,
        Func<FinalAnswerPublication, CancellationToken,
            ValueTask<FinalAnswerPublicationAcknowledgment>> publishFinal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(publishFinal);

        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var gate = new CoreAssistantCompletionGate();
        var answer = new StringBuilder();
        var completedToolResults = 0;
        var blockedRetries = 0;
        var emptyResponseRetries = 0;
        var separateNextModelMessage = false;
        string? finishReason = null;
        var nextInput = input;

        while (completedToolResults < MaximumToolResults)
        {
            var toolResultsBeforeBurst = completedToolResults;
            await foreach (var update in agent.RunStreamingAsync(
                               nextInput,
                               session,
                               options: null,
                               cancellationToken).ConfigureAwait(false))
            {
                finishReason = update.FinishReason?.ToString() ?? finishReason;
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case FunctionCallContent call when !call.InformationalOnly:
                            gate.Track(call);
                            turn.Report(
                                AgentActivityKind.ToolCall,
                                $"Using {call.Name}",
                                "The model selected the next required action.");
                            break;

                        case FunctionResultContent result:
                            completedToolResults++;
                            separateNextModelMessage = answer.Length > 0;
                            gate.Observe(result);

                            if (result.Exception is not null)
                            {
                                turn.Report(
                                    AgentActivityKind.Error,
                                    "Tool returned an error",
                                    result.Exception.GetBaseException().Message);
                            }
                            break;

                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            if (separateNextModelMessage)
                            {
                                // A single line break reads as a continuation, not a new
                                // block, in the chat display; a blank line is what actually
                                // separates this fresh segment from what came before it.
                                answer.Append(Environment.NewLine + Environment.NewLine);
                                turn.PublishResponseText(Environment.NewLine + Environment.NewLine);
                                separateNextModelMessage = false;
                            }
                            answer.Append(text.Text);
                            turn.PublishResponseText(text.Text);
                            break;
                    }
                }
            }

            if (answer.Length > 0)
            {
                if (gate.TryGetBlocker(out var blocker)
                    && !string.Equals(blocker.Code, SkippedBlockerCode, StringComparison.Ordinal))
                {
                    blockedRetries++;
                    if (blockedRetries > MaximumBlockedRetries)
                    {
                        answer.Clear();
                        answer.Append(blocker.TruthfulFailureAnswer);
                        turn.PublishResponseText(Environment.NewLine + blocker.TruthfulFailureAnswer);
                        break;
                    }

                    turn.Report(
                        AgentActivityKind.Warning,
                        "Completion not yet verified",
                        blocker.ContinuationInstruction);
                    // The live stream already showed the rejected attempt's text and
                    // is never cleared (unlike the internal answer buffer below), so
                    // the next attempt needs its own visible break or it runs directly
                    // into the discarded text with no separator at all.
                    turn.PublishResponseText(Environment.NewLine + Environment.NewLine);
                    answer.Clear();
                    separateNextModelMessage = false;
                    nextInput =
                    [
                        new ChatMessage(ChatRole.User, blocker.ContinuationInstruction)
                    ];
                    continue;
                }

                break;
            }

            if (completedToolResults == toolResultsBeforeBurst)
            {
                emptyResponseRetries++;
                if (emptyResponseRetries > MaximumEmptyResponseRetries)
                {
                    throw new InvalidOperationException(
                        "The model returned neither a final answer nor a tool result.");
                }

                turn.Report(
                    AgentActivityKind.Warning,
                    "Empty model response",
                    "The model returned neither a tool call nor a final answer; asking it to continue.");
                nextInput =
                [
                    new ChatMessage(
                        ChatRole.User,
                        "Your previous response contained neither a tool call nor a final answer. Choose an available tool for the next concrete step, or give the truthful final answer now.")
                ];
                continue;
            }

            emptyResponseRetries = 0;
            nextInput =
            [
                new ChatMessage(
                    ChatRole.User,
                    "Continue from the new tool result already in this session. Choose the next advancing action or return the truthful final answer.")
            ];
        }

        if (answer.Length == 0)
        {
            throw new InvalidOperationException(
                $"The model used {MaximumToolResults} tool results without returning a final answer. Ali stopped instead of running indefinitely.");
        }

        var exactAnswer = FinalAnswerRenderer.Compose(answer.ToString(), turn.WebSources);
        finishReason = ChatFinishReason.Stop.ToString();
        var publication = new FinalAnswerPublication(
            turn.ConversationId,
            turn.UserMessageId,
            turn.AssistantMessageId,
            "publication_" + turn.AssistantMessageId,
            exactAnswer,
            TurnStateIntegrity.Digest(exactAnswer),
            turn.UsedEvidenceTool ? EvidenceStatus.Verified : EvidenceStatus.Unverified,
            finishReason);
        var acknowledgment = await publishFinal(publication, cancellationToken)
            .ConfigureAwait(false);
        FinalAnswerPublicationBoundary.RequireExactInMemoryAcknowledgment(
            publication,
            acknowledgment);

        return new AgentHarnessRunResult(
            WroteAnswer: true,
            FinishReason: finishReason,
            Paused: false,
            ResumeIdentity: null,
            CompletedSuccessfully: true);
    }
}
