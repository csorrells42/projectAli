using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ali.Core.Sources;

namespace Ali.Infrastructure.Sources;

public sealed class TavilyFirecrawlSourceRetriever(
    HttpClient httpClient,
    WebSourceBackendSettings settings) : ISourceRetriever
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public Task<SourceRetrievalResult> RetrieveAsync(string userText, CancellationToken cancellationToken) =>
        RetrieveAsync(
            new SourceQueryPlan(
                true,
                true,
                "general_sources",
                userText,
                [userText],
                Array.Empty<string>()),
            cancellationToken);

    public async Task<SourceRetrievalResult> RetrieveAsync(
        SourceQueryPlan plan,
        CancellationToken cancellationToken)
    {
        if (!plan.UseSources || IsLocalDocumentPlan(plan))
        {
            return SourceRetrievalResult.Empty;
        }

        if (!settings.Enabled)
        {
            return new SourceRetrievalResult(
                Array.Empty<SourceExcerpt>(),
                ["Internet source backend is disabled in internet_backends.json."],
                plan.RequiresSourceGrounding);
        }

        var warnings = new List<string>();
        var query = BuildSearchQuery(plan);
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SourceRetrievalResult(
                Array.Empty<SourceExcerpt>(),
                ["The source planner requested internet lookup but did not provide a usable query."],
                plan.RequiresSourceGrounding);
        }

        var results = await SearchWithTavilyAsync(query, plan, warnings, cancellationToken).ConfigureAwait(false);
        if (results.Count == 0)
        {
            results = await SearchWithFirecrawlAsync(query, warnings, cancellationToken).ConfigureAwait(false);
        }

        if (results.Count == 0)
        {
            if (warnings.Count == 0)
            {
                warnings.Add("No internet search results were returned by Tavily or Firecrawl.");
            }

            return new SourceRetrievalResult(Array.Empty<SourceExcerpt>(), warnings, plan.RequiresSourceGrounding);
        }

        var excerpts = new List<SourceExcerpt>();
        var now = DateTimeOffset.UtcNow;
        var maxResults = Math.Clamp(settings.MaxSearchResults, 1, 10);
        var maxExtractedPages = Math.Clamp(settings.MaxExtractedPages, 0, maxResults);
        var excerptLimit = Math.Clamp(settings.MaxExcerptCharacters, 600, 8000);

        foreach (var result in results.Take(maxResults))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var excerpt = result.Content;
            if (settings.UseFirecrawlForPageExtraction
                && excerpts.Count < maxExtractedPages
                && !string.IsNullOrWhiteSpace(result.Url))
            {
                var scraped = await TryScrapeWithFirecrawlAsync(result.Url, warnings, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(scraped))
                {
                    excerpt = scraped;
                }
            }

            if (string.IsNullOrWhiteSpace(excerpt))
            {
                excerpt = result.Description;
            }

            if (string.IsNullOrWhiteSpace(excerpt))
            {
                continue;
            }

            excerpts.Add(new SourceExcerpt(
                excerpts.Count + 1,
                ResolveTopic(plan),
                string.IsNullOrWhiteSpace(result.Title) ? result.Url : result.Title,
                result.Url,
                now,
                TrimExcerpt(BuildExcerptBody(result, excerpt), excerptLimit)));
        }

        return excerpts.Count == 0
            ? new SourceRetrievalResult(
                Array.Empty<SourceExcerpt>(),
                warnings.Count == 0 ? ["Internet search results did not include usable text."] : warnings,
                plan.RequiresSourceGrounding)
            : new SourceRetrievalResult(excerpts, warnings, plan.RequiresSourceGrounding);
    }

    private async Task<IReadOnlyList<WebSearchResult>> SearchWithTavilyAsync(
        string query,
        SourceQueryPlan plan,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var apiKey = settings.ResolveTavilyApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            warnings.Add($"Tavily API key is not configured. Set {settings.TavilyApiKeyEnvironmentVariable} or add tavilyApiKey to internet_backends.json.");
            return Array.Empty<WebSearchResult>();
        }

        try
        {
            var payload = new
            {
                query,
                search_depth = NormalizeTavilySearchDepth(settings.TavilySearchDepth),
                chunks_per_source = 3,
                max_results = Math.Clamp(settings.MaxSearchResults, 1, 10),
                topic = ResolveTavilyTopic(plan),
                include_answer = false,
                include_raw_content = false,
                include_images = false,
                include_image_descriptions = false,
                include_favicon = true,
                auto_parameters = false,
                safe_search = false
            };
            var body = await PostJsonAsync(
                BuildEndpoint(settings.TavilyBaseUrl, "search"),
                payload,
                apiKey,
                cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<TavilySearchResponse>(body, JsonOptions);
            return response?.Results?
                       .Where(result => !string.IsNullOrWhiteSpace(result.Url))
                       .Select(result => new WebSearchResult(
                           result.Title ?? result.Url!,
                           result.Url!,
                           result.Content,
                           result.Content,
                           result.PublishedDate,
                           "Tavily"))
                       .ToList()
                   ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            warnings.Add($"Tavily search failed: {ex.Message}");
            return Array.Empty<WebSearchResult>();
        }
    }

    private async Task<IReadOnlyList<WebSearchResult>> SearchWithFirecrawlAsync(
        string query,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = new
            {
                query,
                limit = Math.Clamp(settings.MaxSearchResults, 1, 10)
            };
            var body = await PostJsonAsync(
                BuildEndpoint(settings.FirecrawlBaseUrl, "search"),
                payload,
                settings.ResolveFirecrawlApiKey(),
                cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<FirecrawlSearchResponse>(body, JsonOptions);
            var results = (response?.Data?.Web ?? [])
                .Concat(response?.Data?.News ?? [])
                .Where(result => !string.IsNullOrWhiteSpace(result.Url))
                .Select(result => new WebSearchResult(
                    result.Title ?? result.Url!,
                    result.Url!,
                    result.Description,
                    result.Description,
                    result.Date,
                    "Firecrawl"))
                .ToList();
            if (results.Count == 0)
            {
                warnings.Add("Firecrawl fallback search returned no web results.");
            }

            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            warnings.Add($"Firecrawl fallback search failed: {ex.Message}");
            return Array.Empty<WebSearchResult>();
        }
    }

    private async Task<string?> TryScrapeWithFirecrawlAsync(
        string url,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = new
            {
                url,
                formats = new[] { "markdown" }
            };
            var body = await PostJsonAsync(
                BuildEndpoint(settings.FirecrawlBaseUrl, "scrape"),
                payload,
                settings.ResolveFirecrawlApiKey(),
                cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<FirecrawlScrapeResponse>(body, JsonOptions);
            return response?.Data?.Markdown;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            warnings.Add($"Firecrawl could not extract {url}: {ex.Message}");
            return null;
        }
    }

    private async Task<string> PostJsonAsync(
        Uri endpoint,
        object payload,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 5, 120)));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearerToken.Trim()}");
        }

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{endpoint.Host}{endpoint.AbsolutePath} returned HTTP {(int)response.StatusCode}: {TrimError(body)}");
        }

        return body;
    }

    private static string BuildSearchQuery(SourceQueryPlan plan)
    {
        var query = string.Join(
            ' ',
            new[] { plan.Topic }
                .Concat(plan.QueryTerms)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(query) ? plan.SearchText : query;
    }

    private static bool IsLocalDocumentPlan(SourceQueryPlan plan) =>
        string.Equals(plan.Intent, "local_documents", StringComparison.OrdinalIgnoreCase)
        || string.Equals(plan.Topic, "local_documents", StringComparison.OrdinalIgnoreCase)
        || plan.PreferredSourceTopics.Any(topic => string.Equals(topic, "local_documents", StringComparison.OrdinalIgnoreCase));

    private static string ResolveTopic(SourceQueryPlan plan) =>
        plan.PreferredSourceTopics.FirstOrDefault(topic => !string.IsNullOrWhiteSpace(topic))
        ?? (string.IsNullOrWhiteSpace(plan.Intent) ? "web" : plan.Intent);

    private static string ResolveTavilyTopic(SourceQueryPlan plan) =>
        plan.Intent.Contains("news", StringComparison.OrdinalIgnoreCase)
        || plan.PreferredSourceTopics.Any(topic => topic.Contains("news", StringComparison.OrdinalIgnoreCase))
            ? "news"
            : "general";

    private static string NormalizeTavilySearchDepth(string? value) =>
        string.Equals(value, "basic", StringComparison.OrdinalIgnoreCase) ? "basic" : "advanced";

    private static Uri BuildEndpoint(string baseUrl, string path)
    {
        var trimmedBase = string.IsNullOrWhiteSpace(baseUrl)
            ? "https://api.tavily.com"
            : baseUrl.Trim().TrimEnd('/');
        var trimmedPath = path.Trim().TrimStart('/');
        if (trimmedBase.EndsWith($"/{trimmedPath}", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmedBase, UriKind.Absolute);
        }

        return new Uri($"{trimmedBase}/{trimmedPath}", UriKind.Absolute);
    }

    private static string BuildExcerptBody(WebSearchResult result, string excerpt)
    {
        var lines = new List<string>
        {
            $"Provider: {result.Provider}"
        };
        if (!string.IsNullOrWhiteSpace(result.PublishedDate))
        {
            lines.Add($"Published: {result.PublishedDate}");
        }

        lines.Add(excerpt);
        return string.Join(Environment.NewLine, lines);
    }

    private static string TrimExcerpt(string text, int limit)
    {
        var normalized = text.Replace("\0", string.Empty).ReplaceLineEndings(Environment.NewLine).Trim();
        return normalized.Length <= limit ? normalized : normalized[..limit];
    }

    private static string TrimError(string text)
    {
        var normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }

    private sealed record WebSearchResult(
        string Title,
        string Url,
        string? Content,
        string? Description,
        string? PublishedDate,
        string Provider);

    private sealed class TavilySearchResponse
    {
        public List<TavilySearchResult>? Results { get; set; }
    }

    private sealed class TavilySearchResult
    {
        public string? Title { get; set; }

        public string? Url { get; set; }

        public string? Content { get; set; }

        [JsonPropertyName("raw_content")]
        public string? RawContent { get; set; }

        [JsonPropertyName("published_date")]
        public string? PublishedDate { get; set; }
    }

    private sealed class FirecrawlSearchResponse
    {
        public FirecrawlSearchData? Data { get; set; }
    }

    private sealed class FirecrawlSearchData
    {
        public List<FirecrawlSearchResult>? Web { get; set; }

        public List<FirecrawlSearchResult>? News { get; set; }
    }

    private sealed class FirecrawlSearchResult
    {
        public string? Title { get; set; }

        public string? Url { get; set; }

        public string? Description { get; set; }

        public string? Date { get; set; }
    }

    private sealed class FirecrawlScrapeResponse
    {
        public FirecrawlScrapeData? Data { get; set; }
    }

    private sealed class FirecrawlScrapeData
    {
        public string? Markdown { get; set; }
    }
}
