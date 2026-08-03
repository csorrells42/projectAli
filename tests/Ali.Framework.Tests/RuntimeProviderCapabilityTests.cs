using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Runtime;
using Ali.UI.ViewModels;

namespace Ali.Framework.Tests;

public sealed class RuntimeProviderCapabilityTests
{
    [Fact]
    public void EndpointPolicy_RequiresExplicitHttpsForRemoteProviders()
    {
        var https = new Uri("https://runtime.example.test/v1/");
        var http = new Uri("http://runtime.example.test/v1/");
        var userInfo = new Uri("https://key@runtime.example.test/v1/");
        var query = new Uri("https://runtime.example.test/v1/?api-version=1");

        Assert.False(LocalEndpointPolicy.Validate(https, false).IsAllowed);
        Assert.False(LocalEndpointPolicy.Validate(http, false, true).IsAllowed);
        Assert.True(LocalEndpointPolicy.Validate(https, false, true).IsAllowed);
        Assert.False(LocalEndpointPolicy.Validate(userInfo, false, true).IsAllowed);
        Assert.False(LocalEndpointPolicy.Validate(query, false, true).IsAllowed);
        Assert.True(LocalEndpointPolicy.IsRemote(https));
        Assert.False(LocalEndpointPolicy.IsRemote(new Uri("http://127.0.0.1:1234/v1/")));
    }

    [Fact]
    public void RuntimeSettings_AllowOnlyExplicitCustomRemoteAndNeverContainApiKey()
    {
        using var folder = new TestFolder();
        const string apiKey = "cp9-secret-canary";
        var credentialStore = new RuntimeCredentialStore(folder.Path);
        credentialStore.SaveApiKey(apiKey);
        var options = CreateOptions(new Uri("https://runtime.example.test/v1/")) with
        {
            AllowRemoteHttpsEndpoint = true,
            ApiKeyEnvironmentVariable = "ALI_CP9_TEST_API_KEY"
        };

        RuntimeSettingsStore.Save(folder.Path, options);

        var settingsText = File.ReadAllText(RuntimeSettingsStore.GetSettingsPath(folder.Path));
        Assert.DoesNotContain(apiKey, settingsText, StringComparison.Ordinal);
        Assert.Contains("ALI_CP9_TEST_API_KEY", settingsText, StringComparison.Ordinal);
        Assert.Equal(apiKey, credentialStore.LoadApiKey());
        Assert.DoesNotContain(
            apiKey,
            File.ReadAllText(Path.Combine(folder.Path, "runtime-credentials.dpapi")),
            StringComparison.Ordinal);

        var namedProvider = options with { Engine = LocalRuntimeEngines.LmStudio };
        var error = Assert.Throws<InvalidDataException>(() =>
            RuntimeSettingsStore.Save(folder.Path, namedProvider));
        Assert.Contains("Custom engine", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoteRuntime_RequiresProtectedKeyAndSendsBearerWithoutLeakingIt()
    {
        const string apiKey = "cp9-bearer-secret-canary";
        var options = CreateOptions(new Uri("https://runtime.example.test/v1/")) with
        {
            AllowRemoteHttpsEndpoint = true,
            CapabilityProbeEnabled = false
        };
        var handler = new CapabilityProbeHandler(nativeToolCallSupported: true);
        using var client = new HttpClient(handler);
        var runtime = new OpenAiCompatibleLocalModelRuntime(
            client,
            options,
            assistantProfile: null,
            apiKeyResolver: () => apiKey,
            capabilityProfiles: null);

        var health = await runtime.CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.True(health.Succeeded, health.Summary);
        Assert.NotEmpty(handler.AuthorizationHeaders);
        Assert.All(handler.AuthorizationHeaders, authorization =>
        {
            Assert.Equal("Bearer", authorization?.Scheme);
            Assert.Equal(apiKey, authorization?.Parameter);
        });
        Assert.DoesNotContain(apiKey, health.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, JsonSerializer.Serialize(health.CapabilityProfile), StringComparison.Ordinal);

        var chatOnlyDispatch = ((IBoundModelDispatchSource)runtime).CaptureBoundModelDispatch();
        var protocolError = Assert.Throws<InvalidOperationException>(() =>
            AliOrchestrationPlanningClient.RequireBoundEngineeringProtocol(chatOnlyDispatch));
        Assert.Contains("Autonomous engineering is disabled", protocolError.Message, StringComparison.OrdinalIgnoreCase);

        var missingKeyHandler = new CapabilityProbeHandler(nativeToolCallSupported: true);
        using var missingKeyClient = new HttpClient(missingKeyHandler);
        var missingKeyRuntime = new OpenAiCompatibleLocalModelRuntime(
            missingKeyClient,
            options,
            assistantProfile: null,
            apiKeyResolver: () => null,
            capabilityProfiles: null);

        var missingKeyHealth = await missingKeyRuntime.CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.False(missingKeyHealth.Succeeded);
        Assert.Contains("requires an API key", missingKeyHealth.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(missingKeyHandler.RequestBodies);
    }

    [Fact]
    public async Task RemoteInventory_RequiresExplicitApprovalAndSendsTheProtectedBearer()
    {
        const string apiKey = "cp9-inventory-secret-canary";
        var handler = new CapabilityProbeHandler(nativeToolCallSupported: true);
        using var client = new HttpClient(handler);

        var choices = await MainWindowViewModel.FetchInstalledRuntimeModelChoicesAsync(
            client,
            new Uri("https://runtime.example.test/v1/"),
            allowPrivateLanEndpoint: false,
            allowRemoteHttpsEndpoint: true,
            apiKey,
            TestContext.Current.CancellationToken);

        Assert.Equal("provider/model-revision-1", Assert.Single(choices).Model);
        var authorization = Assert.Single(handler.AuthorizationHeaders);
        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal(apiKey, authorization?.Parameter);
    }

    [Theory]
    [InlineData(true, true, RuntimeProtocolIdentities.NativeOpenAiTools)]
    [InlineData(false, false, RuntimeProtocolIdentities.StructuredDecision)]
    public async Task CapabilityProbe_SelectsOnlyAFunctionallyProvenProtocol(
        bool nativeProbeSucceeds,
        bool nativeToolsEnabled,
        string expectedProtocol)
    {
        using var folder = new TestFolder();
        var handler = new CapabilityProbeHandler(nativeProbeSucceeds);
        using var client = new HttpClient(handler);
        var options = CreateOptions(new Uri("http://127.0.0.1:1234/v1/")) with
        {
            SupportsToolCalls = nativeToolsEnabled,
            CapabilityProbeEnabled = true,
            TokenizerIdentity = "provider-tokenizer-revision-7",
            RollingWindowMode = "provider-managed-sliding"
        };
        var profiles = new RuntimeCapabilityProfileStore(folder.Path);
        var runtime = new OpenAiCompatibleLocalModelRuntime(
            client,
            options,
            assistantProfile: null,
            apiKeyResolver: null,
            capabilityProfiles: profiles);

        var health = await runtime.CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.True(health.Succeeded, health.Summary);
        var profile = Assert.IsType<RuntimeCapabilityProfile>(health.CapabilityProfile);
        Assert.Equal(expectedProtocol, profile.ProtocolIdentity);
        Assert.Equal(
            nativeProbeSucceeds ? RuntimeCapabilityState.Supported : RuntimeCapabilityState.Unsupported,
            profile.NativeToolCalling.State);
        Assert.Equal(RuntimeCapabilityState.Supported, profile.StructuredDecision.State);
        Assert.Equal(profile, profiles.Load(profile.Identity));

        var dispatch = ((IBoundModelDispatchSource)runtime).CaptureBoundModelDispatch();
        Assert.Equal(profile.Identity, dispatch.RuntimeBinding.CapabilityProfileIdentity);
        Assert.Equal(profile.Identity, dispatch.ModelBinding.CapabilityProfileIdentity);
        Assert.Equal(expectedProtocol, dispatch.GenerationSettingsBinding.ProtocolIdentity);
        Assert.Equal("provider-tokenizer-revision-7", dispatch.GenerationSettingsBinding.TokenizerIdentity);
        Assert.Equal("provider-managed-sliding", dispatch.GenerationSettingsBinding.RollingWindowMode);
        Assert.Equal(8_192, dispatch.GenerationSettingsBinding.ContextTokens);
        Assert.Equal(
            nativeProbeSucceeds,
            AliOrchestrationPlanningClient.RequireBoundEngineeringProtocol(dispatch));
    }

    [Fact]
    public async Task CapabilityProbe_FailsClosedWhenNativeToolsAreEnabledButNotProven()
    {
        var handler = new CapabilityProbeHandler(nativeToolCallSupported: false);
        using var client = new HttpClient(handler);
        var options = CreateOptions(new Uri("http://127.0.0.1:1234/v1/")) with
        {
            SupportsToolCalls = true,
            CapabilityProbeEnabled = true
        };
        var runtime = new OpenAiCompatibleLocalModelRuntime(client, options);

        var health = await runtime.CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.False(health.Succeeded);
        Assert.Contains("exact endpoint/model probe", health.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            RuntimeCapabilityState.Unsupported,
            health.CapabilityProfile?.NativeToolCalling.State);
        Assert.Equal(
            RuntimeCapabilityState.Supported,
            health.CapabilityProfile?.StructuredDecision.State);
    }

    [Fact]
    public async Task VisionProbeFailure_IsAdvisoryAndPreservesManualVisionOverride()
    {
        var handler = new CapabilityProbeHandler(
            nativeToolCallSupported: false,
            visionFailureStatus: HttpStatusCode.BadGateway);
        using var client = new HttpClient(handler);
        var runtime = new OpenAiCompatibleLocalModelRuntime(
            client,
            CreateOptions(new Uri("http://127.0.0.1:1234/v1/")) with
            {
                SupportsVision = true,
                CapabilityProbeEnabled = true
            });

        var health = await runtime.CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.True(health.Succeeded, health.Summary);
        Assert.Equal(RuntimeCapabilityState.Unknown, health.CapabilityProfile?.Vision.State);
        Assert.True(runtime.ActiveProfile.SupportsVision);
        Assert.Contains("manual override", health.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TypedVisionStabilityRefusal_DisablesVisionForThatActivationOnly()
    {
        var handler = new CapabilityProbeHandler(
            nativeToolCallSupported: false,
            visionFailureStatus: HttpStatusCode.UnsupportedMediaType);
        using var client = new HttpClient(handler);
        var runtime = new OpenAiCompatibleLocalModelRuntime(
            client,
            CreateOptions(new Uri("http://127.0.0.1:1234/v1/")) with
            {
                SupportsVision = true,
                CapabilityProbeEnabled = true
            });

        var health = await runtime.CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.True(health.Succeeded, health.Summary);
        Assert.Equal(RuntimeCapabilityState.Unsupported, health.CapabilityProfile?.Vision.State);
        Assert.False(runtime.ActiveProfile.SupportsVision);
        Assert.Contains("typed endpoint stability response", health.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CapabilityIdentity_ChangesWithContextTokenizerAndProtocolMaterial()
    {
        var observedAt = DateTimeOffset.Parse("2026-08-03T12:00:00Z");
        var supported = new RuntimeCapabilityObservation(
            RuntimeCapabilityState.Supported,
            "functional test",
            observedAt);
        var unsupported = new RuntimeCapabilityObservation(
            RuntimeCapabilityState.Unsupported,
            "functional test",
            observedAt);
        var baseline = CreateOptions(new Uri("http://127.0.0.1:1234/v1/")) with
        {
            SupportsToolCalls = true,
            TokenizerIdentity = "tokenizer-a",
            ContextTokens = 8_192
        };

        var native = RuntimeCapabilityProfile.Create(
            baseline,
            supported,
            supported,
            unsupported,
            supported,
            unsupported);
        var differentContext = RuntimeCapabilityProfile.Create(
            baseline with { ContextTokens = 65_536 },
            supported,
            supported,
            unsupported,
            supported,
            unsupported);
        var differentTokenizer = RuntimeCapabilityProfile.Create(
            baseline with { TokenizerIdentity = "tokenizer-b" },
            supported,
            supported,
            unsupported,
            supported,
            unsupported);
        var structured = RuntimeCapabilityProfile.Create(
            baseline with { SupportsToolCalls = false },
            unsupported,
            supported,
            unsupported,
            supported,
            unsupported);

        Assert.NotEqual(native.Identity, differentContext.Identity);
        Assert.NotEqual(native.Identity, differentTokenizer.Identity);
        Assert.NotEqual(native.Identity, structured.Identity);
        Assert.Equal(8_192, native.ContextTokens);
        Assert.Equal(65_536, differentContext.ContextTokens);
        Assert.Equal(RuntimeProtocolIdentities.NativeOpenAiTools, native.ProtocolIdentity);
        Assert.Equal(RuntimeProtocolIdentities.StructuredDecision, structured.ProtocolIdentity);
    }

    private static OpenAiCompatibleRuntimeOptions CreateOptions(Uri endpoint) =>
        new(
            Enabled: true,
            Endpoint: endpoint,
            Model: "provider/model-revision-1",
            DisplayName: "Provider model revision 1",
            Family: "provider-reported",
            Size: "provider-reported",
            Quantization: "provider-reported",
            ContextTokens: 8_192,
            OutputTokenLimit: 1_024,
            Temperature: 0.2,
            TopP: 0.9,
            StreamingEnabled: false,
            SupportsVision: false,
            SupportsToolCalls: false,
            AllowPrivateLanEndpoint: false)
        {
            Engine = LocalRuntimeEngines.GenericOpenAi
        };

    private sealed class CapabilityProbeHandler(
        bool nativeToolCallSupported,
        HttpStatusCode? visionFailureStatus = null) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        public List<AuthenticationHeaderValue?> AuthorizationHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AuthorizationHeaders.Add(request.Headers.Authorization);
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                return Json("{\"data\":[{\"id\":\"provider/model-revision-1\",\"object\":\"model\"}]}");
            }

            if (body.Contains("\"image_url\"", StringComparison.Ordinal)
                && visionFailureStatus is { } visionStatus)
            {
                return new HttpResponseMessage(visionStatus)
                {
                    Content = new StringContent(
                        "{\"error\":{\"message\":\"typed image refusal\"}}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            using var payload = JsonDocument.Parse(body);
            if (payload.RootElement.TryGetProperty("tools", out var tools)
                && tools.ValueKind == JsonValueKind.Array)
            {
                return nativeToolCallSupported
                    ? Json(
                        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"call_capability\",\"type\":\"function\",\"function\":{\"name\":\"ali_runtime_capability_probe\",\"arguments\":\"{\\\"value\\\":\\\"ali-runtime-capability-v1\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}")
                    : Completion("native tool protocol unavailable");
            }

            if (payload.RootElement.TryGetProperty("response_format", out _))
            {
                return Completion("{\"accepted\":true,\"nonce\":\"ali-structured-decision-v1\"}");
            }

            return Completion("OK");
        }

        private static HttpResponseMessage Completion(string content) =>
            Json(JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new { role = "assistant", content },
                        finish_reason = "stop"
                    }
                },
                usage = new { completion_tokens = 1 }
            }));

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class TestFolder : IDisposable
    {
        public TestFolder()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "AliRuntimeProviderCapabilityTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
