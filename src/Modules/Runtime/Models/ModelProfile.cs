namespace Ali.Modules.Runtime.Models;

public sealed record ModelProfile(
    string ProfileId,
    string DisplayName,
    string RuntimeLocation,
    string RuntimeEndpoint,
    string RuntimeKind,
    string PackageId,
    string Family,
    string Size,
    string Quantization,
    int ContextTokens,
    int OutputTokenLimit,
    double? Temperature,
    bool? StreamingEnabled,
    bool SupportsVision,
    bool SupportsToolCalls,
    bool IsLastKnownGood)
{
    public int MaximumToolActionsPerRequest { get; init; } = 512;

    public string ProtocolIdentity { get; init; } = RuntimeProtocolIdentities.ChatOnly;

    public string CapabilityProfileIdentity { get; init; } = "unprobed";

    public string TokenizerIdentity { get; init; } = "provider-reported-or-unknown";

    public string RollingWindowMode { get; init; } = "provider-managed";

    public static ModelProfile UnconfiguredFactorySafe() =>
        new(
            ProfileId: "factory-safe-unconfigured",
            DisplayName: "Factory Safe - local runtime not configured",
            RuntimeLocation: "This PC",
            RuntimeEndpoint: "none",
            RuntimeKind: "DevelopmentStub",
            PackageId: "none",
            Family: "none",
            Size: "none",
            Quantization: "Q4",
            ContextTokens: 4096,
            OutputTokenLimit: 512,
            Temperature: null,
            StreamingEnabled: null,
            SupportsVision: false,
            SupportsToolCalls: false,
            IsLastKnownGood: false);
}
