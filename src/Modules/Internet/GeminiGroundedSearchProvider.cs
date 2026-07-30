using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ali.Modules.Internet;

internal sealed record GeminiGroundedSearchHit(
    string Title,
    string Url,
    string Content);

/// <summary>
/// A single-purpose Google Search grounding adapter. The endpoint and model are
/// intentionally pinned so an API key cannot silently opt into a more expensive
/// model. Source URLs are accepted only from Gemini's grounding metadata.
/// </summary>
internal sealed class GeminiGroundedSearchProvider
{
    internal const string PinnedModel = "gemini-3.5-flash-lite";
    private const string EndpointRoot = "https://generativelanguage.googleapis.com/v1beta/models/";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly Lazy<HttpClient> SharedClient = new(CreateClient);

    private readonly HttpClient httpClient;
    private readonly Func<WebSourceBackendSettings> settingsProvider;
    private readonly GeminiGroundingUsageLedger usageLedger;

    public GeminiGroundedSearchProvider(
        Func<WebSourceBackendSettings> settingsProvider,
        string? dataRoot)
        : this(SharedClient.Value, settingsProvider, new GeminiGroundingUsageLedger(dataRoot))
    {
    }

    internal GeminiGroundedSearchProvider(
        HttpClient httpClient,
        Func<WebSourceBackendSettings> settingsProvider,
        GeminiGroundingUsageLedger usageLedger)
    {
        this.httpClient = httpClient;
        this.settingsProvider = settingsProvider;
        this.usageLedger = usageLedger;
    }

    public bool IsConfigured()
    {
        var settings = settingsProvider() ?? new WebSourceBackendSettings();
        return settings.GeminiGroundedSearchEnabled
            && !string.IsNullOrWhiteSpace(settings.ResolveGeminiApiKey());
    }

    public string UsageStatus() =>
        usageLedger.GetStatus(settingsProvider() ?? new WebSourceBackendSettings(), DateTimeOffset.UtcNow);

    public async Task<IReadOnlyList<GeminiGroundedSearchHit>> SearchAsync(
        string query,
        SourceQueryPlan plan,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var settings = settingsProvider() ?? new WebSourceBackendSettings();
        if (!settings.GeminiGroundedSearchEnabled)
        {
            warnings.Add("Google grounded search is disabled in Internet settings.");
            return [];
        }

        var apiKey = settings.ResolveGeminiApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            warnings.Add($"Google grounded search is not configured. Save a Gemini API key or set {settings.GeminiApiKeyEnvironmentVariable}.");
            return [];
        }

        var reservation = usageLedger.TryReserve(settings, DateTimeOffset.UtcNow);
        if (!reservation.Allowed || string.IsNullOrWhiteSpace(reservation.ReservationId))
        {
            warnings.Add(reservation.Status);
            return [];
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            EndpointRoot + PinnedModel + ":generateContent");
        request.Headers.Add("x-goog-api-key", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var maxOutputTokens = Math.Clamp(settings.GeminiMaxOutputTokens, 128, 2048);
        var prompt = BuildResearchPrompt(query, plan);
        var payload = new
        {
            system_instruction = new
            {
                parts = new[]
                {
                    new
                    {
                        text = "You are a search research component for another assistant. Use Google Search. Return concise factual research notes supported by the grounding sources. Never invent links or claim a source that was not returned by the grounding tool."
                    }
                }
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = prompt } }
                }
            },
            tools = new[] { new { google_search = new { } } },
            generationConfig = new
            {
                maxOutputTokens,
                thinkingConfig = new
                {
                    thinkingLevel = "minimal"
                }
            }
        };
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                warnings.Add(BuildSafeHttpFailure(response.StatusCode, body, apiKey));
                return [];
            }

            var parsed = JsonSerializer.Deserialize<GeminiResponse>(body, JsonOptions);
            var candidate = parsed?.Candidates?.FirstOrDefault();
            var answer = string.Join(
                Environment.NewLine,
                candidate?.Content?.Parts?
                    .Select(part => part.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                ?? []);
            var metadata = candidate?.GroundingMetadata;
            var chunks = metadata?.GroundingChunks ?? [];
            var searchQueryCount = metadata?.WebSearchQueries?
                .Count(queryText => !string.IsNullOrWhiteSpace(queryText)) ?? 0;
            var grounded = chunks.Any(chunk => chunk.Web is not null
                && Uri.TryCreate(chunk.Web.Uri, UriKind.Absolute, out _));
            var promptTokens = Math.Max(0, parsed?.UsageMetadata?.PromptTokenCount ?? 0);
            var outputTokens = ResolveOutputTokens(parsed?.UsageMetadata);
            usageLedger.RecordUsage(
                reservation.ReservationId,
                promptTokens,
                outputTokens,
                grounded,
                searchQueryCount,
                DateTimeOffset.UtcNow);

            var hits = BuildHits(chunks, metadata?.GroundingSupports ?? [], answer);
            if (hits.Count == 0)
            {
                warnings.Add("Google grounding returned no verifiable source citations, so Ali discarded the generated notes.");
            }
            return hits;
        }
        catch (HttpRequestException ex)
        {
            warnings.Add("Google grounded search failed safely: " + ex.Message);
            return [];
        }
    }

    private static IReadOnlyList<GeminiGroundedSearchHit> BuildHits(
        IReadOnlyList<GroundingChunk> chunks,
        IReadOnlyList<GroundingSupport> supports,
        string answer)
    {
        var results = new List<GeminiGroundedSearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < chunks.Count; index++)
        {
            var web = chunks[index].Web;
            if (web is null
                || !Uri.TryCreate(web.Uri, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || !seen.Add(uri.AbsoluteUri))
            {
                continue;
            }

            var supportedText = string.Join(
                " ",
                supports
                    .Where(support => support.GroundingChunkIndices?.Contains(index) == true)
                    .Select(support => support.Segment?.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(supportedText)) supportedText = answer;
            if (string.IsNullOrWhiteSpace(supportedText)) continue;
            results.Add(new GeminiGroundedSearchHit(
                string.IsNullOrWhiteSpace(web.Title) ? uri.Host : web.Title.Trim(),
                uri.AbsoluteUri,
                supportedText.Trim()));
        }
        return results;
    }

    private static string BuildResearchPrompt(string query, SourceQueryPlan plan)
    {
        var temporal = string.IsNullOrWhiteSpace(plan.TemporalSelection)
            ? "none"
            : plan.TemporalSelection;
        return $"Research this request using current Google Search results: {query.Trim()}\n"
            + $"Current UTC timestamp: {DateTimeOffset.UtcNow:O}. Temporal requirement: {temporal}.\n"
            + "Prioritize primary and authoritative sources. Distinguish confirmed facts from forecasts or interpretation.\n"
            + "For present conditions, current events, latest values, or today-specific claims, establish the source observation/publication date in the grounded text. "
            + "Do not describe an older dated observation as current. If Google grounding cannot establish freshness for a requested current claim, say that the current value was not verified instead of substituting an older observation.";
    }

    private static int ResolveOutputTokens(GeminiUsageMetadata? usage)
    {
        if (usage is null) return 0;
        var explicitOutput = Math.Max(0, usage.CandidatesTokenCount)
            + Math.Max(0, usage.ThoughtsTokenCount);
        var derivedOutput = Math.Max(0, usage.TotalTokenCount - usage.PromptTokenCount);
        return Math.Max(explicitOutput, derivedOutput);
    }

    private static string BuildSafeHttpFailure(HttpStatusCode statusCode, string body, string apiKey)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message))
            {
                var safeMessage = (message.GetString() ?? string.Empty)
                    .Replace(apiKey, "[redacted]", StringComparison.Ordinal);
                return $"Google grounded search returned HTTP {(int)statusCode}: "
                    + Trim(safeMessage, 300);
            }
        }
        catch (JsonException)
        {
        }
        return $"Google grounded search returned HTTP {(int)statusCode} ({statusCode}).";
    }

    private static string Trim(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? "The provider did not include an error message."
            : value.Trim().Length <= maximum
                ? value.Trim()
                : value.Trim()[..maximum] + "...";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AliLocalDesktop/1.0");
        return client;
    }

    private sealed class GeminiResponse
    {
        public List<GeminiCandidate>? Candidates { get; set; }

        public GeminiUsageMetadata? UsageMetadata { get; set; }
    }

    private sealed class GeminiCandidate
    {
        public GeminiContent? Content { get; set; }

        public GeminiGroundingMetadata? GroundingMetadata { get; set; }
    }

    private sealed class GeminiContent
    {
        public List<GeminiPart>? Parts { get; set; }
    }

    private sealed class GeminiPart
    {
        public string? Text { get; set; }
    }

    private sealed class GeminiGroundingMetadata
    {
        public List<string>? WebSearchQueries { get; set; }

        public List<GroundingChunk>? GroundingChunks { get; set; }

        public List<GroundingSupport>? GroundingSupports { get; set; }
    }

    private sealed class GroundingChunk
    {
        public GroundingWebSource? Web { get; set; }
    }

    private sealed class GroundingWebSource
    {
        public string? Uri { get; set; }

        public string? Title { get; set; }
    }

    private sealed class GroundingSupport
    {
        public GroundingSegment? Segment { get; set; }

        public List<int>? GroundingChunkIndices { get; set; }
    }

    private sealed class GroundingSegment
    {
        public string? Text { get; set; }
    }

    private sealed class GeminiUsageMetadata
    {
        public int PromptTokenCount { get; set; }

        public int CandidatesTokenCount { get; set; }

        public int ThoughtsTokenCount { get; set; }

        public int TotalTokenCount { get; set; }
    }
}
