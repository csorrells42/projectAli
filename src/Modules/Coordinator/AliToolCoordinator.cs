using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Ali.Modules.AgentWorkMemory;
using Ali.Modules.Coding;
using Ali.Modules.Evidence;
using Ali.Modules.WorkstationFiles;
using Ali.Modules.Identity;
using Ali.Modules.Internet;
using Ali.Modules.Memory;
using Ali.Modules.Mcp;
using Ali.Modules.Permissions;
using Ali.Modules.Reminders;
using Ali.Modules.Runtime;
using Ali.Modules.UserMemory;
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
    private readonly AliAgentHarnessRunner _harness;
    private readonly AsyncLocal<CoordinatorTurnContext?> _turn = new();
    private readonly IUserMemoryService? _userMemories;
    private readonly IActiveUserSession? _activeUsers;
    private readonly Func<UserMemorySettings>? _memorySettings;
    private readonly string _assistantName;

    public AliToolCoordinator(
        ILocalModelRuntime runtime,
        IChatClient chatClient,
        ISourceRetriever localLibrary,
        ISourceRetriever webSources,
        McpWebResearchClient webResearch,
        IMemoryStore memories,
        IReminderStore reminders,
        AssistantProfile assistantProfile,
        McpClientManager mcpClients,
        AgentToolPermissionStore toolPermissions,
        AliWorkstationFileAccess fileAccess,
        AliAgentWorkMemory workMemory,
        AliCodingModule? codingModule = null,
        IUserMemoryService? userMemories = null,
        IActiveUserSession? activeUsers = null,
        Func<UserMemorySettings>? memorySettings = null)
    {
        _assistantName = assistantProfile.Normalize().AssistantName;
        _userMemories = userMemories;
        _activeUsers = activeUsers;
        _memorySettings = memorySettings;
        codingModule ??= new AliCodingModule(fileAccess);
        var catalog = new AliToolCatalog(
            localLibrary,
            webSources,
            webResearch,
            memories,
            reminders,
            assistantProfile,
            mcpClients,
            toolPermissions,
            fileAccess,
            codingModule,
            () => _turn.Value,
            userMemories,
            activeUsers,
            memorySettings);
        _harness = new AliAgentHarnessRunner(
            chatClient,
            runtime,
            assistantProfile,
            catalog,
            mcpClients,
            toolPermissions,
            fileAccess,
            workMemory,
            activeUsers,
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
            attachments,
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
        IReadOnlyList<ChatAttachment> attachments,
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
            var learnedAnswer = new StringBuilder();
            var result = await _harness.RunAsync(
                turn,
                userText,
                history,
                attachments,
                chunk =>
                {
                    if (!chunk.IsActivity && !chunk.IsReasoning && !string.IsNullOrWhiteSpace(chunk.Text))
                    {
                        learnedAnswer.Append(chunk.Text);
                    }
                    writer.TryWrite(chunk);
                },
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

            QueueBackgroundLearning(turn, userText, learnedAnswer.ToString());

            turn.Report(AgentActivityKind.Complete, "Response complete", $"{_assistantName} finished the agent run.");
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

    private void QueueBackgroundLearning(CoordinatorTurnContext turn, string userText, string answer)
    {
        if (_userMemories is null || _activeUsers is null || _memorySettings is null)
        {
            return;
        }
        var settings = _memorySettings().Normalize();
        if (!settings.Enabled || !settings.AutomaticBackgroundLearning || string.IsNullOrWhiteSpace(answer))
        {
            return;
        }
        if (_activeUsers.RequiresSelection)
        {
            turn.Report(AgentActivityKind.Warning, "Background memory review skipped", "Select the active user profile before Ali stores personal memory.");
            return;
        }

        var user = _activeUsers.Current;
        var conversation = $"User: {userText.Trim()}\nAssistant: {answer.Trim()}";
        turn.Report(AgentActivityKind.Status, "Background memory review queued", "The visible answer is complete; durable-memory extraction will run at low effort.");
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await _userMemories.RememberAsync(user, conversation, "conversation", timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // Background learning is deliberately fail-safe and must never disturb a completed answer.
            }
        });
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

}
