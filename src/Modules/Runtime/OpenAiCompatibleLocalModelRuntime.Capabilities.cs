using System.Net.Http.Headers;
using System.Text.Json;
using Ali.Modules.Orchestration.Planning;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Runtime;

public sealed partial class OpenAiCompatibleLocalModelRuntime
{
    private Task<HttpResponseMessage> SendRuntimeAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        SendRuntimeAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

    private Task<HttpResponseMessage> SendRuntimeAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEndpointAllowed();
        if (LocalEndpointPolicy.IsRemote(_options.Endpoint))
        {
            var apiKey = _apiKeyResolver();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
                    "The selected remote runtime requires an API key from Ali's protected credential store or configured environment variable.");
            }
            if (request.Headers.Authorization is not null)
            {
                throw new InvalidOperationException(
                    "The runtime request already contains an authorization header.");
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        return _httpClient.SendAsync(request, completionOption, cancellationToken);
    }

    private async Task<RuntimeCapabilityProfile> ProbeCapabilityProfileAsync(
        bool streamingSupported,
        VisionCapabilityProbeResult visionProbe,
        CancellationToken cancellationToken)
    {
        var observedAt = DateTimeOffset.UtcNow;
        var native = await ProbeNativeToolCallingAsync(observedAt, cancellationToken)
            .ConfigureAwait(false);
        var structured = await ProbeStructuredDecisionAsync(observedAt, cancellationToken)
            .ConfigureAwait(false);
        var reasoning = ThinkingControl == ModelThinkingControl.None
            ? new RuntimeCapabilityObservation(
                RuntimeCapabilityState.Unknown,
                "No explicit reasoning convention was selected for this endpoint/model.",
                observedAt)
            : new RuntimeCapabilityObservation(
                RuntimeCapabilityState.Supported,
                "The configured typed reasoning control was accepted by the successful prompt probe.",
                observedAt,
                ThinkingControl.ToString());
        var streaming = !_options.StreamingEnabled
            ? new RuntimeCapabilityObservation(
                RuntimeCapabilityState.Unknown,
                "Streaming was disabled in the selected generation settings and was not probed.",
                observedAt)
            : new RuntimeCapabilityObservation(
                streamingSupported ? RuntimeCapabilityState.Supported : RuntimeCapabilityState.Unsupported,
                "Observed by the bounded streaming health prompt.",
                observedAt);
        return RuntimeCapabilityProfile.Create(
            _options,
            native,
            structured,
            reasoning,
            streaming,
            visionProbe.Observation);
    }

    private RuntimeCapabilityProfile CreateUnprobedCapabilityProfile(
        bool streamingSupported,
        VisionCapabilityProbeResult visionProbe)
    {
        var observedAt = DateTimeOffset.UtcNow;
        RuntimeCapabilityObservation Unknown(string detail) => new(
            RuntimeCapabilityState.Unknown,
            detail,
            observedAt);
        return RuntimeCapabilityProfile.Create(
            _options,
            Unknown("Functional native-tool probing is disabled in the selected runtime settings."),
            Unknown("Functional structured-decision probing is disabled in the selected runtime settings."),
            Unknown("Reasoning control was not independently capability-probed."),
            _options.StreamingEnabled && streamingSupported
                ? new RuntimeCapabilityObservation(
                    RuntimeCapabilityState.Supported,
                    "Observed by the bounded streaming health prompt.",
                    observedAt)
                : Unknown("Streaming was disabled or not proven."),
            visionProbe.Observation);
    }

    private async Task<VisionCapabilityProbeResult> ProbeVisionCapabilityAdvisoryAsync(
        CancellationToken cancellationToken)
    {
        var observedAt = DateTimeOffset.UtcNow;
        if (!_options.SupportsVision)
        {
            return new VisionCapabilityProbeResult(
                new RuntimeCapabilityObservation(
                    RuntimeCapabilityState.Unknown,
                    "Vision is manually disabled; no image was sent during capability probing.",
                    observedAt),
                TypedStabilityRefusal: false);
        }

        try
        {
            var text = await SendNonStreamingPromptAsync(
                    BuildVisionProbeRequest(),
                    cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text)
                ? new VisionCapabilityProbeResult(
                    new RuntimeCapabilityObservation(
                        RuntimeCapabilityState.Unknown,
                        "The advisory image probe returned no content; the manual Vision setting remains enabled.",
                        observedAt),
                    TypedStabilityRefusal: false)
                : new VisionCapabilityProbeResult(
                    new RuntimeCapabilityObservation(
                        RuntimeCapabilityState.Supported,
                        "Observed by the explicit one-pixel image health prompt.",
                        observedAt),
                    TypedStabilityRefusal: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is System.Net.HttpStatusCode.UnsupportedMediaType
                or System.Net.HttpStatusCode.UnprocessableEntity)
        {
            return new VisionCapabilityProbeResult(
                new RuntimeCapabilityObservation(
                    RuntimeCapabilityState.Unsupported,
                    "The endpoint returned a typed HTTP stability refusal for image content.",
                    observedAt,
                    $"HTTP {(int)exception.StatusCode.Value}"),
                TypedStabilityRefusal: true);
        }
        catch (Exception exception) when (exception is HttpRequestException
                                              or JsonException
                                              or InvalidOperationException
                                              or IOException
                                              or NotSupportedException)
        {
            return new VisionCapabilityProbeResult(
                new RuntimeCapabilityObservation(
                    RuntimeCapabilityState.Unknown,
                    "The advisory image probe failed; the manual Vision setting remains enabled.",
                    observedAt,
                    exception.GetType().Name),
                TypedStabilityRefusal: false);
        }
    }

    private sealed record VisionCapabilityProbeResult(
        RuntimeCapabilityObservation Observation,
        bool TypedStabilityRefusal);

    private async Task<RuntimeCapabilityObservation> ProbeNativeToolCallingAsync(
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        const string toolName = "ali_native_tool_probe";
        const string nonce = "ali-runtime-capability-v1";
        var probe = AIFunctionFactory.CreateDeclaration(
            toolName,
            "Return the exact nonce supplied by the user. This probe tests ordinary OpenAI-compatible function calling.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    nonce = new
                    {
                        type = "string",
                        description = "The exact nonce supplied by the user."
                    }
                },
                required = new[] { "nonce" },
                additionalProperties = false
            }));
        var options = new ChatOptions
        {
            Tools = [probe],
            ToolMode = ChatToolMode.Auto,
            AllowMultipleToolCalls = false,
            MaxOutputTokens = 256,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [AliInternalModelRoutingProperties.SuppressInjectedPersona] = true
            }
        };
        try
        {
            var response = await GetResponseAsync(
                    [new Microsoft.Extensions.AI.ChatMessage(
                        Microsoft.Extensions.AI.ChatRole.User,
                        $"Call {toolName} exactly once with nonce set to {nonce}. Do not answer with prose.")],
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            var calls = response.Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>()
                .Where(call => !call.InformationalOnly)
                .ToArray();
            var returnedNonce = calls.Length == 1
                && string.Equals(calls[0].Name, toolName, StringComparison.Ordinal)
                && calls[0].Arguments?.TryGetValue("nonce", out var nonceValue) == true
                    ? nonceValue switch
                    {
                        string text => text,
                        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
                        _ => nonceValue?.ToString()
                    }
                    : TryReadQwenTextualProbeNonce(response.Text, toolName);
            var supported = string.Equals(returnedNonce, nonce, StringComparison.Ordinal);
            return new RuntimeCapabilityObservation(
                supported ? RuntimeCapabilityState.Supported : RuntimeCapabilityState.Unsupported,
                "Observed through a minimal standard OpenAI-compatible function call.",
                observedAt,
                supported
                    ? "The endpoint returned exactly one typed tool call with the exact nonce."
                    : $"Expected one {toolName} call with the exact nonce, but received neither a typed call nor the strict Qwen text envelope.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
                                              or JsonException
                                              or InvalidOperationException
                                              or NotSupportedException)
        {
            return new RuntimeCapabilityObservation(
                RuntimeCapabilityState.Unsupported,
                "The exact native function-call probe was rejected or malformed.",
                observedAt,
                exception.GetType().Name);
        }
    }

    private static string? TryReadQwenTextualProbeNonce(string? text, string expectedToolName)
    {
        const string openMarker = "<tools>";
        const string closeMarker = "</tools>";
        var trimmed = text?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith(openMarker, StringComparison.Ordinal)
            || !trimmed.EndsWith(closeMarker, StringComparison.Ordinal)
            || trimmed.IndexOf(openMarker, openMarker.Length, StringComparison.Ordinal) >= 0)
        {
            return null;
        }

        var json = trimmed[openMarker.Length..^closeMarker.Length].Trim();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("name", out var name)
                || !string.Equals(name.GetString(), expectedToolName, StringComparison.Ordinal)
                || !root.TryGetProperty("arguments", out var arguments)
                || arguments.ValueKind != JsonValueKind.Object
                || !arguments.TryGetProperty("nonce", out var nonce)
                || nonce.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return nonce.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<RuntimeCapabilityObservation> ProbeStructuredDecisionAsync(
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        const string nonce = "ali-structured-decision-v1";
        var transport = AliOrchestrationProtocol.CreateDeclaration([]);
        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                transport.JsonSchema,
                "ali_orchestration_decision_transport"),
            MaxOutputTokens = 256,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [AliInternalModelRoutingProperties.SuppressInjectedPersona] = true
            }
        };
        try
        {
            var response = await GetResponseAsync(
                    BuildOrchestrationTransportProbeMessages(nonce, native: false),
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            var decoded = AliOrchestrationDecisionDecoder.DecodeCompatibility(response);
            var returnedNonce = (decoded.Decision?.NextAction as AnswerDirectlyAction)?.Answer;
            var valid = string.Equals(returnedNonce, nonce, StringComparison.Ordinal);
            return new RuntimeCapabilityObservation(
                valid ? RuntimeCapabilityState.Supported : RuntimeCapabilityState.Unsupported,
                "Observed through Ali's production grammar-safe structured-decision transport.",
                observedAt,
                valid
                    ? "Structured production transport and exact decision decoder succeeded."
                    : decoded.Error ?? "Response did not contain the exact probe decision.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
                                              or JsonException
                                              or InvalidOperationException
                                              or NotSupportedException)
        {
            return new RuntimeCapabilityObservation(
                RuntimeCapabilityState.Unsupported,
                "The exact JSON-schema decision probe was rejected or malformed.",
                observedAt,
                exception.GetType().Name);
        }
    }

    private static Microsoft.Extensions.AI.ChatMessage[] BuildOrchestrationTransportProbeMessages(
        string nonce,
        bool native)
    {
        var decision = JsonSerializer.Serialize(new
        {
            workUpdate = (object?)null,
            materialClaims = Array.Empty<object>(),
            nextAction = new
            {
                kind = "answerDirectly",
                answer = nonce
            }
        });
        var transport = native
            ? $"Call {Ali.Modules.Capabilities.OrchestrationProtocolCapability.ToolName} exactly once."
            : "Return exactly one JSON transport object and no prose.";
        return
        [
            new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.System,
                "Use Ali's production orchestration transport. The transport contains exactly one "
                + $"{AliOrchestrationProtocol.DecisionJsonPropertyName} string. That string must contain "
                + "the complete strict decision object supplied by the user."),
            new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.User,
                transport + " Put this exact JSON object in "
                + AliOrchestrationProtocol.DecisionJsonPropertyName + ": " + decision)
        ];
    }
}
