using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ali.Modules.Internet;

namespace Ali.Modules.Internet;

public enum InternetSearchProvider
{
    Tavily,
    Firecrawl,
    BraveSearch,
    Serper
}

public sealed record InternetBackendProviderProbeResult(
    string Provider,
    bool IsConfigured,
    bool Succeeded,
    string Status,
    string RemainingEstimate);

public sealed class TavilyFirecrawlSourceRetriever : ISourceRetriever
{
    private readonly HttpClient httpClient;
    private readonly Func<WebSourceBackendSettings> settingsProvider;
    private WebSourceBackendSettings settings;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public TavilyFirecrawlSourceRetriever(HttpClient httpClient, WebSourceBackendSettings settings)
        : this(httpClient, () => settings)
    {
    }

    public TavilyFirecrawlSourceRetriever(HttpClient httpClient, Func<WebSourceBackendSettings> settingsProvider)
    {
        this.httpClient = httpClient;
        this.settingsProvider = settingsProvider;
        this.settings = settingsProvider() ?? new WebSourceBackendSettings();
    }

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
        settings = settingsProvider() ?? new WebSourceBackendSettings();

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
            results = await SearchWithBraveAsync(query, plan, warnings, cancellationToken).ConfigureAwait(false);
        }

        if (results.Count == 0)
        {
            results = await SearchWithSerperAsync(query, plan, warnings, cancellationToken).ConfigureAwait(false);
        }

        if (results.Count == 0)
        {
            if (warnings.Count == 0)
            {
                warnings.Add("No internet search results were returned by Tavily, Firecrawl, Brave Search, or Serper.");
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

    public async Task<InternetBackendProviderProbeResult> TestProviderAsync(
        InternetSearchProvider provider,
        string? query,
        CancellationToken cancellationToken)
    {
        settings = settingsProvider() ?? new WebSourceBackendSettings();
        var testQuery = string.IsNullOrWhiteSpace(query) ? "current weather in Birmingham Alabama" : query.Trim();
        var plan = new SourceQueryPlan(
            true,
            true,
            "internet_provider_test",
            testQuery,
            [testQuery],
            ["news", "weather"]);
        var warnings = new List<string>();
        var configured = IsProviderConfigured(provider);
        if (!configured)
        {
            return new InternetBackendProviderProbeResult(
                ProviderDisplayName(provider),
                false,
                false,
                MissingProviderConfigurationMessage(provider),
                ProviderRemainingUnavailable(provider));
        }

        var results = provider switch
        {
            InternetSearchProvider.Tavily => await SearchWithTavilyAsync(testQuery, plan, warnings, cancellationToken).ConfigureAwait(false),
            InternetSearchProvider.Firecrawl => await SearchWithFirecrawlAsync(testQuery, plan, warnings, cancellationToken).ConfigureAwait(false),
            InternetSearchProvider.BraveSearch => await SearchWithBraveAsync(testQuery, plan, warnings, cancellationToken).ConfigureAwait(false),
            InternetSearchProvider.Serper => await SearchWithSerperAsync(testQuery, plan, warnings, cancellationToken).ConfigureAwait(false),
            _ => Array.Empty<WebSearchResult>()
        };

        var remaining = (provider is InternetSearchProvider.Tavily ? TryReadTavilyUsageWarning(warnings) : null)
                        ?? TryReadProviderRemainingWarning(ProviderDisplayName(provider), warnings)
                        ?? await EstimateRemainingAsync(provider, warnings, cancellationToken).ConfigureAwait(false);
        if (results.Count > 0)
        {
            var first = results[0];
            return new InternetBackendProviderProbeResult(
                ProviderDisplayName(provider),
                true,
                true,
                $"OK: returned {results.Count} result(s). First: {first.Title}",
                remaining);
        }

        return new InternetBackendProviderProbeResult(
            ProviderDisplayName(provider),
            true,
            false,
            warnings.Count == 0 ? "Configured, but the test query returned no usable results." : string.Join(" ", warnings.Distinct(StringComparer.OrdinalIgnoreCase)),
            remaining);
    }

    public async Task<IReadOnlyList<InternetBackendProviderProbeResult>> TestConfiguredProvidersAsync(
        string? query,
        CancellationToken cancellationToken)
    {
        foreach (var provider in new[]
                 {
                     InternetSearchProvider.Tavily,
                     InternetSearchProvider.Firecrawl,
                     InternetSearchProvider.BraveSearch,
                     InternetSearchProvider.Serper
                 })
        {
            if (!IsProviderConfigured(provider))
            {
                continue;
            }

            return
            [
                await TestProviderAsync(provider, query, cancellationToken).ConfigureAwait(false)
            ];
        }

        return Array.Empty<InternetBackendProviderProbeResult>();
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
                include_usage = true,
                auto_parameters = false,
                safe_search = false
            };
            var body = await PostJsonAsync(
                BuildTavilyEndpoint("search"),
                payload,
                apiKey,
                cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<TavilySearchResponse>(body, JsonOptions);
            if (response?.Usage?.Credits is not null)
            {
                AddWarningOnce(warnings, $"Tavily reported this query used {response.Usage.Credits} credit(s).");
            }

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
                timeout = Math.Clamp(settings.RequestTimeoutSeconds, 5, 120) * 1000,
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

    private async Task<IReadOnlyList<WebSearchResult>> SearchWithBraveAsync(
        string query,
        SourceQueryPlan plan,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var apiKey = settings.ResolveBraveSearchApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            warnings.Add($"Brave Search API key is not configured. Set {settings.BraveSearchApiKeyEnvironmentVariable} or add braveSearchApiKey to internet_backends.json.");
            return Array.Empty<WebSearchResult>();
        }

        try
        {
            var endpoint = BuildBraveSearchEndpoint(query, plan);
            var response = await GetWithHeaderAsync(
                endpoint,
                "X-Subscription-Token",
                apiKey,
                cancellationToken).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<BraveSearchResponse>(response.Body, JsonOptions);
            var results = (parsed?.Web?.Results ?? [])
                .Select(BuildBraveSearchResult)
                .Where(result => result is not null)
                .Select(result => result!)
                .Take(Math.Clamp(settings.MaxSearchResults, 1, 10))
                .ToList();

            if (results.Count == 0)
            {
                warnings.Add("Brave Search fallback returned no web results.");
            }

            AddRemainingHeaderWarning("Brave Search", response.Headers, warnings);
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            warnings.Add($"Brave Search fallback failed: {ex.Message}");
            return Array.Empty<WebSearchResult>();
        }
    }

    private async Task<IReadOnlyList<WebSearchResult>> SearchWithSerperAsync(
        string query,
        SourceQueryPlan plan,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var apiKey = settings.ResolveSerperApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            warnings.Add($"Serper API key is not configured. Set {settings.SerperApiKeyEnvironmentVariable} or add serperApiKey to internet_backends.json.");
            return Array.Empty<WebSearchResult>();
        }

        try
        {
            var payload = new
            {
                q = query,
                num = Math.Clamp(settings.MaxSearchResults, 1, 10),
                gl = "us",
                hl = "en",
                tbs = ResolveSerperTbs(plan)
            };
            var response = await PostJsonWithHeaderAsync(
                BuildSerperEndpoint(IsCurrentNewsPlan(plan) ? "news" : "search"),
                payload,
                "X-API-KEY",
                apiKey,
                cancellationToken).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<SerperSearchResponse>(response.Body, JsonOptions);
            var results = (parsed?.Organic ?? [])
                .Select(BuildSerperOrganicResult)
                .Concat((parsed?.News ?? []).Select(BuildSerperNewsResult))
                .Where(result => result is not null)
                .Select(result => result!)
                .Take(Math.Clamp(settings.MaxSearchResults, 1, 10))
                .ToList();

            if (results.Count == 0)
            {
                warnings.Add("Serper fallback returned no web results.");
            }

            AddRemainingHeaderWarning("Serper", response.Headers, warnings);
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            warnings.Add($"Serper fallback failed: {ex.Message}");
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

    private async Task<HttpProviderResponse> PostJsonWithHeaderAsync(
        Uri endpoint,
        object payload,
        string headerName,
        string headerValue,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 5, 120)));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation(headerName, headerValue.Trim());
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{endpoint.Host}{endpoint.AbsolutePath} returned HTTP {(int)response.StatusCode}: {TrimError(body)}");
        }

        return new HttpProviderResponse(body, ReadHeaders(response));
    }

    private async Task<HttpProviderResponse> GetWithHeaderAsync(
        Uri endpoint,
        string headerName,
        string headerValue,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 5, 120)));
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation(headerName, headerValue.Trim());
        using var response = await httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{endpoint.Host}{endpoint.AbsolutePath} returned HTTP {(int)response.StatusCode}: {TrimError(body)}");
        }

        return new HttpProviderResponse(body, ReadHeaders(response));
    }

    private async Task<string> GetWithBearerAsync(
        Uri endpoint,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 5, 120)));
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearerToken.Trim()}");
        using var response = await httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{endpoint.Host}{endpoint.AbsolutePath} returned HTTP {(int)response.StatusCode}: {TrimError(body)}");
        }

        return body;
    }

    private static IReadOnlyDictionary<string, string> ReadHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        return headers;
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

    private static WebSearchResult? BuildBraveSearchResult(BraveSearchResult result)
    {
        var url = FirstNonBlank(result.Url);
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var snippets = new[] { result.Description }
            .Concat(result.ExtraSnippets ?? [])
            .Where(snippet => !string.IsNullOrWhiteSpace(snippet));
        return new WebSearchResult(
            FirstNonBlank(result.Title, url) ?? url,
            url,
            string.Join(Environment.NewLine, snippets),
            result.Description,
            result.Age,
            "Brave Search");
    }

    private static WebSearchResult? BuildSerperOrganicResult(SerperOrganicResult result)
    {
        var url = FirstNonBlank(result.Link);
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return new WebSearchResult(
            FirstNonBlank(result.Title, url) ?? url,
            url,
            FirstNonBlank(result.Snippet, result.Title),
            result.Snippet,
            result.Date,
            "Serper");
    }

    private static WebSearchResult? BuildSerperNewsResult(SerperNewsResult result)
    {
        var url = FirstNonBlank(result.Link);
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return new WebSearchResult(
            FirstNonBlank(result.Title, url) ?? url,
            url,
            FirstNonBlank(result.Snippet, result.Title),
            result.Snippet,
            result.Date,
            "Serper");
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private Uri BuildTavilyEndpoint(string path) =>
        BuildEndpoint(settings.TavilyBaseUrl, "https://api.tavily.com", path);

    private Uri BuildFirecrawlEndpoint(string path) =>
        BuildEndpoint(settings.FirecrawlBaseUrl, "https://api.firecrawl.dev/v2", path);

    private Uri BuildBraveSearchEndpoint(string query, SourceQueryPlan plan)
    {
        var endpoint = BuildEndpoint(settings.BraveSearchBaseUrl, "https://api.search.brave.com/res/v1/web", "search");
        var queryParts = new Dictionary<string, string?>
        {
            ["q"] = query,
            ["count"] = Math.Clamp(settings.MaxSearchResults, 1, 10).ToString(CultureInfo.InvariantCulture),
            ["country"] = "US",
            ["search_lang"] = "en",
            ["safesearch"] = "moderate",
            ["extra_snippets"] = "true",
            ["enable_rich_callback"] = HasPlanLabel(plan, "weather") || HasPlanLabel(plan, "sports") ? "1" : null,
            ["freshness"] = ResolveBraveFreshness(plan)
        };
        return new Uri($"{endpoint}?{BuildQueryString(queryParts)}", UriKind.Absolute);
    }

    private Uri BuildSerperEndpoint(string path) =>
        BuildEndpoint(settings.SerperBaseUrl, "https://google.serper.dev", path);

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

    private static string BuildQueryString(IReadOnlyDictionary<string, string?> values) =>
        string.Join(
            '&',
            values
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));

    private static string? ResolveBraveFreshness(SourceQueryPlan plan) =>
        IsCurrentNewsPlan(plan) ? "pd" : null;

    private static string? ResolveSerperTbs(SourceQueryPlan plan) =>
        IsCurrentNewsPlan(plan) ? "qdr:d" : null;

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

    private bool IsProviderConfigured(InternetSearchProvider provider) =>
        provider switch
        {
            InternetSearchProvider.Tavily => !string.IsNullOrWhiteSpace(settings.ResolveTavilyApiKey()),
            InternetSearchProvider.Firecrawl => !string.IsNullOrWhiteSpace(settings.ResolveFirecrawlApiKey()) || !IsOfficialFirecrawlEndpoint(settings.FirecrawlBaseUrl),
            InternetSearchProvider.BraveSearch => !string.IsNullOrWhiteSpace(settings.ResolveBraveSearchApiKey()),
            InternetSearchProvider.Serper => !string.IsNullOrWhiteSpace(settings.ResolveSerperApiKey()),
            _ => false
        };

    private string MissingProviderConfigurationMessage(InternetSearchProvider provider) =>
        provider switch
        {
            InternetSearchProvider.Tavily => $"Missing {settings.TavilyApiKeyEnvironmentVariable} or saved Tavily API key.",
            InternetSearchProvider.Firecrawl => $"Missing {settings.FirecrawlApiKeyEnvironmentVariable} or saved Firecrawl API key.",
            InternetSearchProvider.BraveSearch => $"Missing {settings.BraveSearchApiKeyEnvironmentVariable} or saved Brave Search API key.",
            InternetSearchProvider.Serper => $"Missing {settings.SerperApiKeyEnvironmentVariable} or saved Serper API key.",
            _ => "Provider is not configured."
        };

    private static string ProviderDisplayName(InternetSearchProvider provider) =>
        provider switch
        {
            InternetSearchProvider.Tavily => "Tavily",
            InternetSearchProvider.Firecrawl => "Firecrawl",
            InternetSearchProvider.BraveSearch => "Brave Search",
            InternetSearchProvider.Serper => "Serper",
            _ => provider.ToString()
        };

    private string ProviderRemainingUnavailable(InternetSearchProvider provider) =>
        provider is InternetSearchProvider.Serper && settings.SerperFreeQueryAllowance > 0
            ? $"Remaining unknown. Serper advertises {settings.SerperFreeQueryAllowance:N0} free starter queries, but the API response did not report remaining quota."
            : "Remaining unknown until a provider response reports quota headers or account usage.";

    private async Task<string> EstimateRemainingAsync(
        InternetSearchProvider provider,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (provider is InternetSearchProvider.Firecrawl)
        {
            return await EstimateFirecrawlRemainingAsync(warnings, cancellationToken).ConfigureAwait(false);
        }

        if (provider is InternetSearchProvider.Tavily)
        {
            return "Remaining unknown. Tavily search responses report per-request credits when enabled, but not account quota in this path.";
        }

        if (provider is InternetSearchProvider.Serper)
        {
            return ProviderRemainingUnavailable(provider);
        }

        return ProviderRemainingUnavailable(provider);
    }

    private async Task<string> EstimateFirecrawlRemainingAsync(
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!CanCallFirecrawl(out var apiKey, out var missingKeyWarning) || string.IsNullOrWhiteSpace(apiKey))
        {
            AddWarningOnce(warnings, missingKeyWarning);
            return "Remaining unknown. Firecrawl is not configured.";
        }

        try
        {
            var body = await GetWithBearerAsync(
                BuildFirecrawlEndpoint("team/credit-usage"),
                apiKey,
                cancellationToken).ConfigureAwait(false);
            return TryReadCreditUsageSummary(body) ?? "Remaining unknown. Firecrawl credit endpoint did not include a recognizable remaining field.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            AddWarningOnce(warnings, $"Firecrawl credit usage check failed: {ex.Message}");
            return "Remaining unknown. Firecrawl credit usage check failed.";
        }
    }

    private static void AddRemainingHeaderWarning(
        string provider,
        IReadOnlyDictionary<string, string> headers,
        List<string> warnings)
    {
        var remaining = ReadRemainingFromHeaders(headers);
        if (!string.IsNullOrWhiteSpace(remaining))
        {
            AddWarningOnce(warnings, $"{provider} reported remaining quota: {remaining}");
        }
    }

    private static string? TryReadProviderRemainingWarning(string provider, IReadOnlyList<string> warnings)
    {
        var prefix = $"{provider} reported remaining quota:";
        var warning = warnings
            .FirstOrDefault(warning => warning.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(warning)
            ? null
            : warning[prefix.Length..].Trim();
    }

    private static string? TryReadTavilyUsageWarning(IReadOnlyList<string> warnings)
    {
        var warning = warnings.FirstOrDefault(item => item.StartsWith("Tavily reported this query used", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(warning)
            ? null
            : $"{warning} Remaining account quota is not reported by Tavily search responses.";
    }

    private static string? ReadRemainingFromHeaders(IReadOnlyDictionary<string, string> headers)
    {
        foreach (var name in new[]
                 {
                     "X-RateLimit-Remaining",
                     "RateLimit-Remaining",
                     "Ratelimit-Remaining",
                     "X-Requests-Remaining",
                     "X-Searches-Remaining"
                 })
        {
            if (headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? TryReadCreditUsageSummary(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var remaining = TryReadJsonScalar(document.RootElement, "remainingCredits", "remaining_credits", "creditsRemaining", "credits_remaining", "remaining", "availableCredits", "available_credits");
            var used = TryReadJsonScalar(document.RootElement, "creditsUsed", "credits_used", "usedCredits", "used_credits", "used");
            var limit = TryReadJsonScalar(document.RootElement, "creditLimit", "credit_limit", "totalCredits", "total_credits", "limit");
            if (string.IsNullOrWhiteSpace(remaining))
            {
                return null;
            }

            var parts = new List<string> { $"Remaining: {remaining}" };
            if (!string.IsNullOrWhiteSpace(used))
            {
                parts.Add($"Used: {used}");
            }

            if (!string.IsNullOrWhiteSpace(limit))
            {
                parts.Add($"Limit: {limit}");
            }

            return string.Join("; ", parts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadJsonScalar(JsonElement value, params string[] names)
    {
        if (value.ValueKind is JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (value.TryGetProperty(name, out var property)
                    && property.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                {
                    return property.ToString();
                }
            }

            foreach (var property in value.EnumerateObject())
            {
                var nested = TryReadJsonScalar(property.Value, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        if (value.ValueKind is JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var nested = TryReadJsonScalar(item, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

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

    private sealed record HttpProviderResponse(
        string Body,
        IReadOnlyDictionary<string, string> Headers);

    private sealed record FirecrawlFormat(string Type);

    private sealed class TavilySearchResponse
    {
        public List<TavilySearchResult>? Results { get; set; }

        public TavilyUsage? Usage { get; set; }
    }

    private sealed class TavilyUsage
    {
        public int? Credits { get; set; }
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

    private sealed class BraveSearchResponse
    {
        public BraveWebResults? Web { get; set; }
    }

    private sealed class BraveWebResults
    {
        public List<BraveSearchResult>? Results { get; set; }
    }

    private sealed class BraveSearchResult
    {
        public string? Title { get; set; }

        public string? Url { get; set; }

        public string? Description { get; set; }

        public string? Age { get; set; }

        [JsonPropertyName("extra_snippets")]
        public List<string>? ExtraSnippets { get; set; }
    }

    private sealed class SerperSearchResponse
    {
        public List<SerperOrganicResult>? Organic { get; set; }

        public List<SerperNewsResult>? News { get; set; }
    }

    private sealed class SerperOrganicResult
    {
        public string? Title { get; set; }

        public string? Link { get; set; }

        public string? Snippet { get; set; }

        public string? Date { get; set; }
    }

    private sealed class SerperNewsResult
    {
        public string? Title { get; set; }

        public string? Link { get; set; }

        public string? Snippet { get; set; }

        public string? Date { get; set; }
    }
}
