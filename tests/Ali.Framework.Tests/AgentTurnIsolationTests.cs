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
        var input = AliAgentHarnessRunner.BuildInitialInput(history, "hello Ali", []);

        Assert.Equal(3, input.Count);
        Assert.Equal("what can you do", input[0].Text);
        Assert.Equal("Previous turn did not finish.", input[1].Text);
        Assert.Equal("hello Ali", input[2].Text);
        Assert.Equal(ChatRole.User, input[2].Role);
    }

    [Fact]
    public void InitialInput_DoesNotInjectMemoryOrIdentityBeforeTheModelChoosesATool()
    {
        var input = AliAgentHarnessRunner.BuildInitialInput([], "Where does the user work?", []);

        Assert.Single(input);
        Assert.Equal(ChatRole.User, input[0].Role);
        Assert.Equal("Where does the user work?", input[0].Text);
    }

    [Fact]
    public void CompatibilityToolResults_AreBoundedBeforeTheNextModelDecision()
    {
        var hugeBuildTranscript = new string('x', 50_000) + "\nerror CS1002: ; expected";

        var compacted = AliToolCallingChatClient.SerializeToolResultForModel(new
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
        Assert.Equal("broad current query", retriever.Plans[0].Topic);
        Assert.Equal("refined authoritative query", retriever.Plans[1].Topic);
        Assert.All(retriever.Plans, plan => Assert.Equal("model-selected-query", plan.TemporalSelection));
        Assert.Contains("internal fetch time is deliberately not exposed", first.Status, StringComparison.Ordinal);
        Assert.Contains("current status could not be verified", first.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentWebSearch_PreservesTheModelSelectedQueryWithoutRewritingItsYear()
    {
        var retriever = new NonEmptySourceRetriever();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user",
            "assistant",
            "What are the most important software-engineering developments today?",
            _ => { });
        var sourceTools = new AliSourceTools(
            retriever,
            retriever,
            null!,
            () => turn);

        await sourceTools.SearchCurrentWebAsync(
            "current software engineering developments 2024",
            "news",
            TestContext.Current.CancellationToken);

        var plan = Assert.Single(retriever.Plans);
        Assert.Contains("2024", plan.SearchText, StringComparison.Ordinal);
        Assert.Equal("current software engineering developments 2024", plan.Topic);
        Assert.Equal("model-selected-query", plan.TemporalSelection);
    }

    [Fact]
    public async Task CurrentWebSearch_PreservesHistoricalYearExplicitlyRequestedByUser()
    {
        var retriever = new NonEmptySourceRetriever();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user",
            "assistant",
            "Compare software engineering in 2024 with today.",
            _ => { });
        var sourceTools = new AliSourceTools(
            retriever,
            retriever,
            null!,
            () => turn);

        await sourceTools.SearchCurrentWebAsync(
            "software engineering 2024 compared with current developments",
            "news",
            TestContext.Current.CancellationToken);

        Assert.Contains("2024", Assert.Single(retriever.Plans).SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentWebSourceSerialization_OmitsInternalFetchTimestamp()
    {
        var source = new CoordinatorSourceItem(
            "Official release notes",
            "news",
            "https://example.com/releases",
            DateTimeOffset.UtcNow,
            "Published: 2026-07-29");

        var json = System.Text.Json.JsonSerializer.Serialize(source);

        Assert.DoesNotContain("RetrievedAt", json, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-07-30T", json, StringComparison.Ordinal);
        Assert.Contains("Official release notes", json, StringComparison.Ordinal);
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
