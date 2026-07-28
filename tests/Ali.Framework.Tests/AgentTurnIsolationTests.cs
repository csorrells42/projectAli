using Ali.Modules.Coordinator;
using Ali.Modules.Internet;
using Microsoft.Extensions.AI;
using RuntimeChatMessage = Ali.Modules.Runtime.ChatMessage;
using RuntimeChatRole = Ali.Modules.Runtime.ChatRole;

namespace Ali.Framework.Tests;

public sealed class AgentTurnIsolationTests
{
    [Fact]
    public void InitialInput_RebuildsEveryTurnFromCanonicalConversationHistory()
    {
        var history = new RuntimeChatMessage[]
        {
            new("user-1", RuntimeChatRole.User, "what can you do", DateTimeOffset.UtcNow),
            new("assistant-1", RuntimeChatRole.Assistant, "Previous turn did not finish.", DateTimeOffset.UtcNow)
        };
        var memory = new CoordinatorMemoryResult("No memories", [], []);

        var input = AliAgentHarnessRunner.BuildInitialInput(history, "hello Ali", memory, []);

        Assert.Equal(4, input.Count);
        Assert.Equal("what can you do", input[0].Text);
        Assert.Equal("Previous turn did not finish.", input[1].Text);
        Assert.Equal(ChatRole.System, input[2].Role);
        Assert.Equal("hello Ali", input[3].Text);
        Assert.Equal(ChatRole.User, input[3].Role);
    }

    [Fact]
    public void InitialInput_PlacesRetrievedPerUserMemoryImmediatelyBeforeCurrentQuestion()
    {
        var memory = new CoordinatorMemoryResult(
            "Found one memory",
            [new CoordinatorMemoryItem("memory-1", "The current user works in Stuart, Florida.", "dates_places", DateTimeOffset.UtcNow)],
            []);

        var input = AliAgentHarnessRunner.BuildInitialInput([], "Where does the user work?", memory, []);

        Assert.Equal(2, input.Count);
        Assert.Equal(ChatRole.System, input[0].Role);
        Assert.Contains("PER-USER MEM0 MEMORY", input[0].Text, StringComparison.Ordinal);
        Assert.Contains("Stuart, Florida", input[0].Text, StringComparison.Ordinal);
        Assert.Equal(ChatRole.User, input[1].Role);
        Assert.Equal("Where does the user work?", input[1].Text);
    }

    [Fact]
    public async Task CurrentWebSearch_StopsAfterTwoEmptyAttemptsWithinOneTurn()
    {
        var retriever = new EmptySourceRetriever();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user",
            "assistant",
            "current sports prediction",
            _ => { });
        var sourceTools = new AliSourceTools(
            retriever,
            retriever,
            null!,
            () => turn);

        var first = await sourceTools.SearchCurrentWebAsync("first query", "sports", TestContext.Current.CancellationToken);
        var second = await sourceTools.SearchCurrentWebAsync("second query", "sports", TestContext.Current.CancellationToken);
        var third = await sourceTools.SearchCurrentWebAsync("third query", "sports", TestContext.Current.CancellationToken);

        Assert.True(first.CanRetry);
        Assert.False(second.CanRetry);
        Assert.False(third.CanRetry);
        Assert.Equal(2, retriever.CallCount);
        Assert.Contains("limit was reached", third.Status, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class EmptySourceRetriever : ISourceRetriever
    {
        public int CallCount { get; private set; }

        public Task<SourceRetrievalResult> RetrieveAsync(string userText, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(SourceRetrievalResult.Empty);
        }

        public Task<SourceRetrievalResult> RetrieveAsync(SourceQueryPlan plan, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new SourceRetrievalResult([], ["No sources"], true));
        }
    }
}
