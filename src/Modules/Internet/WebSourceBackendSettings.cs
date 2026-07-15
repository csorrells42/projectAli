using System.Text.Json;

namespace Ali.Modules.Internet;

public sealed class WebSourceBackendSettings
{
    public bool Enabled { get; set; } = true;

    public string TavilyBaseUrl { get; set; } = "https://api.tavily.com";

    public string TavilyApiKeyEnvironmentVariable { get; set; } = "TAVILY_API_KEY";

    public string? TavilyApiKey { get; set; }

    public string TavilySearchDepth { get; set; } = "advanced";

    public string TavilyCurrentNewsTimeRange { get; set; } = "day";

    public string FirecrawlBaseUrl { get; set; } = "https://api.firecrawl.dev/v2";

    public string FirecrawlApiKeyEnvironmentVariable { get; set; } = "FIRECRAWL_API_KEY";

    public string? FirecrawlApiKey { get; set; }

    public string BraveSearchBaseUrl { get; set; } = "https://api.search.brave.com/res/v1/web/search";

    public string BraveSearchApiKeyEnvironmentVariable { get; set; } = "BRAVE_SEARCH_API_KEY";

    public string? BraveSearchApiKey { get; set; }

    public string SerperBaseUrl { get; set; } = "https://google.serper.dev";

    public string SerperApiKeyEnvironmentVariable { get; set; } = "SERPER_API_KEY";

    public string? SerperApiKey { get; set; }

    public int SerperFreeQueryAllowance { get; set; } = 2500;

    public bool UseFirecrawlForPageExtraction { get; set; } = true;

    public bool UseFirecrawlSearchScrapeOptions { get; set; } = true;

    public int MaxSearchResults { get; set; } = 5;

    public int MaxExtractedPages { get; set; } = 3;

    public int MaxExcerptCharacters { get; set; } = 2400;

    public int RequestTimeoutSeconds { get; set; } = 25;

    public string? ResolveTavilyApiKey() =>
        ResolveApiKey(TavilyApiKey, TavilyApiKeyEnvironmentVariable);

    public string? ResolveFirecrawlApiKey() =>
        ResolveApiKey(FirecrawlApiKey, FirecrawlApiKeyEnvironmentVariable);

    public string? ResolveBraveSearchApiKey() =>
        ResolveApiKey(BraveSearchApiKey, BraveSearchApiKeyEnvironmentVariable);

    public string? ResolveSerperApiKey() =>
        ResolveApiKey(SerperApiKey, SerperApiKeyEnvironmentVariable);

    private static string? ResolveApiKey(string? configuredValue, string? environmentVariable)
    {
        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            return configuredValue.Trim();
        }

        return string.IsNullOrWhiteSpace(environmentVariable)
            ? null
            : Environment.GetEnvironmentVariable(environmentVariable.Trim());
    }
}

public static class WebSourceBackendSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string GetSettingsPath(string dataRoot) =>
        Path.Combine(dataRoot, "Sources", "internet_backends.json");

    public static string GetExamplePath(string dataRoot) =>
        Path.Combine(dataRoot, "Sources", "internet_backends.example.json");

    public static WebSourceBackendSettings LoadOrDefault(string dataRoot)
    {
        var path = GetSettingsPath(dataRoot);
        if (!File.Exists(path))
        {
            return new WebSourceBackendSettings();
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<WebSourceBackendSettings>(stream, JsonOptions)
                   ?? new WebSourceBackendSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new WebSourceBackendSettings();
        }
    }

    public static void Save(string dataRoot, WebSourceBackendSettings settings)
    {
        var path = GetSettingsPath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, settings, JsonOptions);
    }

    public static void WriteDefaultIfMissing(string dataRoot)
    {
        var path = GetSettingsPath(dataRoot);
        if (File.Exists(path))
        {
            return;
        }

        Save(dataRoot, new WebSourceBackendSettings());
    }

    public static void WriteExample(string dataRoot)
    {
        var path = GetExamplePath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            return;
        }

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, new WebSourceBackendSettings(), JsonOptions);
    }
}
