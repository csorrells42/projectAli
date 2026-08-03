using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ali.Modules.Runtime;

public enum RuntimeCapabilityState
{
    Unknown,
    Supported,
    Unsupported
}

public sealed record RuntimeCapabilityObservation(
    RuntimeCapabilityState State,
    string Provenance,
    DateTimeOffset ObservedAtUtc,
    string? Detail = null);

public sealed record RuntimeCapabilityProfile(
    string Identity,
    string Provider,
    string Endpoint,
    string Model,
    string ProtocolIdentity,
    string TokenizerIdentity,
    string RollingWindowMode,
    int ContextTokens,
    int OutputTokenLimit,
    RuntimeCapabilityObservation NativeToolCalling,
    RuntimeCapabilityObservation StructuredDecision,
    RuntimeCapabilityObservation ReasoningControl,
    RuntimeCapabilityObservation Streaming,
    RuntimeCapabilityObservation Vision)
{
    public bool IsEngineeringProtocolSafe =>
        NativeToolCalling.State == RuntimeCapabilityState.Supported
        || StructuredDecision.State == RuntimeCapabilityState.Supported;

    internal static RuntimeCapabilityProfile Create(
        OpenAiCompatibleRuntimeOptions options,
        RuntimeCapabilityObservation nativeToolCalling,
        RuntimeCapabilityObservation structuredDecision,
        RuntimeCapabilityObservation reasoningControl,
        RuntimeCapabilityObservation streaming,
        RuntimeCapabilityObservation vision)
    {
        ArgumentNullException.ThrowIfNull(options);
        var provider = LocalRuntimeEngines.Normalize(options.Engine);
        var endpoint = options.Endpoint.AbsoluteUri;
        var protocolIdentity = RuntimeProtocolIdentities.Resolve(options, nativeToolCalling, structuredDecision);
        var identityMaterial = JsonSerializer.SerializeToUtf8Bytes(new
        {
            provider,
            endpoint,
            options.Model,
            protocolIdentity,
            options.TokenizerIdentity,
            options.RollingWindowMode,
            options.ContextTokens,
            options.OutputTokenLimit,
            options.Temperature,
            options.TopP,
            options.StreamingEnabled,
            options.ThinkingControl,
            options.ThinkingEnabled,
            options.ReasoningEffort,
            nativeToolCallingState = nativeToolCalling.State,
            structuredDecisionState = structuredDecision.State,
            reasoningControlState = reasoningControl.State,
            streamingState = streaming.State,
            visionState = vision.State
        });
        try
        {
            return new RuntimeCapabilityProfile(
                Convert.ToHexString(SHA256.HashData(identityMaterial)).ToLowerInvariant(),
                provider,
                endpoint,
                options.Model,
                protocolIdentity,
                options.TokenizerIdentity,
                options.RollingWindowMode,
                options.ContextTokens,
                options.OutputTokenLimit,
                nativeToolCalling,
                structuredDecision,
                reasoningControl,
                streaming,
                vision);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identityMaterial);
        }
    }
}

internal static class RuntimeProtocolIdentities
{
    internal const string NativeOpenAiTools = "openai-compatible-native-tools-v1";
    internal const string StructuredDecision = "ali-validated-json-schema-decision-v1";
    internal const string ChatOnly = "openai-compatible-chat-only-v1";

    internal static string Resolve(
        OpenAiCompatibleRuntimeOptions options,
        RuntimeCapabilityObservation native,
        RuntimeCapabilityObservation structured) =>
        options.SupportsToolCalls && native.State == RuntimeCapabilityState.Supported
            ? NativeOpenAiTools
            : structured.State == RuntimeCapabilityState.Supported
                ? StructuredDecision
                : ChatOnly;
}

internal sealed class RuntimeCapabilityProfileStore(string dataRoot)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _root = Path.Combine(
        Path.GetFullPath(dataRoot ?? throw new ArgumentNullException(nameof(dataRoot))),
        "RuntimeCapabilities");

    internal void Save(RuntimeCapabilityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, profile.Identity + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profile, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal RuntimeCapabilityProfile? Load(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity)
            || identity.Any(character => !char.IsAsciiHexDigit(character)))
        {
            return null;
        }
        var path = Path.Combine(_root, identity + ".json");
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<RuntimeCapabilityProfile>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
