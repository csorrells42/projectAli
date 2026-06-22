using Ali.Core.Models;

namespace Ali.Infrastructure.Runtime;

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
            SupportsVision: SupportsVision,
            SupportsToolCalls: SupportsToolCalls,
            IsLastKnownGood: isLastKnownGood);
}
