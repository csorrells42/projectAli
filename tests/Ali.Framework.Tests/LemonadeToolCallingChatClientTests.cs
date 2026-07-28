using System.Runtime.CompilerServices;
using Ali.Modules.Coordinator;
using Ali.Modules.Runtime;
using Ali.UI.ViewModels;
using Microsoft.Extensions.AI;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests;

public sealed class LemonadeToolCallingChatClientTests
{
    [Fact]
    public void GptOssRuntimeOffersLargeJobOutputLimits()
    {
        var choice = Assert.Single(RuntimeModelChoiceCatalog.KnownChoices());

        Assert.Contains(4096, choice.OutputTokenLimits);
        Assert.Contains(8192, choice.OutputTokenLimits);
    }

    [Fact]
    public async Task PlainFinalAnswer_IsReturnedWithoutASecondModelPassOrRewrite()
    {
        const string answer = "I am doing well today, thank you.";
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                $$"""{"action":"final","answer":"{{answer}}"}""")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        var tool = AIFunctionFactory.Create(
            () => "ok",
            "read_current_state",
            "Read authoritative current state when it is needed.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "How are you doing today?")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        Assert.Equal(answer, response.Text);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task ActivityMessages_UseConfiguredAssistantName()
    {
        var activity = new List<AssistantStreamChunk>();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "Hello",
            activity.Add);
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                """{"action":"final","answer":"Hello there."}""")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Bob",
            () => turn);
        var tool = AIFunctionFactory.Create(
            () => "ok",
            "read_current_state",
            "Read authoritative current state when it is needed.");

        await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Hello")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        Assert.Contains(activity, item => item.Text.Contains("Bob", StringComparison.Ordinal));
        Assert.DoesNotContain(activity, item =>
            item.Text.Contains("Ali", StringComparison.Ordinal)
            || (item.ActivityDetail?.Contains("Ali", StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task TruncatedFinalAnswer_ContinuesWithoutResendingTheToolCatalog()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                """{"action":"final","answer":"public class Game {\n"""))
            {
                FinishReason = ChatFinishReason.Length
            },
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                """{"action":"final","answer":"    static void Main() {}\n}"}"""))
            {
                FinishReason = ChatFinishReason.Stop
            });
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Charlie",
            () => null);
        var tool = AIFunctionFactory.Create(
            () => "ok",
            "file_access_write",
            "Create a requested file.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Write a C# game.")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        Assert.Contains("public class Game", response.Text, StringComparison.Ordinal);
        Assert.Contains("static void Main", response.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("output limit", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, inner.CallCount);
        Assert.DoesNotContain(
            "AVAILABLE TOOLS",
            string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosedFinalEnvelopeWithLengthFinishReason_StillContinues()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                """{"action":"final","answer":"Desktop tree through Desktops.exe"}"""))
            {
                FinishReason = ChatFinishReason.Length
            },
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                """{"action":"final","answer":"and the remaining files through the end of the tree."}"""))
            {
                FinishReason = ChatFinishReason.Stop
            });
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Charlie",
            () => null);
        var tool = AIFunctionFactory.Create(() => "ok", "file_access_ls", "List files.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Fully expand the tree.")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        Assert.Contains("Desktops.exe", response.Text, StringComparison.Ordinal);
        Assert.Contains("end of the tree", response.Text, StringComparison.Ordinal);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task TruncatedToolCall_ContinuesAndRunsAsOneCompleteToolRequest()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                """{"action":"call","tool":"file_access_write","arguments":{"fileName":"Desktop/Game.cs","content":"public class Game {\n"""))
            {
                FinishReason = ChatFinishReason.Length
            },
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                """    static void Main() {}\n}","overwrite":false},"summary":"Create the game"}"""))
            {
                FinishReason = ChatFinishReason.Stop
            });
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Charlie",
            () => null);
        var tool = AIFunctionFactory.Create(
            () => "ok",
            "file_access_write",
            "Create a requested file.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Create Desktop/Game.cs.")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("file_access_write", call.Name);
        var content = Assert.IsType<System.Text.Json.JsonElement>(call.Arguments!["content"]);
        Assert.Contains("static void Main", content.GetString(), StringComparison.Ordinal);
        Assert.Equal(2, inner.CallCount);
    }

    private sealed class RecordingChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public int CallCount { get; private set; }

        public List<IReadOnlyList<AIChatMessage>> ObservedMessages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ObservedMessages.Add(messages.ToList());
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new ChatResponse(new AIChatMessage(AIChatRole.Assistant, "script exhausted")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var result = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in result.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
