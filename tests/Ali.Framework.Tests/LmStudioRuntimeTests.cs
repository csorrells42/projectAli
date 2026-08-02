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
    public void EngineCatalog_UsesLmStudioAsTheSafeLocalDefaultAndKeepsCustomOpenAi()
    {
        var endpoint = LocalRuntimeEngines.DefaultEndpoint(LocalRuntimeEngines.LmStudio);

        Assert.Equal("http://127.0.0.1:1234/v1/", endpoint.ToString());
        Assert.Equal(LocalRuntimeEngines.LmStudio, LocalRuntimeEngines.Choices[0]);
        Assert.Contains(LocalRuntimeEngines.GenericOpenAi, LocalRuntimeEngines.Choices);
        Assert.Equal(LocalRuntimeEngines.LmStudio, LocalRuntimeEngines.Normalize(string.Empty, endpoint));
        Assert.Equal(
            LocalRuntimeEngines.GenericOpenAi,
            LocalRuntimeEngines.Normalize(LocalRuntimeEngines.GenericOpenAi, endpoint));
    }

    [Fact]
    public void MissingRuntimeSettings_DefaultToUnselectedLmStudioWithoutGuessingAModel()
    {
        var options = RuntimeSettingsStore.GetDefaultOptions();

        Assert.False(options.Enabled);
        Assert.Equal(LocalRuntimeEngines.LmStudio, options.Engine);
        Assert.Equal("http://127.0.0.1:1234/v1/", options.Endpoint.ToString());
        Assert.Empty(options.Model);
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
                  "object": "model"
                }
              ]
            }
            """;

        var choice = Assert.Single(RuntimeModelChoiceCatalog.ParseRuntimeModelChoices(json));

        Assert.Equal("openai/gpt-oss-20b", choice.Model);
        Assert.Equal("Installed local runtime model", choice.Source);
    }

    [Fact]
    public async Task Shutdown_UnloadsExactLmStudioInstanceAndVerifiesRelease()
    {
        var handler = new LmStudioReleaseHandler();
        using var client = new HttpClient(handler);
        var runtime = new OpenAiCompatibleLocalModelRuntime(
            client,
            new OpenAiCompatibleRuntimeOptions(
                Enabled: true,
                Endpoint: LocalRuntimeEngines.DefaultEndpoint(LocalRuntimeEngines.LmStudio),
                Model: "openai/gpt-oss-20b",
                DisplayName: "GPT-OSS 20B",
                Family: "GPT-OSS",
                Size: "20B",
                Quantization: "Q4_K_M",
                ContextTokens: 8192,
                OutputTokenLimit: 2048,
                Temperature: 1,
                TopP: null,
                StreamingEnabled: true,
                SupportsVision: false,
                SupportsToolCalls: true,
                AllowPrivateLanEndpoint: false)
            {
                Engine = LocalRuntimeEngines.LmStudio
            });

        await runtime.ShutdownAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "GET /api/v1/models",
                "POST /api/v1/models/unload",
                "GET /api/v1/models"
            ],
            handler.Requests.Select(request => $"{request.Method} {request.Path}"));
        Assert.Contains("\"instance_id\":\"openai/gpt-oss-20b\"", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void LmStudioReleaseVerification_RejectsAnUnknownInventoryShape()
    {
        Assert.Throws<System.Text.Json.JsonException>(() =>
            OpenAiCompatibleLocalModelRuntime.LmStudioListsModelAsLoaded(
                "{\"data\":[]}",
                "openai/gpt-oss-20b"));
    }

    [Fact]
    public void SettingsUi_ExplainsLmStudioServerAndModelRefresh()
    {
        var xaml = File.ReadAllText(
            Path.Combine(TestRepository.Root, "src", "UI", "SettingsWindow.xaml"));

        Assert.Contains("RuntimeEngineGuidanceText", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Refresh models\"", xaml, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:1234/v1/", xaml, StringComparison.Ordinal);
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
                ReasoningEffort = "low"
            });

    private sealed class LmStudioReleaseHandler : HttpMessageHandler
    {
        private bool _unloaded;

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
                && request.RequestUri!.AbsolutePath == "/api/v1/models/unload")
            {
                _unloaded = true;
                return Json("{\"instance_id\":\"openai/gpt-oss-20b\"}");
            }

            if (request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath == "/api/v1/models")
            {
                return Json(_unloaded
                    ? ModelInventory("[]")
                    : ModelInventory("[{\"id\":\"openai/gpt-oss-20b\",\"config\":{\"context_length\":8192}}]"));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string ModelInventory(string loadedInstances) =>
            $$"""
              {
                "models": [
                  {
                    "type": "llm",
                    "key": "openai/gpt-oss-20b",
                    "selected_variant": "openai/gpt-oss-20b@q4_k_m",
                    "loaded_instances": {{loadedInstances}}
                  }
                ]
              }
              """;

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

    private sealed record RecordedRequest(string Method, string Path, string Body);
}
