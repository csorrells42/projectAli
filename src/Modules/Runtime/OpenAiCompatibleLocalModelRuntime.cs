using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ali.Modules.Evidence;
using Ali.Modules.Identity;
using Ali.Modules.Runtime.Models;
using Ali.Modules.Runtime;
using Ali.Modules.Time;
using Ali;

namespace Ali.Modules.Runtime;

public sealed class OpenAiCompatibleLocalModelRuntime : ILocalModelRuntime
{
    private const int HealthProbeAttempts = 3;
    private const int HealthProbeOutputTokenLimit = 512;
    private const int QwenVisibleOutputTokenFloor = 512;
    private const int QwenVisibleOutputRetryTokenLimit = 1024;
    private const int MaxAutomaticLengthContinuations = 1;
    private const string HealthProbeExpectedResponse = "OK";
    private const string SourcePlannerConversationId = "source_query_plan";
    private const string SourceAnswerVerifierConversationId = "source_answer_verifier";
    private const string VisibleOutputRetryInstruction =
        "The previous runtime attempt produced no visible assistant content. Follow the existing instructions exactly, but write the final result in visible assistant message content only. Do not include hidden reasoning, analysis, or <think> blocks. If the task requires JSON, return only that JSON.";
    private const string ContinueAfterLengthInstruction =
        "Continue exactly from where your previous answer stopped. Do not restart, repeat completed text, summarize, or add a preface.";
    private const string OutputLimitReachedNotice =
        "Response reached the configured output limit before the model finished. Ask me to continue or increase the Runtime output limit.";
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
    private readonly AssistantProfile _assistantProfile;
    private readonly EndpointValidationResult _endpointValidation;

    public OpenAiCompatibleLocalModelRuntime(
        HttpClient httpClient,
        OpenAiCompatibleRuntimeOptions options,
        AssistantProfile? assistantProfile = null)
    {
        _httpClient = httpClient;
        _options = options;
        _assistantProfile = (assistantProfile ?? AssistantProfile.CreateDefault()).Normalize();
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
            if (string.IsNullOrWhiteSpace(content))
            {
                yield return new ModelToken(
                    "Unknown: local model runtime completed without visible assistant content. The model may have spent its output budget on hidden reasoning.",
                    EvidenceStatus.Unverified);
                yield break;
            }

            yield return new ModelToken(content, EvidenceStatus.Unverified);
            yield break;
        }

        var isHealthCheck = IsHealthCheckRequest(request);
        var firstAttempt = new StreamingAttemptState();
        await foreach (var token in StreamChatAttemptAsync(request, isHealthCheck, firstAttempt, cancellationToken).ConfigureAwait(false))
        {
            yield return token;
        }

        if (!firstAttempt.EmittedContent && !isHealthCheck)
        {
            var fallbackContent = await SendVisibleOutputRetryAsync(request, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fallbackContent))
            {
                yield return new ModelToken(fallbackContent, EvidenceStatus.Unverified);
                yield break;
            }

            yield return new ModelToken(
                "Unknown: local model runtime completed without visible assistant content. The model may have spent its output budget on hidden reasoning.",
                EvidenceStatus.Unverified);
            yield break;
        }

        var previousAttempt = firstAttempt;
        for (var continuationIndex = 0;
             !isHealthCheck
             && continuationIndex < MaxAutomaticLengthContinuations
             && IsLengthFinish(previousAttempt.FinishReason)
             && previousAttempt.EmittedContent;
             continuationIndex++)
        {
            var continuationRequest = BuildContinuationRequest(request, previousAttempt.Text.ToString());
            var continuationAttempt = new StreamingAttemptState();
            await foreach (var token in StreamChatAttemptAsync(continuationRequest, isHealthCheck: false, continuationAttempt, cancellationToken).ConfigureAwait(false))
            {
                yield return token;
            }

            if (!continuationAttempt.EmittedContent)
            {
                break;
            }

            previousAttempt = continuationAttempt;
        }

        if (!isHealthCheck && IsLengthFinish(previousAttempt.FinishReason))
        {
            yield return new ModelToken(
                $"{Environment.NewLine}{Environment.NewLine}{OutputLimitReachedNotice}",
                EvidenceStatus.Unknown,
                FinishReason: "length");
        }
    }

    private async IAsyncEnumerable<ModelToken> StreamChatAttemptAsync(
        ChatRequest request,
        bool isHealthCheck,
        StreamingAttemptState state,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var uri = BuildUri("chat/completions");
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

            var streamEvent = OpenAiStreamParser.ExtractStreamEvent(
                line["data:".Length..],
                includeReasoning: isHealthCheck);
            if (streamEvent.IsDone)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(streamEvent.FinishReason))
            {
                state.FinishReason = streamEvent.FinishReason;
            }

            if (!string.IsNullOrEmpty(streamEvent.Content))
            {
                state.EmittedContent = true;
                state.Text.Append(streamEvent.Content);
                if (isHealthCheck)
                {
                    WriteHealthLog($"response STREAM POST {uri} delta={streamEvent.Content}");
                }

                yield return new ModelToken(
                    streamEvent.Content,
                    EvidenceStatus.Unverified,
                    streamEvent.FinishReason);
            }
        }
    }

    private static ChatRequest BuildContinuationRequest(ChatRequest request, string partialAnswer)
    {
        var history = request.History.ToList();
        history.Add(new ChatMessage(
            $"runtime_original_user_{Guid.NewGuid():N}",
            ChatRole.User,
            request.UserText,
            DateTimeOffset.UtcNow,
            EvidenceStatus.Unverified));

        if (!string.IsNullOrWhiteSpace(partialAnswer))
        {
            history.Add(new ChatMessage(
                $"runtime_partial_assistant_{Guid.NewGuid():N}",
                ChatRole.Assistant,
                partialAnswer,
                DateTimeOffset.UtcNow,
                EvidenceStatus.Unverified));
        }

        return request with
        {
            UserText = ContinueAfterLengthInstruction,
            History = history
        };
    }

    private static bool IsLengthFinish(string? finishReason) =>
        string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase);

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
                BuildProbeRequest("Return exactly OK. Do not explain. Do not include thinking text."),
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

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Model))
        {
            return;
        }

        try
        {
            var uri = BuildOllamaApiUri("generate");
            var payload = JsonSerializer.Serialize(
                new
                {
                    model = _options.Model,
                    keep_alive = 0,
                    stream = false
                },
                JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            WriteHealthLog($"request POST {uri} unload payload={payload}");
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            WriteHealthLog($"response POST {uri} unload status={(int)response.StatusCode} body={TrimForUser(body)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            WriteHealthLog($"unload exception type={ex.GetType().Name} message={ex.Message}");
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
        var isHealthCheck = IsHealthCheckRequest(request);
        var text = await SendNonStreamingPromptAsync(
            request,
            maxTokens: isHealthCheck ? HealthProbeOutputTokenLimit : null,
            isHealthCheck,
            cancellationToken).ConfigureAwait(false);

        if (!isHealthCheck && ShouldDisableThinking() && string.IsNullOrWhiteSpace(text))
        {
            text = await SendNonStreamingPromptAsync(
                request,
                maxTokens: QwenVisibleOutputRetryTokenLimit,
                isHealthCheck: false,
                cancellationToken).ConfigureAwait(false);
        }

        if (!isHealthCheck && ShouldDisableThinking() && string.IsNullOrWhiteSpace(text))
        {
            text = await SendVisibleOutputRetryAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return text;
    }

    private async Task<string> SendVisibleOutputRetryAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var retryRequest = request with
        {
            History = request.History
                .Append(new ChatMessage(
                    "runtime_visible_output_retry",
                    ChatRole.System,
                    VisibleOutputRetryInstruction,
                    DateTimeOffset.UtcNow,
                    EvidenceStatus.Unverified))
                .ToList()
        };

        return await SendNonStreamingPromptAsync(
            retryRequest,
            maxTokens: QwenVisibleOutputRetryTokenLimit,
            isHealthCheck: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendNonStreamingPromptAsync(
        ChatRequest request,
        int? maxTokens,
        bool isHealthCheck,
        CancellationToken cancellationToken)
    {
        var uri = BuildUri("chat/completions");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
        var payload = JsonSerializer.Serialize(
            BuildChatPayload(
                request,
                stream: false,
                maxTokens: maxTokens),
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

        var result = OpenAiStreamParser.ExtractMessageResult(body, includeReasoning: isHealthCheck);
        var content = result.Content ?? string.Empty;
        if (!isHealthCheck && IsLengthFinish(result.FinishReason) && !string.IsNullOrWhiteSpace(content))
        {
            return $"{content}{Environment.NewLine}{Environment.NewLine}{OutputLimitReachedNotice}";
        }

        return content;
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
                           BuildProbeRequest("Return exactly OK. Do not explain. Do not include thinking text."),
                           cancellationToken).ConfigureAwait(false))
        {
            builder.Append(token.Text);
        }

        var streamedText = builder.ToString();
        return IsExpectedHealthProbeResponse(NormalizeHealthProbeText(streamedText))
            || (!string.IsNullOrWhiteSpace(streamedText)
                && !streamedText.TrimStart().StartsWith("Unknown:", StringComparison.OrdinalIgnoreCase));
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

    private Uri BuildOllamaApiUri(string relativePath)
    {
        var builder = new UriBuilder(_options.Endpoint)
        {
            Path = $"/api/{relativePath.TrimStart('/')}",
            Query = string.Empty
        };

        return builder.Uri;
    }

    private object BuildChatPayload(ChatRequest request, bool? stream = null, int? maxTokens = null)
    {
        var messages = new List<object>();
        if (!IsHealthCheckRequest(request) && !IsPlannerRequest(request))
        {
            messages.Add(new
            {
                role = "system",
                content = (object)BuildAssistantPersonaInstruction()
            });
            messages.Add(new
            {
                role = "system",
                content = (object)BuildCurrentDateInstruction()
            });
        }

        messages.AddRange(request.History
            .Select(message => new
            {
                role = message.Role.ToString().ToLowerInvariant(),
                content = (object)message.Text
            }));
        messages.Add(new
        {
            role = "user",
            content = BuildUserContent(request)
        });

        var plannerRequest = IsPlannerRequest(request);
        return new
        {
            model = _options.Model,
            messages = messages.ToArray(),
            stream = stream ?? _options.StreamingEnabled,
            max_tokens = ResolveMaxTokens(request, maxTokens),
            temperature = maxTokens.HasValue || plannerRequest ? 0 : _options.Temperature,
            top_p = maxTokens.HasValue || plannerRequest ? 0.1 : _options.TopP,
            think = ShouldDisableThinking() ? false : (bool?)null
        };
    }

    private int ResolveMaxTokens(ChatRequest request, int? requestedMaxTokens)
    {
        if (requestedMaxTokens.HasValue)
        {
            return requestedMaxTokens.Value;
        }

        var configuredLimit = ShouldDisableThinking()
            ? Math.Max(_options.OutputTokenLimit, QwenVisibleOutputTokenFloor)
            : _options.OutputTokenLimit;
        return configuredLimit;
    }

    private string BuildAssistantPersonaInstruction()
    {
        var assistantName = _assistantProfile.AssistantName;
        return $"You are {assistantName}, the local desktop assistant in this application. If asked who you are or what your name is, identify yourself as {assistantName}. The assistant name is separate from the human user's name; never treat saved memories, user statements like my name is, or customer profile details as your own identity unless the app assistant profile explicitly names you. Do not prepend your name or identity to ordinary answers. Do not argue that your name is Qwen, the model package, or the model provider; those are implementation details. Answer in the user's language; for English prompts, answer only in English unless the user explicitly asks for translation or another language. If asked whether you are connected to the internet, answer as {assistantName}: you run on this computer and can use only the local app/runtime features that are enabled; do not claim live web browsing, training cutoffs, or internet limitations unless the user asks specifically about web access. If the app provides source excerpts, treat them as app-provided evidence and do not say you lack real-time data. Keep normal replies concise: usually one short paragraph or a few bullets. Avoid emoji and emoticons in normal replies.";
    }

    private static string BuildCurrentDateInstruction()
    {
        return CurrentDateTimeSnapshot.Capture().BuildSystemInstruction();
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

    private static bool IsSourcePlannerRequest(ChatRequest request) =>
        string.Equals(request.ConversationId, SourcePlannerConversationId, StringComparison.Ordinal);

    private static bool IsSourceAnswerVerifierRequest(ChatRequest request) =>
        string.Equals(request.ConversationId, SourceAnswerVerifierConversationId, StringComparison.Ordinal);

    private static bool IsPlannerRequest(ChatRequest request) =>
        IsSourcePlannerRequest(request)
        || IsSourceAnswerVerifierRequest(request);

    private bool ShouldDisableThinking() =>
        IsQwenThinkingRuntime(_options.Model) || IsQwenThinkingRuntime(_options.Family);

    private static bool IsQwenThinkingRuntime(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value.Contains("qwen", StringComparison.OrdinalIgnoreCase)
            || value.Contains("qwq", StringComparison.OrdinalIgnoreCase));

    private static void WriteHealthLog(string message)
    {
        try
        {
            var root = AliServices.DesktopSettingsRoot;
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
        BuildProbeRequest("Describe this image in one short phrase.") with
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
        var userText = request.UserText;
        if (request.Attachments.Count == 0)
        {
            return userText;
        }

        var content = new List<object>
        {
            new
            {
                type = "text",
                text = userText
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

    private sealed class StreamingAttemptState
    {
        public bool EmittedContent { get; set; }

        public string? FinishReason { get; set; }

        public StringBuilder Text { get; } = new();
    }

    private sealed record ModelsCheckResult(bool Succeeded, string Summary)
    {
        public static ModelsCheckResult Success(string summary) => new(true, summary);

        public static ModelsCheckResult Failure(string summary) => new(false, summary);
    }
}
