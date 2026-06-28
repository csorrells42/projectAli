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
    private static readonly Regex MultiDayForecastRegex = new(
        @"\b(?:5|five|3|three|4|four|7|seven|10|ten)\s*-?\s*day\b|\bweek(?:ly|end)?\s+forecast\b|\bforecast\s+(?:for\s+)?(?:the\s+)?(?:week|next\s+week)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PresidentRegex = new(
        @"President\s+Donald\s+J\.?\s+Trump",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VicePresidentRegex = new(
        @"Vice\s+President\s+JD\s+Vance",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
        var deterministicSourceAnswer = TryBuildDeterministicSourceAnswer(userText, sourcePlan, sourceResult);
        if (!string.IsNullOrWhiteSpace(deterministicSourceAnswer))
        {
            yield return new AssistantStreamChunk(
                conversationId,
                userMessageId,
                assistantMessageId,
                deterministicSourceAnswer,
                EvidenceStatus.Verified);

            var deterministicSourceAppendix = SourcePromptFormatter.BuildAnswerAppendix(sourceResult);
            if (!string.IsNullOrWhiteSpace(deterministicSourceAppendix))
            {
                yield return new AssistantStreamChunk(
                    conversationId,
                    userMessageId,
                    assistantMessageId,
                    $"{Environment.NewLine}{Environment.NewLine}{deterministicSourceAppendix}",
                    EvidenceStatus.Verified);
            }

            yield break;
        }

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

    private static bool IsDisabledMultiDayForecastRequest(string userText) =>
        MultiDayForecastRegex.IsMatch(userText);

    private static string? TryBuildDeterministicSourceAnswer(
        string userText,
        SourceQueryPlan sourcePlan,
        SourceRetrievalResult sourceResult)
    {
        if (!sourceResult.HasSources)
        {
            return null;
        }

        if (string.Equals(sourcePlan.Intent, "official_info", StringComparison.OrdinalIgnoreCase))
        {
            return TryBuildDeterministicOfficeholderAnswer(sourcePlan, sourceResult);
        }

        if (!string.Equals(sourcePlan.Intent, "weather", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var forecast = sourceResult.Excerpts.FirstOrDefault(excerpt =>
            excerpt.Excerpt.Contains("National Weather Service local forecast:", StringComparison.OrdinalIgnoreCase));
        if (forecast is null)
        {
            return null;
        }

        return BuildCurrentDayOnlyForecast(
            forecast.Excerpt,
            includeMultiDayReworkNote: IsDisabledMultiDayForecastRequest(userText));
    }

    private static string BuildCurrentDayOnlyForecast(string forecastExcerpt, bool includeMultiDayReworkNote)
    {
        var lines = forecastExcerpt
            .Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToList();
        var currentLine = lines.FirstOrDefault(line =>
            line.StartsWith("Today:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("This Afternoon:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Tonight:", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(currentLine))
        {
            currentLine = lines.FirstOrDefault(line =>
                !line.StartsWith("National Weather Service local forecast", StringComparison.OrdinalIgnoreCase));
        }

        var answer = string.IsNullOrWhiteSpace(currentLine)
            ? "Current-day forecast details were not available in the approved weather source."
            : $"Current-day forecast: {currentLine}";
        return includeMultiDayReworkNote
            ? $"{answer}{Environment.NewLine}Multi-day forecasts are being reworked for this release, so I am only showing the current-day forecast right now."
            : answer;
    }

    private static string? TryBuildDeterministicOfficeholderAnswer(SourceQueryPlan sourcePlan, SourceRetrievalResult sourceResult)
    {
        var terms = sourcePlan.SearchText;
        var asksPresident = terms.Contains("president", StringComparison.OrdinalIgnoreCase)
                            && !terms.Contains("vice president", StringComparison.OrdinalIgnoreCase);
        var asksVicePresident = terms.Contains("vice", StringComparison.OrdinalIgnoreCase)
                                && terms.Contains("president", StringComparison.OrdinalIgnoreCase);
        if (!asksPresident && !asksVicePresident)
        {
            return null;
        }

        var administration = sourceResult.Excerpts.FirstOrDefault(excerpt =>
            excerpt.Name.Contains("White House", StringComparison.OrdinalIgnoreCase)
            || excerpt.Url.Contains("whitehouse.gov/administration", StringComparison.OrdinalIgnoreCase)
            || excerpt.Excerpt.Contains("The Administration", StringComparison.OrdinalIgnoreCase));
        if (administration is null)
        {
            return null;
        }

        var lines = new List<string>();
        if (asksPresident && PresidentRegex.IsMatch(administration.Excerpt))
        {
            lines.Add("The current President of the United States is Donald J. Trump, the 45th and 47th President of the United States.");
        }

        if (asksVicePresident && VicePresidentRegex.IsMatch(administration.Excerpt))
        {
            lines.Add("The current Vice President of the United States is JD Vance.");
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

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
