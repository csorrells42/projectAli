using System.Net.Http.Headers;
using System.Text.Json;
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
        bool visionProbeSucceeded,
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
        var vision = !_options.SupportsVision
            ? new RuntimeCapabilityObservation(
                RuntimeCapabilityState.Unknown,
                "Vision is manually disabled; no image was sent during capability probing.",
                observedAt)
            : new RuntimeCapabilityObservation(
                visionProbeSucceeded ? RuntimeCapabilityState.Supported : RuntimeCapabilityState.Unsupported,
                "Observed by the explicit one-pixel image health prompt.",
                observedAt);
        return RuntimeCapabilityProfile.Create(
            _options,
            native,
            structured,
            reasoning,
            streaming,
            vision);
    }

    private RuntimeCapabilityProfile CreateUnprobedCapabilityProfile(
        bool streamingSupported,
        bool visionProbeSucceeded)
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
            _options.SupportsVision && visionProbeSucceeded
                ? new RuntimeCapabilityObservation(
                    RuntimeCapabilityState.Supported,
                    "Observed by the explicit one-pixel image health prompt.",
                    observedAt)
                : Unknown("Vision was disabled or not proven."));
    }

    private async Task<RuntimeCapabilityObservation> ProbeNativeToolCallingAsync(
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        const string nonce = "ali-runtime-capability-v1";
        var probe = AIFunctionFactory.Create(
            (string value) => value,
            "ali_runtime_capability_probe",
            "Return the supplied fixed capability nonce through one typed tool call.");
        var options = new ChatOptions
        {
            Tools = [probe],
            ToolMode = ChatToolMode.RequireSpecific(probe.Name),
            AllowMultipleToolCalls = false,
            MaxOutputTokens = 64,
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
                        $"Call {probe.Name} exactly once with value '{nonce}'.")],
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            var call = response.Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>()
                .SingleOrDefault(content =>
                    !content.InformationalOnly
                    && string.Equals(content.Name, probe.Name, StringComparison.Ordinal));
            var returnedNonce = ReadStringArgument(call?.Arguments, "value");
            return new RuntimeCapabilityObservation(
                string.Equals(returnedNonce, nonce, StringComparison.Ordinal)
                    ? RuntimeCapabilityState.Supported
                    : RuntimeCapabilityState.Unsupported,
                "Observed through an exact required function-call round trip.",
                observedAt,
                call is null ? "No typed function call was returned." : "Typed function-call envelope returned.");
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

    private async Task<RuntimeCapabilityObservation> ProbeStructuredDecisionAsync(
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        const string nonce = "ali-structured-decision-v1";
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "accepted", "nonce" },
            properties = new
            {
                accepted = new { type = "boolean", @const = true },
                nonce = new { type = "string", @const = nonce }
            }
        });
        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                schema,
                "ali_runtime_structured_decision_probe"),
            MaxOutputTokens = 64,
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
                        $"Return the supplied schema with accepted true and nonce '{nonce}'.")],
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(response.Text ?? string.Empty);
            var root = document.RootElement;
            var valid = root.ValueKind == JsonValueKind.Object
                && root.EnumerateObject().Count() == 2
                && root.TryGetProperty("accepted", out var accepted)
                && accepted.ValueKind == JsonValueKind.True
                && root.TryGetProperty("nonce", out var returnedNonce)
                && returnedNonce.ValueKind == JsonValueKind.String
                && string.Equals(returnedNonce.GetString(), nonce, StringComparison.Ordinal);
            return new RuntimeCapabilityObservation(
                valid ? RuntimeCapabilityState.Supported : RuntimeCapabilityState.Unsupported,
                "Observed through Ali's exact JSON-schema decision envelope.",
                observedAt,
                valid ? "Exact schema returned." : "Response did not match the exact schema.");
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

    private static string? ReadStringArgument(
        IDictionary<string, object?>? arguments,
        string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value))
        {
            return null;
        }
        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };
    }
}
