using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ali.Modules.Evidence;
using Ali.Modules.Feedback;
using Ali.Modules.Memory;
using Ali.Modules.Permissions;
using Ali.Modules.Runtime;
using Ali.Modules.Internet;
using Ali.Modules.Time;

namespace Ali;

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
    IMemoryStore? memoryStore = null)
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
    private static readonly Regex SourcesCheckedHeaderRegex = new(
        @"^\s*Sources checked\s*:\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SourceListLineRegex = new(
        @"^\s*(?:[-*]\s*)?(?:\[\d+\]|\d+[\.)])\s+",
        RegexOptions.CultureInvariant);
    private static readonly Regex SourceContinuationLineRegex = new(
        @"^\s*(?:https?://|www\.|[\w.-]+\.[a-z]{2,}/)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex InlineSourceCitationRegex = new(
        @"(?<!\w)\s*\[(?:\d{1,2})(?:\s*,\s*\d{1,2})*\]",
        RegexOptions.CultureInvariant);
    private static readonly Regex MarkdownLinkOrImageRegex = new(
        @"!?\[(?<label>[^\]]*)\]\([^)]+\)",
        RegexOptions.CultureInvariant);
    private static readonly Regex RawUrlRegex = new(
        @"\b(?:https?://|www\.)\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ExplicitSourceRequestRegex = new(
        @"\bprovide\s+(?:your\s+|the\s+)?sources\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NoCurrentDataRefusalRegex = new(
        @"\b(?:do not|don't|cannot|can't|unable to|lack|no)\b.{0,80}\b(?:real[-\s]?time|current|live|internet|web|browsing|latest)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DatedEvidenceFactRegex = new(
        @"Source \[(?<source>\d+)\] date (?<date>\d{4}-\d{2}-\d{2})(?:: (?<context>.*))?",
        RegexOptions.CultureInvariant);
    private static readonly Regex AnswerMonthDateRegex = new(
        @"\b(?<month>Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t)?(?:ember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\.?\s+(?<day>\d{1,2})(?:,\s*(?<year>20\d{2}))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AnswerIsoDateRegex = new(@"\b(?<date>20\d{2}-\d{2}-\d{2})\b", RegexOptions.CultureInvariant);
    public ILocalModelRuntime Runtime { get; } = runtime;

    public PermissionService Permissions { get; } = permissionService;

    public CorrectionQueueService Corrections { get; } = correctionQueue;

    public ISourceRetriever Sources { get; } = sourceRetriever ?? new NoOpSourceRetriever();

    public ISourceQueryPlanner SourcePlanner { get; } = sourceQueryPlanner ?? new ModelSourceQueryPlanner(runtime);

    public IMemoryStore? Memories { get; } = memoryStore;

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

        var plannerHistory = AddSavedMemories(history);
        var sourcePlan = await SourcePlanner.PlanAsync(userText, plannerHistory, cancellationToken).ConfigureAwait(false);
        var includeVisibleSources = ShouldIncludeVisibleSources(userText);
        var sourceResult = sourcePlan.UseSources
            ? await Sources.RetrieveAsync(sourcePlan, cancellationToken).ConfigureAwait(false)
            : SourceRetrievalResult.Empty;

        var answerHistory = ShouldIncludeSavedMemoriesInAnswer(userText, sourcePlan)
            ? plannerHistory
            : history;
        var enrichedHistory = answerHistory;
        if (sourceResult.HasSources)
        {
            enrichedHistory = answerHistory
                .Append(new ChatMessage(
                    $"msg_sources_instruction_{Guid.NewGuid():N}",
                    ChatRole.System,
                    SourcePromptFormatter.BuildPromptInstruction(sourceResult, includeVisibleSources),
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

        if (sourcePlan.UseSources && !sourceResult.HasSources && sourceResult.Warnings.Count > 0)
        {
            yield return new AssistantStreamChunk(
                conversationId,
                userMessageId,
                assistantMessageId,
                BuildSourceLookupFailureAnswer(sourceResult),
                EvidenceStatus.Verified);
            yield break;
        }

        var request = new ChatRequest(conversationId, userMessageId, userText, enrichedHistory)
        {
            Attachments = attachments
        };

        if (!sourceResult.HasSources)
        {
            var directAnswer = await CollectRuntimeAnswerAsync(request, cancellationToken).ConfigureAwait(false);
            var retryPlan = !sourcePlan.UseSources
                ? await TryPlanSourceRetryFromAnswerAsync(userText, answerHistory, directAnswer, cancellationToken).ConfigureAwait(false)
                : SourceQueryPlan.NoSources;
            if (retryPlan.UseSources)
            {
                var retrySourceResult = await Sources.RetrieveAsync(retryPlan, cancellationToken).ConfigureAwait(false);
                if (retrySourceResult.HasSources || retrySourceResult.Warnings.Count > 0)
                {
                    if (!retrySourceResult.HasSources && retrySourceResult.Warnings.Count > 0)
                    {
                        yield return new AssistantStreamChunk(
                            conversationId,
                            userMessageId,
                            assistantMessageId,
                            BuildSourceLookupFailureAnswer(retrySourceResult),
                            EvidenceStatus.Verified);
                        yield break;
                    }

                    var retryHistory = answerHistory;
                    if (retrySourceResult.HasSources)
                    {
                        retryHistory = retryHistory
                            .Append(new ChatMessage(
                                $"msg_sources_instruction_retry_{Guid.NewGuid():N}",
                                ChatRole.System,
                                SourcePromptFormatter.BuildPromptInstruction(retrySourceResult, includeVisibleSources),
                                DateTimeOffset.UtcNow,
                                EvidenceStatus.Verified))
                            .Append(new ChatMessage(
                                $"msg_sources_context_retry_{Guid.NewGuid():N}",
                                ChatRole.User,
                                SourcePromptFormatter.BuildUntrustedExcerptContext(retrySourceResult),
                                DateTimeOffset.UtcNow,
                                EvidenceStatus.Verified))
                            .ToList();
                    }
                    else
                    {
                        retryHistory = retryHistory
                            .Append(new ChatMessage(
                                $"msg_sources_empty_retry_{Guid.NewGuid():N}",
                                ChatRole.System,
                                SourcePromptFormatter.BuildNoSourceResultContext(retryPlan, retrySourceResult),
                                DateTimeOffset.UtcNow,
                                EvidenceStatus.Verified))
                            .ToList();
                    }

                    var retryRequest = new ChatRequest(conversationId, userMessageId, userText, retryHistory)
                    {
                        Attachments = attachments
                    };
                    var retryAnswer = await CollectRuntimeAnswerAsync(retryRequest, cancellationToken).ConfigureAwait(false);
                    var cleanedRetryAnswer = await FinalizeSourceGroundedAnswerAsync(
                        userText,
                        retryPlan,
                        retrySourceResult,
                        retryAnswer.Text,
                        includeVisibleSources,
                        cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(cleanedRetryAnswer))
                    {
                        yield return new AssistantStreamChunk(
                            conversationId,
                            userMessageId,
                            assistantMessageId,
                            cleanedRetryAnswer,
                            retryAnswer.EvidenceStatus,
                            retryAnswer.FinishReason);
                    }

                    var retrySourceAppendix = includeVisibleSources
                        ? SourcePromptFormatter.BuildAnswerAppendix(retrySourceResult)
                        : string.Empty;
                    if (!string.IsNullOrWhiteSpace(retrySourceAppendix))
                    {
                        yield return new AssistantStreamChunk(
                            conversationId,
                            userMessageId,
                            assistantMessageId,
                            $"{Environment.NewLine}{Environment.NewLine}{retrySourceAppendix}",
                            EvidenceStatus.Verified);
                    }

                    yield break;
                }
            }

            if (!string.IsNullOrWhiteSpace(directAnswer.Text))
            {
                yield return new AssistantStreamChunk(
                    conversationId,
                    userMessageId,
                    assistantMessageId,
                    directAnswer.Text,
                    directAnswer.EvidenceStatus,
                    directAnswer.FinishReason);
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

        var cleanedAnswer = await FinalizeSourceGroundedAnswerAsync(
            userText,
            sourcePlan,
            sourceResult,
            answer.ToString(),
            includeVisibleSources,
            cancellationToken).ConfigureAwait(false);
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

        var sourceAppendix = includeVisibleSources
            ? SourcePromptFormatter.BuildAnswerAppendix(sourceResult)
            : string.Empty;
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

    private async Task<string> FinalizeSourceGroundedAnswerAsync(
        string userText,
        SourceQueryPlan sourcePlan,
        SourceRetrievalResult sourceResult,
        string draftAnswer,
        bool includeVisibleSources,
        CancellationToken cancellationToken)
    {
        var cleanedDraft = StripModelGeneratedSourceAppendix(draftAnswer);
        if (!sourceResult.HasSources || string.IsNullOrWhiteSpace(cleanedDraft))
        {
            return cleanedDraft;
        }

        if (IsCurrentNewsPlan(sourcePlan))
        {
            return BuildExtractiveSourceAnswer(sourcePlan, sourceResult, includeVisibleSources);
        }

        var verifierHistory = new List<ChatMessage>
        {
            new(
                "source_answer_verifier_system",
                ChatRole.System,
                BuildSourceAnswerVerifierInstruction(sourceResult, includeVisibleSources),
                DateTimeOffset.UtcNow,
                EvidenceStatus.Verified),
            new(
                "source_answer_verifier_sources",
                ChatRole.User,
                SourcePromptFormatter.BuildUntrustedExcerptContext(sourceResult),
                DateTimeOffset.UtcNow,
                EvidenceStatus.Verified)
        };
        var verifierRequest = new ChatRequest(
            ConversationId: "source_answer_verifier",
            UserMessageId: "source_answer_verifier_user",
            UserText: BuildSourceAnswerVerifierUserText(userText, cleanedDraft, includeVisibleSources),
            History: verifierHistory);

        try
        {
            var verified = await CollectRuntimeAnswerAsync(verifierRequest, cancellationToken).ConfigureAwait(false);
            var finalAnswer = TryReadVerifiedSourceAnswer(verified.Text);
            var candidateAnswer = string.IsNullOrWhiteSpace(finalAnswer)
                ? cleanedDraft
                : StripModelGeneratedSourceAppendix(finalAnswer);
            var guarded = ApplyTemporalEvidenceGuard(userText, sourcePlan, sourceResult, candidateAnswer);
            var visibleAnswer = includeVisibleSources ? guarded : StripVisibleSourceArtifacts(guarded);
            return ReplaceSourceRefusalWithExtractiveAnswer(sourcePlan, sourceResult, visibleAnswer, includeVisibleSources);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            var guarded = ApplyTemporalEvidenceGuard(userText, sourcePlan, sourceResult, cleanedDraft);
            var visibleAnswer = includeVisibleSources ? guarded : StripVisibleSourceArtifacts(guarded);
            return ReplaceSourceRefusalWithExtractiveAnswer(sourcePlan, sourceResult, visibleAnswer, includeVisibleSources);
        }
    }

    private static string ReplaceSourceRefusalWithExtractiveAnswer(
        SourceQueryPlan sourcePlan,
        SourceRetrievalResult sourceResult,
        string answer,
        bool includeVisibleSources)
    {
        if (string.IsNullOrWhiteSpace(answer)
            || !sourceResult.HasSources
            || !NoCurrentDataRefusalRegex.IsMatch(answer))
        {
            return answer;
        }

        return BuildExtractiveSourceAnswer(sourcePlan, sourceResult, includeVisibleSources);
    }

    private static string BuildExtractiveSourceAnswer(
        SourceQueryPlan sourcePlan,
        SourceRetrievalResult sourceResult,
        bool includeVisibleSources)
    {
        var isCurrentNewsPlan = IsCurrentNewsPlan(sourcePlan);
        var heading = isCurrentNewsPlan
            ? "Here are the latest items I found:"
            : "Here is what I found from the source lookup:";
        var lines = new List<string> { heading };
        foreach (var source in sourceResult.Excerpts.Take(4))
        {
            var title = CleanVisibleSourceArtifacts(source.Name);
            var summary = BuildSourceExcerptSummary(source.Excerpt);
            var citation = includeVisibleSources ? $" [{source.Index}]" : string.Empty;
            lines.Add(isCurrentNewsPlan || string.IsNullOrWhiteSpace(summary)
                ? $"- {title}{citation}"
                : $"- {title}{citation}: {summary}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildSourceExcerptSummary(string excerpt)
    {
        var lines = excerpt
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanVisibleSourceArtifacts)
            .Where(line => line.Length >= 40)
            .Where(line => !line.Contains("Share to ", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.Contains("Video Player", StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        var summary = string.Join(" ", lines);
        return summary.Length <= 240 ? summary : $"{summary[..240].TrimEnd('.', ',', ';', ':')}...";
    }

    private static bool ShouldIncludeVisibleSources(string userText) =>
        !string.IsNullOrWhiteSpace(userText)
        && ExplicitSourceRequestRegex.IsMatch(userText);

    private static string StripVisibleSourceArtifacts(string answer) =>
        string.IsNullOrWhiteSpace(answer)
            ? string.Empty
            : CleanVisibleSourceArtifacts(InlineSourceCitationRegex.Replace(answer, string.Empty));

    private static string CleanVisibleSourceArtifacts(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = MarkdownLinkOrImageRegex.Replace(value, match => match.Groups["label"].Value.Trim());
        cleaned = RawUrlRegex.Replace(cleaned, string.Empty);
        cleaned = Regex.Replace(cleaned, @"\s+([,.;:])", "$1", RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(cleaned, @"(?:^|;\s*)\s*;\s*", "; ", RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(cleaned, @"[ \t]{2,}", " ", RegexOptions.CultureInvariant);
        return cleaned.Trim(' ', ';');
    }

    private static string StripModelGeneratedSourceAppendix(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return string.Empty;
        }

        var withoutTrailingAppendix = SourcesCheckedRegex.Replace(answer, string.Empty);
        var lines = withoutTrailingAppendix.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var cleaned = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            if (!SourcesCheckedHeaderRegex.IsMatch(lines[index]))
            {
                cleaned.Add(lines[index]);
                continue;
            }

            index++;
            while (index < lines.Length
                   && (string.IsNullOrWhiteSpace(lines[index])
                       || SourceListLineRegex.IsMatch(lines[index])
                       || SourceContinuationLineRegex.IsMatch(lines[index])))
            {
                index++;
            }

            index--;
        }

        return string.Join(Environment.NewLine, cleaned).Trim();
    }

    private static string ApplyTemporalEvidenceGuard(
        string userText,
        SourceQueryPlan sourcePlan,
        SourceRetrievalResult sourceResult,
        string answer)
    {
        if (string.IsNullOrWhiteSpace(answer) || !sourceResult.HasSources || !sourceResult.RequiresSourceGrounding)
        {
            return answer;
        }

        if (IsCurrentNewsPlan(sourcePlan))
        {
            return answer;
        }

        var clock = CurrentDateTimeSnapshot.Capture();
        var currentDate = clock.LocalDate;
        var sourceFacts = ExtractDatedSourceFacts(sourceResult)
            .Where(fact => fact.Date >= currentDate)
            .OrderBy(fact => fact.Date)
            .ToArray();
        if (sourceFacts.Length == 0)
        {
            return answer;
        }

        if (string.Equals(sourcePlan.TemporalSelection, "earliest_after_reference", StringComparison.OrdinalIgnoreCase))
        {
            var answerDates = ExtractDates(answer).ToHashSet();
            return answerDates.Contains(sourceFacts[0].Date)
                ? answer
                : BuildTemporalCorrectedAnswer(sourceFacts[0], clock);
        }

        var sourceDates = sourceFacts.Select(fact => fact.Date).ToHashSet();
        var unsupportedEarlierAnswerDates = ExtractDates(answer)
            .Where(date => date < sourceFacts[0].Date && !sourceDates.Contains(date))
            .ToArray();
        if (unsupportedEarlierAnswerDates.Length == 0)
        {
            return answer;
        }

        var userYears = ExtractYears(userText).ToHashSet();
        if (unsupportedEarlierAnswerDates.Any(date => date.Year < currentDate.Year && userYears.Contains(date.Year)))
        {
            return answer;
        }

        return BuildTemporalCorrectedAnswer(sourceFacts[0], clock);
    }

    private static bool IsCurrentNewsPlan(SourceQueryPlan sourcePlan) =>
        string.Equals(sourcePlan.Intent, "current_news", StringComparison.OrdinalIgnoreCase)
        || sourcePlan.PreferredSourceTopics.Any(topic => string.Equals(topic, "news", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<DatedSourceFact> ExtractDatedSourceFacts(SourceRetrievalResult sourceResult)
    {
        foreach (var fact in SourcePromptFormatter.BuildDatedEvidenceFacts(sourceResult))
        {
            var match = DatedEvidenceFactRegex.Match(fact);
            if (!match.Success
                || !DateOnly.TryParseExact(
                    match.Groups["date"].Value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date)
                || !int.TryParse(match.Groups["source"].Value, CultureInfo.InvariantCulture, out var sourceIndex))
            {
                continue;
            }

            yield return new DatedSourceFact(
                date,
                sourceIndex,
                match.Groups["context"].Value.Trim());
        }
    }

    private static IEnumerable<DateOnly> ExtractDates(string text)
    {
        foreach (Match match in AnswerIsoDateRegex.Matches(text))
        {
            if (DateOnly.TryParseExact(
                match.Groups["date"].Value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
            {
                yield return parsed;
            }
        }

        foreach (Match match in AnswerMonthDateRegex.Matches(text))
        {
            if (!TryReadMonth(match.Groups["month"].Value, out var month)
                || !int.TryParse(match.Groups["day"].Value, CultureInfo.InvariantCulture, out var day)
                || !int.TryParse(match.Groups["year"].Value, CultureInfo.InvariantCulture, out var year))
            {
                continue;
            }

            if (TryCreateDateOnly(year, month, day, out var parsed))
            {
                yield return parsed;
            }
        }
    }

    private static bool TryCreateDateOnly(int year, int month, int day, out DateOnly date)
    {
        try
        {
            date = new DateOnly(year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            date = default;
            return false;
        }
    }

    private static IEnumerable<int> ExtractYears(string text)
    {
        foreach (Match match in Regex.Matches(text, @"\b(20\d{2})\b", RegexOptions.CultureInvariant))
        {
            if (int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var year))
            {
                yield return year;
            }
        }
    }

    private static string BuildTemporalCorrectedAnswer(
        DatedSourceFact sourceFact,
        CurrentDateTimeSnapshot clock)
    {
        var context = NormalizeDatedFactContext(sourceFact.Context);
        var dateText = sourceFact.Date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        var currentDateText = clock.LocalDate.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(context)
            ? $"Using the current local date ({currentDateText}), the earliest upcoming dated item I found is {dateText} [{sourceFact.SourceIndex}]."
            : $"Using the current local date ({currentDateText}), the earliest upcoming dated item I found is {dateText}: {context} [{sourceFact.SourceIndex}].";
    }

    private static string NormalizeDatedFactContext(string context)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            return string.Empty;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = context
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanVisibleSourceArtifacts)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Where(part => !AnswerMonthDateRegex.IsMatch(part))
            .Where(part => seen.Add(part))
            .Take(5)
            .ToArray();
        return string.Join("; ", parts);
    }

    private static bool TryReadMonth(string value, out int month)
    {
        var normalized = value.Trim().TrimEnd('.').ToLowerInvariant();
        var monthNames = CultureInfo.InvariantCulture.DateTimeFormat;
        for (var index = 1; index <= 12; index++)
        {
            if (normalized.Equals(monthNames.GetMonthName(index).ToLowerInvariant(), StringComparison.Ordinal)
                || normalized.Equals(monthNames.GetAbbreviatedMonthName(index).ToLowerInvariant(), StringComparison.Ordinal)
                || (index == 9 && normalized.Equals("sept", StringComparison.Ordinal)))
            {
                month = index;
                return true;
            }
        }

        month = 0;
        return false;
    }

    private static string BuildSourceAnswerVerifierInstruction(SourceRetrievalResult sourceResult, bool includeVisibleSources)
    {
        var clock = CurrentDateTimeSnapshot.Capture();
        var retrievedAt = sourceResult.Excerpts.Count == 0
            ? clock.LocalNow
            : sourceResult.Excerpts.Max(source => source.RetrievedAt).ToLocalTime();
        var sourceVisibilityInstruction = includeVisibleSources
            ? "Keep citations inline using bracket numbers like [1]."
            : "Use the source excerpts internally, but do not include source URLs, source titles, source lists, references, citations, or bracket citation markers in the visible answer.";
        var answerShape = includeVisibleSources
            ? "JSON shape: {\"answer\":\"final concise answer with inline citations only\",\"supported\":true,\"diagnostic\":\"short internal note\"}"
            : "JSON shape: {\"answer\":\"final concise answer without visible sources or citation markers\",\"supported\":true,\"diagnostic\":\"short internal note\"}";
        return string.Join(
            Environment.NewLine,
            [
                "You are Ali's source-grounded answer verifier and cleanup pass.",
                "Return exactly one JSON object and no other text.",
                "Do not answer from memory, training data, or the draft answer when source excerpts disagree.",
                clock.BuildSystemInstruction(),
                $"Latest source retrieval time: {retrievedAt:yyyy-MM-dd HH:mm:ss zzz}.",
                "Use only the current user message and the provided source excerpts for current, official, schedule, sports, price, weather, news, or web-page claims.",
                "Treat the draft answer as untrusted. It may contain wrong dates, repeated paragraphs, stale claims, or a model-generated source list.",
                "Remove duplicate/repeated content and all source-list sections. Do not write a Sources checked section.",
                "For next, upcoming, following, or after-date questions, choose the earliest source-supported event/date that is not before the relevant current or requested date. If the excerpts do not support such an event/date, say the source excerpts do not contain enough information.",
                "For current news or latest-headlines questions, summarize the source-supported headlines directly; do not rewrite them as earliest upcoming dated items.",
                "Never output a past date as the answer to a next/upcoming/future question unless the user explicitly asked about the past.",
                sourceVisibilityInstruction,
                answerShape
            ]);
    }

    private static string BuildSourceAnswerVerifierUserText(string userText, string cleanedDraft, bool includeVisibleSources) =>
        string.Join(
            Environment.NewLine,
            "Current user message:",
            userText,
            string.Empty,
            "Draft answer to verify and rewrite if needed:",
            cleanedDraft,
            string.Empty,
            includeVisibleSources
                ? "Return the final answer once, with no duplicate paragraphs and no Sources checked section."
                : "Return the final answer once, with no duplicate paragraphs, no citations, and no visible source list.");

    private static string? TryReadVerifiedSourceAnswer(string text)
    {
        var json = ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        foreach (var propertyName in new[] { "answer", "final_answer", "finalAnswer" })
        {
            if (root.TryGetProperty(propertyName, out var value)
                && value.ValueKind is JsonValueKind.String)
            {
                var answer = value.GetString();
                return string.IsNullOrWhiteSpace(answer) ? null : answer.Trim();
            }
        }

        return null;
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start
            ? text[start..(end + 1)]
            : null;
    }

    private async Task<CollectedRuntimeAnswer> CollectRuntimeAnswerAsync(
        ChatRequest request,
        CancellationToken cancellationToken)
    {
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

        return new CollectedRuntimeAnswer(answer.ToString(), evidenceStatus, finishReason);
    }

    private async Task<SourceQueryPlan> TryPlanSourceRetryFromAnswerAsync(
        string userText,
        IReadOnlyList<ChatMessage> answerHistory,
        CollectedRuntimeAnswer directAnswer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directAnswer.Text))
        {
            return SourceQueryPlan.NoSources;
        }

        var retryHistory = answerHistory
            .Append(new ChatMessage(
                $"msg_source_retry_draft_{Guid.NewGuid():N}",
                ChatRole.Assistant,
                BuildSourceRetryDraftContext(directAnswer.Text),
                DateTimeOffset.UtcNow,
                directAnswer.EvidenceStatus))
            .ToList();

        var retryPlan = await SourcePlanner.PlanAsync(userText, retryHistory, cancellationToken).ConfigureAwait(false);
        return retryPlan.UseSources ? retryPlan : SourceQueryPlan.NoSources;
    }

    private static string BuildSourceRetryDraftContext(string answer)
    {
        var normalized = answer.ReplaceLineEndings(" ").Trim();
        var excerpt = normalized.Length <= 1_200 ? normalized : normalized[..1_200];
        return string.Join(
            Environment.NewLine,
            "Draft answer produced before Ali's final source decision.",
            "If this draft shows that the user needs current, live, official, internet, web, or source-backed evidence, decide that source retrieval is needed.",
            "If this draft says it lacks real-time data, browsing, current news, live updates, latest information, or internet access, decide that source retrieval is needed.",
            "Draft answer:",
            excerpt);
    }

    private static string BuildSourceLookupFailureAnswer(SourceRetrievalResult result)
    {
        var lines = new List<string>
        {
            "I tried the source lookup for this current/source-backed question, but the internet backend did not return usable source excerpts."
        };

        if (result.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Source backend warnings:");
            foreach (var warning in result.Warnings.Take(5))
            {
                lines.Add($"- {warning}");
            }
        }

        lines.Add(string.Empty);
        lines.Add("Configure the internet backend settings or API keys, then ask again.");
        return string.Join(Environment.NewLine, lines);
    }

    private sealed record CollectedRuntimeAnswer(
        string Text,
        EvidenceStatus EvidenceStatus,
        string? FinishReason);

    private sealed record DatedSourceFact(
        DateOnly Date,
        int SourceIndex,
        string Context);

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
            "Saved local user memories. These are facts about the human user and their context, not facts about the assistant identity.",
            "Use them only when they directly help answer the current user message.",
            "Never use saved memories, user names, friend names, or customer profile details to rename the assistant or answer as if they are the assistant's name.",
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
