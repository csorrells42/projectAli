using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ali.Modules.Embeddings;

public static class LocalEmbeddingProviders
{
    public const string LmStudio = "LM Studio";
    public const string Ollama = "Ollama";
    public const string LlamaCpp = "llama.cpp";
    public const string Lemonade = "Lemonade";
    public const string Custom = "Custom";

    public static IReadOnlyList<string> Choices { get; } =
    [
        LmStudio,
        Ollama,
        LlamaCpp,
        Lemonade,
        Custom
    ];

    public static bool IsKnown(string? provider) =>
        Choices.Any(choice => string.Equals(choice, provider, StringComparison.OrdinalIgnoreCase));
}

public static class LocalEmbeddingProtocolIdentities
{
    public const string OpenAiCompatibleV1 = "openai-compatible-embeddings-v1";

    public static IReadOnlyList<string> Choices { get; } = [OpenAiCompatibleV1];

    public static bool IsKnown(string? protocolIdentity) =>
        Choices.Contains(protocolIdentity, StringComparer.Ordinal);
}

public enum EmbeddingPromptMode
{
    Plain,
    SearchDocument,
    SearchQuery
}

public enum EmbeddingInputRole
{
    StoredDocument,
    RetrievalQuery
}

public sealed record LocalEmbeddingConfiguration(
    string Provider,
    Uri Endpoint,
    string Model,
    int Dimensions,
    string ProtocolIdentity,
    int ContextTokens,
    EmbeddingPromptMode DocumentPromptMode,
    EmbeddingPromptMode QueryPromptMode)
{
    private const int MaximumDimensions = 8192;
    private const int MaximumContextTokens = 262_144;

    public static bool TryCreate(
        string? provider,
        string? endpoint,
        string? model,
        int dimensions,
        string? protocolIdentity,
        int contextTokens,
        EmbeddingPromptMode documentPromptMode,
        EmbeddingPromptMode queryPromptMode,
        out LocalEmbeddingConfiguration? configuration,
        out string failure)
    {
        configuration = null;
        if (!LocalEmbeddingProviders.IsKnown(provider))
        {
            failure = "Select a supported local embedding provider.";
            return false;
        }

        if (string.IsNullOrEmpty(endpoint)
            || !string.Equals(endpoint, endpoint.Trim(), StringComparison.Ordinal)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            || endpointUri.Scheme != Uri.UriSchemeHttp
            || !endpointUri.IsLoopback
            || !string.IsNullOrEmpty(endpointUri.UserInfo))
        {
            failure = "The local embedding endpoint must be an absolute loopback HTTP URL.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(model)
            || !string.Equals(model, model.Trim(), StringComparison.Ordinal))
        {
            failure = "The local embedding model ID is required and cannot include surrounding whitespace.";
            return false;
        }

        if (dimensions is < 1 or > MaximumDimensions)
        {
            failure = $"Embedding dimensions must be from 1 through {MaximumDimensions}.";
            return false;
        }

        if (!LocalEmbeddingProtocolIdentities.IsKnown(protocolIdentity))
        {
            failure = "Select a supported embedding protocol identity.";
            return false;
        }

        if (contextTokens is < 1 or > MaximumContextTokens)
        {
            failure = $"Embedding context must be from 1 through {MaximumContextTokens:N0} tokens.";
            return false;
        }

        if (!Enum.IsDefined(documentPromptMode) || !Enum.IsDefined(queryPromptMode))
        {
            failure = "Select valid document and query embedding prompt modes.";
            return false;
        }

        configuration = new LocalEmbeddingConfiguration(
            provider!,
            endpointUri,
            model,
            dimensions,
            protocolIdentity!,
            contextTokens,
            documentPromptMode,
            queryPromptMode);
        failure = string.Empty;
        return true;
    }

    public EmbeddingPromptMode ResolvePromptMode(EmbeddingInputRole role) => role switch
    {
        EmbeddingInputRole.StoredDocument => DocumentPromptMode,
        EmbeddingInputRole.RetrievalQuery => QueryPromptMode,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    public string CaptureBindingIdentity(EmbeddingInputRole role)
    {
        var promptMode = ResolvePromptMode(role);
        var material = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Provider,
            endpoint = Endpoint.AbsoluteUri,
            Model,
            Dimensions,
            ProtocolIdentity,
            ContextTokens,
            promptMode
        });
        try
        {
            return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    public bool TryGetOpenAiApiBaseUri(out Uri? apiBaseUri, out string failure)
    {
        apiBaseUri = null;
        if (!string.IsNullOrEmpty(Endpoint.Query) || !string.IsNullOrEmpty(Endpoint.Fragment))
        {
            failure = "The embedding endpoint cannot contain a query or fragment when an OpenAI API base URL is required.";
            return false;
        }

        const string finalSegment = "embeddings";
        var path = Endpoint.AbsolutePath;
        if (!path.EndsWith(finalSegment, StringComparison.Ordinal)
            || path.Length == finalSegment.Length
            || path[path.Length - finalSegment.Length - 1] != '/')
        {
            failure = "The embedding endpoint must end with /embeddings to derive its OpenAI API base URL.";
            return false;
        }

        var builder = new UriBuilder(Endpoint)
        {
            Path = path[..^finalSegment.Length],
            Query = string.Empty,
            Fragment = string.Empty
        };
        apiBaseUri = builder.Uri;
        failure = string.Empty;
        return true;
    }
}

public sealed record LocalEmbeddingResult(
    bool Success,
    float[]? Vector,
    string Message,
    string BindingIdentity,
    EmbeddingPromptMode PromptMode,
    int EffectiveContextTokens)
{
    public static LocalEmbeddingResult Failed(
        string message,
        LocalEmbeddingConfiguration configuration,
        EmbeddingInputRole role) =>
        new(
            false,
            null,
            message,
            configuration.CaptureBindingIdentity(role),
            configuration.ResolvePromptMode(role),
            configuration.ContextTokens);

    public static LocalEmbeddingResult Completed(
        float[] vector,
        LocalEmbeddingConfiguration configuration,
        EmbeddingInputRole role) =>
        new(
            true,
            vector,
            $"Created a {vector.Length}-dimension local embedding.",
            configuration.CaptureBindingIdentity(role),
            configuration.ResolvePromptMode(role),
            configuration.ContextTokens);
}

public sealed class OpenAiCompatibleEmbeddingClient(HttpClient httpClient)
{
    private const int MaximumResponseBytes = 1_048_576;
    private const int MaximumFailureCharacters = 240;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<LocalEmbeddingResult> CreateEmbeddingAsync(
        LocalEmbeddingConfiguration configuration,
        string input,
        EmbeddingInputRole role,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            var promptMode = configuration.ResolvePromptMode(role);
            var promptedInput = EmbeddingPromptFormatter.Format(input, promptMode);
            using var request = new HttpRequestMessage(HttpMethod.Post, configuration.Endpoint)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { model = configuration.Model, input = promptedInput }, JsonOptions),
                    Encoding.UTF8,
                    "application/json")
            };
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var body = await ReadBoundedBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                return LocalEmbeddingResult.Failed(
                    $"The {configuration.Provider} embedding response exceeded {MaximumResponseBytes} bytes.",
                    configuration,
                    role);
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = ExtractFailureDetail(body);
                return LocalEmbeddingResult.Failed(
                    string.IsNullOrEmpty(detail)
                        ? $"The {configuration.Provider} embedding endpoint returned HTTP {(int)response.StatusCode}."
                        : $"The {configuration.Provider} embedding endpoint returned HTTP {(int)response.StatusCode}: {detail}",
                    configuration,
                    role);
            }

            if (!TryReadOpenAiVector(body, out var vector, out var parseFailure))
            {
                return LocalEmbeddingResult.Failed(
                    $"The {configuration.Provider} embedding response was invalid: {parseFailure}",
                    configuration,
                    role);
            }

            if (vector.Length != configuration.Dimensions)
            {
                return LocalEmbeddingResult.Failed(
                    $"The {configuration.Provider} embedding model returned {vector.Length} dimensions; exactly {configuration.Dimensions} are configured.",
                    configuration,
                    role);
            }

            return LocalEmbeddingResult.Completed(vector, configuration, role);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LocalEmbeddingResult.Failed(
                $"The {configuration.Provider} embedding request timed out.",
                configuration,
                role);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            return LocalEmbeddingResult.Failed(
                $"The {configuration.Provider} embedding request failed safely: {Bound(ex.Message)}",
                configuration,
                role);
        }
    }

    public Task<LocalEmbeddingResult> ProbeConfiguredContextAsync(
        LocalEmbeddingConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var input = EmbeddingContextProbeInputBuilder.Build(configuration.ContextTokens);
        return CreateEmbeddingAsync(
            configuration,
            input,
            EmbeddingInputRole.RetrievalQuery,
            cancellationToken);
    }

    private static async Task<string?> ReadBoundedBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (output.Length + read > MaximumResponseBytes)
            {
                return null;
            }
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static bool TryReadOpenAiVector(
        string json,
        out float[] vector,
        out string failure)
    {
        vector = [];
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            failure = "the data array is missing";
            return false;
        }

        var first = data.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object
            || !first.TryGetProperty("embedding", out var embedding)
            || embedding.ValueKind != JsonValueKind.Array)
        {
            failure = "data[0].embedding is missing";
            return false;
        }

        var values = new List<float>();
        foreach (var item in embedding.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number
                || !item.TryGetSingle(out var value)
                || !float.IsFinite(value))
            {
                failure = "the embedding contains a non-finite or non-numeric value";
                return false;
            }
            values.Add(value);
        }
        if (values.Count == 0)
        {
            failure = "the embedding vector is empty";
            return false;
        }

        vector = values.ToArray();
        failure = string.Empty;
        return true;
    }

    private static string ExtractFailureDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return Bound(error.GetString());
                }
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return Bound(message.GetString());
                }
            }
            if (root.TryGetProperty("message", out var direct)
                && direct.ValueKind == JsonValueKind.String)
            {
                return Bound(direct.GetString());
            }
        }
        catch (JsonException)
        {
        }
        return Bound(body);
    }

    private static string Bound(string? value)
    {
        var normalized = (value ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return normalized.Length <= MaximumFailureCharacters
            ? normalized
            : normalized[..MaximumFailureCharacters];
    }
}

public static class EmbeddingPromptFormatter
{
    public static string Format(string input, EmbeddingPromptMode mode)
    {
        ArgumentNullException.ThrowIfNull(input);
        return mode switch
        {
            EmbeddingPromptMode.Plain => input,
            EmbeddingPromptMode.SearchDocument => "search_document: " + input,
            EmbeddingPromptMode.SearchQuery => "search_query: " + input,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }
}

internal static class EmbeddingContextProbeInputBuilder
{
    internal static string Build(int contextTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contextTokens);
        var output = new StringBuilder(checked(contextTokens * 6));
        for (var index = 0; index < contextTokens; index++)
        {
            output.Append("probe");
            if (index + 1 < contextTokens)
            {
                output.Append(' ');
            }
        }
        return output.ToString();
    }
}
