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

    public ModelThinkingControl ThinkingControl { get; init; } = ModelThinkingControl.None;

    public bool AllowRemoteHttpsEndpoint { get; init; }

    public string ApiKeyEnvironmentVariable { get; init; } =
        RuntimeCredentialStore.DefaultApiKeyEnvironmentVariable;

    public string TokenizerIdentity { get; init; } = "provider-reported-or-unknown";

    public string RollingWindowMode { get; init; } = "provider-managed";

    public bool CapabilityProbeEnabled { get; init; } = true;

    public int MaximumToolActionsPerRequest { get; init; } = 512;

    public string? OpenRouterReasoningEffort { get; init; }

    public string? ProviderOnly { get; init; }

    public string? FallbackModel { get; init; }

    public string? FallbackProviderOnly { get; init; }

    public ModelProfile ToModelProfile(bool isLastKnownGood) =>
        new(
            ProfileId: $"openai-compatible-{Model}-{Quantization}-{ContextTokens}",
            DisplayName: DisplayName,
            RuntimeLocation: LocalEndpointPolicy.IsRemote(Endpoint)
                ? "Remote HTTPS runtime"
                : AllowPrivateLanEndpoint
                    ? "Private LAN AI Workstation"
                    : "This PC",
            RuntimeEndpoint: Endpoint.ToString(),
            RuntimeKind: LocalEndpointPolicy.IsRemote(Endpoint)
                ? $"{LocalRuntimeEngines.Normalize(Engine)} remote HTTPS"
                : $"{LocalRuntimeEngines.Normalize(Engine)} local HTTP",
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
            IsLastKnownGood: isLastKnownGood)
        {
            MaximumToolActionsPerRequest = MaximumToolActionsPerRequest,
            ProtocolIdentity = RuntimeProtocolIdentities.ChatOnly,
            TokenizerIdentity = TokenizerIdentity,
            RollingWindowMode = RollingWindowMode
        };
}

public static class LocalRuntimeEngines
{
    public const string LmStudio = "LM Studio";
    public const string Ollama = "Ollama";
    public const string LlamaCpp = "llama.cpp";
    public const string Lemonade = "Lemonade";
    public const string GenericOpenAi = "OpenAI-compatible/Custom";

    public static IReadOnlyList<string> Choices { get; } =
    [
        LmStudio,
        Ollama,
        LlamaCpp,
        Lemonade,
        GenericOpenAi
    ];

    public static string Normalize(string? engine)
    {
        if (string.IsNullOrWhiteSpace(engine))
        {
            return GenericOpenAi;
        }

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

        if (string.Equals(engine, GenericOpenAi, StringComparison.OrdinalIgnoreCase)
            || string.Equals(engine, "OpenAI-compatible", StringComparison.OrdinalIgnoreCase)
            || string.Equals(engine, "OpenAI-compatible / Custom", StringComparison.OrdinalIgnoreCase)
            || string.Equals(engine, "OpenAI compatible", StringComparison.OrdinalIgnoreCase)
            || string.Equals(engine, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            return GenericOpenAi;
        }

        return GenericOpenAi;
    }

    public static string Normalize(string? engine, Uri endpoint) => Normalize(engine);

    public static Uri DefaultEndpoint(string engine)
    {
        var presetId = Normalize(engine) switch
        {
            LmStudio => "lm-studio",
            Ollama => "ollama",
            LlamaCpp => "llama-cpp",
            Lemonade => "lemonade",
            _ => "openai-compatible-custom"
        };
        var preset = RuntimeProviderPresetCatalog.LoadDefault().RequireLlm(presetId);
        return Uri.TryCreate(preset.Endpoint, UriKind.Absolute, out var endpoint)
            ? endpoint
            : throw new InvalidOperationException(
                $"Runtime preset '{preset.DisplayName}' does not provide a default endpoint.");
    }
}

public static class OllamaRuntimeSafetyPolicy
{
    public const int DefaultContextTokens = 8192;
    public const int DefaultGptOssContextTokens = 65_536;
    public const int DefaultGptOssOutputTokenLimit = 8_192;
    public const string KeepAlive = "30m";
    public const string DefaultGptOssReasoningEffort = "low";

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
        if (options.ThinkingControl != ModelThinkingControl.GptOssReasoningEffort)
        {
            return "off";
        }

        return NormalizeGptOssReasoningEffort(options.ReasoningEffort);
    }

    public static OpenAiCompatibleRuntimeOptions Normalize(OpenAiCompatibleRuntimeOptions options) =>
        LocalRuntimeEngines.Normalize(options.Engine) == LocalRuntimeEngines.Ollama
            ? options with
            {
                Engine = LocalRuntimeEngines.Ollama,
                ContextTokens = ResolveContextTokens(options.ContextTokens),
                ReasoningEffort = options.ThinkingControl == ModelThinkingControl.GptOssReasoningEffort
                    ? ResolveReasoningEffort(options)
                    : null
            }
            : options with { Engine = LocalRuntimeEngines.Normalize(options.Engine) };
}
