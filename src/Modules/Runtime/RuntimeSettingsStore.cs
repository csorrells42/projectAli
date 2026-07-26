using System.Text.Json;

namespace Ali.Modules.Runtime;

public static class RuntimeSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string GetSettingsPath(string dataDirectory) =>
        Path.Combine(dataDirectory, "runtime-settings.json");

    public static string GetExamplePath(string dataDirectory) =>
        Path.Combine(dataDirectory, "runtime-settings.example.json");

    public static OpenAiCompatibleRuntimeOptions GetDefaultOptions() =>
        new(
            Enabled: false,
            Endpoint: new Uri("http://127.0.0.1:11434/v1/"),
            Model: string.Empty,
            DisplayName: "Local OpenAI-compatible runtime",
            Family: "local",
            Size: "unknown",
            Quantization: "Q4",
            ContextTokens: OllamaRuntimeSafetyPolicy.DefaultContextTokens,
            OutputTokenLimit: 256,
            Temperature: 0.2,
            TopP: null,
            StreamingEnabled: true,
            SupportsVision: false,
            SupportsToolCalls: false,
            AllowPrivateLanEndpoint: false);

    public static OpenAiCompatibleRuntimeOptions LoadOrDefault(string dataDirectory) =>
        LoadOpenAiCompatibleOptions(dataDirectory) ?? GetDefaultOptions();

    public static OpenAiCompatibleRuntimeOptions? LoadOpenAiCompatibleOptions(string dataDirectory)
    {
        var filePath = GetSettingsPath(dataDirectory);
        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var options = JsonSerializer.Deserialize<OpenAiCompatibleRuntimeOptions>(json, JsonOptions);
                return options is null ? null : OllamaRuntimeSafetyPolicy.Normalize(options);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return null;
            }
        }

        var endpoint = Environment.GetEnvironmentVariable("ALI_OPENAI_BASE_URL");
        var model = Environment.GetEnvironmentVariable("ALI_OPENAI_MODEL");

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return OllamaRuntimeSafetyPolicy.Normalize(new OpenAiCompatibleRuntimeOptions(
            Enabled: true,
            Endpoint: new Uri(endpoint),
            Model: model,
            DisplayName: Environment.GetEnvironmentVariable("ALI_OPENAI_DISPLAY_NAME") ?? $"Local {model}",
            Family: Environment.GetEnvironmentVariable("ALI_OPENAI_FAMILY") ?? "local",
            Size: Environment.GetEnvironmentVariable("ALI_OPENAI_SIZE") ?? "unknown",
            Quantization: Environment.GetEnvironmentVariable("ALI_OPENAI_QUANTIZATION") ?? "Q4",
            ContextTokens: ReadIntEnvironment("ALI_OPENAI_CONTEXT", OllamaRuntimeSafetyPolicy.DefaultContextTokens),
            OutputTokenLimit: ReadIntEnvironment("ALI_OPENAI_OUTPUT_LIMIT", 256),
            Temperature: ReadDoubleEnvironment("ALI_OPENAI_TEMPERATURE", 0.2),
            TopP: ReadNullableDoubleEnvironment("ALI_OPENAI_TOP_P"),
            StreamingEnabled: ReadBoolEnvironment("ALI_OPENAI_STREAMING", true),
            SupportsVision: ReadBoolEnvironment("ALI_OPENAI_SUPPORTS_VISION", false),
            SupportsToolCalls: ReadBoolEnvironment("ALI_OPENAI_SUPPORTS_TOOL_CALLS", false),
            AllowPrivateLanEndpoint: ReadBoolEnvironment("ALI_ALLOW_PRIVATE_LAN_RUNTIME", false)));
    }

    public static void Save(string dataDirectory, OpenAiCompatibleRuntimeOptions options)
    {
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(
            GetSettingsPath(dataDirectory),
            JsonSerializer.Serialize(OllamaRuntimeSafetyPolicy.Normalize(options), JsonOptions));
    }

    public static void WriteDefaultIfMissing(string dataDirectory)
    {
        var filePath = GetSettingsPath(dataDirectory);
        if (File.Exists(filePath))
        {
            return;
        }

        Save(dataDirectory, LoadOpenAiCompatibleOptions(dataDirectory) ?? GetDefaultOptions());
    }

    public static void WriteExample(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var filePath = GetExamplePath(dataDirectory);
        if (File.Exists(filePath))
        {
            return;
        }

        var options = new OpenAiCompatibleRuntimeOptions(
            Enabled: true,
            Endpoint: new Uri("http://127.0.0.1:11434/v1/"),
            Model: "ali-deepseek-coder-v2:16b-low",
            DisplayName: "Ali DeepSeek Coder V2 16B - technical",
            Family: "DeepSeek Coder",
            Size: "16B",
            Quantization: "Q4 low-load",
            ContextTokens: 4096,
            OutputTokenLimit: 256,
            Temperature: 0.2,
            TopP: null,
            StreamingEnabled: true,
            SupportsVision: false,
            SupportsToolCalls: false,
            AllowPrivateLanEndpoint: false);

        File.WriteAllText(filePath, JsonSerializer.Serialize(options, JsonOptions));
    }

    private static int ReadIntEnvironment(string name, int defaultValue) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : defaultValue;

    private static double ReadDoubleEnvironment(string name, double defaultValue) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : defaultValue;

    private static double? ReadNullableDoubleEnvironment(string name) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : null;

    private static bool ReadBoolEnvironment(string name, bool defaultValue) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : defaultValue;
}

