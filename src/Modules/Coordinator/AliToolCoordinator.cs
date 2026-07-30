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
    private readonly IActiveUserSession? _activeUsers;
    private readonly Func<UserMemorySettings>? _memorySettings;
    private readonly AliUserMemoryReviewQueue? _memoryReviewQueue;
    private readonly string _assistantName;

    public event Action<AssistantStreamChunk>? BackgroundActivity;

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
        Func<UserMemorySettings>? memorySettings = null,
        string? workflowCheckpointPath = null,
        Func<AgentOrchestrationSettings>? orchestrationSettings = null)
    {
        _assistantName = assistantProfile.Normalize().AssistantName;
        _activeUsers = activeUsers;
        _memorySettings = memorySettings;
        _memoryReviewQueue = userMemories is null ? null : new AliUserMemoryReviewQueue(userMemories);
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
            memorySettings,
            orchestrationSettings,
            _memoryReviewQueue is null
                ? null
                : cancellationToken => _memoryReviewQueue.DrainAsync(cancellationToken));
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
            () => _turn.Value,
            workflowCheckpointPath ?? Path.Combine(Path.GetTempPath(), "ProjectAli", "WorkflowCheckpoints"),
            orchestrationSettings ?? (() => new AgentOrchestrationSettings()));
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
        _memoryReviewQueue?.BeginForegroundTurn();
        try
        {
            var result = await _harness.RunAsync(
                turn,
                userText,
                history,
                attachments,
                chunk =>
                {
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

            QueueIncomingUserMemoryReview(turn, userText);

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
            _memoryReviewQueue?.EndForegroundTurn();
            _turn.Value = null;
        }
    }

    private void QueueIncomingUserMemoryReview(
        CoordinatorTurnContext turn,
        string userText)
    {
        if (_memoryReviewQueue is null || _activeUsers is null || _memorySettings is null)
        {
            return;
        }

        var settings = _memorySettings().Normalize();
        if (!settings.Enabled)
        {
            return;
        }

        if (_activeUsers.RequiresSelection)
        {
            turn.Report(
                AgentActivityKind.Warning,
                "Personal memory review skipped",
                "Select the active user profile before Mem0 reviews incoming text.");
            return;
        }

        var review = _memoryReviewQueue.Enqueue(_activeUsers.Current, userText);
        _ = PublishMemoryReviewOutcomeAsync(turn, review);
        turn.Report(
            AgentActivityKind.Status,
            "Personal memory review queued",
            "The answer is available. Mem0 will ask the configured local model whether this user turn contains durable personal information.");
    }

    private async Task PublishMemoryReviewOutcomeAsync(
        CoordinatorTurnContext turn,
        Task<MemoryOperationResult> review)
    {
        MemoryOperationResult result;
        try
        {
            result = await review.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = MemoryOperationResult.Failed(
                $"Mem0 review failed safely: {ex.Message}",
                "background_review_failed");
        }

        var count = result.Memories.Count;
        var title = !result.Success
            ? "Mem0 review failed; no memory change was confirmed."
            : count == 0
                ? "Mem0 found nothing worth keeping from that turn."
                : $"Mem0 found {count} durable memory change{(count == 1 ? string.Empty : "s")}.";
        var changed = count == 0
            ? string.Empty
            : " Changes: " + string.Join(" | ", result.Memories.Take(3).Select(memory => memory.Text));
        BackgroundActivity?.Invoke(new AssistantStreamChunk(
            turn.ConversationId,
            turn.UserMessageId,
            turn.AssistantMessageId,
            title,
            Ali.Modules.Evidence.EvidenceStatus.Unknown,
            IsActivity: true,
            ActivityKind: result.Success ? AgentActivityKind.Status : AgentActivityKind.Warning,
            ActivityDetail: $"{result.Message}{changed}"));
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
