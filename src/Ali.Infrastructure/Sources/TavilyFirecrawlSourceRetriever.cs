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

        var results = await ScrapeDirectUrlsAsync(plan, warnings, cancellationToken).ConfigureAwait(false);
        if (results.Count == 0)
        {
            results = await SearchWithTavilyAsync(query, plan, warnings, cancellationToken).ConfigureAwait(false);
        }

        if (results.Count == 0)
        {
            results = await SearchWithFirecrawlAsync(query, plan, warnings, cancellationToken).ConfigureAwait(false);
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
                && !result.Provider.Equals("Firecrawl", StringComparison.OrdinalIgnoreCase)
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

    private async Task<IReadOnlyList<WebSearchResult>> ScrapeDirectUrlsAsync(
        SourceQueryPlan plan,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var urls = ExtractDirectUrls(plan).ToArray();
        if (urls.Length == 0)
        {
            return Array.Empty<WebSearchResult>();
        }

        var results = new List<WebSearchResult>();
        foreach (var url in urls.Take(Math.Clamp(settings.MaxSearchResults, 1, 10)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scraped = await TryScrapeWithFirecrawlAsync(url, warnings, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(scraped))
            {
                continue;
            }

            results.Add(new WebSearchResult(
                BuildDirectUrlTitle(url),
                url,
                scraped,
                null,
                null,
                "Firecrawl"));
        }

        return results;
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
            var tavilyTopic = ResolveTavilyTopic(plan);
            var payload = new
            {
                query,
                search_depth = NormalizeTavilySearchDepth(settings.TavilySearchDepth),
                chunks_per_source = 3,
                max_results = Math.Clamp(settings.MaxSearchResults, 1, 10),
                topic = tavilyTopic,
                time_range = ResolveTavilyTimeRange(plan, tavilyTopic, settings),
                include_answer = false,
                include_raw_content = "markdown",
                include_images = false,
                include_image_descriptions = false,
                include_favicon = true,
                auto_parameters = false,
                safe_search = false
            };
            var body = await PostJsonAsync(
                BuildTavilyEndpoint("search"),
                payload,
                apiKey,
                cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<TavilySearchResponse>(body, JsonOptions);
            return response?.Results?
                       .Where(result => !string.IsNullOrWhiteSpace(result.Url))
                       .Select(result => new WebSearchResult(
                           result.Title ?? result.Url!,
                           result.Url!,
                           result.RawContent ?? result.Content,
                           result.Content,
                           result.PublishedDate,
                           "Tavily"))
                       .ToList()
                   ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            warnings.Add($"Tavily search failed: {ex.Message}");
            return Array.Empty<WebSearchResult>();
        }
    }

    private async Task<IReadOnlyList<WebSearchResult>> SearchWithFirecrawlAsync(
        string query,
        SourceQueryPlan plan,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!CanCallFirecrawl(out var apiKey, out var missingKeyWarning))
        {
            AddWarningOnce(warnings, missingKeyWarning);
            return Array.Empty<WebSearchResult>();
        }

        try
        {
            var payload = new
            {
                query,
                limit = Math.Clamp(settings.MaxSearchResults, 1, 10),
                sources = ResolveFirecrawlSources(plan),
                tbs = ResolveFirecrawlTbs(plan),
                scrapeOptions = settings.UseFirecrawlSearchScrapeOptions
                    ? new
                    {
                        formats = new[] { new FirecrawlFormat("markdown") },
                        onlyMainContent = true,
                        timeout = Math.Clamp(settings.RequestTimeoutSeconds, 5, 120) * 1000
                    }
                    : null
            };
            var body = await PostJsonAsync(
                BuildFirecrawlEndpoint("search"),
                payload,
                apiKey,
                cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<FirecrawlSearchResponse>(body, JsonOptions);
            if (response is null)
            {
                warnings.Add("Firecrawl fallback search returned an empty response.");
                return Array.Empty<WebSearchResult>();
            }

            if (response.Success is false)
            {
                warnings.Add($"Firecrawl fallback search failed: {BuildFirecrawlFailureDetail(response.Error, response.Code)}{BuildFirecrawlKeyHint()}");
                return Array.Empty<WebSearchResult>();
            }

            AddWarningOnce(warnings, response.Warning ?? string.Empty);
            var results = (response?.Data?.Web ?? [])
                .Concat(response?.Data?.News ?? [])
                .Select(BuildFirecrawlSearchResult)
                .Where(result => result is not null)
                .Select(result => result!)
                .ToList();
            if (results.Count == 0)
            {
                warnings.Add("Firecrawl fallback search returned no web results.");
            }

            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            warnings.Add($"Firecrawl fallback search failed: {ex.Message}{BuildFirecrawlKeyHint()}");
            return Array.Empty<WebSearchResult>();
        }
    }

    private async Task<string?> TryScrapeWithFirecrawlAsync(
        string url,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!CanCallFirecrawl(out var apiKey, out var missingKeyWarning))
        {
            AddWarningOnce(warnings, missingKeyWarning);
            return null;
        }

        try
        {
            var payload = new
            {
                url,
                formats = new[] { "markdown" },
                onlyMainContent = true,
                timeout = Math.Clamp(settings.RequestTimeoutSeconds, 5, 120) * 1000
            };
            var body = await PostJsonAsync(
                BuildFirecrawlEndpoint("scrape"),
                payload,
                apiKey,
                cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<FirecrawlScrapeResponse>(body, JsonOptions);
            if (response is null)
            {
                warnings.Add($"Firecrawl could not extract {url}: empty response.");
                return null;
            }

            if (response.Success is false)
            {
                warnings.Add($"Firecrawl could not extract {url}: {BuildFirecrawlFailureDetail(response.Error, response.Code)}{BuildFirecrawlKeyHint()}");
                return null;
            }

            AddWarningOnce(warnings, response.Data?.Warning ?? string.Empty);
            return ResolveFirecrawlScrapeText(response.Data);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            warnings.Add($"Firecrawl could not extract {url}: {ex.Message}{BuildFirecrawlKeyHint()}");
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

    private static IReadOnlyList<string> ExtractDirectUrls(SourceQueryPlan plan) =>
        new[] { plan.Topic }
            .Concat(plan.QueryTerms)
            .SelectMany(ExtractDirectUrls)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

    private static IEnumerable<string> ExtractDirectUrls(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var start = token.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                start = token.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
            }

            if (start < 0)
            {
                continue;
            }

            var candidate = token[start..].TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'');
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                yield return uri.ToString();
            }
        }
    }

    private static bool IsLocalDocumentPlan(SourceQueryPlan plan) =>
        ExtractDirectUrls(plan).Count == 0
        && (string.Equals(plan.Intent, "local_documents", StringComparison.OrdinalIgnoreCase)
            || string.Equals(plan.Topic, "local_documents", StringComparison.OrdinalIgnoreCase)
            || plan.PreferredSourceTopics.Any(topic => string.Equals(topic, "local_documents", StringComparison.OrdinalIgnoreCase)));

    private static string ResolveTopic(SourceQueryPlan plan) =>
        plan.PreferredSourceTopics.FirstOrDefault(topic => !string.IsNullOrWhiteSpace(topic))
        ?? (string.IsNullOrWhiteSpace(plan.Intent) ? "web" : plan.Intent);

    private static string ResolveTavilyTopic(SourceQueryPlan plan) =>
        HasPlanLabel(plan, "finance")
            ? "finance"
            : HasPlanLabel(plan, "news")
                ? "news"
                : "general";

    private static string? ResolveTavilyTimeRange(
        SourceQueryPlan plan,
        string tavilyTopic,
        WebSourceBackendSettings settings)
    {
        if (!IsCurrentNewsPlan(plan) && !string.Equals(tavilyTopic, "news", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return NormalizeTavilyTimeRange(settings.TavilyCurrentNewsTimeRange);
    }

    private static string? NormalizeTavilyTimeRange(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "day" or "week" or "month" or "year" or "d" or "w" or "m" or "y"
            ? normalized
            : "day";
    }

    private static string[] ResolveFirecrawlSources(SourceQueryPlan plan) =>
        IsCurrentNewsPlan(plan) ? ["web", "news"] : ["web"];

    private static string? ResolveFirecrawlTbs(SourceQueryPlan plan) =>
        IsCurrentNewsPlan(plan) ? "qdr:d" : null;

    private static bool IsCurrentNewsPlan(SourceQueryPlan plan) =>
        HasPlanLabel(plan, "news");

    private static bool HasPlanLabel(SourceQueryPlan plan, string label) =>
        plan.Intent.Contains(label, StringComparison.OrdinalIgnoreCase)
        || plan.Topic.Contains(label, StringComparison.OrdinalIgnoreCase)
        || plan.PreferredSourceTopics.Any(topic => topic.Contains(label, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeTavilySearchDepth(string? value) =>
        string.Equals(value, "basic", StringComparison.OrdinalIgnoreCase) ? "basic" : "advanced";

    private static string? ResolveFirecrawlScrapeText(FirecrawlScrapeData? data)
    {
        if (data is null)
        {
            return null;
        }

        foreach (var candidate in new[] { data.Markdown, data.Summary, data.Answer, data.Highlights })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static WebSearchResult? BuildFirecrawlSearchResult(FirecrawlSearchResult result)
    {
        var url = FirstNonBlank(result.Url, result.Metadata?.SourceUrl, result.Metadata?.Url);
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var title = FirstNonBlank(result.Title, result.Metadata?.Title, url) ?? url;
        var description = FirstNonBlank(result.Description, result.Snippet, result.Metadata?.Description);
        var content = FirstNonBlank(result.Markdown, description);
        return new WebSearchResult(
            title,
            url,
            content,
            description,
            result.Date,
            "Firecrawl");
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private Uri BuildTavilyEndpoint(string path) =>
        BuildEndpoint(settings.TavilyBaseUrl, "https://api.tavily.com", path);

    private Uri BuildFirecrawlEndpoint(string path) =>
        BuildEndpoint(settings.FirecrawlBaseUrl, "https://api.firecrawl.dev/v2", path);

    private static Uri BuildEndpoint(string baseUrl, string fallbackBaseUrl, string path)
    {
        var trimmedBase = string.IsNullOrWhiteSpace(baseUrl)
            ? fallbackBaseUrl
            : baseUrl.Trim().TrimEnd('/');
        var trimmedPath = path.Trim().TrimStart('/');
        if (trimmedBase.EndsWith($"/{trimmedPath}", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmedBase, UriKind.Absolute);
        }

        return new Uri($"{trimmedBase}/{trimmedPath}", UriKind.Absolute);
    }

    private static string BuildDirectUrlTitle(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"{uri.Host}{uri.AbsolutePath}"
            : url;

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
        var normalized = (TryReadApiError(text) ?? text)
            .ReplaceLineEndings(" ")
            .Trim();
        if (normalized.Contains("without an API key", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("API key", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "API key required or request rejected.";
        }

        return normalized.Length <= 240 ? normalized : $"{normalized[..237].TrimEnd()}...";
    }

    private string BuildFirecrawlKeyHint() =>
        string.IsNullOrWhiteSpace(settings.ResolveFirecrawlApiKey())
            ? $" Set {settings.FirecrawlApiKeyEnvironmentVariable} or add firecrawlApiKey to internet_backends.json."
            : string.Empty;

    private static string BuildFirecrawlFailureDetail(string? error, string? code)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return TrimError(error);
        }

        if (!string.IsNullOrWhiteSpace(code))
        {
            return code.Trim();
        }

        return "request did not succeed";
    }

    private bool CanCallFirecrawl(out string? apiKey, out string missingKeyWarning)
    {
        apiKey = settings.ResolveFirecrawlApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey) || !IsOfficialFirecrawlEndpoint(settings.FirecrawlBaseUrl))
        {
            missingKeyWarning = string.Empty;
            return true;
        }

        missingKeyWarning = $"Firecrawl API key is not configured. Set {settings.FirecrawlApiKeyEnvironmentVariable} or add firecrawlApiKey to internet_backends.json.";
        return false;
    }

    private static bool IsOfficialFirecrawlEndpoint(string? baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl)
        || Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, "api.firecrawl.dev", StringComparison.OrdinalIgnoreCase);

    private static void AddWarningOnce(List<string> warnings, string warning)
    {
        if (string.IsNullOrWhiteSpace(warning)
            || warnings.Any(existing => string.Equals(existing, warning, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        warnings.Add(warning);
    }

    private static string? TryReadApiError(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return TryReadApiError(document.RootElement, depth: 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadApiError(JsonElement value, int depth)
    {
        if (depth > 4)
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind is JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "error", "message", "detail", "code" })
            {
                if (value.TryGetProperty(propertyName, out var propertyValue))
                {
                    var message = TryReadApiError(propertyValue, depth + 1);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message;
                    }
                }
            }

            foreach (var property in value.EnumerateObject())
            {
                var message = TryReadApiError(property.Value, depth + 1);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
        }

        if (value.ValueKind is JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var message = TryReadApiError(item, depth + 1);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
        }

        return null;
    }

    private sealed record WebSearchResult(
        string Title,
        string Url,
        string? Content,
        string? Description,
        string? PublishedDate,
        string Provider);

    private sealed record FirecrawlFormat(string Type);

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
        public bool? Success { get; set; }

        public FirecrawlSearchData? Data { get; set; }

        public string? Warning { get; set; }

        public string? Code { get; set; }

        public string? Error { get; set; }
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

        public string? Snippet { get; set; }

        public string? Markdown { get; set; }

        public string? Date { get; set; }

        public FirecrawlSearchMetadata? Metadata { get; set; }
    }

    private sealed class FirecrawlSearchMetadata
    {
        public string? Title { get; set; }

        public string? Url { get; set; }

        [JsonPropertyName("sourceURL")]
        public string? SourceUrl { get; set; }

        public string? Description { get; set; }
    }

    private sealed class FirecrawlScrapeResponse
    {
        public bool? Success { get; set; }

        public FirecrawlScrapeData? Data { get; set; }

        public string? Code { get; set; }

        public string? Error { get; set; }
    }

    private sealed class FirecrawlScrapeData
    {
        public string? Markdown { get; set; }

        public string? Summary { get; set; }

        public string? Answer { get; set; }

        public string? Highlights { get; set; }

        public string? Warning { get; set; }
    }
}
