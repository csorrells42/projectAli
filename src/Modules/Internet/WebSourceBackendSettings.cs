using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;

namespace Ali.Modules.Internet;

internal static class WebProviderAuthenticationModes
{
    public const string ApiKey = "api-key";
    public const string None = "none";
}

public sealed class WebSourceBackendSettings
{
    public bool Enabled { get; set; } = true;

    public List<string> CurrentSearchProviderOrder { get; set; } =
    [
        nameof(InternetSearchProvider.Tavily),
        nameof(InternetSearchProvider.GoogleGroundedSearch),
        nameof(InternetSearchProvider.Firecrawl),
        nameof(InternetSearchProvider.BraveSearch),
        nameof(InternetSearchProvider.Serper)
    ];

    public bool GeminiGroundedSearchEnabled { get; set; } = true;

    public string GeminiApiKeyEnvironmentVariable { get; set; } = "GEMINI_API_KEY";

    public string? GeminiApiKey { get; set; }

    public string GeminiBaseUrl { get; set; } = string.Empty;

    public string GeminiGroundedSearchModel { get; set; } = string.Empty;

    public int GeminiMaxOutputTokens { get; set; } = 1024;

    public int GeminiMaxRequestsPerHour { get; set; } = 30;

    public int GeminiMaxRequestsPerDay { get; set; } = 150;

    public decimal GeminiMonthlySpendLimitUsd { get; set; } = 5m;

    public string TavilyBaseUrl { get; set; } = string.Empty;

    public string TavilyMcpEndpointTemplate { get; set; } = string.Empty;

    public string TavilyApiKeyEnvironmentVariable { get; set; } = "TAVILY_API_KEY";

    public string? TavilyApiKey { get; set; }

    public string TavilySearchDepth { get; set; } = "advanced";

    public string TavilyCurrentNewsTimeRange { get; set; } = "day";

    public string FirecrawlBaseUrl { get; set; } = string.Empty;

    public string FirecrawlMcpEndpointTemplate { get; set; } = string.Empty;

    public string FirecrawlAuthenticationMode { get; set; } = string.Empty;

    public string FirecrawlApiKeyEnvironmentVariable { get; set; } = "FIRECRAWL_API_KEY";

    public string? FirecrawlApiKey { get; set; }

    public string BraveSearchBaseUrl { get; set; } = string.Empty;

    public string BraveSearchApiKeyEnvironmentVariable { get; set; } = "BRAVE_SEARCH_API_KEY";

    public string? BraveSearchApiKey { get; set; }

    public string SerperBaseUrl { get; set; } = string.Empty;

    public string SerperApiKeyEnvironmentVariable { get; set; } = "SERPER_API_KEY";

    public string? SerperApiKey { get; set; }

    public int SerperFreeQueryAllowance { get; set; } = 2500;

    public bool UseFirecrawlForPageExtraction { get; set; } = true;

    public bool UseFirecrawlSearchScrapeOptions { get; set; } = true;

    public bool UseMcpResearch { get; set; } = true;

    public int McpResearchTimeoutSeconds { get; set; }

    public string GoogleMapsDirectionsBaseUrl { get; set; } = string.Empty;

    public int MaxSearchResults { get; set; } = 5;

    public int MaxExtractedPages { get; set; } = 3;

    public int MaxExcerptCharacters { get; set; } = 2400;

    public int RequestTimeoutSeconds { get; set; }

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
    public const string SeedConfigurationFileName = "internet-provider-defaults.json";

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

    public static string GetSeedConfigurationPath() =>
        Path.Combine(AppContext.BaseDirectory, "Configuration", SeedConfigurationFileName);

    public static WebSourceBackendSettings LoadOrDefault(string dataRoot)
    {
        var path = GetSettingsPath(dataRoot);
        if (!File.Exists(path))
        {
            return LoadSeedConfiguration();
        }

        try
        {
            WebSourceBackendSettings settings;
            var json = File.ReadAllText(path);
            var effectiveJson = MergeExternalDefaultsForMissingProperties(json);
            settings = JsonSerializer.Deserialize<WebSourceBackendSettings>(effectiveJson, JsonOptions)
                       ?? throw new InvalidDataException(
                           $"The existing Internet backend settings file '{path}' is empty and was not replaced.");

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
            throw new InvalidDataException(
                $"The existing Internet backend settings file '{path}' could not be loaded and was not replaced.",
                ex);
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

        Save(dataRoot, LoadSeedConfiguration());
    }

    public static void WriteExample(string dataRoot)
    {
        var path = GetExamplePath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            return;
        }

        File.Copy(GetRequiredSeedConfigurationPath(), path);
    }

    private static WebSourceBackendSettings LoadSeedConfiguration()
    {
        var path = GetRequiredSeedConfigurationPath();
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<WebSourceBackendSettings>(stream, JsonOptions)
                   ?? throw new InvalidDataException(
                       $"The external Internet provider seed '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"The external Internet provider seed '{path}' is not valid JSON.",
                ex);
        }
    }

    private static string MergeExternalDefaultsForMissingProperties(string sourceJson)
    {
        var source = JsonNode.Parse(sourceJson) as JsonObject
            ?? throw new InvalidDataException(
                "The existing Internet backend settings must be a JSON object.");
        var seed = JsonNode.Parse(File.ReadAllText(GetRequiredSeedConfigurationPath())) as JsonObject
            ?? throw new InvalidDataException(
                "The external Internet provider seed must be a JSON object.");
        foreach (var property in seed)
        {
            if (!source.ContainsKey(property.Key))
            {
                source[property.Key] = property.Value?.DeepClone();
            }
        }

        return source.ToJsonString(JsonOptions);
    }

    private static string GetRequiredSeedConfigurationPath()
    {
        var path = GetSeedConfigurationPath();
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                "The external Internet provider seed configuration was not found.",
                path);
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
