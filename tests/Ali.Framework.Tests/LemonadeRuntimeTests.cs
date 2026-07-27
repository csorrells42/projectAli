using System.Net;
using System.Text;
using System.Text.Json;
using Ali.Modules.Runtime;
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
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath, body));

            var json = request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/health" => "{\"status\":\"ok\",\"models\":[]}",
                "/api/v1/load" => "{\"status\":\"ok\"}",
                "/api/v1/chat/completions" => "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":\"stop\"}],\"usage\":{\"completion_tokens\":1}}",
                _ => "{}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Body);
}
