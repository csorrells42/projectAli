using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using Ali.Modules.Embeddings;
using Ali.Modules.RAG;

namespace Ali.Modules.UserMemory;

/// <summary>
/// Provider-neutral identity for every choice that can change vector meaning or
/// transport interpretation. Provider-specific integrations may supply a stronger
/// resolved identity without changing participant-memory storage contracts.
/// </summary>
public sealed record ParticipantMemoryEmbeddingIdentity(
    string Provider,
    string Protocol,
    Uri Endpoint,
    string ConfiguredModel,
    string ResolvedModel,
    string Quantization,
    int Dimensions,
    int MaximumContextTokens,
    string QueryPromptMode,
    string DocumentPromptMode,
    string QueryPromptPrefix,
    string DocumentPromptPrefix,
    string ResolutionSource,
    bool ProbeVerified,
    DateTimeOffset? ProbeVerifiedUtc)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Fingerprint
    {
        get
        {
            var canonical = JsonSerializer.Serialize(new
            {
                Provider,
                Protocol,
                Endpoint = Endpoint.AbsoluteUri,
                ConfiguredModel,
                ResolvedModel,
                Quantization,
                Dimensions,
                MaximumContextTokens,
                QueryPromptMode,
                DocumentPromptMode,
                QueryPromptPrefix,
                DocumentPromptPrefix,
                ResolutionSource
            }, JsonOptions);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
        }
    }

    public ParticipantMemoryEmbeddingIdentity Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(Protocol);
        ArgumentNullException.ThrowIfNull(Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConfiguredModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(ResolvedModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(Quantization);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Dimensions);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumContextTokens);
        ArgumentException.ThrowIfNullOrWhiteSpace(QueryPromptMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(DocumentPromptMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(ResolutionSource);
        if ((QueryPromptPrefix?.Length ?? 0) > 128
            || (DocumentPromptPrefix?.Length ?? 0) > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(QueryPromptPrefix),
                "Embedding prompt prefixes are bounded to 128 characters.");
        }
        if ((QueryPromptPrefix?.Any(char.IsControl) ?? false)
            || (DocumentPromptPrefix?.Any(char.IsControl) ?? false))
        {
            throw new ArgumentException(
                "Embedding prompt prefixes cannot contain control characters.",
                nameof(QueryPromptPrefix));
        }
        ValidatePromptMode(QueryPromptMode, QueryPromptPrefix, nameof(QueryPromptMode));
        ValidatePromptMode(DocumentPromptMode, DocumentPromptPrefix, nameof(DocumentPromptMode));
        return this with
        {
            Provider = Provider.Trim(),
            Protocol = Protocol.Trim(),
            ConfiguredModel = ConfiguredModel.Trim(),
            ResolvedModel = ResolvedModel.Trim(),
            Quantization = Quantization.Trim(),
            QueryPromptMode = QueryPromptMode.Trim(),
            DocumentPromptMode = DocumentPromptMode.Trim(),
            // Prefix whitespace is part of the exact embedding input and vector identity.
            QueryPromptPrefix = QueryPromptPrefix ?? string.Empty,
            DocumentPromptPrefix = DocumentPromptPrefix ?? string.Empty,
            ResolutionSource = ResolutionSource.Trim()
        };
    }

    private static void ValidatePromptMode(string mode, string? prefix, string parameterName)
    {
        var normalizedMode = mode.Trim();
        var hasPrefix = !string.IsNullOrEmpty(prefix);
        if ((normalizedMode == "none-v1" && hasPrefix)
            || (normalizedMode == "prefix-v1" && !hasPrefix)
            || normalizedMode is not ("none-v1" or "prefix-v1"))
        {
            throw new ArgumentException(
                "Embedding prompt mode must be none-v1 with no prefix or prefix-v1 with an explicit prefix.",
                parameterName);
        }
    }
}

public interface IParticipantMemoryEmbeddingIdentitySource
{
    ParticipantMemoryEmbeddingIdentity Resolve(LocalVectorLibrarySettings settings);

    ValueTask<ParticipantMemoryEmbeddingIdentity> ResolveAsync(
        LocalVectorLibrarySettings settings,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Resolve(settings));
}

/// <summary>
/// Current provider-neutral fallback. It records unreported provider facts explicitly,
/// so a future provider-resolved profile selects a different fresh collection instead
/// of admitting vectors whose prompt, context, or quantization identity is uncertain.
/// </summary>
internal sealed class ConfiguredParticipantMemoryEmbeddingIdentitySource :
    IParticipantMemoryEmbeddingIdentitySource
{
    public ParticipantMemoryEmbeddingIdentity Resolve(LocalVectorLibrarySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!LocalEmbeddingConfiguration.TryCreate(
                settings.EmbeddingProvider,
                settings.EmbeddingEndpoint,
                settings.EmbeddingModel,
                settings.EmbeddingDimensions,
                out var configuration,
                out var failure)
            || configuration is null)
        {
            throw new InvalidOperationException(
                $"Participant-memory embedding configuration is invalid: {failure}");
        }

        return new ParticipantMemoryEmbeddingIdentity(
            configuration.Provider,
            "openai-compatible-embeddings-v1",
            configuration.Endpoint,
            configuration.Model,
            configuration.Model,
            "provider-not-reported",
            configuration.Dimensions,
            0,
            "none-v1",
            "none-v1",
            string.Empty,
            string.Empty,
            "configured-endpoint-unverified",
            ProbeVerified: false,
            ProbeVerifiedUtc: null).Normalize();
    }
}

/// <summary>
/// Provider-neutral live verification. A fixed probe is embedded at the configured
/// loopback endpoint at both a fixed short input and the app's maximum input length;
/// both exact finite vector digests become part of semantic identity. This detects an
/// endpoint change only when it changes at least one sampled vector; it is not exhaustive
/// proof of model, template, truncation, quantization, or provider identity. An unreported
/// token limit remains zero rather than being invented from the character boundary.
/// </summary>
internal sealed class ProbedParticipantMemoryEmbeddingIdentitySource :
    IParticipantMemoryEmbeddingIdentitySource,
    IDisposable
{
    private const string ProbeText = "ali-participant-memory-embedding-identity-probe-v1";
    private readonly OpenAiCompatibleEmbeddingClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    internal ProbedParticipantMemoryEmbeddingIdentitySource(HttpClient httpClient)
    {
        _client = new OpenAiCompatibleEmbeddingClient(
            httpClient ?? throw new ArgumentNullException(nameof(httpClient)));
    }

    public ParticipantMemoryEmbeddingIdentity Resolve(LocalVectorLibrarySettings settings) =>
        throw new InvalidOperationException(
            "Live participant-memory embedding verification must run through the bounded async path.");

    public async ValueTask<ParticipantMemoryEmbeddingIdentity> ResolveAsync(
        LocalVectorLibrarySettings settings,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(settings);
        if (!LocalEmbeddingConfiguration.TryCreate(
                settings.EmbeddingProvider,
                settings.EmbeddingEndpoint,
                settings.EmbeddingModel,
                settings.EmbeddingDimensions,
                out var configuration,
                out var failure)
            || configuration is null)
        {
            throw new InvalidOperationException(
                $"Participant-memory embedding configuration is invalid: {failure}");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var probe = await _client.CreateMem0CompatibleEmbeddingAsync(
                configuration,
                ProbeText,
                cancellationToken).ConfigureAwait(false);
            if (!probe.Success || probe.Vector is null)
            {
                throw new InvalidOperationException(
                    "The configured participant-memory embedding endpoint failed its live fixed-vector probe: "
                    + probe.Message);
            }
            var maximumInputProbe = await _client.CreateMem0CompatibleEmbeddingAsync(
                configuration,
                new string('x', ParticipantMemoryLimits.MaximumMemoryTextLength),
                cancellationToken).ConfigureAwait(false);
            if (!maximumInputProbe.Success || maximumInputProbe.Vector is null)
            {
                throw new InvalidOperationException(
                    $"The configured participant-memory embedding endpoint failed its {ParticipantMemoryLimits.MaximumMemoryTextLength}-character input-boundary probe: "
                    + maximumInputProbe.Message);
            }

            var vectors = new[] { probe.Vector, maximumInputProbe.Vector };
            var bytes = new byte[(vectors.Length + vectors.Sum(vector => vector.Length)) * sizeof(int)];
            var offset = 0;
            foreach (var vector in vectors)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    bytes.AsSpan(offset, sizeof(int)),
                    vector.Length);
                offset += sizeof(int);
                foreach (var value in vector)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(
                        bytes.AsSpan(offset, sizeof(int)),
                        BitConverter.SingleToInt32Bits(value));
                    offset += sizeof(int);
                }
            }
            var hash = SHA256.HashData(bytes);
            try
            {
                var verifiedAt = DateTimeOffset.UtcNow;
                var identity = new ParticipantMemoryEmbeddingIdentity(
                    configuration.Provider,
                    "openai-compatible-embeddings-v1",
                    configuration.Endpoint,
                    configuration.Model,
                    configuration.Model,
                    "fixed-probe-sha256:" + Convert.ToHexString(hash).ToLowerInvariant(),
                    configuration.Dimensions,
                    0,
                    "none-v1",
                    "none-v1",
                    string.Empty,
                    string.Empty,
                    $"live-fixed-vector-and-{ParticipantMemoryLimits.MaximumMemoryTextLength}-character-boundary-probe-v1",
                    ProbeVerified: true,
                    ProbeVerifiedUtc: verifiedAt).Normalize();
                return identity;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                CryptographicOperations.ZeroMemory(hash);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _gate.Dispose();
        }
    }
}
