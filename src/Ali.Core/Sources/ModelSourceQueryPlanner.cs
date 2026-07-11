using System.Text;
using System.Text.Json;
using Ali.Core.Evidence;
using Ali.Core.Runtime;

namespace Ali.Core.Sources;

public sealed class ModelSourceQueryPlanner(ILocalModelRuntime runtime) : ISourceQueryPlanner
{
    private const int MaxPlannerOutputCharacters = 4096;

    public async Task<SourceQueryPlan> PlanAsync(
        string userText,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            return SourceQueryPlan.NoSources;
        }

        var plannerHistory = new List<ChatMessage>
        {
            new(
                "source_planner_system",
                ChatRole.System,
                BuildPlannerInstruction(history),
                DateTimeOffset.UtcNow,
                EvidenceStatus.Unverified)
        };
        var request = new ChatRequest(
            ConversationId: "source_query_plan",
            UserMessageId: "source_query_plan_user",
            UserText: userText,
            History: plannerHistory);

        try
        {
            var output = new StringBuilder();
            await foreach (var token in runtime.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
            {
                output.Append(token.Text);
                if (output.Length > MaxPlannerOutputCharacters)
                {
                    break;
                }
            }

            var plan = TryParsePlan(output.ToString(), userText);
            if (plan is { UseSources: true })
            {
                return plan;
            }

            return await TryPlanWithSourceNeedDecisionAsync(userText, history, cancellationToken).ConfigureAwait(false)
                   ?? plan
                   ?? SourceQueryPlan.NoSources;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            return SourceQueryPlan.NoSources;
        }
    }

    private async Task<SourceQueryPlan?> TryPlanWithSourceNeedDecisionAsync(
        string userText,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        var plannerHistory = new List<ChatMessage>
        {
            new(
                "source_need_decision_system",
                ChatRole.System,
                BuildSourceNeedDecisionInstruction(history),
                DateTimeOffset.UtcNow,
                EvidenceStatus.Unverified)
        };
        var request = new ChatRequest(
            ConversationId: "source_need_decision",
            UserMessageId: "source_need_decision_user",
            UserText: userText,
            History: plannerHistory);

        try
        {
            var output = new StringBuilder();
            await foreach (var token in runtime.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
            {
                output.Append(token.Text);
                if (output.Length > MaxPlannerOutputCharacters)
                {
                    break;
                }
            }

            return TryParsePlan(output.ToString(), userText);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    private static string BuildPlannerInstruction(IReadOnlyList<ChatMessage> history)
    {
        var lines = new List<string>
        {
            "You are Ali's source and internet tool planner.",
            "Return exactly one JSON object and no other text.",
            "Do not answer the user.",
            "Decide whether Ali should call source tools before answering.",
            "Set use_sources to true when the user asks for current, recent, live, official, source-backed, weather, sports, prices, laws, regulations, public figures, product availability, internet, news, web page, documentation, or local document/library information.",
            "Set use_sources to false for stable explanations, math, definitions, creative writing, casual chat, or ordinary background knowledge that does not depend on current facts.",
            "When the user asks about current events or news, choose intent current_news and preferred_source_topics news.",
            "When the user asks about a direct URL, website, docs, or an online source, choose intent docs or research and copy exact URLs verbatim into query_terms.",
            "When the user asks about local files, folders, documents, manuals, or the local RAG library, choose intent local_documents and preferred_source_topics local_documents.",
            "When the user asks about weather, sports scores, officeholders, prices, or regulations, use sources because those facts change.",
            "Use query_terms as the search query Ali should send to the source backend. Keep them short and specific.",
            "preferred_source_topics should contain broad routing labels only, such as news, weather, sports, health, ai, software, government, finance, law, reference, local_documents.",
            "requires_source_grounding should be true whenever the answer must come from retrieved evidence instead of memory.",
            "JSON shape:",
            "{\"use_sources\":false,\"requires_source_grounding\":false,\"intent\":\"stable_knowledge\",\"topic\":\"\",\"query_terms\":[],\"preferred_source_topics\":[]}",
            "Allowed intents include stable_knowledge, current_news, weather, sports_score, official_info, docs, research, local_documents, local_app, general_sources.",
            "Recent conversation context for location, pronouns, and continuity:"
        };

        foreach (var message in history
                     .Where(message => message.Role is ChatRole.System
                                       && message.Text.Contains("Saved local user memories", StringComparison.OrdinalIgnoreCase))
                     .TakeLast(2))
        {
            lines.Add($"SavedMemory: {TrimForPlanner(message.Text)}");
        }

        foreach (var message in history
                     .Where(message => message.Role is ChatRole.User or ChatRole.Assistant)
                     .TakeLast(6))
        {
            lines.Add($"{message.Role}: {TrimForPlanner(message.Text)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildSourceNeedDecisionInstruction(IReadOnlyList<ChatMessage> history)
    {
        var lines = new List<string>
        {
            "You are Ali's internet/source routing decider.",
            "Return exactly one JSON object and no other text.",
            "Do not answer the user.",
            "Your only job is to decide if Ali must retrieve current/source evidence before answering.",
            "If the user's answer depends on what is happening now, recent events, live facts, official pages, news, weather, sports, prices, laws, regulations, public figures, websites, documentation, products, or local documents, set use_sources true.",
            "If the user's answer is stable background knowledge, math, definitions, creative writing, or casual chat, set use_sources false.",
            "When use_sources is true, provide intent, topic, query_terms, and preferred_source_topics for the source backend. If the user gave a URL, copy the exact URL verbatim into query_terms.",
            "For current events and news, use intent current_news and preferred_source_topics news.",
            "For local files/documents/library questions, use intent local_documents and preferred_source_topics local_documents.",
            "JSON shape:",
            "{\"use_sources\":true,\"requires_source_grounding\":true,\"intent\":\"current_news\",\"topic\":\"openai latest headlines\",\"query_terms\":[\"OpenAI latest headlines\"],\"preferred_source_topics\":[\"news\"]}",
            "Recent conversation context:"
        };

        foreach (var message in history
                     .Where(message => message.Role is ChatRole.User or ChatRole.Assistant)
                     .TakeLast(4))
        {
            lines.Add($"{message.Role}: {TrimForPlanner(message.Text)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static SourceQueryPlan? TryParsePlan(string text, string fallbackUserText)
    {
        var json = ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
        {
            return TryParseLoosePlan(text, fallbackUserText);
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var useSources = ReadBool(root, "use_sources", "useSources");
        if (!useSources)
        {
            return SourceQueryPlan.NoSources;
        }

        var requiresSourceGrounding = ReadBool(root, "requires_source_grounding", "requiresSourceGrounding") || useSources;
        var intent = ReadString(root, "intent", "general_sources");
        var topic = ReadString(root, "topic", string.Empty);
        var queryTerms = ReadStringArray(root, "query_terms", "queryTerms").ToList();
        var preferredSourceTopics = ReadStringArray(root, "preferred_source_topics", "preferredSourceTopics");

        if (queryTerms.Count == 0 && string.IsNullOrWhiteSpace(topic))
        {
            queryTerms.Add(fallbackUserText.Trim());
        }

        return new SourceQueryPlan(
            true,
            requiresSourceGrounding,
            intent,
            topic,
            queryTerms,
            preferredSourceTopics);
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start
            ? text[start..(end + 1)]
            : null;
    }

    private static SourceQueryPlan? TryParseLoosePlan(string text, string fallbackUserText)
    {
        var normalized = text.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var saysUseSources = ContainsPair(normalized, "use_sources", "true")
                             || ContainsPair(normalized, "useSources", "true")
                             || ContainsPair(normalized, "requires_source", "true")
                             || ContainsPair(normalized, "source", "needed")
                             || ContainsPair(normalized, "source", "required")
                             || ContainsPair(normalized, "retrieve", "sources");
        var saysNoSources = ContainsPair(normalized, "use_sources", "false")
                            || ContainsPair(normalized, "useSources", "false")
                            || ContainsPair(normalized, "source", "not needed")
                            || ContainsPair(normalized, "no", "sources");
        if (!saysUseSources || saysNoSources)
        {
            return null;
        }

        var intent = normalized.Contains("current_news", StringComparison.OrdinalIgnoreCase)
                     || normalized.Contains("news", StringComparison.OrdinalIgnoreCase)
            ? "current_news"
            : "general_sources";
        var preferredTopics = intent.Equals("current_news", StringComparison.OrdinalIgnoreCase)
            ? new[] { "news" }
            : Array.Empty<string>();
        return new SourceQueryPlan(
            true,
            true,
            intent,
            fallbackUserText.Trim(),
            [fallbackUserText.Trim()],
            preferredTopics);
    }

    private static bool ContainsPair(string text, string left, string right) =>
        text.Contains(left, StringComparison.OrdinalIgnoreCase)
        && text.Contains(right, StringComparison.OrdinalIgnoreCase);

    private static bool ReadBool(JsonElement root, string snakeName, string camelName) =>
        (root.TryGetProperty(snakeName, out var snakeValue) && ReadFlexibleBool(snakeValue))
        || (root.TryGetProperty(camelName, out var camelValue) && ReadFlexibleBool(camelValue));

    private static bool ReadFlexibleBool(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed)
                                    ? parsed
                                    : string.Equals(value.GetString(), "yes", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static string ReadString(JsonElement root, string propertyName, string fallback) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string snakeName, string camelName)
    {
        var value = root.TryGetProperty(snakeName, out var snakeValue)
            ? snakeValue
            : root.TryGetProperty(camelName, out var camelValue)
                ? camelValue
                : default;

        if (value.ValueKind is JsonValueKind.String)
        {
            return value.GetString()?
                       .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Where(item => !string.IsNullOrWhiteSpace(item))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .Take(12)
                       .ToList()
                   ?? [];
        }

        if (value.ValueKind is not JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind is JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private static string TrimForPlanner(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }
}
