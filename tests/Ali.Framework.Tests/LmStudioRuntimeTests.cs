using System.Net;
using System.Text;
using Ali.Modules.Runtime;
using Ali.UI.ViewModels;

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

    private sealed record RecordedRequest(string Method, string Path, string Body);
}
