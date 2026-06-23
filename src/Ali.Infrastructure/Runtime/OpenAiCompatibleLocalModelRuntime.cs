using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ali.Core.Evidence;
using Ali.Core.Models;
using Ali.Core.Runtime;

namespace Ali.Infrastructure.Runtime;

public sealed class OpenAiCompatibleLocalModelRuntime : ILocalModelRuntime
{
    private const int HealthProbeAttempts = 3;
    private const int HealthProbeOutputTokenLimit = 512;
    private const string HealthProbeExpectedResponse = "OK";
    private static readonly TimeSpan HealthProbeRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly Regex ThinkBlockRegex = new(
        @"<think>.*?</think>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
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

        var uri = BuildUri("chat/completions");
        var isHealthCheck = IsHealthCheckRequest(request);
        var payload = JsonSerializer.Serialize(
            BuildChatPayload(request, maxTokens: isHealthCheck ? HealthProbeOutputTokenLimit : null),
            JsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        httpRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        if (isHealthCheck)
        {
            WriteHealthLog($"request STREAM POST {uri} payload={payload}");
        }

        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (isHealthCheck)
        {
            WriteHealthLog($"response STREAM POST {uri} status={(int)response.StatusCode}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (isHealthCheck)
            {
                WriteHealthLog($"response STREAM POST {uri} error={error}");
            }
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
                if (isHealthCheck)
                {
                    WriteHealthLog($"response STREAM POST {uri} delta={content}");
                }
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

            var nonStreamingText = await SendNonStreamingProbeWithRetryAsync(
                BuildProbeRequest("Return exactly OK. Do not explain. Do not include thinking text. /no_think"),
                cancellationToken).ConfigureAwait(false);

            var normalizedNonStreamingText = NormalizeHealthProbeText(nonStreamingText);
            if (!IsExpectedHealthProbeResponse(normalizedNonStreamingText))
            {
                return FailureHealth(
                    started,
                    $"Tiny non-streaming prompt did not return exactly OK after thinking-text cleanup. Raw: {TrimForUser(nonStreamingText)}",
                    streamingSupported);
            }

            if (_options.StreamingEnabled)
            {
                streamingSupported = await CheckStreamingPromptWithRetryAsync(cancellationToken).ConfigureAwait(false);
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
            WriteHealthLog($"exception type={ex.GetType().Name} message={ex.Message}");
            return FailureHealth(started, $"Local runtime health check failed: {ex.Message}", streamingSupported);
        }
    }

    private async Task<ModelsCheckResult> CheckModelsEndpointAsync(CancellationToken cancellationToken)
    {
        var uri = BuildUri("models");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        WriteHealthLog($"request GET {uri}");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        WriteHealthLog($"response GET {uri} status={(int)response.StatusCode} body={TrimForUser(body)}");

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
        var uri = BuildUri("chat/completions");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
        var isHealthCheck = IsHealthCheckRequest(request);
        var payload = JsonSerializer.Serialize(
            BuildChatPayload(
                request,
                stream: false,
                maxTokens: isHealthCheck ? HealthProbeOutputTokenLimit : null),
            JsonOptions);
        httpRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        if (isHealthCheck)
        {
            WriteHealthLog($"request POST {uri} payload={payload}");
        }

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (isHealthCheck)
        {
            WriteHealthLog($"response POST {uri} status={(int)response.StatusCode} body={body}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Chat completion failed with HTTP {(int)response.StatusCode}. {TrimForUser(body)}");
        }

        return OpenAiStreamParser.ExtractMessageContent(body) ?? string.Empty;
    }

    private async Task<string> SendNonStreamingProbeWithRetryAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= HealthProbeAttempts; attempt++)
        {
            var text = await SendNonStreamingPromptAsync(request, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            if (attempt < HealthProbeAttempts)
            {
                await Task.Delay(HealthProbeRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return string.Empty;
    }

    private async Task<bool> CheckStreamingPromptAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        await foreach (var token in StreamChatAsync(
                           BuildProbeRequest("Return exactly OK. Do not explain. Do not include thinking text. /no_think"),
                           cancellationToken).ConfigureAwait(false))
        {
            builder.Append(token.Text);
        }

        return IsExpectedHealthProbeResponse(NormalizeHealthProbeText(builder.ToString()));
    }

    private async Task<bool> CheckStreamingPromptWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= HealthProbeAttempts; attempt++)
        {
            if (await CheckStreamingPromptAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (attempt < HealthProbeAttempts)
            {
                await Task.Delay(HealthProbeRetryDelay, cancellationToken).ConfigureAwait(false);
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

    private object BuildChatPayload(ChatRequest request, bool? stream = null, int? maxTokens = null)
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
            max_tokens = maxTokens ?? _options.OutputTokenLimit,
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

    private static string NormalizeHealthProbeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = ThinkBlockRegex.Replace(value, string.Empty)
            .ReplaceLineEndings("\n")
            .Trim();
        var doneThinkingIndex = cleaned.LastIndexOf("done thinking.", StringComparison.OrdinalIgnoreCase);
        if (doneThinkingIndex >= 0)
        {
            cleaned = cleaned[(doneThinkingIndex + "done thinking.".Length)..].Trim();
        }

        return cleaned.Trim().Trim('.', '"', '\'', '`').Trim();
    }

    private static bool IsExpectedHealthProbeResponse(string value) =>
        string.Equals(value, HealthProbeExpectedResponse, StringComparison.OrdinalIgnoreCase);

    private static bool IsHealthCheckRequest(ChatRequest request) =>
        string.Equals(request.ConversationId, "health_check", StringComparison.Ordinal);

    private static void WriteHealthLog(string message)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ali",
                "BootstrapData");
            Directory.CreateDirectory(root);
            File.AppendAllText(
                Path.Combine(root, "runtime-health.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Health logging must never make the runtime path fail.
        }
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
