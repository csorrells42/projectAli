using System.Net;
using System.Text;
using System.Text.Json;
using Ali.Modules.Runtime;
using Ali.UI.ViewModels;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests;

public sealed class LmStudioRuntimeTests
{
    [Fact]
    public void EngineCatalog_UsesExplicitProvidersWithoutInferringFromPorts()
    {
        var lmStudioEndpoint = LocalRuntimeEngines.DefaultEndpoint(LocalRuntimeEngines.LmStudio);
        var ollamaEndpoint = LocalRuntimeEngines.DefaultEndpoint(LocalRuntimeEngines.Ollama);
        var llamaCppEndpoint = LocalRuntimeEngines.DefaultEndpoint(LocalRuntimeEngines.LlamaCpp);
        var lemonadeEndpoint = LocalRuntimeEngines.DefaultEndpoint(LocalRuntimeEngines.Lemonade);
        var customEndpoint = LocalRuntimeEngines.DefaultEndpoint(LocalRuntimeEngines.GenericOpenAi);

        Assert.Equal("http://127.0.0.1:1234/v1/", lmStudioEndpoint.ToString());
        Assert.Equal("http://127.0.0.1:11434/v1/", ollamaEndpoint.ToString());
        Assert.Equal("http://127.0.0.1:8080/v1/", llamaCppEndpoint.ToString());
        Assert.Equal("http://127.0.0.1:13305/api/v1/", lemonadeEndpoint.ToString());
        Assert.Equal("http://127.0.0.1:1234/v1/", customEndpoint.ToString());
        Assert.Equal(
            "LM Studio|Ollama|llama.cpp|Lemonade|OpenAI-compatible/Custom",
            string.Join('|', LocalRuntimeEngines.Choices));
        Assert.Equal(LocalRuntimeEngines.GenericOpenAi, LocalRuntimeEngines.Normalize(string.Empty, ollamaEndpoint));
        Assert.Equal(LocalRuntimeEngines.GenericOpenAi, LocalRuntimeEngines.Normalize("unknown-provider", lemonadeEndpoint));
        Assert.Equal(LocalRuntimeEngines.LmStudio, LocalRuntimeEngines.Normalize(LocalRuntimeEngines.LmStudio, ollamaEndpoint));
        Assert.Equal(LocalRuntimeEngines.Ollama, LocalRuntimeEngines.Normalize(LocalRuntimeEngines.Ollama, lmStudioEndpoint));
        Assert.Equal(LocalRuntimeEngines.LlamaCpp, LocalRuntimeEngines.Normalize(LocalRuntimeEngines.LlamaCpp, ollamaEndpoint));
        Assert.Equal(LocalRuntimeEngines.Lemonade, LocalRuntimeEngines.Normalize(LocalRuntimeEngines.Lemonade, lmStudioEndpoint));
        Assert.Equal(LocalRuntimeEngines.GenericOpenAi, LocalRuntimeEngines.Normalize("OpenAI-compatible", lmStudioEndpoint));
    }

    [Fact]
    public void MissingRuntimeSettings_DefaultToUnselectedLmStudioWithoutGuessingAModel()
    {
        var options = RuntimeSettingsStore.GetDefaultOptions();

        Assert.False(options.Enabled);
        Assert.Equal(LocalRuntimeEngines.LmStudio, options.Engine);
        Assert.Equal("http://127.0.0.1:1234/v1/", options.Endpoint.ToString());
        Assert.Empty(options.Model);
        Assert.True(options.CapabilityProbeEnabled);
    }

    [Fact]
    public void RuntimeSettingsFromBeforeCapabilityProbing_EnableTheCurrentSafeDefault()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AliLmStudioRuntimeTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                RuntimeSettingsStore.GetSettingsPath(root),
                """
                {
                  "enabled": true,
                  "endpoint": "http://127.0.0.1:1234/v1/",
                  "model": "openai/gpt-oss-20b",
                  "displayName": "GPT-OSS 20B",
                  "family": "GPT-OSS",
                  "size": "20B",
                  "quantization": "Installed package default",
                  "contextTokens": 65536,
                  "outputTokenLimit": 8192,
                  "temperature": 0.2,
                  "topP": 0.95,
                  "streamingEnabled": true,
                  "supportsVision": false,
                  "supportsToolCalls": true,
                  "allowPrivateLanEndpoint": false,
                  "engine": "LM Studio",
                  "reasoningEffort": "low",
                  "thinkingEnabled": false
                }
                """);

            var loaded = Assert.IsType<OpenAiCompatibleRuntimeOptions>(
                RuntimeSettingsStore.LoadOpenAiCompatibleOptions(root));

            Assert.True(loaded.CapabilityProbeEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OpenAiModelDiscovery_ParsesLmStudioModelIdentifiers()
    {
        const string json =
            """
            {
              "object": "list",
              "data": [
                {
                  "id": "openai/gpt-oss-20b",
                  "object": "model",
                  "context_length": 65536,
                  "max_output_tokens": 8192,
                  "capabilities": ["chat", "tool_calls", "vision"],
                  "thinking_control": "QwenTemplateToggle",
                  "tokenizer_identity": "provider-tokenizer-r4",
                  "rolling_window_mode": "provider-managed-sliding"
                }
              ]
            }
            """;

        var choice = Assert.Single(RuntimeModelChoiceCatalog.ParseRuntimeModelChoices(json));

        Assert.Equal("openai/gpt-oss-20b", choice.Model);
        Assert.Equal("Installed runtime model", choice.Source);
        Assert.Equal(65_536, choice.DefaultContextTokens);
        Assert.Equal(8_192, choice.DefaultOutputTokenLimit);
        Assert.True(choice.SupportsToolCalls);
        Assert.True(choice.SupportsVision);
        Assert.Equal(ModelThinkingControl.QwenTemplateToggle, choice.ThinkingControl);
        Assert.Equal("provider-tokenizer-r4", choice.TokenizerIdentity);
        Assert.Equal("provider-managed-sliding", choice.RollingWindowMode);
    }

    [Fact]
    public void OpenAiModelDiscovery_ExcludesEmbeddingIdsAndMetadataFromChatChoices()
    {
        const string json =
            """
            {
              "data": [
                { "id": "openai/gpt-oss-20b", "object": "model" },
                { "id": "hybrid-chat-model", "object": "model", "capabilities": ["chat", "embedding"] },
                { "id": "text-embedding-nomic-embed-text-v1.5", "object": "model" },
                { "id": "custom-encoder", "object": "model", "type": "embedding" },
                { "id": "metadata-encoder", "object": "model", "capabilities": ["embedding"] }
              ]
            }
            """;

        var choices = RuntimeModelChoiceCatalog.ParseRuntimeModelChoices(json);

        Assert.Equal(
            ["hybrid-chat-model", "openai/gpt-oss-20b"],
            choices.Select(choice => choice.Model).OrderBy(value => value, StringComparer.Ordinal));
    }

    [Fact]
    public async Task OpenAiModelDiscovery_NormalizesAMissingBaseSlashAndParsesABoundedResponse()
    {
        var handler = new RuntimeModelInventoryHandler(
            new StringContent(
                "{\"data\":[{\"id\":\"openai/gpt-oss-20b\",\"object\":\"model\"}]}",
                Encoding.UTF8,
                "application/json"));
        using var client = new HttpClient(handler);

        var choice = Assert.Single(await MainWindowViewModel.FetchInstalledRuntimeModelChoicesAsync(
            client,
            new Uri("http://127.0.0.1:1234/v1"),
            TestContext.Current.CancellationToken));

        Assert.Equal("openai/gpt-oss-20b", choice.Model);
        Assert.Equal("http://127.0.0.1:1234/v1/models", handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task OpenAiModelDiscovery_RejectsAPublicEndpointBeforeSendingARequest()
    {
        var handler = new RuntimeModelInventoryHandler(
            new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MainWindowViewModel.FetchInstalledRuntimeModelChoicesAsync(
                client,
                new Uri("https://example.com/v1/"),
                TestContext.Current.CancellationToken));

        Assert.Contains("Only loopback endpoints", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.RequestUri);
    }

    [Theory]
    [InlineData("{\"data\":[{\"id\":\"openai/gpt-oss-20b\"}]}")]
    [InlineData("{\"models\":[{\"key\":\"openai/gpt-oss-20b\"}]}")]
    [InlineData("{\"all_models_loaded\":[{\"model_name\":\"openai/gpt-oss-20b\"}]}")]
    public void RuntimeModelInventory_RequiresAnExactParsedModelIdentity(string json)
    {
        Assert.True(LocalRuntimeModelInventory.ListsExactModel(json, "openai/gpt-oss-20b"));
        Assert.False(LocalRuntimeModelInventory.ListsExactModel(json, "openai/gpt-oss-20"));
        Assert.False(LocalRuntimeModelInventory.ListsExactModel(
            "{\"metadata\":{\"name\":\"openai/gpt-oss-20b\"}}",
            "openai/gpt-oss-20b"));
    }

    [Fact]
    public async Task RuntimeHealth_RejectsASubstringModelInventoryMatch()
    {
        var handler = new RuntimeModelInventoryHandler(
            new StringContent(
                "{\"data\":[{\"id\":\"openai/gpt-oss-20b-extra\"}]}",
                Encoding.UTF8,
                "application/json"));
        using var client = new HttpClient(handler);
        var runtime = CreateChatRuntime(client, LocalRuntimeEngines.LmStudio);

        var health = await runtime.CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.False(health.Succeeded);
        Assert.Contains("was not listed", health.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("http://127.0.0.1:1234/v1/models", handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task RuntimeHealth_RejectsAnOversizedModelInventoryBeforePromptCalls()
    {
        var bytes = Encoding.UTF8.GetBytes(
            new string('x', MainWindowViewModel.MaximumRuntimeModelInventoryResponseBytes + 1));
        var handler = new RuntimeModelInventoryHandler(new UnknownLengthContent(bytes));
        using var client = new HttpClient(handler);
        var runtime = CreateChatRuntime(client, LocalRuntimeEngines.LmStudio);

        var health = await runtime.CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.False(health.Succeeded);
        Assert.Contains("model inventory response exceeded", health.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 MiB", health.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenAiModelDiscovery_RejectsOversizedInventoryBeforeJsonParsing(bool includeContentLength)
    {
        var bytes = Encoding.UTF8.GetBytes(
            new string('x', MainWindowViewModel.MaximumRuntimeModelInventoryResponseBytes + 1));
        HttpContent content = includeContentLength
            ? new ByteArrayContent(bytes)
            : new UnknownLengthContent(bytes);
        var handler = new RuntimeModelInventoryHandler(content);
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            MainWindowViewModel.FetchInstalledRuntimeModelChoicesAsync(
                client,
                new Uri("http://127.0.0.1:1234/v1/"),
                TestContext.Current.CancellationToken));

        Assert.Contains("model inventory response exceeded", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 MiB", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAiModelDiscovery_AcceptsAnExactlyOneMiBUnknownLengthInventory()
    {
        const string prefix = "{\"data\":[],\"padding\":\"";
        const string suffix = "\"}";
        var body = prefix
            + new string(
                'x',
                MainWindowViewModel.MaximumRuntimeModelInventoryResponseBytes - prefix.Length - suffix.Length)
            + suffix;
        var bytes = Encoding.UTF8.GetBytes(body);
        Assert.Equal(MainWindowViewModel.MaximumRuntimeModelInventoryResponseBytes, bytes.Length);
        var handler = new RuntimeModelInventoryHandler(new UnknownLengthContent(bytes));
        using var client = new HttpClient(handler);

        var choices = await MainWindowViewModel.FetchInstalledRuntimeModelChoicesAsync(
            client,
            new Uri("http://127.0.0.1:1234/v1/"),
            TestContext.Current.CancellationToken);

        Assert.Empty(choices);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SavedModelChoice_RetainsExplicitToolCallSupport(bool supportsToolCalls)
    {
        var options = RuntimeSettingsStore.GetDefaultOptions() with
        {
            Model = "openai/gpt-oss-20b",
            SupportsToolCalls = supportsToolCalls
        };

        var choice = RuntimeModelChoice.FromOptions(options);

        Assert.Equal(supportsToolCalls, choice.SupportsToolCalls);
    }

    [Theory]
    [InlineData(LocalRuntimeEngines.LmStudio)]
    [InlineData(LocalRuntimeEngines.Ollama)]
    [InlineData(LocalRuntimeEngines.LlamaCpp)]
    [InlineData(LocalRuntimeEngines.GenericOpenAi)]
    public async Task Shutdown_LeavesExternallyOwnedProviderModelLoaded(string engine)
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var runtime = CreateChatRuntime(client, engine);

        await runtime.ShutdownAsync(TestContext.Current.CancellationToken);
        await runtime.UnloadForModelSwitchAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Shutdown_KeepsLemonadeSpecificUnloadAndVerificationLifecycle()
    {
        var handler = new LemonadeReleaseHandler();
        using var client = new HttpClient(handler);
        var runtime = CreateChatRuntime(client, LocalRuntimeEngines.Lemonade);

        await runtime.ShutdownAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["POST /api/v1/unload", "GET /api/v1/health"],
            handler.Requests.Select(request => $"{request.Method} {request.Path}"));
        Assert.Contains("\"model_name\":\"openai/gpt-oss-20b\"", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsUi_ExplainsLmStudioServerAndModelRefresh()
    {
        var xaml = File.ReadAllText(
            Path.Combine(TestRepository.Root, "src", "UI", "SettingsWindow.xaml"));

        Assert.Contains("RuntimeEngineGuidanceText", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Refresh models\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Choose LM Studio, Ollama, llama.cpp, Lemonade, or a custom OpenAI-compatible local or explicitly approved remote HTTPS runtime.", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamedRequiredToolChoice_UsesLmStudioStringDialectAndPublishesOnlyThatTool()
    {
        var handler = new LmStudioChatHandler();
        using var client = new HttpClient(handler);
        var runtime = CreateChatRuntime(client, LocalRuntimeEngines.LmStudio);
        var selected = AIFunctionFactory.Create(
            (bool value) => value,
            "classify_current_turn",
            "Classify the current turn.");
        var unrelated = AIFunctionFactory.Create(
            (string value) => value,
            "unrelated_tool",
            "An unrelated tool.");

        await runtime.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
            new ChatOptions
            {
                Tools = [selected, unrelated],
                ToolMode = new RequiredChatToolMode(selected.Name)
            },
            TestContext.Current.CancellationToken);

        using var payload = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal("required", payload.RootElement.GetProperty("tool_choice").GetString());
        var tool = Assert.Single(payload.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal(
            selected.Name,
            tool.GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task PerCallOutputBudget_IsSentWithoutExpansion()
    {
        var handler = new LmStudioChatHandler();
        using var client = new HttpClient(handler);
        var runtime = CreateChatRuntime(client, LocalRuntimeEngines.LmStudio);

        await runtime.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
            new ChatOptions { MaxOutputTokens = 2_048 },
            TestContext.Current.CancellationToken);

        using var payload = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(2_048, payload.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task MultipleSystemInstructions_AreMergedIntoOneOrderedMessage()
    {
        var handler = new LmStudioChatHandler();
        using var client = new HttpClient(handler);
        var runtime = CreateChatRuntime(client, LocalRuntimeEngines.LmStudio);

        await runtime.GetResponseAsync(
            [
                new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.System,
                    "conversation-specific instruction"),
                new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.System,
                    string.Empty),
                new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.User,
                    "hello")
            ],
            new ChatOptions
            {
                Instructions = "request-specific instruction",
                MaxOutputTokens = 2_048
            },
            TestContext.Current.CancellationToken);

        using var payload = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var messages = payload.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var systemMessage = Assert.Single(
            messages,
            message =>
                string.Equals(
                    message.GetProperty("role").GetString(),
                    "system",
                    StringComparison.Ordinal));
        var content = systemMessage.GetProperty("content").GetString();
        Assert.Contains("request-specific instruction", content, StringComparison.Ordinal);
        Assert.Contains("conversation-specific instruction", content, StringComparison.Ordinal);
        Assert.Equal("user", messages[^1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task CompactedContinuation_IsNormalizedToStrictAlternatingRoles()
    {
        var handler = new LmStudioChatHandler();
        using var client = new HttpClient(handler);
        var runtime = CreateChatRuntime(client, LocalRuntimeEngines.LmStudio);
        var call = new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant,
            [new FunctionCallContent("call-1", "inspect")]);
        var result = new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Tool,
            [new FunctionResultContent("call-1", new { success = true })]);

        await runtime.GetResponseAsync(
            [
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "original request"),
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "compacted evidence"),
                call,
                result,
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "continue the work")
            ],
            new ChatOptions { MaxOutputTokens = 2_048 },
            TestContext.Current.CancellationToken);

        using var payload = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var messages = payload.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(
            ["system", "user", "assistant", "tool", "assistant", "user"],
            messages.Select(message => message.GetProperty("role").GetString() ?? string.Empty).ToArray());
        Assert.Contains(
            "original request",
            messages[1].GetProperty("content").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "compacted evidence",
            messages[1].GetProperty("content").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("call-1", messages[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("call-1", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("Tool result received.", messages[4].GetProperty("content").GetString());
    }

    [Fact]
    public async Task AssistantLedCompactedHistory_GainsNeutralUserBoundaryAndKeepsToolCall()
    {
        var handler = new LmStudioChatHandler();
        using var client = new HttpClient(handler);
        var runtime = CreateChatRuntime(client, LocalRuntimeEngines.LmStudio);
        var call = new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant,
            [new FunctionCallContent("call-1", "inspect")]);
        var result = new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Tool,
            [new FunctionResultContent("call-1", new { success = true })]);

        await runtime.GetResponseAsync(
            [
                new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.Assistant,
                    "Preserved progress summary."),
                call,
                result,
                new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.Assistant,
                    "Ready for the next instruction."),
                new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.User,
                    "continue the work")
            ],
            new ChatOptions { MaxOutputTokens = 2_048 },
            TestContext.Current.CancellationToken);

        using var payload = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var messages = payload.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(
            ["system", "user", "assistant", "tool", "assistant", "user"],
            messages.Select(message => message.GetProperty("role").GetString() ?? string.Empty).ToArray());
        Assert.Equal("Continue the preserved request.", messages[1].GetProperty("content").GetString());
        Assert.Contains(
            "Preserved progress summary.",
            messages[2].GetProperty("content").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("call-1", messages[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GemmaThinking_UsesLmStudioCanonicalTemplateFlagOnce()
    {
        var handler = new LmStudioChatHandler();
        using var client = new HttpClient(handler);
        var runtime = CreateGemmaChatRuntime(client);

        await runtime.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
            new ChatOptions { MaxOutputTokens = 2_048 },
            TestContext.Current.CancellationToken);

        using var payload = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.True(payload.RootElement
            .GetProperty("chat_template_kwargs")
            .GetProperty("enable_thinking")
            .GetBoolean());
        var firstSystemMessage = payload.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetString();
        Assert.False(
            firstSystemMessage?.StartsWith("<|think|>\n", StringComparison.Ordinal) ?? false);
    }

    [Fact]
    public async Task NamedRequiredToolChoice_PreservesOpenAiObjectDialectForGenericProvider()
    {
        var handler = new LmStudioChatHandler();
        using var client = new HttpClient(handler);
        var runtime = CreateChatRuntime(client, LocalRuntimeEngines.GenericOpenAi);
        var selected = AIFunctionFactory.Create(
            (bool value) => value,
            "classify_current_turn",
            "Classify the current turn.");

        await runtime.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
            new ChatOptions
            {
                Tools = [selected],
                ToolMode = new RequiredChatToolMode(selected.Name)
            },
            TestContext.Current.CancellationToken);

        using var payload = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(
            selected.Name,
            payload.RootElement
                .GetProperty("tool_choice")
                .GetProperty("function")
                .GetProperty("name")
                .GetString());
    }

    [Fact]
    public async Task LmStudioRawStringError_IsReportedReadably()
    {
        var handler = new LmStudioChatHandler(
            HttpStatusCode.BadRequest,
            "{\"error\":\"Invalid tool_choice type: object.\"}");
        using var client = new HttpClient(handler);
        var runtime = CreateChatRuntime(client, LocalRuntimeEngines.LmStudio);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            runtime.GetResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Invalid tool_choice type: object.", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("without a readable error", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static OpenAiCompatibleLocalModelRuntime CreateChatRuntime(
        HttpClient client,
        string engine) =>
        new(
            client,
            new OpenAiCompatibleRuntimeOptions(
                Enabled: true,
                Endpoint: LocalRuntimeEngines.DefaultEndpoint(LocalRuntimeEngines.LmStudio),
                Model: "openai/gpt-oss-20b",
                DisplayName: "GPT-OSS 20B",
                Family: "GPT-OSS",
                Size: "20B",
                Quantization: "Q4_K_M",
                ContextTokens: 131072,
                OutputTokenLimit: 8192,
                Temperature: 0.2,
                TopP: 0.95,
                StreamingEnabled: true,
                SupportsVision: false,
                SupportsToolCalls: true,
                AllowPrivateLanEndpoint: false)
            {
                Engine = engine,
                ReasoningEffort = "low",
                ThinkingControl = ModelThinkingControl.GptOssReasoningEffort
            });

    private static OpenAiCompatibleLocalModelRuntime CreateGemmaChatRuntime(HttpClient client) =>
        new(
            client,
            new OpenAiCompatibleRuntimeOptions(
                Enabled: true,
                Endpoint: LocalRuntimeEngines.DefaultEndpoint(LocalRuntimeEngines.LmStudio),
                Model: "google/gemma-4-26b-a4b-qat",
                DisplayName: "Gemma 4 26B A4B QAT",
                Family: "Gemma",
                Size: "26B-A4B",
                Quantization: "Q4_0",
                ContextTokens: 16_384,
                OutputTokenLimit: 8_192,
                Temperature: 0.2,
                TopP: 0.95,
                StreamingEnabled: true,
                SupportsVision: true,
                SupportsToolCalls: true,
                AllowPrivateLanEndpoint: false)
            {
                Engine = LocalRuntimeEngines.LmStudio,
                ThinkingEnabled = true,
                ThinkingControl = ModelThinkingControl.GemmaSystemPromptToken
            });

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class LemonadeReleaseHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                body));

            if (request.Method == HttpMethod.Post
                && request.RequestUri.AbsolutePath == "/api/v1/unload")
            {
                return Json("{}");
            }

            if (request.Method == HttpMethod.Get
                && request.RequestUri.AbsolutePath == "/api/v1/health")
            {
                return Json("{\"all_models_loaded\":[]}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class LmStudioChatHandler(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? responseBody = null) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    responseBody
                    ?? "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class RuntimeModelInventoryHandler(HttpContent content) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private sealed record RecordedRequest(string Method, string Path, string Body);
}
