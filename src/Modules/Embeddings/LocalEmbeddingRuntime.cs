using System.Net;
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

    private static IReadOnlyList<LocalEmbeddingProviderPreset> Presets { get; } =
    [
        new(
            LmStudio,
            "http://127.0.0.1:1234/v1/embeddings",
            "text-embedding-nomic-embed-text-v1",
            768),
        new(
            Ollama,
            "http://127.0.0.1:11434/v1/embeddings",
            "nomic-embed-text",
            768),
        new(
            LlamaCpp,
            "http://127.0.0.1:8080/v1/embeddings",
            "nomic-embed-text-v1-GGUF",
            768),
        new(
            Lemonade,
            "http://127.0.0.1:13305/api/v1/embeddings",
            "nomic-embed-text-v1-GGUF",
            768)
    ];

    public static bool IsKnown(string? provider) =>
        Choices.Any(choice => string.Equals(choice, provider, StringComparison.OrdinalIgnoreCase));

    public static bool TryGetPreset(string? provider, out LocalEmbeddingProviderPreset preset)
    {
        preset = Presets.FirstOrDefault(candidate =>
            string.Equals(candidate.Provider, provider, StringComparison.OrdinalIgnoreCase))!;
        return preset is not null;
    }
}

public sealed record LocalEmbeddingProviderPreset(
    string Provider,
    string Endpoint,
    string Model,
    int Dimensions);

public sealed record LocalEmbeddingConfiguration(
    string Provider,
    Uri Endpoint,
    string Model,
    int Dimensions)
{
    private const int MaximumDimensions = 8192;

    public static bool TryCreate(
        string? provider,
        string? endpoint,
        string? model,
        int dimensions,
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

        configuration = new LocalEmbeddingConfiguration(provider!, endpointUri, model, dimensions);
        failure = string.Empty;
        return true;
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
    string Message)
{
    public static LocalEmbeddingResult Failed(string message) => new(false, null, message);

    public static LocalEmbeddingResult Completed(float[] vector) =>
        new(true, vector, $"Created a {vector.Length}-dimension local embedding.");
}

public sealed class OpenAiCompatibleEmbeddingClient(HttpClient httpClient)
{
    private const int MaximumResponseBytes = 1_048_576;
    private const int MaximumFailureCharacters = 240;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<LocalEmbeddingResult> CreateEmbeddingAsync(
        LocalEmbeddingConfiguration configuration,
        string input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, configuration.Endpoint)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { model = configuration.Model, input }, JsonOptions),
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
                    $"The {configuration.Provider} embedding response exceeded {MaximumResponseBytes} bytes.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = ExtractFailureDetail(body);
                return LocalEmbeddingResult.Failed(
                    string.IsNullOrEmpty(detail)
                        ? $"The {configuration.Provider} embedding endpoint returned HTTP {(int)response.StatusCode}."
                        : $"The {configuration.Provider} embedding endpoint returned HTTP {(int)response.StatusCode}: {detail}");
            }

            if (!TryReadOpenAiVector(body, out var vector, out var parseFailure))
            {
                return LocalEmbeddingResult.Failed(
                    $"The {configuration.Provider} embedding response was invalid: {parseFailure}");
            }

            if (vector.Length != configuration.Dimensions)
            {
                return LocalEmbeddingResult.Failed(
                    $"The {configuration.Provider} embedding model returned {vector.Length} dimensions; exactly {configuration.Dimensions} are configured.");
            }

            return LocalEmbeddingResult.Completed(vector);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LocalEmbeddingResult.Failed($"The {configuration.Provider} embedding request timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            return LocalEmbeddingResult.Failed(
                $"The {configuration.Provider} embedding request failed safely: {Bound(ex.Message)}");
        }
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
