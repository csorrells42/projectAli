using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Ali.Core.Coding;
using Ali.Core.Evidence;
using Ali.Core.Feedback;
using Ali.Core.Memory;
using Ali.Core.Permissions;
using Ali.Core.Runtime;
using Ali.Core.Sources;

namespace Ali.Core.Orchestration;

public sealed record AssistantStreamChunk(
    string ConversationId,
    string UserMessageId,
    string AssistantMessageId,
    string Text,
    EvidenceStatus EvidenceStatus,
    string? FinishReason = null)
{
    public bool ReachedOutputLimit =>
        string.Equals(FinishReason, "length", StringComparison.OrdinalIgnoreCase);
}

public sealed class ConversationOrchestrator(
    ILocalModelRuntime runtime,
    PermissionService permissionService,
    CorrectionQueueService correctionQueue,
    ISourceRetriever? sourceRetriever = null,
    ISourceQueryPlanner? sourceQueryPlanner = null,
    IMemoryStore? memoryStore = null,
    ILocalCodingTool? localCodingTool = null)
{
    private const int MaxPromptMemories = 20;
    private static readonly char[] MemoryRelevanceTokenSeparators =
        [' ', ',', '.', '?', '!', ':', ';', '/', '\\', '-', '_', '(', ')', '[', ']', '"', '\''];
    private static readonly HashSet<string> MemoryRelevanceTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "here",
        "local",
        "location",
        "located",
        "memory",
        "near",
        "nearby",
        "remember",
        "remembered",
        "weather",
        "where"
    };
    private static readonly HashSet<string> MemoryRelevanceIntents = new(StringComparer.OrdinalIgnoreCase)
    {
        "weather",
        "local_app"
    };
    private static readonly Regex SourcesCheckedRegex = new(
        @"(?:\r?\n){0,2}\s*Sources checked:\s*.*\z",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    public ILocalModelRuntime Runtime { get; } = runtime;

    public PermissionService Permissions { get; } = permissionService;

    public CorrectionQueueService Corrections { get; } = correctionQueue;

    public ISourceRetriever Sources { get; } = sourceRetriever ?? new NoOpSourceRetriever();

    public ISourceQueryPlanner SourcePlanner { get; } = sourceQueryPlanner ?? new ModelSourceQueryPlanner(runtime);

    public IMemoryStore? Memories { get; } = memoryStore;

    public ILocalCodingTool? LocalCodingTool { get; } = localCodingTool;

    public async IAsyncEnumerable<AssistantStreamChunk> StreamAnswerAsync(
        string conversationId,
        string userMessageId,
        string assistantMessageId,
        string userText,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);

        if (LocalCodingTool is not null)
        {
            var codingResult = await LocalCodingTool.TryHandleAsync(userText, cancellationToken).ConfigureAwait(false);
            if (codingResult.Handled)
            {
                yield return new AssistantStreamChunk(
                    conversationId,
                    userMessageId,
                    assistantMessageId,
                    codingResult.Message,
                    codingResult.Succeeded ? EvidenceStatus.Verified : EvidenceStatus.Unknown);
                yield break;
            }
        }

        var codingContext = LocalCodingTool is null
            ? CodingContextPack.Empty
            : await LocalCodingTool.BuildContextPackAsync(userText, cancellationToken).ConfigureAwait(false);
        var codingTaskPlan = LocalCodingTool is null
            ? CodingTaskPlan.Empty
            : await LocalCodingTool.BuildTaskPlanAsync(userText, codingContext, cancellationToken).ConfigureAwait(false);
        var plannerHistory = AddSavedMemories(history);
        var sourcePlan = await SourcePlanner.PlanAsync(userText, plannerHistory, cancellationToken).ConfigureAwait(false);
        var sourceResult = sourcePlan.UseSources
            ? await Sources.RetrieveAsync(sourcePlan, cancellationToken).ConfigureAwait(false)
            : SourceRetrievalResult.Empty;
        var answerHistory = ShouldIncludeSavedMemoriesInAnswer(userText, sourcePlan)
            ? plannerHistory
            : history;
        answerHistory = AddCodingContext(answerHistory, codingContext, codingTaskPlan);
        var enrichedHistory = answerHistory;
        if (sourceResult.HasSources)
        {
            enrichedHistory = answerHistory
                .Append(new ChatMessage(
                    $"msg_sources_instruction_{Guid.NewGuid():N}",
                    ChatRole.System,
                    SourcePromptFormatter.BuildPromptInstruction(sourceResult),
                    DateTimeOffset.UtcNow,
                    EvidenceStatus.Verified))
                .Append(new ChatMessage(
                    $"msg_sources_context_{Guid.NewGuid():N}",
                    ChatRole.User,
                    SourcePromptFormatter.BuildUntrustedExcerptContext(sourceResult),
                    DateTimeOffset.UtcNow,
                    EvidenceStatus.Verified))
                .ToList();
        }
        else if (sourcePlan.UseSources)
        {
            enrichedHistory = answerHistory
                .Append(new ChatMessage(
                    $"msg_sources_empty_{Guid.NewGuid():N}",
                    ChatRole.System,
                    SourcePromptFormatter.BuildNoSourceResultContext(sourcePlan, sourceResult),
                    DateTimeOffset.UtcNow,
                    EvidenceStatus.Verified))
                .ToList();
        }

        var request = new ChatRequest(conversationId, userMessageId, userText, enrichedHistory)
        {
            Attachments = attachments
        };

        if (!sourceResult.HasSources)
        {
            await foreach (var token in Runtime.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return new AssistantStreamChunk(
                    conversationId,
                    userMessageId,
                    assistantMessageId,
                    token.Text,
                    token.EvidenceStatus,
                    token.FinishReason);
            }

            yield break;
        }

        var answer = new StringBuilder();
        var evidenceStatus = EvidenceStatus.Unverified;
        string? finishReason = null;
        await foreach (var token in Runtime.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
        {
            answer.Append(token.Text);
            evidenceStatus = token.EvidenceStatus;
            if (!string.IsNullOrWhiteSpace(token.FinishReason))
            {
                finishReason = token.FinishReason;
            }
        }

        var cleanedAnswer = StripModelGeneratedSourceAppendix(answer.ToString());
        if (!string.IsNullOrWhiteSpace(cleanedAnswer))
        {
            yield return new AssistantStreamChunk(
                conversationId,
                userMessageId,
                assistantMessageId,
                cleanedAnswer,
                evidenceStatus,
                finishReason);
        }

        var sourceAppendix = SourcePromptFormatter.BuildAnswerAppendix(sourceResult);
        if (!string.IsNullOrWhiteSpace(sourceAppendix))
        {
            yield return new AssistantStreamChunk(
                conversationId,
                userMessageId,
                assistantMessageId,
                $"{Environment.NewLine}{Environment.NewLine}{sourceAppendix}",
                EvidenceStatus.Verified);
        }
    }

    private static string StripModelGeneratedSourceAppendix(string answer) =>
        SourcesCheckedRegex.Replace(answer, string.Empty).TrimEnd();

    private static IReadOnlyList<ChatMessage> AddCodingContext(
        IReadOnlyList<ChatMessage> history,
        CodingContextPack contextPack,
        CodingTaskPlan taskPlan)
    {
        if ((!contextPack.HasContext || string.IsNullOrWhiteSpace(contextPack.Text))
            && (!taskPlan.HasPlan || string.IsNullOrWhiteSpace(taskPlan.Text)))
        {
            return history;
        }

        var instruction = contextPack.IncludesLastFailure
            ? "A local coding context pack is attached. Use it to explain the likely build/test failure and propose a small fix. Do not say you changed files. File edits still require explicit user confirmation."
            : "A local coding context pack is attached. Use it as read-only context for this coding question. Do not say you changed files or ran tools unless a tool result says so.";
        if (taskPlan.HasPlan)
        {
            instruction += " A guarded coding task plan is also attached. Use it as the work order and preserve its confirmation gates.";
        }

        var updated = history
            .Append(new ChatMessage(
                $"msg_coding_instruction_{Guid.NewGuid():N}",
                ChatRole.System,
                instruction,
                DateTimeOffset.UtcNow,
                EvidenceStatus.Verified))
            .ToList();

        if (contextPack.HasContext && !string.IsNullOrWhiteSpace(contextPack.Text))
        {
            updated.Add(new ChatMessage(
                $"msg_coding_context_{Guid.NewGuid():N}",
                ChatRole.User,
                contextPack.Text,
                DateTimeOffset.UtcNow,
                EvidenceStatus.Verified));
        }

        if (taskPlan.HasPlan && !string.IsNullOrWhiteSpace(taskPlan.Text))
        {
            updated.Add(new ChatMessage(
                $"msg_coding_plan_{Guid.NewGuid():N}",
                ChatRole.User,
                taskPlan.Text,
                DateTimeOffset.UtcNow,
                EvidenceStatus.Verified));
        }

        return updated;
    }

    private IReadOnlyList<ChatMessage> AddSavedMemories(IReadOnlyList<ChatMessage> history)
    {
        if (Memories is null)
        {
            return history;
        }

        var result = Memories.List();
        var memories = result.Memories
            .Where(memory => memory.Active && memory.Sensitivity == MemorySensitivity.Normal)
            .Take(MaxPromptMemories)
            .ToList();
        if (memories.Count == 0)
        {
            return history;
        }

        var lines = new List<string>
        {
            "Saved local user memories. Use these only when they directly help answer the current user message.",
            "Use them for location-dependent requests, explicit memory questions, preferences, and continuity.",
            "Do not mention or paraphrase saved memories unless the user asked about them or they are essential to the answer.",
            "If the current user contradicts a saved memory, follow the current user."
        };

        foreach (var memory in memories)
        {
            lines.Add($"- {memory.Text}");
        }

        return history
            .Append(new ChatMessage(
                $"msg_memories_{Guid.NewGuid():N}",
                ChatRole.System,
                string.Join(Environment.NewLine, lines),
                DateTimeOffset.UtcNow,
                EvidenceStatus.Verified))
            .ToList();
    }

    private static bool ShouldIncludeSavedMemoriesInAnswer(string userText, SourceQueryPlan sourcePlan)
    {
        if (MemoryRelevanceIntents.Contains(sourcePlan.Intent))
        {
            return true;
        }

        var tokens = userText
            .Split(MemoryRelevanceTokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return tokens.Overlaps(MemoryRelevanceTerms)
            || tokens.Contains("my");
    }
}
