using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Ali.Modules.Evidence;
using Ali.Modules.Identity;
using Ali.Modules.Internet;
using Ali.Modules.Memory;
using Ali.Modules.Reminders;
using Ali.Modules.Runtime;
using Microsoft.Extensions.AI;
using RuntimeChatMessage = Ali.Modules.Runtime.ChatMessage;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Thin boundary between Ali's conversation stream, immutable capability modules, and the
/// Agent Framework runner. English interpretation belongs to the model and harness.
/// </summary>
public sealed class AliToolCoordinator
{
    private const int MaximumVisibleSources = 5;
    private readonly ILocalModelRuntime _runtime;
    private readonly AliAgentHarnessRunner _harness;
    private readonly AsyncLocal<CoordinatorTurnContext?> _turn = new();

    public AliToolCoordinator(
        ILocalModelRuntime runtime,
        IChatClient chatClient,
        ISourceRetriever localLibrary,
        ISourceRetriever webSources,
        IMemoryStore memories,
        IReminderStore reminders,
        AssistantProfile assistantProfile)
    {
        _runtime = runtime;
        var catalog = new AliToolCatalog(
            localLibrary,
            webSources,
            memories,
            reminders,
            assistantProfile,
            () => _turn.Value);
        _harness = new AliAgentHarnessRunner(
            chatClient,
            runtime,
            assistantProfile,
            catalog,
            () => _turn.Value);
    }

    public bool ResolveToolApproval(AgentToolApprovalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return _harness.ResolveToolApproval(decision);
    }

    public async IAsyncEnumerable<AssistantStreamChunk> StreamAnswerAsync(
        string conversationId,
        string userMessageId,
        string assistantMessageId,
        string userText,
        IReadOnlyList<RuntimeChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);

        if (attachments.Count > 0)
        {
            yield return Activity(
                conversationId,
                userMessageId,
                assistantMessageId,
                AgentActivityKind.Status,
                "Inspecting the attached image locally");
            var request = new ChatRequest(conversationId, userMessageId, userText, history)
            {
                Attachments = attachments
            };
            await foreach (var token in _runtime.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (!token.IsThinking)
                {
                    yield return new AssistantStreamChunk(
                        conversationId,
                        userMessageId,
                        assistantMessageId,
                        token.Text,
                        token.EvidenceStatus,
                        token.FinishReason);
                }
            }

            yield break;
        }

        var channel = Channel.CreateUnbounded<AssistantStreamChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        var producer = ProduceAgentTurnAsync(
            conversationId,
            userMessageId,
            assistantMessageId,
            userText,
            history,
            channel.Writer,
            cancellationToken);

        await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }

        await producer.ConfigureAwait(false);
    }

    private async Task ProduceAgentTurnAsync(
        string conversationId,
        string userMessageId,
        string assistantMessageId,
        string userText,
        IReadOnlyList<RuntimeChatMessage> history,
        ChannelWriter<AssistantStreamChunk> writer,
        CancellationToken cancellationToken)
    {
        var turn = new CoordinatorTurnContext(
            conversationId,
            userMessageId,
            assistantMessageId,
            userText,
            chunk => writer.TryWrite(chunk));
        _turn.Value = turn;
        try
        {
            var result = await _harness.RunAsync(
                turn,
                userText,
                history,
                chunk => writer.TryWrite(chunk),
                cancellationToken).ConfigureAwait(false);
            PublishSourceAppendix(turn, result.FinishReason, writer);
            if (!result.WroteAnswer)
            {
                writer.TryWrite(new AssistantStreamChunk(
                    conversationId,
                    userMessageId,
                    assistantMessageId,
                    "I could not complete that answer from the available local tools and model response.",
                    EvidenceStatus.Unverified,
                    result.FinishReason));
            }

            turn.Report(AgentActivityKind.Complete, "Response complete", "Ali finished the agent run.");
            writer.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            turn.Report(AgentActivityKind.Error, "Agent run failed safely", ex.Message);
            writer.TryComplete(ex);
        }
        finally
        {
            _turn.Value = null;
        }
    }

    private static void PublishSourceAppendix(
        CoordinatorTurnContext turn,
        string? finishReason,
        ChannelWriter<AssistantStreamChunk> writer)
    {
        var usableSources = turn.WebSources
            .Where(source => Uri.TryCreate(source.Url, UriKind.Absolute, out var uri)
                && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(source => source.Url, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumVisibleSources)
            .ToList();
        if (usableSources.Count == 0)
        {
            return;
        }

        var appendix = new StringBuilder()
            .AppendLine()
            .AppendLine()
            .AppendLine("Sources checked:");
        foreach (var source in usableSources)
        {
            var safeName = source.Name.Replace('[', '(').Replace(']', ')').Trim();
            appendix.Append("- [")
                .Append(string.IsNullOrWhiteSpace(safeName) ? source.Url : safeName)
                .Append("](")
                .Append(source.Url)
                .AppendLine(")");
        }

        writer.TryWrite(new AssistantStreamChunk(
            turn.ConversationId,
            turn.UserMessageId,
            turn.AssistantMessageId,
            appendix.ToString().TrimEnd(),
            EvidenceStatus.Verified,
            finishReason));
    }

    private static AssistantStreamChunk Activity(
        string conversationId,
        string userMessageId,
        string assistantMessageId,
        AgentActivityKind kind,
        string text,
        string? detail = null) =>
        new(
            conversationId,
            userMessageId,
            assistantMessageId,
            text,
            EvidenceStatus.Unknown,
            IsActivity: true,
            ActivityKind: kind,
            ActivityDetail: detail);
}
