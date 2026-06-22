using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ali.Core.Evidence;
using Ali.Core.Models;
using Ali.Core.Runtime;

namespace Ali.Infrastructure.Runtime;

public sealed class OpenAiCompatibleLocalModelRuntime : ILocalModelRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly HttpClient _httpClient;
    private readonly OpenAiCompatibleRuntimeOptions _options;
    private readonly EndpointValidationResult _endpointValidation;

    public OpenAiCompatibleLocalModelRuntime(HttpClient httpClient, OpenAiCompatibleRuntimeOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        _endpointValidation = LocalEndpointPolicy.Validate(options.Endpoint, options.AllowPrivateLanEndpoint);
        ActiveProfile = options.ToModelProfile(isLastKnownGood: false);
    }

    public ModelProfile ActiveProfile { get; private set; }

    public async IAsyncEnumerable<ModelToken> StreamChatAsync(
        ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureEndpointAllowed();

        if (!_options.StreamingEnabled)
        {
            var content = await SendNonStreamingPromptAsync(request, cancellationToken).ConfigureAwait(false);
            yield return new ModelToken(content, EvidenceStatus.Unverified);
            yield break;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildUri("chat/completions"));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(BuildChatPayload(request), JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            yield return new ModelToken(
                $"Unknown: local model runtime returned HTTP {(int)response.StatusCode}. {TrimForUser(error)}",
                EvidenceStatus.Verified);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = OpenAiStreamParser.ExtractContentDelta(line["data:".Length..]);
            if (!string.IsNullOrEmpty(content))
            {
                yield return new ModelToken(content, EvidenceStatus.Unverified);
            }
        }
    }

    public async Task<RuntimeHealthCheck> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var streamingSupported = false;

        if (!_endpointValidation.IsAllowed)
        {
            return new RuntimeHealthCheck(
                Succeeded: false,
                Summary: _endpointValidation.Reason,
                CheckedAt: DateTimeOffset.UtcNow,
                Elapsed: DateTimeOffset.UtcNow - started,
                Endpoint: _options.Endpoint.ToString(),
                ModelPackageId: _options.Model,
                ContextTokens: _options.ContextTokens,
                OutputTokenLimit: _options.OutputTokenLimit,
                Temperature: _options.Temperature,
                ErrorText: _endpointValidation.Reason);
        }

        if (string.IsNullOrWhiteSpace(_options.Model))
        {
            return FailureHealth(
                started,
                "Model/package ID is required before checking a runtime.",
                streamingSupported);
        }

        try
        {
            var modelsResult = await CheckModelsEndpointAsync(cancellationToken).ConfigureAwait(false);
            if (!modelsResult.Succeeded)
            {
                return FailureHealth(started, modelsResult.Summary, streamingSupported);
            }

            var nonStreamingText = await SendNonStreamingPromptAsync(
                BuildProbeRequest("Reply with exactly OK. /no_think"),
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(nonStreamingText))
            {
                return FailureHealth(started, "Tiny non-streaming prompt returned an empty response.", streamingSupported);
            }

            if (_options.StreamingEnabled)
            {
                streamingSupported = await CheckStreamingPromptAsync(cancellationToken).ConfigureAwait(false);
                if (!streamingSupported)
                {
                    return FailureHealth(started, "Tiny streaming prompt returned no content.", streamingSupported);
                }
            }

            if (_options.SupportsVision)
            {
                var visionText = await SendNonStreamingPromptAsync(
                    BuildVisionProbeRequest(),
                    cancellationToken).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(visionText))
                {
                    return FailureHealth(started, "Tiny vision prompt returned an empty response.", streamingSupported);
                }
            }

            if (!await CheckCancellationAsync().ConfigureAwait(false))
            {
                return FailureHealth(started, "Cancellation probe did not cancel cleanly.", streamingSupported);
            }

            ActiveProfile = _options.ToModelProfile(isLastKnownGood: true);
            return new RuntimeHealthCheck(
                Succeeded: true,
                Summary: $"Verified local OpenAI-compatible runtime with model '{_options.Model}'.",
                CheckedAt: DateTimeOffset.UtcNow,
                Elapsed: DateTimeOffset.UtcNow - started,
                Endpoint: _options.Endpoint.ToString(),
                ModelPackageId: _options.Model,
                ContextTokens: _options.ContextTokens,
                OutputTokenLimit: _options.OutputTokenLimit,
                Temperature: _options.Temperature,
                StreamingSupported: streamingSupported);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return FailureHealth(started, $"Local runtime health check failed: {ex.Message}", streamingSupported);
        }
    }

    private async Task<ModelsCheckResult> CheckModelsEndpointAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("models"));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
        {
            return ModelsCheckResult.Success("Models endpoint is unavailable; selected model will be verified by prompt calls.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return ModelsCheckResult.Failure($"Models endpoint failed with HTTP {(int)response.StatusCode}. {TrimForUser(body)}");
        }

        return body.Contains(_options.Model, StringComparison.OrdinalIgnoreCase)
            ? ModelsCheckResult.Success("Selected model was listed by the models endpoint.")
            : ModelsCheckResult.Failure($"Endpoint responded, but model '{_options.Model}' was not listed.");
    }

    private async Task<string> SendNonStreamingPromptAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildUri("chat/completions"));
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(BuildChatPayload(request, stream: false), JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Chat completion failed with HTTP {(int)response.StatusCode}. {TrimForUser(body)}");
        }

        return OpenAiStreamParser.ExtractMessageContent(body) ?? string.Empty;
    }

    private async Task<bool> CheckStreamingPromptAsync(CancellationToken cancellationToken)
    {
        await foreach (var token in StreamChatAsync(
                           BuildProbeRequest("Reply with exactly OK. /no_think"),
                           cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(token.Text))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> CheckCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("models"));
            using var _ = await _httpClient.SendAsync(request, cancellation.Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private Uri BuildUri(string relativePath)
    {
        var baseText = _options.Endpoint.ToString();
        if (!baseText.EndsWith("/", StringComparison.Ordinal))
        {
            baseText += "/";
        }

        return new Uri(new Uri(baseText), relativePath);
    }

    private object BuildChatPayload(ChatRequest request, bool? stream = null)
    {
        var messages = request.History
            .Select(message => new
            {
                role = message.Role.ToString().ToLowerInvariant(),
                content = (object)message.Text
            })
            .Append(new
            {
                role = "user",
                content = BuildUserContent(request)
            })
            .ToArray();

        return new
        {
            model = _options.Model,
            messages,
            stream = stream ?? _options.StreamingEnabled,
            max_tokens = _options.OutputTokenLimit,
            temperature = _options.Temperature,
            top_p = _options.TopP
        };
    }

    private void EnsureEndpointAllowed()
    {
        if (!_endpointValidation.IsAllowed)
        {
            throw new InvalidOperationException(_endpointValidation.Reason);
        }
    }

    private static string TrimForUser(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240] + "...";
    }

    private RuntimeHealthCheck FailureHealth(
        DateTimeOffset started,
        string summary,
        bool streamingSupported) =>
        new(
            Succeeded: false,
            Summary: summary,
            CheckedAt: DateTimeOffset.UtcNow,
            Elapsed: DateTimeOffset.UtcNow - started,
            Endpoint: _options.Endpoint.ToString(),
            ModelPackageId: _options.Model,
            ContextTokens: _options.ContextTokens,
            OutputTokenLimit: _options.OutputTokenLimit,
            Temperature: _options.Temperature,
            StreamingSupported: streamingSupported,
            ErrorText: summary);

    private static ChatRequest BuildProbeRequest(string userText) =>
        new(
            ConversationId: "health_check",
            UserMessageId: "health_user",
            UserText: userText,
            History: Array.Empty<ChatMessage>());

    private static ChatRequest BuildVisionProbeRequest() =>
        BuildProbeRequest("Describe this image in one short phrase. /no_think") with
        {
            Attachments = new[]
            {
                new ChatAttachment(
                    Id: "vision_probe_red_pixel",
                    Kind: AttachmentKind.Image,
                    FileName: "red-pixel.png",
                    ContentType: "image/png",
                    Base64Data: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/luzQ8wAAAABJRU5ErkJggg==",
                    RetainAfterSession: false,
                    CreatedAt: DateTimeOffset.UtcNow)
            }
        };

    private static object BuildUserContent(ChatRequest request)
    {
        if (request.Attachments.Count == 0)
        {
            return request.UserText;
        }

        var content = new List<object>
        {
            new
            {
                type = "text",
                text = request.UserText
            }
        };

        foreach (var attachment in request.Attachments.Where(item => item.Kind == AttachmentKind.Image))
        {
            content.Add(new
            {
                type = "image_url",
                image_url = new
                {
                    url = $"data:{attachment.ContentType};base64,{attachment.Base64Data}"
                }
            });
        }

        return content.ToArray();
    }

    private sealed record ModelsCheckResult(bool Succeeded, string Summary)
    {
        public static ModelsCheckResult Success(string summary) => new(true, summary);

        public static ModelsCheckResult Failure(string summary) => new(false, summary);
    }
}
