using Ali.Modules.Runtime.Models;

namespace Ali.Modules.Runtime;

public sealed record OpenAiCompatibleRuntimeOptions(
    bool Enabled,
    Uri Endpoint,
    string Model,
    string DisplayName,
    string Family,
    string Size,
    string Quantization,
    int ContextTokens,
    int OutputTokenLimit,
    double Temperature,
    double? TopP,
    bool StreamingEnabled,
    bool SupportsVision,
    bool SupportsToolCalls,
    bool AllowPrivateLanEndpoint)
{
    public string Engine { get; init; } = string.Empty;

    public string? ReasoningEffort { get; init; }

    public bool ThinkingEnabled { get; init; }

    public ModelProfile ToModelProfile(bool isLastKnownGood) =>
        new(
            ProfileId: $"openai-compatible-{Model}-{Quantization}-{ContextTokens}",
            DisplayName: DisplayName,
            RuntimeLocation: AllowPrivateLanEndpoint ? "Private LAN AI Workstation" : "This PC",
            RuntimeEndpoint: Endpoint.ToString(),
            RuntimeKind: $"{LocalRuntimeEngines.Normalize(Engine, Endpoint)} local HTTP",
            PackageId: Model,
            Family: Family,
            Size: Size,
            Quantization: Quantization,
            ContextTokens: ContextTokens,
            OutputTokenLimit: OutputTokenLimit,
            Temperature: Temperature,
            StreamingEnabled: StreamingEnabled,
            SupportsVision: SupportsVision,
            SupportsToolCalls: SupportsToolCalls,
            IsLastKnownGood: isLastKnownGood);
}

public static class LocalRuntimeEngines
{
    public const string LmStudio = "LM Studio";
    public const string Ollama = "Ollama";
    public const string LlamaCpp = "llama.cpp";
    public const string Lemonade = "Lemonade";
    public const string GenericOpenAi = "OpenAI-compatible";

    public static IReadOnlyList<string> Choices { get; } =
    [
        LmStudio,
        GenericOpenAi,
        Lemonade
    ];

    public static string Normalize(string? engine, Uri endpoint)
    {
        if (string.Equals(engine, LmStudio, StringComparison.OrdinalIgnoreCase)
            || string.Equals(engine, "LMStudio", StringComparison.OrdinalIgnoreCase)
            || string.Equals(engine, "lm-studio", StringComparison.OrdinalIgnoreCase))
        {
            return LmStudio;
        }

        if (string.Equals(engine, Ollama, StringComparison.OrdinalIgnoreCase))
        {
            return Ollama;
        }

        if (string.Equals(engine, LlamaCpp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(engine, "llamacpp", StringComparison.OrdinalIgnoreCase))
        {
            return LlamaCpp;
        }

        if (string.Equals(engine, Lemonade, StringComparison.OrdinalIgnoreCase))
        {
            return Lemonade;
        }

        if (string.Equals(engine, GenericOpenAi, StringComparison.OrdinalIgnoreCase))
        {
            return GenericOpenAi;
        }

        return endpoint.Port switch
        {
            1234 => LmStudio,
            11434 => Ollama,
            8080 => LlamaCpp,
            13305 => Lemonade,
            _ => GenericOpenAi
        };
    }

    public static Uri DefaultEndpoint(string engine) => Normalize(engine, new Uri("http://127.0.0.1")) switch
    {
        LmStudio => new Uri("http://127.0.0.1:1234/v1/"),
        Ollama => new Uri("http://127.0.0.1:11434/v1/"),
        LlamaCpp => new Uri("http://127.0.0.1:8080/v1/"),
        Lemonade => new Uri("http://127.0.0.1:13305/api/v1/"),
        _ => new Uri("http://127.0.0.1:1234/v1/")
    };
}

public static class OllamaRuntimeSafetyPolicy
{
    public const int DefaultContextTokens = 8192;
    public const string KeepAlive = "30m";
    public const string DefaultGptOssReasoningEffort = "low";

    public static bool IsNativeOllamaEndpoint(Uri endpoint) => endpoint.Port == 11434;

    public static int ResolveContextTokens(int configured) =>
        configured > 0 ? configured : DefaultContextTokens;

    public static bool IsGptOssModel(string? model) =>
        !string.IsNullOrWhiteSpace(model)
        && model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeGptOssReasoningEffort(string? effort) =>
        effort?.Trim().ToLowerInvariant() switch
        {
            "medium" => "medium",
            "high" => "high",
            _ => DefaultGptOssReasoningEffort
        };

    public static string ResolveReasoningEffort(OpenAiCompatibleRuntimeOptions options)
    {
        if (!IsGptOssModel(options.Model) && !IsGptOssModel(options.Family))
        {
            return "off";
        }

        return NormalizeGptOssReasoningEffort(options.ReasoningEffort);
    }

    public static OpenAiCompatibleRuntimeOptions Normalize(OpenAiCompatibleRuntimeOptions options) =>
        LocalRuntimeEngines.Normalize(options.Engine, options.Endpoint) == LocalRuntimeEngines.Ollama
            ? options with
            {
                Engine = LocalRuntimeEngines.Ollama,
                ContextTokens = ResolveContextTokens(options.ContextTokens),
                ReasoningEffort = IsGptOssModel(options.Model) || IsGptOssModel(options.Family)
                    ? ResolveReasoningEffort(options)
                    : null
            }
            : options with { Engine = LocalRuntimeEngines.Normalize(options.Engine, options.Endpoint) };
}
