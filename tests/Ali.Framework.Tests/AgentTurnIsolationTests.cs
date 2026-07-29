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
        Assert.Contains("memory-1", input[0].Text, StringComparison.Ordinal);
        Assert.Contains("Stuart, Florida", input[0].Text, StringComparison.Ordinal);
        Assert.Equal(ChatRole.User, input[1].Role);
        Assert.Equal("Where does the user work?", input[1].Text);
    }

    [Fact]
    public void CompatibilityToolResults_AreBoundedBeforeTheNextModelDecision()
    {
        var hugeBuildTranscript = new string('x', 50_000) + "\nerror CS1002: ; expected";

        var compacted = LemonadeToolCallingChatClient.SerializeToolResultForModel(new
        {
            success = false,
            output = hugeBuildTranscript
        });

        Assert.True(compacted.Length <= 6_000);
        Assert.Contains("compacted for the model", compacted, StringComparison.Ordinal);
        Assert.Contains("error CS1002", compacted, StringComparison.Ordinal);
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

    [Fact]
    public async Task CurrentWebSearch_AllowsOneRefinementWhenFirstResultsAreOffTarget()
    {
        var retriever = new NonEmptySourceRetriever();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user",
            "assistant",
            "verify a current fact",
            _ => { });
        var sourceTools = new AliSourceTools(
            retriever,
            retriever,
            null!,
            () => turn);

        var first = await sourceTools.SearchCurrentWebAsync(
            "broad current query",
            "general",
            TestContext.Current.CancellationToken);
        var second = await sourceTools.SearchCurrentWebAsync(
            "refined authoritative query",
            "general",
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(first.Sources);
        Assert.True(first.CanRetry);
        Assert.True(turn.UsedCurrentWebSearch);
        Assert.NotEmpty(second.Sources);
        Assert.False(second.CanRetry);
        Assert.Equal(2, retriever.CallCount);
        Assert.All(retriever.Plans, plan =>
            Assert.Contains(DateTimeOffset.Now.ToString("yyyy-MM-dd"), plan.SearchText, StringComparison.Ordinal));
        Assert.Contains("RetrievedAt records when Ali fetched", first.Status, StringComparison.Ordinal);
        Assert.Contains("current status could not be verified", first.Status, StringComparison.Ordinal);
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

    private sealed class NonEmptySourceRetriever : ISourceRetriever
    {
        public int CallCount { get; private set; }

        public List<SourceQueryPlan> Plans { get; } = [];

        public Task<SourceRetrievalResult> RetrieveAsync(string userText, CancellationToken cancellationToken) =>
            RetrieveAsync(
                new SourceQueryPlan(true, true, "current_web", userText, [userText], ["general"]),
                cancellationToken);

        public Task<SourceRetrievalResult> RetrieveAsync(SourceQueryPlan plan, CancellationToken cancellationToken)
        {
            CallCount++;
            Plans.Add(plan);
            return Task.FromResult(new SourceRetrievalResult(
                [new SourceExcerpt(
                    1,
                    "general",
                    "Example source",
                    "https://example.com/current",
                    DateTimeOffset.UtcNow,
                    "A result that may or may not directly answer the model's current question.")],
                [],
                true));
        }
    }
}
