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
}
