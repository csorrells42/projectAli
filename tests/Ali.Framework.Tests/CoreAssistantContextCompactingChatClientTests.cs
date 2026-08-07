using System.Runtime.CompilerServices;
using Ali.Modules.Coordinator;
using Microsoft.Extensions.AI;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests;

/// <summary>
/// Regression coverage for the textual "[TOOL_CALLS]tool[ARGS]{...}" promotion
/// added directly to CoreAssistantContextCompactingChatClient, the class that
/// is actually live in the fast-path chain (unlike AliToolCallingChatClient's
/// own promotion logic, which is never constructed anywhere in the app).
/// Streams text one character at a time to exercise the real incremental
/// buffering decision, not just a single-shot buffered response.
/// </summary>
public sealed class CoreAssistantContextCompactingChatClientTests
{
    [Fact]
    public async Task TextualToolCallMarker_StreamedCharacterByCharacter_IsPromotedNotLeaked()
    {
        const string raw = "[TOOL_CALLS]read_file[ARGS]{\"relative_path\": \"foo2/App.xaml\", \"start_line\": 0, \"end_line\": -1}";
        var inner = new CharByCharStreamingClient(raw);
        var client = new CoreAssistantContextCompactingChatClient(inner);
        var readFile = AIFunctionFactory.Create(
            (string relative_path, int start_line, int end_line) => "ok",
            "read_file",
            "Read a bounded section of a project file.");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Inspect App.xaml.")],
            new ChatOptions { Tools = [readFile] },
            TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        var calls = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().ToList();
        var leakedText = updates.SelectMany(u => u.Contents).OfType<TextContent>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        var call = Assert.Single(calls);
        Assert.Equal("read_file", call.Name);
        Assert.Empty(leakedText);
    }

    [Fact]
    public void LongConversationCompaction_KeepsCurrentUserRequestLast()
    {
        var client = new CoreAssistantContextCompactingChatClient(
            new CharByCharStreamingClient("unused"));
        var messages = Enumerable.Range(1, 16)
            .Select(index => new AIChatMessage(
                index % 2 == 0 ? AIChatRole.Assistant : AIChatRole.User,
                $"message-{index}"))
            .Append(new AIChatMessage(AIChatRole.User, "current-request"))
            .ToArray();

        var compacted = client.CompactForTurn(messages);
        var nonSystem = compacted
            .Where(message => message.Role != AIChatRole.System)
            .ToArray();

        Assert.Equal(13, nonSystem.Length);
        Assert.Equal(
            Enumerable.Range(5, 12)
                .Select(index => $"message-{index}")
                .Append("current-request"),
            nonSystem.Select(message => message.Text));
        Assert.Equal("current-request", nonSystem[^1].Text);
    }

    [Fact]
    public async Task QwenToolEnvelope_StreamedCharacterByCharacter_IsPromotedNotLeaked()
    {
        const string raw = "<tools>{\"name\":\"read_file\",\"arguments\":{\"relative_path\":\"foo2/App.xaml\",\"start_line\":0,\"end_line\":-1}}</tools>";
        var inner = new CharByCharStreamingClient(raw);
        var client = new CoreAssistantContextCompactingChatClient(inner);
        var readFile = AIFunctionFactory.Create(
            (string relative_path, int start_line, int end_line) => "ok",
            "read_file",
            "Read a bounded section of a project file.");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Inspect App.xaml.")],
            new ChatOptions { Tools = [readFile] },
            TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        var call = Assert.Single(updates
            .SelectMany(update => update.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("read_file", call.Name);
        Assert.Equal(
            "foo2/App.xaml",
            Assert.IsType<System.Text.Json.JsonElement>(call.Arguments!["relative_path"]).GetString());
        Assert.DoesNotContain(
            updates.SelectMany(update => update.Contents).OfType<TextContent>(),
            content => !string.IsNullOrEmpty(content.Text));
    }

    [Fact]
    public async Task QwenToolEnvelopeWithTrailingProse_IsNotExecuted()
    {
        const string raw = "<tools>{\"name\":\"read_file\",\"arguments\":{\"relative_path\":\"foo2/App.xaml\"}}</tools> extra text";
        var inner = new CharByCharStreamingClient(raw);
        var client = new CoreAssistantContextCompactingChatClient(inner);
        var readFile = AIFunctionFactory.Create(
            (string relative_path) => "ok",
            "read_file",
            "Read a project file.");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Inspect App.xaml.")],
            new ChatOptions { Tools = [readFile] },
            TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        Assert.Empty(updates.SelectMany(update => update.Contents).OfType<FunctionCallContent>());
        Assert.Equal(
            raw,
            string.Concat(updates
                .SelectMany(update => update.Contents)
                .OfType<TextContent>()
                .Select(content => content.Text)));
    }

    [Fact]
    public async Task FencedJsonToolCallAfterProse_PromotesOnlyFirstCallAndDiscardsLaterClaims()
    {
        const string explanation = "I'll create the project folder first.\n";
        const string raw = explanation
            + "```json\n{\"name\":\"list_dir\",\"arguments\":{\"relative_path\":\".\",\"recursive\":false}}\n```"
            + "\nNext I will run another tool.\n"
            + "```json\n{\"name\":\"list_dir\",\"arguments\":{\"relative_path\":\"src\",\"recursive\":false}}\n```"
            + "\nTool Call Succeeded";
        var inner = new CharByCharStreamingClient(raw);
        var client = new CoreAssistantContextCompactingChatClient(inner);
        var listDirectory = AIFunctionFactory.Create(
            (string relative_path, bool recursive) => "src",
            "list_dir",
            "List a project directory.");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new AIChatMessage(AIChatRole.User, "List the workspace.")],
            new ChatOptions { Tools = [listDirectory] },
            TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        var call = Assert.Single(updates
            .SelectMany(update => update.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("list_dir", call.Name);
        Assert.Equal(
            explanation,
            string.Concat(updates
                .SelectMany(update => update.Contents)
                .OfType<TextContent>()
                .Select(content => content.Text)));
    }

    [Fact]
    public async Task BareJsonSerenaToolCallAfterProse_StreamedCharacterByCharacter_IsPromoted()
    {
        const string explanation = "I'll create the file now.\n\n";
        const string raw = explanation
            + "{\"name\": \"create_text_file\", \"arguments\": {\"relative_path\": \"MainWindow.xaml.cs\", \"content\": \"class MainWindow {}\"}}";
        var inner = new CharByCharStreamingClient(raw);
        var client = new CoreAssistantContextCompactingChatClient(inner);
        var createTextFile = AIFunctionFactory.Create(
            (string relative_path, string content) => "ok",
            "create_text_file",
            "Create a project text file.");

        using var bareJsonScope = CoreAssistantContextCompactingChatClient.AllowBareJsonToolCalls(
            new HashSet<string>(StringComparer.Ordinal) { "create_text_file" });
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Create the file.")],
            new ChatOptions { Tools = [createTextFile] },
            TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        var call = Assert.Single(updates
            .SelectMany(update => update.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("create_text_file", call.Name);
        Assert.Equal(
            "MainWindow.xaml.cs",
            Assert.IsType<System.Text.Json.JsonElement>(call.Arguments!["relative_path"]).GetString());
        Assert.Equal(
            explanation,
            string.Concat(updates
                .SelectMany(update => update.Contents)
                .OfType<TextContent>()
                .Select(content => content.Text)));
    }

    [Fact]
    public async Task BareJsonToolCallOutsideSerenaAllowList_RemainsVisibleText()
    {
        const string raw = "{\"name\":\"delete_everything\",\"arguments\":{\"confirm\":true}}";
        var inner = new CharByCharStreamingClient(raw);
        var client = new CoreAssistantContextCompactingChatClient(inner);
        var unregisteredForSerena = AIFunctionFactory.Create(
            (bool confirm) => "not called",
            "delete_everything",
            "A non-Serena test tool.");

        using var bareJsonScope = CoreAssistantContextCompactingChatClient.AllowBareJsonToolCalls(
            new HashSet<string>(StringComparer.Ordinal) { "create_text_file" });
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Show the example.")],
            new ChatOptions { Tools = [unregisteredForSerena] },
            TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        Assert.Empty(updates.SelectMany(update => update.Contents).OfType<FunctionCallContent>());
        Assert.Equal(
            raw,
            string.Concat(updates
                .SelectMany(update => update.Contents)
                .OfType<TextContent>()
                .Select(content => content.Text)));
    }

    [Fact]
    public async Task OrdinaryChatText_StreamedCharacterByCharacter_PassesThroughUnmodified()
    {
        const string answer = "The sky is blue because of Rayleigh scattering.";
        var inner = new CharByCharStreamingClient(answer);
        var client = new CoreAssistantContextCompactingChatClient(inner);
        var someTool = AIFunctionFactory.Create(
            (string query) => "ok",
            "search_current_web",
            "Search the web.");

        var streamedText = new System.Text.StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Why is the sky blue?")],
            new ChatOptions { Tools = [someTool] },
            TestContext.Current.CancellationToken))
        {
            foreach (var content in update.Contents.OfType<TextContent>())
            {
                streamedText.Append(content.Text);
            }
        }

        Assert.Equal(answer, streamedText.ToString());
    }

    [Fact]
    public async Task OpenRouterContentThenEmptyTerminalChunk_PreservesTextAndFinishReason()
    {
        var inner = new OpenRouterStyleStreamingClient("hello");
        var client = new CoreAssistantContextCompactingChatClient(inner);
        var someTool = AIFunctionFactory.Create(
            (string query) => "ok",
            "search_current_web",
            "Search the web.");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new AIChatMessage(AIChatRole.User, "hello")],
            new ChatOptions { Tools = [someTool] },
            TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        Assert.Equal(
            "hello",
            string.Concat(updates
                .SelectMany(update => update.Contents)
                .OfType<TextContent>()
                .Select(content => content.Text)));
        Assert.Equal("stop", updates.Last().FinishReason?.Value);
    }

    [Fact]
    public async Task TextualMarkerForUnregisteredTool_RemainsVisibleText()
    {
        const string raw = "[TOOL_CALLS]delete_everything[ARGS]{\"confirm\": true}";
        var inner = new CharByCharStreamingClient(raw);
        var client = new CoreAssistantContextCompactingChatClient(inner);
        var readFile = AIFunctionFactory.Create(
            (string relative_path) => "ok",
            "read_file",
            "Read a project file.");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Do something.")],
            new ChatOptions { Tools = [readFile] },
            TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        Assert.Empty(updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>());
        var text = string.Concat(updates.SelectMany(u => u.Contents).OfType<TextContent>().Select(t => t.Text));
        Assert.Equal(raw, text);
    }

    private sealed class CharByCharStreamingClient(string fullText) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new AIChatMessage(AIChatRole.Assistant, fullText)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var character in fullText)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(AIChatRole.Assistant, character.ToString());
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class OpenRouterStyleStreamingClient(string fullText) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new AIChatMessage(AIChatRole.Assistant, fullText)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(AIChatRole.Assistant, fullText);
            yield return new ChatResponseUpdate
            {
                Role = AIChatRole.Assistant,
                FinishReason = new ChatFinishReason("stop")
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
