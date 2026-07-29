using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Ali.Modules.Internet;

public sealed class WebSourceBackendSettings
{
    public bool Enabled { get; set; } = true;

    public bool GeminiGroundedSearchEnabled { get; set; } = true;

    public string GeminiApiKeyEnvironmentVariable { get; set; } = "GEMINI_API_KEY";

    public string? GeminiApiKey { get; set; }

    public string GeminiGroundedSearchModel { get; set; } = "gemini-3.5-flash-lite";

    public int GeminiMaxOutputTokens { get; set; } = 1024;

    public int GeminiMaxRequestsPerHour { get; set; } = 30;

    public int GeminiMaxRequestsPerDay { get; set; } = 150;

    public decimal GeminiMonthlySpendLimitUsd { get; set; } = 5m;

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

    public bool UseMcpResearch { get; set; } = true;

    public int McpResearchTimeoutSeconds { get; set; } = 120;

    public int MaxSearchResults { get; set; } = 5;

    public int MaxExtractedPages { get; set; } = 3;

    public int MaxExcerptCharacters { get; set; } = 2400;

    public int RequestTimeoutSeconds { get; set; } = 25;

    public string? ResolveTavilyApiKey() =>
        ResolveApiKey(TavilyApiKey, TavilyApiKeyEnvironmentVariable);

    public string? ResolveGeminiApiKey() =>
        ResolveApiKey(GeminiApiKey, GeminiApiKeyEnvironmentVariable);

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
    private const string ProtectedPrefix = "dpapi:v1:";
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("Ali.GoogleGrounding.ApiKey.v1");
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
            WebSourceBackendSettings settings;
            using (var stream = File.OpenRead(path))
            {
                settings = JsonSerializer.Deserialize<WebSourceBackendSettings>(stream, JsonOptions)
                           ?? new WebSourceBackendSettings();
            }

            var storedKey = settings.GeminiApiKey;
            settings.GeminiApiKey = UnprotectSecret(storedKey);
            if (!string.IsNullOrWhiteSpace(storedKey)
                && !storedKey.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            {
                // Transparently migrate an older plain-text key the first time
                // this version reads the settings file.
                Save(dataRoot, settings);
            }
            return settings;
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
        var plainTextKey = settings.GeminiApiKey;
        try
        {
            settings.GeminiApiKey = ProtectSecret(plainTextKey);
            var temporary = path + ".tmp";
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            settings.GeminiApiKey = plainTextKey;
        }
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

    internal static string? ProtectSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith(ProtectedPrefix, StringComparison.Ordinal)) return value;
        var plain = Encoding.UTF8.GetBytes(value.Trim());
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, DpapiEntropy, DataProtectionScope.CurrentUser);
            try
            {
                return ProtectedPrefix + Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    internal static string? UnprotectSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!value.StartsWith(ProtectedPrefix, StringComparison.Ordinal)) return value.Trim();
        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(value[ProtectedPrefix.Length..]);
        }
        catch (FormatException)
        {
            return null;
        }
        try
        {
            byte[] plain;
            try
            {
                plain = ProtectedData.Unprotect(protectedBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                // A settings folder copied to a different Windows account must
                // not reveal or crash on the original account's protected key.
                return null;
            }
            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }
}
