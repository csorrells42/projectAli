using System.Text;
using System.Text.Json;
using System.Globalization;
using Ali.Core.Evidence;
using Ali.Core.Runtime;

namespace Ali.Core.Sources;

public sealed class ModelSourceQueryPlanner(ILocalModelRuntime runtime) : ISourceQueryPlanner
{
    private const int MaxPlannerOutputCharacters = 4096;
    private static readonly char[] RoutingTokenSeparators =
        [' ', ',', '.', '?', '!', ':', ';', '/', '\\', '-', '_', '(', ')', '[', ']', '"', '\''];
    private static readonly HashSet<string> SportsSubjectTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "alabama",
        "baseball",
        "basketball",
        "braves",
        "crimson",
        "football",
        "game",
        "games",
        "mlb",
        "nba",
        "ncaa",
        "nfl",
        "sec",
        "sports",
        "team",
        "teams",
        "tide",
        "titans"
    };
    private static readonly HashSet<string> SportsChangingTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "against",
        "current",
        "game",
        "games",
        "last",
        "loss",
        "losses",
        "next",
        "played",
        "playing",
        "record",
        "result",
        "results",
        "schedule",
        "score",
        "scores",
        "season",
        "standings",
        "this",
        "today",
        "tonight",
        "upcoming",
        "win",
        "wins",
        "won",
        "year"
    };
    private static readonly HashSet<string> GovernmentOfficeTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "administration",
        "cabinet",
        "congressman",
        "congresswoman",
        "governor",
        "justice",
        "mayor",
        "president",
        "representative",
        "secretary",
        "senator",
        "speaker"
    };
    private static readonly HashSet<string> CurrentOfficeholderTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "are",
        "current",
        "currently",
        "is",
        "now",
        "office",
        "serves",
        "serving",
        "today",
        "who"
    };
    private static readonly HashSet<string> LocalDocumentSubjectTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "document",
        "documents",
        "doc",
        "file",
        "files",
        "folder",
        "library",
        "local",
        "manual",
        "rag"
    };
    private static readonly HashSet<string> LocalDocumentActionTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "about",
        "according",
        "answer",
        "based",
        "contains",
        "does",
        "find",
        "in",
        "inside",
        "read",
        "search",
        "say",
        "says",
        "summarize",
        "tell",
        "what",
        "where"
    };

    public async Task<SourceQueryPlan> PlanAsync(
        string userText,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            return SourceQueryPlan.NoSources;
        }

        var guardedPlan = TryBuildGuardedSourcePlan(userText);
        if (guardedPlan is not null)
        {
            return guardedPlan;
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

            return TryParsePlan(output.ToString()) ?? SourceQueryPlan.NoSources;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            return SourceQueryPlan.NoSources;
        }
    }

    private static string BuildPlannerInstruction(IReadOnlyList<ChatMessage> history)
    {
        var lines = new List<string>
        {
            "You are the app's source query planner.",
            "Return exactly one JSON object and no other text.",
            "Do not answer the user.",
            "Do not say you lack internet access; decide whether the app should try approved local source retrieval.",
            "Set use_sources to true only when the current user message needs live, current, official, source-backed, weather, scores, prices, news, web, local document/library, or app-approved source information.",
            "Direct file paths, local library questions, RAG folder questions, and document/manual questions require sources with intent local_documents and preferred_source_topics local_documents.",
            "Sports scores, schedules, standings, team records, season records, and relative-date sports questions such as last year, this year, next game, or current season require sources.",
            "Current public or political officeholders require sources, including the current president, governor, mayor, senator, representative, secretary, justice, cabinet, or administration.",
            "Set use_sources to false for stable explanations, math, physics, definitions, casual chat, creative writing, or ordinary background knowledge.",
            "When use_sources is true, choose concise topic/query terms that will find approved catalog sources.",
            "JSON shape:",
            "{\"use_sources\":false,\"requires_source_grounding\":false,\"intent\":\"stable_knowledge\",\"topic\":\"\",\"query_terms\":[],\"preferred_source_topics\":[]}",
            "Allowed intents include stable_knowledge, current_news, weather, sports_score, official_info, docs, research, local_documents, local_app, general_sources.",
            "preferred_source_topics should use broad catalog topics such as news, weather, sports, health, ai, software, government, education, science, finance, law, reference, local_documents.",
            "Recent conversation context for location or pronouns:"
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

    private static SourceQueryPlan? TryBuildGuardedSourcePlan(string userText)
    {
        var tokens = TokenizeForRouting(userText);
        var localDocumentPlan = TryBuildLocalDocumentPlan(userText, tokens);
        if (localDocumentPlan is not null)
        {
            return localDocumentPlan;
        }

        var governmentPlan = TryBuildGovernmentOfficeholderPlan(tokens);
        if (governmentPlan is not null)
        {
            return governmentPlan;
        }

        return TryBuildSportsPlan(tokens);
    }

    private static SourceQueryPlan? TryBuildLocalDocumentPlan(string userText, IReadOnlySet<string> tokens)
    {
        if (!LooksLikeWindowsPath(userText)
            && (!tokens.Overlaps(LocalDocumentSubjectTerms) || !tokens.Overlaps(LocalDocumentActionTerms)))
        {
            return null;
        }

        var terms = tokens
            .Concat(["local", "document", "library"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();

        return new SourceQueryPlan(
            true,
            true,
            "local_documents",
            userText,
            terms,
            ["local_documents"]);
    }

    private static SourceQueryPlan? TryBuildGovernmentOfficeholderPlan(IReadOnlySet<string> tokens)
    {
        if (!tokens.Overlaps(GovernmentOfficeTerms))
        {
            return null;
        }

        var isUnitedStatesQuestion = tokens.Contains("united")
                                     || tokens.Contains("states")
                                     || tokens.Contains("america")
                                     || tokens.Contains("usa")
                                     || tokens.Contains("us");
        var isCurrentOfficeholderQuestion = tokens.Overlaps(CurrentOfficeholderTerms);
        if (!isCurrentOfficeholderQuestion && !(tokens.Contains("president") && isUnitedStatesQuestion))
        {
            return null;
        }

        var queryTerms = tokens.ToList();
        queryTerms.AddRange(["official", "current", "government"]);
        if (tokens.Contains("president") && isUnitedStatesQuestion)
        {
            queryTerms.AddRange(["white", "house", "administration", "president", "united", "states"]);
        }

        var distinctTerms = queryTerms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();

        return new SourceQueryPlan(
            true,
            true,
            "official_info",
            string.Join(' ', distinctTerms),
            distinctTerms,
            ["government"]);
    }

    private static SourceQueryPlan? TryBuildSportsPlan(IReadOnlySet<string> tokens)
    {
        if (!tokens.Overlaps(SportsSubjectTerms) || !tokens.Overlaps(SportsChangingTerms))
        {
            return null;
        }

        var queryTerms = tokens.ToList();
        if (tokens.Contains("alabama") && tokens.Contains("football"))
        {
            queryTerms.AddRange(["crimson", "tide", "rolltide", "college", "sec", "schedule", "record"]);
        }

        if (tokens.Contains("last") && tokens.Contains("year"))
        {
            queryTerms.Add((DateTimeOffset.Now.Year - 1).ToString(CultureInfo.InvariantCulture));
        }

        if (tokens.Contains("this") && tokens.Contains("year"))
        {
            queryTerms.Add(DateTimeOffset.Now.Year.ToString(CultureInfo.InvariantCulture));
        }

        if (tokens.Contains("next") || tokens.Contains("upcoming"))
        {
            queryTerms.Add(DateTimeOffset.Now.Year.ToString(CultureInfo.InvariantCulture));
            queryTerms.Add((DateTimeOffset.Now.Year + 1).ToString(CultureInfo.InvariantCulture));
        }

        var distinctTerms = queryTerms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();

        return new SourceQueryPlan(
            true,
            true,
            "sports_score",
            string.Join(' ', distinctTerms),
            distinctTerms,
            ["sports"]);
    }

    private static HashSet<string> TokenizeForRouting(string text) =>
        text.Split(RoutingTokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeRoutingToken)
            .Where(token => token.Length >= 3
                            || token.Equals("al", StringComparison.OrdinalIgnoreCase)
                            || token.Equals("is", StringComparison.OrdinalIgnoreCase)
                            || token.Equals("us", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeRoutingToken(string token) =>
        new string(token.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static bool LooksLikeWindowsPath(string text)
    {
        for (var i = 0; i + 2 < text.Length; i++)
        {
            if (char.IsLetter(text[i]) && text[i + 1] == ':' && (text[i + 2] == '\\' || text[i + 2] == '/'))
            {
                return true;
            }
        }

        return false;
    }

    private static SourceQueryPlan? TryParsePlan(string text)
    {
        var json = ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var useSources = ReadBool(root, "use_sources");
        var requiresSourceGrounding = ReadBool(root, "requires_source_grounding") || useSources;
        var intent = ReadString(root, "intent", useSources ? "general_sources" : "stable_knowledge");
        var topic = ReadString(root, "topic", string.Empty);
        var queryTerms = ReadStringArray(root, "query_terms");
        var preferredSourceTopics = ReadStringArray(root, "preferred_source_topics");

        if (!useSources)
        {
            return SourceQueryPlan.NoSources;
        }

        if (queryTerms.Count == 0 && string.IsNullOrWhiteSpace(topic))
        {
            return SourceQueryPlan.NoSources;
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

    private static bool ReadBool(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True;

    private static string ReadString(JsonElement root, string propertyName, string fallback) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind is not JsonValueKind.Array)
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
