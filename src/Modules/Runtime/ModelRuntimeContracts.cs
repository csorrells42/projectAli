using Ali.Modules.Evidence;
using Ali.Modules.Runtime.Models;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Runtime;

public sealed record ChatRequest(
    string ConversationId,
    string UserMessageId,
    string UserText,
    IReadOnlyList<ChatMessage> History)
{
    public IReadOnlyList<ChatAttachment> Attachments { get; init; } = Array.Empty<ChatAttachment>();
}

public sealed record ChatAttachment(
    string Id,
    AttachmentKind Kind,
    string FileName,
    string ContentType,
    string Base64Data,
    bool RetainAfterSession,
    DateTimeOffset CreatedAt);

public enum AttachmentKind
{
    Image
}

public sealed record ChatMessage(
    string Id,
    ChatRole Role,
    string Text,
    DateTimeOffset CreatedAt,
    EvidenceStatus EvidenceStatus = EvidenceStatus.Unverified);

public enum ChatRole
{
    User,
    Assistant,
    System
}

public sealed record ModelToken(
    string Text,
    EvidenceStatus EvidenceStatus,
    string? FinishReason = null,
    bool IsThinking = false)
{
    public bool ReachedOutputLimit =>
        string.Equals(FinishReason, "length", StringComparison.OrdinalIgnoreCase);
}

public sealed record RuntimeHealthCheck(
    bool Succeeded,
    string Summary,
    DateTimeOffset CheckedAt,
    TimeSpan Elapsed,
    string? Endpoint = null,
    string? ModelPackageId = null,
    int? ContextTokens = null,
    int? OutputTokenLimit = null,
    double? Temperature = null,
    bool? StreamingSupported = null,
    string? ErrorText = null)
{
    public RuntimeCapabilityProfile? CapabilityProfile { get; init; }
}

public interface ILocalModelRuntime
{
    ModelProfile ActiveProfile { get; }

    IAsyncEnumerable<ModelToken> StreamChatAsync(
        ChatRequest request,
        CancellationToken cancellationToken);

    Task<RuntimeHealthCheck> CheckHealthAsync(CancellationToken cancellationToken);

    Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public interface IModelSwitchAwareRuntime
{
    string RuntimeIdentity { get; }

    Task UnloadForModelSwitchAsync(CancellationToken cancellationToken);
}

public interface IReasoningEffortRuntime
{
    string ReasoningEffort { get; }

    void SetReasoningEffort(string effort);
}

public interface IOpenRouterReasoningRuntime
{
    string? OpenRouterReasoningEffort { get; }

    void SetOpenRouterReasoningEffort(string? effort);
}

/// <summary>
/// Internal request properties shared by model-facing orchestration lanes. The runtime consumes
/// these mechanically; they are not user prompt text and never select a tool or interpret intent.
/// </summary>
internal static class AliInternalModelRoutingProperties
{
    internal const string SuppressInjectedPersona = "ali.internalRouting";
    internal const string BoundReasoningEffort = "ali.boundReasoningEffort";
    internal const string BoundOpenRouterReasoningEffort = "ali.boundOpenRouterReasoningEffort";
}

internal sealed record BoundRuntimeBindingMaterial(
    string Engine,
    string Implementation,
    string RuntimeKind,
    string RuntimeLocation,
    string RuntimeEndpoint)
{
    public string ProtocolIdentity { get; init; } = RuntimeProtocolIdentities.ChatOnly;

    public string CapabilityProfileIdentity { get; init; } = "unprobed";
}

internal sealed record BoundModelBindingMaterial(
    string ProfileId,
    string PackageId,
    string Family,
    string Size,
    string Quantization,
    bool SupportsVision,
    bool SupportsToolCalls)
{
    public string CapabilityProfileIdentity { get; init; } = "unprobed";
}

internal sealed record BoundGenerationSettingsBindingMaterial(
    int ContextTokens,
    int OutputTokenLimit,
    double? Temperature,
    double? TopP,
    bool? StreamingEnabled,
    string ThinkingControl,
    bool? ThinkingEnabled,
    string ReasoningEffort)
{
    public string? OpenRouterReasoningEffort { get; init; }

    public string TokenizerIdentity { get; init; } = "provider-reported-or-unknown";

    public string RollingWindowMode { get; init; } = "provider-managed";

    public string ProtocolIdentity { get; init; } = RuntimeProtocolIdentities.ChatOnly;
}

/// <summary>
/// One immutable dispatch view of a concrete model client and every setting that can change the
/// request it sends. Capturing the client itself prevents a switching wrapper from redirecting an
/// already-authorized completion to a different model.
/// </summary>
internal sealed record BoundModelDispatchSnapshot(
    IChatClient ChatClient,
    ModelProfile Profile,
    BoundRuntimeBindingMaterial RuntimeBinding,
    BoundModelBindingMaterial ModelBinding,
    BoundGenerationSettingsBindingMaterial GenerationSettingsBinding);

/// <summary>
/// Captures the exact concrete client/profile/settings tuple that one model dispatch will use.
/// The returned client is borrowed from the runtime owner and must not be disposed by callers.
/// </summary>
internal interface IBoundModelDispatchSource
{
    BoundModelDispatchSnapshot CaptureBoundModelDispatch();
}
