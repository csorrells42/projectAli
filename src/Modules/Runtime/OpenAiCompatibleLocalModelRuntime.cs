using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ali.Modules.Diagnostics;
using Ali.Modules.Evidence;
using Ali.Modules.Identity;
using Ali.Modules.Runtime.Models;
using Ali.Modules.Runtime;
using Ali.Modules.Time;
using Ali;

namespace Ali.Modules.Runtime;

public sealed partial class OpenAiCompatibleLocalModelRuntime : ILocalModelRuntime, IModelSwitchAwareRuntime, IReasoningEffortRuntime, IOpenRouterReasoningRuntime, Microsoft.Extensions.AI.IChatClient, IBoundModelDispatchSource
{
    private const int HealthProbeAttempts = 3;
    private const int HealthProbeOutputTokenLimit = 512;
    private const int MaximumLowReasoningHealthProbeTokens = 256;
    private const string HealthProbeExpectedResponse = "OK";
    private const string SourcePlannerConversationId = "source_query_plan";
    private const string SourceAnswerVerifierConversationId = "source_answer_verifier";
    private const string VisibleOutputRetryInstruction =
        "The previous runtime attempt produced no visible assistant content. Follow the existing instructions exactly, but write the final result in visible assistant message content only. Do not include hidden reasoning, analysis, or <think> blocks. If the task requires JSON, return only that JSON.";
    private const string OutputLimitReachedNotice =
        "Response reached the configured output limit before the model finished. Ask me to continue or increase the Runtime output limit.";
    private static readonly TimeSpan HealthProbeRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan UnloadVerificationInterval = TimeSpan.FromMilliseconds(250);
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
    private readonly Func<string?> _apiKeyResolver;
    private readonly RuntimeCapabilityProfileStore? _capabilityProfiles;
    private readonly SemaphoreSlim _lemonadeLoadGate = new(1, 1);
    private string _reasoningEffort;
    private string _openRouterReasoningEffort;
    private int? _lastHealthProbeCompletionTokens;
    private bool _nativeToolCallingAdvertised;
    private int _lemonadeModelPrepared;
    private int _requestInFlight;
    private RuntimeCapabilityProfile? _capabilityProfile;

    public OpenAiCompatibleLocalModelRuntime(
        HttpClient httpClient,
        OpenAiCompatibleRuntimeOptions options,
        AssistantProfile? assistantProfile = null)
        : this(httpClient, options, assistantProfile, null, null)
    {
    }

    internal OpenAiCompatibleLocalModelRuntime(
        HttpClient httpClient,
        OpenAiCompatibleRuntimeOptions options,
        AssistantProfile? assistantProfile,
        Func<string?>? apiKeyResolver = null,
        RuntimeCapabilityProfileStore? capabilityProfiles = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _assistantProfile = (assistantProfile ?? AssistantProfile.CreateDefault()).Normalize();
        _endpointValidation = LocalEndpointPolicy.Validate(
            options.Endpoint,
            options.AllowPrivateLanEndpoint,
            options.AllowRemoteHttpsEndpoint);
        _apiKeyResolver = apiKeyResolver ?? (() => null);
        _capabilityProfiles = capabilityProfiles;
        _reasoningEffort = OllamaRuntimeSafetyPolicy.ResolveReasoningEffort(options);
        _openRouterReasoningEffort = NormalizeOpenRouterReasoningEffort(options.OpenRouterReasoningEffort);
        ActiveProfile = options.ToModelProfile(isLastKnownGood: false);
    }

    public ModelProfile ActiveProfile { get; private set; }

    public string RuntimeIdentity =>
        $"{LocalRuntimeEngines.Normalize(_options.Engine)}|{_options.Endpoint}|{_options.Model}";

    public string ReasoningEffort => Volatile.Read(ref _reasoningEffort);

    public void SetReasoningEffort(string effort)
    {
        if (ThinkingControl != ModelThinkingControl.GptOssReasoningEffort)
        {
            return;
        }

        Volatile.Write(
            ref _reasoningEffort,
            OllamaRuntimeSafetyPolicy.NormalizeGptOssReasoningEffort(effort));
    }

    public string? OpenRouterReasoningEffort =>
        string.IsNullOrEmpty(Volatile.Read(ref _openRouterReasoningEffort))
            ? null
            : Volatile.Read(ref _openRouterReasoningEffort);

    public void SetOpenRouterReasoningEffort(string? effort) =>
        Volatile.Write(ref _openRouterReasoningEffort, NormalizeOpenRouterReasoningEffort(effort));

    BoundModelDispatchSnapshot IBoundModelDispatchSource.CaptureBoundModelDispatch()
    {
        var profile = ActiveProfile with { };
        var capabilityProfile = Volatile.Read(ref _capabilityProfile);
        var protocolIdentity = capabilityProfile?.ProtocolIdentity
            ?? profile.ProtocolIdentity;
        var capabilityProfileIdentity = capabilityProfile?.Identity
            ?? profile.CapabilityProfileIdentity;
        var reasoningEffort = ReasoningEffort;
        var openRouterReasoningEffort = OpenRouterReasoningEffort;
        return new BoundModelDispatchSnapshot(
            this,
            profile,
            new BoundRuntimeBindingMaterial(
                LocalRuntimeEngines.Normalize(_options.Engine),
                GetType().FullName ?? GetType().Name,
                profile.RuntimeKind,
                profile.RuntimeLocation,
                _options.Endpoint.ToString())
            {
                ProtocolIdentity = protocolIdentity,
                CapabilityProfileIdentity = capabilityProfileIdentity
            },
            new BoundModelBindingMaterial(
                profile.ProfileId,
                _options.Model,
                _options.Family,
                _options.Size,
                _options.Quantization,
                _options.SupportsVision,
                profile.SupportsToolCalls)
            {
                CapabilityProfileIdentity = capabilityProfileIdentity
            },
            new BoundGenerationSettingsBindingMaterial(
                _options.ContextTokens,
                _options.OutputTokenLimit,
                _options.Temperature,
                _options.TopP,
                _options.StreamingEnabled,
                ThinkingControl.ToString(),
                _options.ThinkingEnabled,
                reasoningEffort)
            {
                OpenRouterReasoningEffort = openRouterReasoningEffort,
                TokenizerIdentity = _options.TokenizerIdentity,
                RollingWindowMode = _options.RollingWindowMode,
                ProtocolIdentity = protocolIdentity
            });
    }

    public async IAsyncEnumerable<ModelToken> StreamChatAsync(
        ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _requestInFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "The local model runtime already has one request in flight. Ali does not queue or overlap model generations.");
        }

        try
        {
            EnsureEndpointAllowed();
            await EnsureLemonadeModelLoadedAsync(cancellationToken).ConfigureAwait(false);

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
                yield return new ModelToken(
                    "Unknown: local model runtime completed without visible assistant content. Ali did not automatically submit a second generation.",
                    EvidenceStatus.Unverified);
            }
        }
        finally
        {
            Volatile.Write(ref _requestInFlight, 0);
        }
    }

    private async IAsyncEnumerable<ModelToken> StreamChatAttemptAsync(
        ChatRequest request,
        bool isHealthCheck,
        StreamingAttemptState state,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var useNativeOllama = IsNativeOllamaEndpoint();
        var uri = useNativeOllama ? BuildOllamaApiUri("chat") : BuildUri("chat/completions");
        var payload = SerializeChatPayload(
            request,
            stream: true,
            maxTokens: isHealthCheck ? HealthProbeOutputTokenLimit : null,
            useNativeOllama);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            useNativeOllama ? "application/x-ndjson" : "text/event-stream"));
        httpRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        WriteRequestMetadata(
            uri,
            request,
            stream: true,
            isHealthCheck,
            isHealthCheck ? HealthProbeOutputTokenLimit : null);

        AliTransportDiagnostics.RecordModelRequest(payload);
        using var response = await SendRuntimeAsync(
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
            AliTransportDiagnostics.RecordModelResponse(error);
            if (isHealthCheck)
            {
                WriteHealthLog($"response STREAM POST {uri} error={error}");
            }

            yield return new ModelToken(
                $"Unknown: {FormatChatHttpError(response.StatusCode, error)}",
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

            AliTransportDiagnostics.AppendModelResponseLine(line);

            var streamEvent = useNativeOllama
                ? ExtractNativeOllamaStreamEvent(line)
                : line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? OpenAiStreamParser.ExtractStreamEvent(
                        line["data:".Length..],
                        includeReasoning: isHealthCheck)
                    : new OpenAiStreamEvent(null, null, IsDone: false);
            if (isHealthCheck && !string.IsNullOrEmpty(streamEvent.Thinking))
            {
                yield return new ModelToken(
                    streamEvent.Thinking,
                    EvidenceStatus.Unverified,
                    IsThinking: true);
            }

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
                yield return new ModelToken(
                    streamEvent.Content,
                    EvidenceStatus.Unverified,
                    streamEvent.FinishReason);
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

            _lastHealthProbeCompletionTokens = null;
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

            var reasoningControlFailure = ValidateReasoningControlHealthProbe();
            if (reasoningControlFailure is not null)
            {
                return FailureHealth(started, reasoningControlFailure, streamingSupported);
            }

            if (_options.StreamingEnabled)
            {
                streamingSupported = await CheckStreamingPromptWithRetryAsync(cancellationToken).ConfigureAwait(false);
                if (!streamingSupported)
                {
                    return FailureHealth(started, "Tiny streaming prompt returned no content.", streamingSupported);
                }
            }

            var visionProbe = await ProbeVisionCapabilityAdvisoryAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!await CheckCancellationAsync().ConfigureAwait(false))
            {
                return FailureHealth(started, "Cancellation probe did not cancel cleanly.", streamingSupported);
            }

            var capabilityProfile = _options.CapabilityProbeEnabled
                ? await ProbeCapabilityProfileAsync(
                        streamingSupported,
                        visionProbe,
                        cancellationToken)
                    .ConfigureAwait(false)
                : CreateUnprobedCapabilityProfile(streamingSupported, visionProbe);
            _capabilityProfiles?.Save(capabilityProfile);
            Volatile.Write(ref _capabilityProfile, capabilityProfile);

            if (_options.SupportsToolCalls
                && capabilityProfile.NativeToolCalling.State != RuntimeCapabilityState.Supported)
            {
                return FailureHealth(
                    started,
                    "Native tool calls were enabled in settings, but the exact endpoint/model probe did not return the required typed tool call.",
                    streamingSupported) with
                {
                    CapabilityProfile = capabilityProfile
                };
            }

            var nativeToolsEnabled = _options.SupportsToolCalls
                && capabilityProfile.NativeToolCalling.State == RuntimeCapabilityState.Supported;
            ActiveProfile = _options.ToModelProfile(isLastKnownGood: true) with
            {
                SupportsToolCalls = nativeToolsEnabled,
                SupportsVision = _options.SupportsVision && !visionProbe.TypedStabilityRefusal,
                ProtocolIdentity = capabilityProfile.ProtocolIdentity,
                CapabilityProfileIdentity = capabilityProfile.Identity,
                TokenizerIdentity = capabilityProfile.TokenizerIdentity,
                RollingWindowMode = capabilityProfile.RollingWindowMode
            };
            var reasoningVerification = ThinkingControl != ModelThinkingControl.None
                ? $" Reasoning control '{ThinkingControl}' was sent explicitly"
                  + (_lastHealthProbeCompletionTokens.HasValue
                      ? $"; the tiny probe used {_lastHealthProbeCompletionTokens.Value:N0} completion tokens."
                      : ".")
                : string.Empty;
            var engineeringProtocol = capabilityProfile.IsEngineeringProtocolSafe
                ? $" Engineering protocol: {capabilityProfile.ProtocolIdentity}."
                : " This model is connected for chat, but neither native tools nor the validated structured-decision protocol was proven; autonomous engineering remains disabled.";
            var visionSummary = _options.SupportsVision
                ? visionProbe.TypedStabilityRefusal
                    ? " Vision was refused by a typed endpoint stability response and is disabled for this activation."
                    : capabilityProfile.Vision.State == RuntimeCapabilityState.Supported
                        ? " Vision image input was functionally observed."
                        : " Vision remains enabled by manual override; its advisory probe was inconclusive."
                : string.Empty;
            return new RuntimeHealthCheck(
                Succeeded: true,
                Summary: $"Verified runtime with model '{_options.Model}'.{reasoningVerification}{engineeringProtocol}{visionSummary}",
                CheckedAt: DateTimeOffset.UtcNow,
                Elapsed: DateTimeOffset.UtcNow - started,
                Endpoint: _options.Endpoint.ToString(),
                ModelPackageId: _options.Model,
                ContextTokens: _options.ContextTokens,
                OutputTokenLimit: _options.OutputTokenLimit,
                Temperature: _options.Temperature,
                StreamingSupported: streamingSupported)
            {
                CapabilityProfile = capabilityProfile
            };
        }
        catch (Exception ex) when (ex is HttpRequestException
                                      or TaskCanceledException
                                      or OperationCanceledException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or JsonException
                                      or InvalidOperationException)
        {
            WriteHealthLog($"exception type={ex.GetType().Name} message={ex.Message}");
            return FailureHealth(started, $"Runtime health check failed: {ex.Message}", streamingSupported);
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        var engine = LocalRuntimeEngines.Normalize(_options.Engine);
        var action = engine == LocalRuntimeEngines.Lemonade
            ? "lemonade-unload-and-verify"
            : "external-provider-retains-model";
        WriteHealthLog($"runtime shutdown runtime={RuntimeIdentity} action={action}");
        await UnloadForModelSwitchAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UnloadForModelSwitchAsync(CancellationToken cancellationToken)
    {
        var engine = LocalRuntimeEngines.Normalize(_options.Engine);
        if (engine != LocalRuntimeEngines.Lemonade)
        {
            WriteHealthLog($"runtime transition runtime={RuntimeIdentity} action=external-provider-retains-model");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.Model))
        {
            return;
        }

        await UnloadLemonadeAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _lemonadeModelPrepared, 0);
    }

    private async Task UnloadLemonadeAsync(CancellationToken cancellationToken)
    {
        var unloadUri = BuildServerRootUri("api/v1/unload");
        await PostJsonAndRequireSuccessAsync(
            unloadUri,
            new { model_name = _options.Model },
            "Lemonade model unload",
            cancellationToken).ConfigureAwait(false);

        await WaitForModelReleaseAsync(
            BuildServerRootUri("api/v1/health"),
            body => !LemonadeListsModelAsLoaded(body, _options.Model),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureLemonadeModelLoadedAsync(CancellationToken cancellationToken)
    {
        if (LocalRuntimeEngines.Normalize(_options.Engine) != LocalRuntimeEngines.Lemonade)
        {
            return;
        }

        await _lemonadeLoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var healthUri = BuildServerRootUri("api/v1/health");
            string? healthBody = null;
            using (var healthRequest = new HttpRequestMessage(HttpMethod.Get, healthUri))
            using (var healthResponse = await SendRuntimeAsync(healthRequest, cancellationToken).ConfigureAwait(false))
            {
                if (healthResponse.IsSuccessStatusCode)
                {
                    healthBody = await healthResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (LemonadeListsModelAsReady(healthBody, _options.Model, _options.ContextTokens))
                    {
                        Volatile.Write(ref _lemonadeModelPrepared, 1);
                        return;
                    }
                }
            }

            Volatile.Write(ref _lemonadeModelPrepared, 0);
            if (!string.IsNullOrWhiteSpace(healthBody)
                && LemonadeListsModelAsLoaded(healthBody, _options.Model))
            {
                await UnloadLemonadeAsync(cancellationToken).ConfigureAwait(false);
            }

            await PostJsonAndRequireSuccessAsync(
                BuildServerRootUri("api/v1/load"),
                new
                {
                    model_name = _options.Model,
                    ctx_size = _options.ContextTokens,
                    save_options = false
                },
                $"Lemonade model load with {_options.ContextTokens:N0}-token context",
                cancellationToken).ConfigureAwait(false);

            await WaitForLemonadeReadyAsync(_options.ContextTokens, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _lemonadeModelPrepared, 1);
        }
        finally
        {
            _lemonadeLoadGate.Release();
        }
    }

    private async Task WaitForLemonadeReadyAsync(int requiredContextTokens, CancellationToken cancellationToken)
    {
        var uri = BuildServerRootUri("api/v1/health");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await SendRuntimeAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (LemonadeListsModelAsReady(body, _options.Model, requiredContextTokens))
                {
                    return;
                }
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PostJsonAndRequireSuccessAsync(
        Uri uri,
        object payload,
        string operation,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };

        WriteHealthLog($"runtime operation='{operation}' request runtime={RuntimeIdentity} endpoint={uri}");
        using var response = await SendRuntimeAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            WriteHealthLog(
                $"runtime operation='{operation}' failed runtime={RuntimeIdentity} status={(int)response.StatusCode} error={TrimForUser(error)}");
            throw new HttpRequestException(
                $"{operation} failed with HTTP {(int)response.StatusCode}. {TrimForUser(error)}");
        }

        WriteHealthLog($"runtime operation='{operation}' accepted runtime={RuntimeIdentity} status={(int)response.StatusCode}");
    }

    private async Task WaitForModelReleaseAsync(
        Uri statusUri,
        Func<string, bool> released,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, statusUri);
            using var response = await SendRuntimeAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var releaseVerified = false;
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    releaseVerified = released(body);
                }
                catch (JsonException ex)
                {
                    WriteHealthLog(
                        $"model-switch release probe returned invalid JSON runtime={RuntimeIdentity} endpoint={statusUri} error={ex.Message}");
                }
            }

            if (releaseVerified)
            {
                WriteHealthLog($"model-switch release verified runtime={RuntimeIdentity} endpoint={statusUri}");
                return;
            }

            await Task.Delay(UnloadVerificationInterval, cancellationToken).ConfigureAwait(false);
        }

    }

    private static bool LemonadeListsModelAsLoaded(string json, string model)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("all_models_loaded", out var models)
            && models.ValueKind == JsonValueKind.Array
            && models.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.String
                    ? string.Equals(item.GetString(), model, StringComparison.OrdinalIgnoreCase)
                    : MatchesModelProperty(item, "model_name", model));
    }

    internal static bool LemonadeListsModelAsReady(string json, string model, int requiredContextTokens)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("all_models_loaded", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in models.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                if (string.Equals(item.GetString(), model, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }
            if (!MatchesModelProperty(item, "model_name", model)) continue;

            if (item.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && !string.Equals(status.GetString(), "ready", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (item.TryGetProperty("backend_health", out var backendHealth)
                && backendHealth.ValueKind == JsonValueKind.String
                && !string.Equals(backendHealth.GetString(), "ready", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (item.TryGetProperty("loaded", out var loaded)
                && loaded.ValueKind is JsonValueKind.True or JsonValueKind.False
                && !loaded.GetBoolean())
            {
                return false;
            }
            if (item.TryGetProperty("recipe_options", out var options)
                && options.ValueKind == JsonValueKind.Object
                && options.TryGetProperty("ctx_size", out var context)
                && context.TryGetInt32(out var actualContext)
                && actualContext != requiredContextTokens)
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private static bool MatchesModelProperty(JsonElement element, string propertyName, string model) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), model, StringComparison.OrdinalIgnoreCase);

    private async Task<ModelsCheckResult> CheckModelsEndpointAsync(CancellationToken cancellationToken)
    {
        var uri = LocalRuntimeModelInventory.BuildModelsUri(_options.Endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        WriteHealthLog($"request GET {uri}");
        using var response = await SendRuntimeAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var body = await LocalRuntimeModelInventory
            .ReadBoundedBodyAsync(response.Content, cancellationToken)
            .ConfigureAwait(false);
        WriteHealthLog($"response GET {uri} status={(int)response.StatusCode} body={TrimForUser(body)}");

        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
        {
            return ModelsCheckResult.Success("Models endpoint is unavailable; selected model will be verified by prompt calls.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return ModelsCheckResult.Failure($"Models endpoint failed with HTTP {(int)response.StatusCode}. {TrimForUser(body)}");
        }

        bool listsSelectedModel;
        try
        {
            listsSelectedModel = LocalRuntimeModelInventory.ListsExactModel(body, _options.Model);
        }
        catch (JsonException ex)
        {
            return ModelsCheckResult.Failure($"Models endpoint returned invalid JSON: {TrimForUser(ex.Message)}");
        }

        if (!listsSelectedModel)
        {
            return ModelsCheckResult.Failure($"Endpoint responded, but model '{_options.Model}' was not listed.");
        }

        _nativeToolCallingAdvertised = ModelAdvertisesToolCalling(body, _options.Model);
        return ModelsCheckResult.Success(
            _nativeToolCallingAdvertised
                ? "Selected model was listed and advertises native tool calling."
                : "Selected model was listed by the models endpoint.");
    }

    internal static bool ModelAdvertisesToolCalling(string body, string model)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return FindModelWithToolCallingLabel(document.RootElement, model);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool FindModelWithToolCallingLabel(JsonElement element, string model)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var matchesModel = new[] { "id", "model", "model_name", "name" }
                .Any(property => element.TryGetProperty(property, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && string.Equals(value.GetString(), model, StringComparison.OrdinalIgnoreCase));
            if (matchesModel
                && element.TryGetProperty("labels", out var labels)
                && labels.ValueKind == JsonValueKind.Array
                && labels.EnumerateArray().Any(label =>
                    label.ValueKind == JsonValueKind.String
                    && label.GetString()?.Contains("tool", StringComparison.OrdinalIgnoreCase) == true))
            {
                return true;
            }

            return element.EnumerateObject().Any(property =>
                FindModelWithToolCallingLabel(property.Value, model));
        }

        return element.ValueKind == JsonValueKind.Array
            && element.EnumerateArray().Any(item => FindModelWithToolCallingLabel(item, model));
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
                maxTokens: null,
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
            maxTokens: null,
            isHealthCheck: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendNonStreamingPromptAsync(
        ChatRequest request,
        int? maxTokens,
        bool isHealthCheck,
        CancellationToken cancellationToken)
    {
        await EnsureLemonadeModelLoadedAsync(cancellationToken).ConfigureAwait(false);
        var useNativeOllama = IsNativeOllamaEndpoint();
        var uri = useNativeOllama ? BuildOllamaApiUri("chat") : BuildUri("chat/completions");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
        var payload = SerializeChatPayload(request, stream: false, maxTokens, useNativeOllama);
        httpRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        WriteRequestMetadata(uri, request, stream: false, isHealthCheck, maxTokens);

        AliTransportDiagnostics.RecordModelRequest(payload);
        using var response = await SendRuntimeAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        AliTransportDiagnostics.RecordModelResponse(body);
        if (isHealthCheck)
        {
            WriteHealthLog($"response POST {uri} status={(int)response.StatusCode} body_chars={body.Length}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                FormatChatHttpError(response.StatusCode, body),
                inner: null,
                response.StatusCode);
        }

        if (isHealthCheck)
        {
            _lastHealthProbeCompletionTokens = ExtractCompletionTokens(body, useNativeOllama);
        }

        var result = useNativeOllama
            ? ExtractNativeOllamaMessageResult(body)
            : OpenAiStreamParser.ExtractMessageResult(body, includeReasoning: isHealthCheck);
        var content = result.Content ?? string.Empty;
        if (!isHealthCheck && IsLengthFinish(result.FinishReason) && !string.IsNullOrWhiteSpace(content))
        {
            return $"{content}{Environment.NewLine}{Environment.NewLine}{OutputLimitReachedNotice}";
        }

        return content;
    }

    private string? ValidateReasoningControlHealthProbe()
    {
        if (ThinkingControl != ModelThinkingControl.GptOssReasoningEffort
            || !string.Equals(ReasoningEffort, "low", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!_lastHealthProbeCompletionTokens.HasValue)
        {
            return "The runtime did not report completion-token usage for the low-effort startup probe, so Ali cannot verify that its GPT-OSS reasoning setting was honored.";
        }

        if (_lastHealthProbeCompletionTokens.Value > MaximumLowReasoningHealthProbeTokens)
        {
            return $"The tiny low-effort startup probe used {_lastHealthProbeCompletionTokens.Value:N0} completion tokens. The runtime may be ignoring GPT-OSS reasoning controls; activation is blocked above {MaximumLowReasoningHealthProbeTokens:N0} tokens.";
        }

        return null;
    }

    private static int? ExtractCompletionTokens(string json, bool nativeOllama)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (nativeOllama)
        {
            return root.TryGetProperty("eval_count", out var evalCount)
                && evalCount.TryGetInt32(out var nativeTokens)
                    ? nativeTokens
                    : null;
        }

        return root.TryGetProperty("usage", out var usage)
            && usage.TryGetProperty("completion_tokens", out var completionTokens)
            && completionTokens.TryGetInt32(out var openAiTokens)
                ? openAiTokens
                : null;
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
            if (!token.IsThinking)
            {
                builder.Append(token.Text);
            }
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
            using var _ = await SendRuntimeAsync(request, cancellation.Token).ConfigureAwait(false);
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

    private Uri BuildServerRootUri(string relativePath)
    {
        var builder = new UriBuilder(_options.Endpoint)
        {
            Path = $"/{relativePath.TrimStart('/')}",
            Query = string.Empty
        };

        return builder.Uri;
    }

    private string FormatChatHttpError(System.Net.HttpStatusCode statusCode, string body)
    {
        var detail = ExtractRuntimeErrorMessage(body);
        return string.IsNullOrWhiteSpace(detail)
            ? $"Local model runtime returned HTTP {(int)statusCode} without a readable error message."
            : $"Local model runtime returned HTTP {(int)statusCode}: {detail}";
    }

    private static string ExtractRuntimeErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var message = FindErrorMessage(document.RootElement);
            return TrimForUser(message ?? string.Empty);
        }
        catch (JsonException)
        {
            return TrimForUser(body);
        }
    }

    private static string? FindErrorMessage(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "message", "detail", "error_description" })
            {
                if (element.TryGetProperty(propertyName, out var candidate)
                    && candidate.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(candidate.GetString()))
                {
                    return candidate.GetString();
                }
            }

            foreach (var propertyName in new[] { "error", "details", "response" })
            {
                if (!element.TryGetProperty(propertyName, out var nested))
                {
                    continue;
                }

                if (nested.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(nested.GetString()))
                {
                    return nested.GetString();
                }

                if (FindErrorMessage(nested) is { Length: > 0 } nestedMessage)
                {
                    return nestedMessage;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (FindErrorMessage(item) is { Length: > 0 } nestedMessage)
                {
                    return nestedMessage;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.String
                 && element.GetString() is { } nestedText
                 && nestedText.TrimStart() is ['{' or '[', ..])
        {
            try
            {
                using var nestedDocument = JsonDocument.Parse(nestedText);
                return FindErrorMessage(nestedDocument.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private bool IsNativeOllamaEndpoint() =>
        LocalRuntimeEngines.Normalize(_options.Engine) == LocalRuntimeEngines.Ollama;

    private bool IsLmStudioEndpoint() =>
        LocalRuntimeEngines.Normalize(_options.Engine) == LocalRuntimeEngines.LmStudio;

    private object? ResolveNativeThinkingValue(string? reasoningEffortOverride = null) =>
        ThinkingControl switch
        {
            ModelThinkingControl.GptOssReasoningEffort =>
                OllamaRuntimeSafetyPolicy.NormalizeGptOssReasoningEffort(
                    reasoningEffortOverride ?? ReasoningEffort),
            ModelThinkingControl.QwenTemplateToggle => _options.ThinkingEnabled,
            _ => null
        };

    private object? ResolveOpenAiChatTemplateKwargs(string? reasoningEffortOverride = null)
    {
        if (ThinkingControl == ModelThinkingControl.GptOssReasoningEffort)
        {
            return new Dictionary<string, object>
            {
                ["reasoning_effort"] = OllamaRuntimeSafetyPolicy.NormalizeGptOssReasoningEffort(
                    reasoningEffortOverride ?? ReasoningEffort)
            };
        }

        if (ThinkingControl == ModelThinkingControl.QwenTemplateToggle
            || (ThinkingControl == ModelThinkingControl.GemmaSystemPromptToken
                && IsLmStudioEndpoint()))
        {
            return new Dictionary<string, object>
            {
                ["enable_thinking"] = _options.ThinkingEnabled
            };
        }

        return null;
    }

    private static string NormalizeOpenRouterReasoningEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return string.Empty;
        }

        return effort.Trim().ToLowerInvariant() switch
        {
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            _ => throw new InvalidOperationException(
                $"OpenRouter reasoning effort '{effort}' is not supported. Select low, medium, high, or Model default.")
        };
    }

    private object? ResolveOpenRouterReasoning(string? effortOverride = null)
    {
        var effort = NormalizeOpenRouterReasoningEffort(
            effortOverride ?? OpenRouterReasoningEffort);
        return string.IsNullOrEmpty(effort)
            ? null
            : new Dictionary<string, object> { ["effort"] = effort };
    }

    private string ResolveOpenAiThinkingDescription() =>
        ModelThinkingPolicy.Describe(
            ThinkingControl,
            _options.ThinkingEnabled,
            ReasoningEffort);

    private string ResolveNativeThinkingDescription(string? reasoningEffortOverride = null) =>
        ResolveNativeThinkingValue(reasoningEffortOverride) switch
        {
            string effort => effort,
            bool enabled => enabled.ToString().ToLowerInvariant(),
            _ => "omitted"
        };

    private bool IsExpectedNativeThinkingValue(JsonElement value, string? reasoningEffortOverride = null)
    {
        var expected = ResolveNativeThinkingValue(reasoningEffortOverride);
        return expected switch
        {
            string effort => value.ValueKind == JsonValueKind.String
                             && string.Equals(value.GetString(), effort, StringComparison.OrdinalIgnoreCase),
            bool enabled => value.ValueKind == (enabled ? JsonValueKind.True : JsonValueKind.False),
            _ => false
        };
    }

    private static OpenAiStreamEvent ExtractNativeOllamaStreamEvent(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new OpenAiStreamEvent(null, null, IsDone: false);
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var content = root.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var contentElement)
            && contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString()
                : null;
        var thinking = root.TryGetProperty("message", out message)
            && message.TryGetProperty("thinking", out var thinkingElement)
            && thinkingElement.ValueKind == JsonValueKind.String
                ? thinkingElement.GetString()
                : null;
        var done = root.TryGetProperty("done", out var doneElement)
            && doneElement.ValueKind == JsonValueKind.True;
        var finishReason = root.TryGetProperty("done_reason", out var reasonElement)
            && reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()
                : done ? "stop" : null;

        return new OpenAiStreamEvent(
            content,
            finishReason,
            IsDone: done && string.IsNullOrEmpty(content) && string.IsNullOrEmpty(thinking),
            Thinking: thinking);
    }

    private static OpenAiMessageResult ExtractNativeOllamaMessageResult(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var content = root.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var contentElement)
            && contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString()
                : null;
        var finishReason = root.TryGetProperty("done_reason", out var reasonElement)
            && reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()
                : null;
        return new OpenAiMessageResult(content, finishReason);
    }

    private void WriteRequestMetadata(
        Uri uri,
        ChatRequest request,
        bool stream,
        bool isHealthCheck,
        int? requestedMaxTokens)
    {
        var messageCount = request.History.Count + 1;
        if (!isHealthCheck && !IsPlannerRequest(request))
        {
            messageCount += 2;
        }

        var promptCharacters = request.UserText.Length
            + request.History.Sum(message => message.Text.Length);
        var estimatedPromptTokens = Math.Max(1, (promptCharacters + 3) / 4);
        var nativeOllama = IsNativeOllamaEndpoint();
        var context = nativeOllama
            ? _options.ContextTokens.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "provider-managed";
        var requestedOutputTokens = ResolveMaxTokens(requestedMaxTokens);
        WriteHealthLog(
            $"request POST {uri} model={_options.Model} stream={stream} "
            + $"think={(nativeOllama ? ResolveNativeThinkingDescription() : ResolveOpenAiThinkingDescription())} keep_alive={(nativeOllama ? OllamaRuntimeSafetyPolicy.KeepAlive : "provider-managed")} "
            + $"num_ctx={context} num_predict={requestedOutputTokens} messages={messageCount} "
            + $"estimated_prompt_tokens={estimatedPromptTokens} health={isHealthCheck}");
    }

    private string SerializeChatPayload(
        ChatRequest request,
        bool stream,
        int? maxTokens,
        bool useNativeOllama)
    {
        var payload = JsonSerializer.Serialize(
            useNativeOllama
                ? BuildNativeOllamaChatPayload(request, stream, maxTokens)
                : BuildChatPayload(request, stream, maxTokens),
            JsonOptions);

        if (useNativeOllama)
        {
            ValidateNativeOllamaPayload(payload);
        }
        else
        {
            ValidateOpenAiCompatiblePayload(payload);
        }

        return payload;
    }

    private object BuildChatPayload(ChatRequest request, bool? stream = null, int? maxTokens = null)
    {
        var messages = BuildTextMessages(request);
        messages[^1] = new
        {
            role = "user",
            content = BuildUserContent(request)
        };

        var requestedMaxTokens = ResolveMaxTokens(maxTokens);
        return new
        {
            model = _options.Model,
            models = ResolveFallbackModels(),
            messages = messages.ToArray(),
            stream = stream ?? _options.StreamingEnabled,
            max_tokens = requestedMaxTokens,
            temperature = _options.Temperature,
            top_p = _options.TopP,
            reasoning = ResolveOpenRouterReasoning(),
            provider = ResolveProviderRouting(),
            chat_template_kwargs = ResolveOpenAiChatTemplateKwargs(),
            think = (bool?)null
        };
    }

    private string[]? ResolveFallbackModels() =>
        string.IsNullOrWhiteSpace(_options.FallbackModel)
            ? null
            : [_options.FallbackModel.Trim()];

    private object? ResolveProviderRouting()
    {
        var providers = new[] { _options.ProviderOnly, _options.FallbackProviderOnly }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return providers.Length == 0
            ? null
            : new
            {
                only = providers,
                order = providers,
                allow_fallbacks = false
            };
    }

    private object BuildNativeOllamaChatPayload(ChatRequest request, bool stream, int? maxTokens)
    {
        var messages = BuildTextMessages(request);
        if (request.Attachments.Any(item => item.Kind == AttachmentKind.Image))
        {
            messages[^1] = new
            {
                role = "user",
                content = request.UserText,
                images = request.Attachments
                    .Where(item => item.Kind == AttachmentKind.Image)
                    .Select(item => item.Base64Data)
                    .ToArray()
            };
        }

        var requestedMaxTokens = ResolveMaxTokens(maxTokens);
        return new
        {
            model = _options.Model,
            messages = messages.ToArray(),
            stream,
            think = ResolveNativeThinkingValue(),
            keep_alive = OllamaRuntimeSafetyPolicy.KeepAlive,
            options = new
            {
                num_ctx = _options.ContextTokens,
                num_predict = requestedMaxTokens,
                temperature = _options.Temperature,
                top_p = _options.TopP
            }
        };
    }

    private List<object> BuildTextMessages(ChatRequest request)
    {
        var messages = new List<object>();
        if (!IsHealthCheckRequest(request) && !IsPlannerRequest(request))
        {
            messages.Add(new
            {
                role = "system",
                content = (object)BuildPrimarySystemInstruction(BuildAssistantPersonaInstruction())
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
            content = (object)request.UserText
        });

        return messages;
    }

    private void ValidateNativeOllamaPayload(
        string payload,
        string? reasoningEffortOverride = null)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var expectedThinking = ResolveNativeThinkingValue(reasoningEffortOverride);
        if (expectedThinking is not null
            && (!root.TryGetProperty("think", out var think)
                || !IsExpectedNativeThinkingValue(think, reasoningEffortOverride)))
        {
            throw new InvalidOperationException(
                $"Refusing to send Ollama request without the required thinking mode ({ResolveNativeThinkingDescription(reasoningEffortOverride)}).");
        }

        if (expectedThinking is null && root.TryGetProperty("think", out _))
        {
            throw new InvalidOperationException(
                "Refusing to send a thinking field to a model that does not require one.");
        }

        if (!root.TryGetProperty("keep_alive", out var keepAlive)
            || !string.Equals(keepAlive.GetString(), OllamaRuntimeSafetyPolicy.KeepAlive, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to send Ollama request without the bounded keep-alive policy.");
        }

        if (!root.TryGetProperty("options", out var options)
            || !options.TryGetProperty("num_ctx", out var context)
            || !context.TryGetInt32(out var contextTokens)
            || contextTokens < 1)
        {
            throw new InvalidOperationException(
                "Refusing to send an Ollama request without the positive context selected by the user.");
        }
    }

    private void ValidateOpenAiCompatiblePayload(
        string payload,
        string? reasoningEffortOverride = null,
        string? openRouterReasoningEffortOverride = null)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var expectedOpenRouterEffort = NormalizeOpenRouterReasoningEffort(
            openRouterReasoningEffortOverride ?? OpenRouterReasoningEffort);
        if (!string.IsNullOrEmpty(expectedOpenRouterEffort)
            && (!root.TryGetProperty("reasoning", out var reasoning)
                || reasoning.ValueKind != JsonValueKind.Object
                || !reasoning.TryGetProperty("effort", out var effort)
                || effort.ValueKind != JsonValueKind.String
                || !string.Equals(effort.GetString(), expectedOpenRouterEffort, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Refusing to send an OpenRouter request without reasoning effort {expectedOpenRouterEffort}.");
        }

        if (ThinkingControl == ModelThinkingControl.None
            || (ThinkingControl == ModelThinkingControl.GemmaSystemPromptToken
                && !IsLmStudioEndpoint()))
        {
            return;
        }

        if (!root.TryGetProperty("chat_template_kwargs", out var templateArguments)
            || templateArguments.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Refusing to send an OpenAI-compatible reasoning-model request without explicit chat-template controls.");
        }

        if (ThinkingControl == ModelThinkingControl.GptOssReasoningEffort)
        {
            var expectedReasoningEffort = OllamaRuntimeSafetyPolicy.NormalizeGptOssReasoningEffort(
                reasoningEffortOverride ?? ReasoningEffort);
            if (!templateArguments.TryGetProperty("reasoning_effort", out var reasoningEffort)
                || reasoningEffort.ValueKind != JsonValueKind.String
                || !string.Equals(reasoningEffort.GetString(), expectedReasoningEffort, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Refusing to send a GPT-OSS request without reasoning effort {expectedReasoningEffort}.");
            }

            return;
        }

        if (!templateArguments.TryGetProperty("enable_thinking", out var enableThinking)
            || enableThinking.ValueKind != (_options.ThinkingEnabled ? JsonValueKind.True : JsonValueKind.False))
        {
            throw new InvalidOperationException(
                $"Refusing to send a reasoning-model request without thinking set to {_options.ThinkingEnabled.ToString().ToLowerInvariant()}.");
        }
    }

    private int ResolveMaxTokens(int? requestedMaxTokens)
    {
        return requestedMaxTokens ?? _options.OutputTokenLimit;
    }

    private string BuildAssistantPersonaInstruction()
    {
        var assistantName = _assistantProfile.AssistantName;
        return $"You are {assistantName}, the local desktop assistant application created by Chris Sorrells. Your current foundation model package is {_options.Model}; its configured family is {_options.Family}. The application identity and foundation model identity are different but both are truthful parts of your identity. If asked who you are or what your name is, identify yourself as {assistantName}. If asked which model powers you, report the current foundation model package and family exactly. If asked whether you are Qwen, GPT-OSS, Gemma, or another model family, explain that you are {assistantName} and accurately name the currently loaded foundation model rather than denying it or inventing a different provider. If asked who made you, say that Chris Sorrells created the {assistantName} application; do not attribute the application to OpenAI or any foundation-model provider. The assistant name is separate from the human user's name; never treat saved memories, user statements like my name is, or customer profile details as your own identity unless the app assistant profile explicitly names you. Do not prepend your name or identity to ordinary answers. Answer in the user's language; for English prompts, answer only in English unless the user explicitly asks for translation or another language. If asked whether you are connected to the internet, answer as {assistantName}: you run on this computer and can use only the local app/runtime features that are enabled. If the app provides source excerpts, treat them as app-provided evidence and do not say you lack real-time data. Never claim that you generated, created, changed, sent, searched, opened, or controlled anything unless the current conversation contains an explicit tool or application result proving that action completed. Image attachments are inputs for inspection only; no image-generation tool is currently connected. You may explain how to create something, but clearly distinguish an explanation from actually performing the action. Keep normal replies concise: usually one short paragraph or a few bullets. Avoid emoji and emoticons in normal replies.";
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
        if (LocalEndpointPolicy.IsRemote(_options.Endpoint)
            && LocalRuntimeEngines.Normalize(_options.Engine) != LocalRuntimeEngines.GenericOpenAi)
        {
            throw new InvalidOperationException(
                "Remote endpoints require the explicit OpenAI-compatible/Custom engine.");
        }
    }

    private static bool IsLengthFinish(string? finishReason) =>
        string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase);

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

    private ModelThinkingControl ThinkingControl => _options.ThinkingControl;

    private bool ShouldDisableThinking() =>
        ThinkingControl == ModelThinkingControl.QwenTemplateToggle
        && !_options.ThinkingEnabled;

    private string BuildPrimarySystemInstruction(string content) =>
        ThinkingControl == ModelThinkingControl.GemmaSystemPromptToken
        && _options.ThinkingEnabled
        && !IsLmStudioEndpoint()
            ? $"<|think|>\n{content}"
            : content;

    private static void WriteHealthLog(string message)
    {
        try
        {
            var root = Path.Combine(AliServices.DesktopUserDataRoot, "Logs");
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
