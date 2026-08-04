namespace Ali.Modules.Internet;

public sealed record WebSourceBackendSettingsSnapshot(
    long Version,
    WebSourceBackendSettings Settings);

public sealed class WebSourceBackendSettingsSnapshotOwner
{
    private readonly string _dataRoot;
    private readonly object _writerGate = new();
    private WebSourceBackendSettingsSnapshot _published;

    public WebSourceBackendSettingsSnapshotOwner(string dataRoot)
    {
        _dataRoot = Path.GetFullPath(
            dataRoot ?? throw new ArgumentNullException(nameof(dataRoot)));
        _published = new WebSourceBackendSettingsSnapshot(
            1,
            Clone(WebSourceBackendSettingsStore.LoadOrDefault(_dataRoot)));
    }

    public WebSourceBackendSettingsSnapshot Capture()
    {
        var published = Volatile.Read(ref _published);
        return new WebSourceBackendSettingsSnapshot(
            published.Version,
            Clone(published.Settings));
    }

    public WebSourceBackendSettingsSnapshot Save(WebSourceBackendSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_writerGate)
        {
            var frozen = Clone(settings);
            WebSourceBackendSettingsStore.Save(_dataRoot, frozen);
            return Publish(frozen);
        }
    }

    public WebSourceBackendSettingsSnapshot Reload()
    {
        lock (_writerGate)
        {
            var loaded = Clone(WebSourceBackendSettingsStore.LoadOrDefault(_dataRoot));
            return Publish(loaded);
        }
    }

    private WebSourceBackendSettingsSnapshot Publish(WebSourceBackendSettings settings)
    {
        var current = Volatile.Read(ref _published);
        var next = new WebSourceBackendSettingsSnapshot(
            current.Version == long.MaxValue ? 1 : current.Version + 1,
            Clone(settings));
        Volatile.Write(ref _published, next);
        return new WebSourceBackendSettingsSnapshot(next.Version, Clone(next.Settings));
    }

    private static WebSourceBackendSettings Clone(WebSourceBackendSettings settings) =>
        new()
        {
            Enabled = settings.Enabled,
            CurrentSearchProviderOrder = settings.CurrentSearchProviderOrder is null
                ? []
                : [.. settings.CurrentSearchProviderOrder],
            GeminiGroundedSearchEnabled = settings.GeminiGroundedSearchEnabled,
            GeminiApiKeyEnvironmentVariable = settings.GeminiApiKeyEnvironmentVariable,
            GeminiApiKey = settings.GeminiApiKey,
            GeminiBaseUrl = settings.GeminiBaseUrl,
            GeminiGroundedSearchModel = settings.GeminiGroundedSearchModel,
            GeminiMaxOutputTokens = settings.GeminiMaxOutputTokens,
            GeminiMaxRequestsPerHour = settings.GeminiMaxRequestsPerHour,
            GeminiMaxRequestsPerDay = settings.GeminiMaxRequestsPerDay,
            GeminiMonthlySpendLimitUsd = settings.GeminiMonthlySpendLimitUsd,
            TavilyBaseUrl = settings.TavilyBaseUrl,
            TavilyMcpEndpointTemplate = settings.TavilyMcpEndpointTemplate,
            TavilyApiKeyEnvironmentVariable = settings.TavilyApiKeyEnvironmentVariable,
            TavilyApiKey = settings.TavilyApiKey,
            TavilySearchDepth = settings.TavilySearchDepth,
            TavilyCurrentNewsTimeRange = settings.TavilyCurrentNewsTimeRange,
            FirecrawlBaseUrl = settings.FirecrawlBaseUrl,
            FirecrawlMcpEndpointTemplate = settings.FirecrawlMcpEndpointTemplate,
            FirecrawlAuthenticationMode = settings.FirecrawlAuthenticationMode,
            FirecrawlApiKeyEnvironmentVariable = settings.FirecrawlApiKeyEnvironmentVariable,
            FirecrawlApiKey = settings.FirecrawlApiKey,
            BraveSearchBaseUrl = settings.BraveSearchBaseUrl,
            BraveSearchApiKeyEnvironmentVariable = settings.BraveSearchApiKeyEnvironmentVariable,
            BraveSearchApiKey = settings.BraveSearchApiKey,
            SerperBaseUrl = settings.SerperBaseUrl,
            SerperApiKeyEnvironmentVariable = settings.SerperApiKeyEnvironmentVariable,
            SerperApiKey = settings.SerperApiKey,
            SerperFreeQueryAllowance = settings.SerperFreeQueryAllowance,
            UseFirecrawlForPageExtraction = settings.UseFirecrawlForPageExtraction,
            UseFirecrawlSearchScrapeOptions = settings.UseFirecrawlSearchScrapeOptions,
            UseMcpResearch = settings.UseMcpResearch,
            McpResearchTimeoutSeconds = settings.McpResearchTimeoutSeconds,
            GoogleMapsDirectionsBaseUrl = settings.GoogleMapsDirectionsBaseUrl,
            MaxSearchResults = settings.MaxSearchResults,
            MaxExtractedPages = settings.MaxExtractedPages,
            MaxExcerptCharacters = settings.MaxExcerptCharacters,
            RequestTimeoutSeconds = settings.RequestTimeoutSeconds
        };
}
