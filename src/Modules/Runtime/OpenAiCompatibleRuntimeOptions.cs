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
    public string? ReasoningEffort { get; init; }

    public ModelProfile ToModelProfile(bool isLastKnownGood) =>
        new(
            ProfileId: $"openai-compatible-{Model}-{Quantization}-{ContextTokens}",
            DisplayName: DisplayName,
            RuntimeLocation: AllowPrivateLanEndpoint ? "Private LAN AI Workstation" : "This PC",
            RuntimeEndpoint: Endpoint.ToString(),
            RuntimeKind: "OpenAI-compatible local HTTP",
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

public static class OllamaRuntimeSafetyPolicy
{
    public const int DefaultContextTokens = 8192;
    public const int MaximumContextTokens = 32768;
    public const string KeepAlive = "30m";
    public const string DefaultGptOssReasoningEffort = "low";

    public static bool IsNativeOllamaEndpoint(Uri endpoint) => endpoint.Port == 11434;

    public static int ClampContextTokens(int configured) =>
        Math.Clamp(configured > 0 ? configured : DefaultContextTokens, 512, MaximumContextTokens);

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
        IsNativeOllamaEndpoint(options.Endpoint)
            ? options with
            {
                ContextTokens = ClampContextTokens(options.ContextTokens),
                ReasoningEffort = IsGptOssModel(options.Model) || IsGptOssModel(options.Family)
                    ? ResolveReasoningEffort(options)
                    : null
            }
            : options;
}
