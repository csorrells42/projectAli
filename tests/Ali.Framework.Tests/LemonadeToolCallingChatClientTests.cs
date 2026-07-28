using System.Runtime.CompilerServices;
using Ali.Modules.Coordinator;
using Ali.Modules.Runtime;
using Microsoft.Extensions.AI;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests;

public sealed class LemonadeToolCallingChatClientTests
{
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

    private sealed class RecordingChatClient(ChatResponse response) : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(response);
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
