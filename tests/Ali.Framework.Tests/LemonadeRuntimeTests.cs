using System.Net;
using System.Text;
using System.Text.Json;
using Ali.Modules.Runtime;
using Ali.Modules.Runtime.Models;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests;

public sealed class LemonadeRuntimeTests
{
    [Fact]
    public async Task FirstChat_LoadsLemonadeWithConfiguredContextBeforeGeneration()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var runtime = new OpenAiCompatibleLocalModelRuntime(
            client,
            new OpenAiCompatibleRuntimeOptions(
                true,
                new Uri("http://127.0.0.1:13305/api/v1/"),
                "gpt-oss-20b-mxfp4-GGUF",
                "GPT-OSS 20B",
                "gpt-oss",
                "20B",
                "MXFP4",
                8192,
                2048,
                0.2,
                0.9,
                true,
                false,
                false,
                false)
            { Engine = LocalRuntimeEngines.Lemonade, ReasoningEffort = "low" });

        var response = await runtime.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Return OK")],
            new ChatOptions { MaxOutputTokens = 32 },
            TestContext.Current.CancellationToken);

        Assert.Contains(handler.Requests, request => request.Path == "/api/v1/load");
        var load = Assert.Single(handler.Requests, request => request.Path == "/api/v1/load");
        using var document = JsonDocument.Parse(load.Body);
        Assert.Equal(8192, document.RootElement.GetProperty("ctx_size").GetInt32());
        Assert.Equal("gpt-oss-20b-mxfp4-GGUF", document.RootElement.GetProperty("model_name").GetString());
        Assert.Equal("OK", response.Text);
    }

    [Fact]
    public async Task ConsecutiveChats_ReuseTheReadyLemonadeModelWithoutReloading()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var runtime = CreateLemonadeRuntime(
            client,
            "gpt-oss-20b-mxfp4-GGUF",
            "gpt-oss",
            thinkingEnabled: false,
            contextTokens: 8192,
            outputTokenLimit: 2048);

        _ = await runtime.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "first")],
            cancellationToken: TestContext.Current.CancellationToken);
        _ = await runtime.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "second")],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count(request => request.Path == "/api/v1/chat/completions"));
        Assert.Single(handler.Requests, request => request.Path == "/api/v1/load");
    }

    [Fact]
    public async Task UserSelectedMaximumContextAndOutput_AreSentUnchanged()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var runtime = CreateLemonadeRuntime(
            client,
            "Qwen2.5-Coder-14B-Instruct-GGUF-Q4_K_M",
            "Qwen",
            thinkingEnabled: false,
            contextTokens: 262_144,
            outputTokenLimit: 32_768);

        _ = await runtime.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Return OK")],
            cancellationToken: TestContext.Current.CancellationToken);

        var load = Assert.Single(handler.Requests, request => request.Path == "/api/v1/load");
        using var loadPayload = JsonDocument.Parse(load.Body);
        Assert.Equal(262_144, loadPayload.RootElement.GetProperty("ctx_size").GetInt32());

        var chat = Assert.Single(handler.Requests, request => request.Path == "/api/v1/chat/completions");
        using var chatPayload = JsonDocument.Parse(chat.Body);
        Assert.Equal(32_768, chatPayload.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task PerCallReasoningOverride_DoesNotReplaceSelectedMainEffort()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var runtime = new OpenAiCompatibleLocalModelRuntime(
            client,
            new OpenAiCompatibleRuntimeOptions(
                true,
                new Uri("http://127.0.0.1:13305/api/v1/"),
                "gpt-oss-20b-mxfp4-GGUF",
                "GPT-OSS 20B",
                "gpt-oss",
                "20B",
                "MXFP4",
                8192,
                2048,
                0.2,
                0.9,
                true,
                false,
                false,
                false)
            {
                Engine = LocalRuntimeEngines.Lemonade,
                ReasoningEffort = "high",
                ThinkingControl = ModelThinkingControl.GptOssReasoningEffort
            });
        var options = new ChatOptions
        {
            MaxOutputTokens = 64,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["ali.reasoningEffortOverride"] = "low"
            }
        };

        _ = await runtime.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Return OK")],
            options,
            TestContext.Current.CancellationToken);

        var chat = Assert.Single(handler.Requests, request => request.Path == "/api/v1/chat/completions");
        using var payload = JsonDocument.Parse(chat.Body);
        Assert.Equal(
            "high",
            payload.RootElement
                .GetProperty("chat_template_kwargs")
                .GetProperty("reasoning_effort")
                .GetString());
        Assert.Equal("high", runtime.ReasoningEffort);
    }

    [Fact]
    public async Task Qwen25Coder_OmitsUnsupportedThinkingFields()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var runtime = CreateLemonadeRuntime(
            client,
            "Qwen2.5-Coder-14B-Instruct-GGUF-Q4_K_M",
            "Qwen",
            thinkingEnabled: true);

        _ = await runtime.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Return OK")],
            new ChatOptions { MaxOutputTokens = 32 },
            TestContext.Current.CancellationToken);

        var chat = Assert.Single(handler.Requests, request => request.Path == "/api/v1/chat/completions");
        using var payload = JsonDocument.Parse(chat.Body);
        Assert.False(payload.RootElement.TryGetProperty("chat_template_kwargs", out _));
        Assert.False(payload.RootElement.TryGetProperty("think", out _));
        var identity = payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("created by Chris Sorrells", identity, StringComparison.Ordinal);
        Assert.Contains("Qwen2.5-Coder-14B-Instruct-GGUF-Q4_K_M", identity, StringComparison.Ordinal);
        Assert.Contains("configured family is Qwen", identity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GemmaThinkingCheckbox_AddsOnlyTheRequiredSystemToken()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var runtime = CreateLemonadeRuntime(
            client,
            "gemma-4-26B-A4B-it-qat-q4_0-gguf-Q4_0",
            "Gemma",
            thinkingEnabled: true);

        _ = await runtime.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Return OK")],
            new ChatOptions { MaxOutputTokens = 32 },
            TestContext.Current.CancellationToken);

        var chat = Assert.Single(handler.Requests, request => request.Path == "/api/v1/chat/completions");
        using var payload = JsonDocument.Parse(chat.Body);
        var firstSystemMessage = payload.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetString();
        Assert.StartsWith("<|think|>\n", firstSystemMessage, StringComparison.Ordinal);
        Assert.False(payload.RootElement.TryGetProperty("chat_template_kwargs", out _));
        Assert.False(payload.RootElement.TryGetProperty("think", out _));
    }

    [Theory]
    [InlineData(ModelThinkingControl.QwenTemplateToggle, "chat_template_kwargs.enable_thinking=true")]
    [InlineData(ModelThinkingControl.GptOssReasoningEffort, "chat_template_kwargs.reasoning_effort=low")]
    [InlineData(ModelThinkingControl.None, "no thinking control required; no thinking field sent")]
    public void ThinkingProtocol_UsesTheExplicitCapabilitySetting(
        ModelThinkingControl control,
        string expected)
    {
        Assert.Equal(expected, ModelThinkingPolicy.Describe(control, true, "low"));
    }

    [Fact]
    public async Task LaterChat_ReloadsWhenLemonadeEvictsThePreviouslyPreparedModel()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var runtime = new OpenAiCompatibleLocalModelRuntime(
            client,
            new OpenAiCompatibleRuntimeOptions(
                true,
                new Uri("http://127.0.0.1:13305/api/v1/"),
                "gpt-oss-20b-mxfp4-GGUF",
                "GPT-OSS 20B",
                "gpt-oss",
                "20B",
                "MXFP4",
                8192,
                2048,
                0.2,
                0.9,
                true,
                false,
                false,
                false)
            { Engine = LocalRuntimeEngines.Lemonade, ReasoningEffort = "low" });

        await runtime.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "first")],
            new ChatOptions { MaxOutputTokens = 32 },
            TestContext.Current.CancellationToken);
        handler.EvictModel();
        var second = await runtime.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "second")],
            new ChatOptions { MaxOutputTokens = 32 },
            TestContext.Current.CancellationToken);

        Assert.Equal("OK", second.Text);
        Assert.Equal(2, handler.Requests.Count(request => request.Path == "/api/v1/load"));
    }

    [Fact]
    public async Task LargeTurn_ForwardsSelectedOutputWithoutLocalClamping()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var runtime = new OpenAiCompatibleLocalModelRuntime(
            client,
            new OpenAiCompatibleRuntimeOptions(
                true,
                new Uri("http://127.0.0.1:13305/api/v1/"),
                "gpt-oss-20b-mxfp4-GGUF",
                "GPT-OSS 20B",
                "gpt-oss",
                "20B",
                "MXFP4",
                4096,
                2048,
                0.2,
                0.9,
                true,
                false,
                false,
                false)
            { Engine = LocalRuntimeEngines.Lemonade, ReasoningEffort = "low" });

        await runtime.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.User,
                new string('x', 6_000))],
            new ChatOptions { MaxOutputTokens = 2048 },
            TestContext.Current.CancellationToken);

        var chat = Assert.Single(handler.Requests, request => request.Path == "/api/v1/chat/completions");
        using var payload = JsonDocument.Parse(chat.Body);
        Assert.Equal(2048, payload.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task OversizedTurn_IsForwardedWithoutLocalCapacityRejection()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var runtime = new OpenAiCompatibleLocalModelRuntime(
            client,
            new OpenAiCompatibleRuntimeOptions(
                true,
                new Uri("http://127.0.0.1:13305/api/v1/"),
                "gpt-oss-20b-mxfp4-GGUF",
                "GPT-OSS 20B",
                "gpt-oss",
                "20B",
                "MXFP4",
                4096,
                2048,
                0.2,
                0.9,
                true,
                false,
                false,
                false)
            { Engine = LocalRuntimeEngines.Lemonade, ReasoningEffort = "low" });

        await runtime.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.User,
                new string('x', 20_000))],
            new ChatOptions { MaxOutputTokens = 2048 },
            TestContext.Current.CancellationToken);

        var chat = Assert.Single(handler.Requests, request => request.Path == "/api/v1/chat/completions");
        using var payload = JsonDocument.Parse(chat.Body);
        Assert.Equal(2048, payload.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task RuntimeJsonError_IsReducedToItsHumanReadableMessage()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"code\":\"bad_request\",\"message\":\"The selected model rejected this request.\"}}");
        using var client = new HttpClient(handler);
        var runtime = new OpenAiCompatibleLocalModelRuntime(
            client,
            new OpenAiCompatibleRuntimeOptions(
                true,
                new Uri("http://127.0.0.1:13305/api/v1/"),
                "gpt-oss-20b-mxfp4-GGUF",
                "GPT-OSS 20B",
                "gpt-oss",
                "20B",
                "MXFP4",
                8192,
                2048,
                0.2,
                0.9,
                true,
                false,
                false,
                false)
            { Engine = LocalRuntimeEngines.Lemonade, ReasoningEffort = "low" });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            runtime.GetResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Return OK")],
                new ChatOptions { MaxOutputTokens = 32 },
                TestContext.Current.CancellationToken));

        Assert.Contains("The selected model rejected this request.", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bad_request", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LemonadeReadiness_RequiresReadyBackendAndSufficientContext()
    {
        const string ready = """
            {"all_models_loaded":[{"model_name":"gpt-oss","loaded":true,"status":"ready","backend_health":"ready","recipe_options":{"ctx_size":16384}}]}
            """;
        const string tooSmall = """
            {"all_models_loaded":[{"model_name":"gpt-oss","loaded":true,"status":"ready","backend_health":"ready","recipe_options":{"ctx_size":8192}}]}
            """;
        const string loading = """
            {"all_models_loaded":[{"model_name":"gpt-oss","loaded":true,"status":"loading","backend_health":"starting","recipe_options":{"ctx_size":16384}}]}
            """;

        Assert.True(OpenAiCompatibleLocalModelRuntime.LemonadeListsModelAsReady(ready, "gpt-oss", 16384));
        Assert.False(OpenAiCompatibleLocalModelRuntime.LemonadeListsModelAsReady(tooSmall, "gpt-oss", 16384));
        Assert.False(OpenAiCompatibleLocalModelRuntime.LemonadeListsModelAsReady(loading, "gpt-oss", 16384));
    }

    [Fact]
    public void ModelMetadata_EnablesNativeToolsOnlyForTheSelectedLabeledModel()
    {
        const string body = """
            {"data":[
              {"id":"gpt-oss-20b-mxfp4-GGUF","labels":["tool-calling","chat"]},
              {"id":"other","labels":["chat"]}
            ]}
            """;

        Assert.True(OpenAiCompatibleLocalModelRuntime.ModelAdvertisesToolCalling(
            body,
            "gpt-oss-20b-mxfp4-GGUF"));
        Assert.False(OpenAiCompatibleLocalModelRuntime.ModelAdvertisesToolCalling(body, "other"));
    }

    [Fact]
    public async Task SuccessfulCandidateHealthCheck_LeavesTheVerifiedModelResidentForActivation()
    {
        var fallback = new TrackingRuntime("fallback", succeeded: true);
        var candidate = new TrackingRuntime("candidate", succeeded: true);
        var controller = new SafeActivatingLocalRuntime(fallback, candidate);

        var health = await controller.CheckCandidateAsync(TestContext.Current.CancellationToken);

        Assert.True(health.Succeeded);
        Assert.Equal(1, candidate.HealthChecks);
        Assert.Equal(0, candidate.Shutdowns);
        Assert.True(controller.ActivateLastHealthChecked());
        Assert.Equal(candidate.ActiveProfile, controller.ActiveProfile);
    }

    private static OpenAiCompatibleLocalModelRuntime CreateLemonadeRuntime(
        HttpClient client,
        string model,
        string family,
        bool thinkingEnabled,
        int contextTokens = 8192,
        int outputTokenLimit = 2048) =>
        new(
            client,
            new OpenAiCompatibleRuntimeOptions(
                true,
                new Uri("http://127.0.0.1:13305/api/v1/"),
                model,
                model,
                family,
                "unknown",
                "Installed package default",
                contextTokens,
                outputTokenLimit,
                0.2,
                0.9,
                true,
                false,
                false,
                false)
            {
                Engine = LocalRuntimeEngines.Lemonade,
                ReasoningEffort = "low",
                ThinkingEnabled = thinkingEnabled,
                ThinkingControl = family.Equals("gpt-oss", StringComparison.OrdinalIgnoreCase)
                    ? ModelThinkingControl.GptOssReasoningEffort
                    : family.Equals("Gemma", StringComparison.OrdinalIgnoreCase)
                        ? ModelThinkingControl.GemmaSystemPromptToken
                        : ModelThinkingControl.None
            });

    private sealed class RecordingHandler(
        HttpStatusCode chatStatus = HttpStatusCode.OK,
        string? chatResponseBody = null) : HttpMessageHandler
    {
        private bool _loaded;
        private string _loadedModel = "gpt-oss-20b-mxfp4-GGUF";
        private int _loadedContext = 8192;

        public List<RecordedRequest> Requests { get; } = [];

        public void EvictModel() => _loaded = false;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath, body));

            if (request.RequestUri!.AbsolutePath == "/api/v1/load")
            {
                _loaded = true;
                using var load = JsonDocument.Parse(body);
                _loadedModel = load.RootElement.GetProperty("model_name").GetString() ?? _loadedModel;
                _loadedContext = load.RootElement.GetProperty("ctx_size").GetInt32();
            }

            var json = request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/health" when _loaded => JsonSerializer.Serialize(new
                {
                    status = "ok",
                    all_models_loaded = new[]
                    {
                        new
                        {
                            model_name = _loadedModel,
                            loaded = true,
                            status = "ready",
                            backend_health = "ready",
                            recipe_options = new { ctx_size = _loadedContext }
                        }
                    }
                }),
                "/api/v1/health" => "{\"status\":\"ok\",\"all_models_loaded\":[]}",
                "/api/v1/load" => "{\"status\":\"ok\"}",
                "/api/v1/chat/completions" => chatResponseBody
                    ?? "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}],\"usage\":{\"completion_tokens\":1}}",
                _ => "{}"
            };
            var status = request.RequestUri.AbsolutePath == "/api/v1/chat/completions"
                ? chatStatus
                : HttpStatusCode.OK;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Body);

    private sealed class TrackingRuntime(string identity, bool succeeded) : ILocalModelRuntime, IModelSwitchAwareRuntime
    {
        public int HealthChecks { get; private set; }

        public int Shutdowns { get; private set; }

        public int Unloads { get; private set; }

        public string RuntimeIdentity => identity;

        public ModelProfile ActiveProfile { get; } = ModelProfile.UnconfiguredFactorySafe() with
        {
            ProfileId = identity,
            DisplayName = identity
        };

        public async IAsyncEnumerable<ModelToken> StreamChatAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new ModelToken("ok", Ali.Modules.Evidence.EvidenceStatus.Unverified);
        }

        public Task<RuntimeHealthCheck> CheckHealthAsync(CancellationToken cancellationToken)
        {
            HealthChecks++;
            return Task.FromResult(new RuntimeHealthCheck(
                succeeded,
                succeeded ? "ready" : "failed",
                DateTimeOffset.UtcNow,
                TimeSpan.Zero));
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            Shutdowns++;
            return Task.CompletedTask;
        }

        public Task UnloadForModelSwitchAsync(CancellationToken cancellationToken)
        {
            Unloads++;
            return Task.CompletedTask;
        }
    }
}
