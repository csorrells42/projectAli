using Ali.Modules.Runtime;
using Ali.Modules.Runtime.Models;

namespace Ali.Framework.Tests.OrchestrationV2;

internal static class PlanningTestModelProfile
{
    internal static ModelProfile GptOss65K() => new(
        ProfileId: "test-gpt-oss-65k",
        DisplayName: "GPT-OSS 20B test profile",
        RuntimeLocation: "test",
        RuntimeEndpoint: "http://127.0.0.1:1234/v1/",
        RuntimeKind: "test",
        PackageId: "openai/gpt-oss-20b",
        Family: "gpt-oss",
        Size: "20b",
        Quantization: "test",
        ContextTokens: 65_536,
        OutputTokenLimit: 8_192,
        Temperature: null,
        StreamingEnabled: false,
        SupportsVision: true,
        SupportsToolCalls: true,
        IsLastKnownGood: true)
    {
        ProtocolIdentity = RuntimeProtocolIdentities.StructuredDecision,
        CapabilityProfileIdentity = "test-probed-engineering-profile-v1"
    };
}
