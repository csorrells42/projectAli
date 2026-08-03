using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.State;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class AnswerDraftStoreTests
{
    [Fact]
    public void CompleteSegments_CommitOneExactSequenceAndHashChain()
    {
        var store = new AliAnswerDraftStore("answer-7", ["claim-a", "claim-b"]);
        var initial = store.Snapshot(0);

        var first = store.Append(new AliAppendAnswerSegmentAction(
            "answer-7",
            0,
            initial.PreviousSegmentHash,
            "first ",
            ["claim-a"]));
        var second = store.Append(new AliAppendAnswerSegmentAction(
            "answer-7",
            1,
            first.SegmentHash,
            "second",
            ["claim-b"]));
        var completed = store.Finish("answer-7");

        Assert.Equal("first second", completed.Text);
        Assert.Equal(TurnStateIntegrity.Digest(completed.Text), completed.AnswerDigest);
        Assert.Equal(["claim-a", "claim-b"], completed.CoveredClaimIds);
        Assert.Equal(2, completed.Segments.Count);
        Assert.Equal(initial.PreviousSegmentHash, first.PreviousSegmentHash);
        Assert.Equal(first.SegmentHash, second.PreviousSegmentHash);
        Assert.Equal(6, first.CommittedOffset);
        Assert.Equal(12, second.CommittedOffset);
        Assert.NotEqual(first.SegmentHash, second.SegmentHash);
    }

    [Theory]
    [InlineData("other-answer", 0, "genesis")]
    [InlineData("answer-7", 1, "genesis")]
    [InlineData("answer-7", 0, "wrong-hash")]
    public void WrongAnswerSequenceOrHash_IsDiscardedWithoutChangingCommittedState(
        string answerId,
        int sequence,
        string hashMode)
    {
        var store = new AliAnswerDraftStore("answer-7", ["claim-a"]);
        var before = store.Snapshot(0);
        var previousHash = hashMode == "genesis"
            ? before.PreviousSegmentHash
            : new string('0', 64);

        Assert.Throws<InvalidDataException>(() => store.Append(
            new AliAppendAnswerSegmentAction(
                answerId,
                sequence,
                previousHash,
                "must not commit",
                ["claim-a"])));

        var after = store.Snapshot(0);
        Assert.Equal(before.AnswerId, after.AnswerId);
        Assert.Equal(before.NextSequence, after.NextSequence);
        Assert.Equal(before.PreviousSegmentHash, after.PreviousSegmentHash);
        Assert.Equal(before.CommittedOffset, after.CommittedOffset);
        Assert.Equal(before.PriorTail, after.PriorTail);
        Assert.Equal(before.RemainingClaimIds, after.RemainingClaimIds);
    }

    [Fact]
    public void FinishBeforeRequiredClaimCoverage_FailsWithoutClosingDraft()
    {
        var store = new AliAnswerDraftStore("answer-7", ["claim-a", "claim-b"]);
        var initial = store.Snapshot(0);
        var first = store.Append(new AliAppendAnswerSegmentAction(
            "answer-7",
            0,
            initial.PreviousSegmentHash,
            "first",
            ["claim-a"]));

        Assert.Throws<InvalidDataException>(() => store.Finish("answer-7"));

        store.Append(new AliAppendAnswerSegmentAction(
            "answer-7",
            1,
            first.SegmentHash,
            " second",
            ["claim-b"]));
        Assert.Equal("first second", store.Finish("answer-7").Text);
    }

    [Fact]
    public void ProjectionPages_AreStableAndReassembleWithoutTruncation()
    {
        var request = Request(string.Concat(Enumerable.Repeat("paged context ", 80)));
        var pager = new AliCompletionProjectionPager(request);
        var text = new System.Text.StringBuilder();
        var pageCount = 0;

        while (!pager.IsExhausted)
        {
            var page = pager.Peek(73);
            Assert.Equal(page, pager.Peek(73));
            Assert.True(page.Text.Length <= 73);
            Assert.Equal(pager.Cursor, page.Cursor);
            text.Append(page.Text);
            pager.Commit(page);
            pageCount++;
        }

        Assert.True(pageCount > 1);
        Assert.Equal(AliCompletionProjectionPager.BuildSource(request), text.ToString());
        var terminal = pager.Peek(0);
        Assert.Empty(terminal.Text);
        Assert.False(terminal.HasMore);
    }

    [Fact]
    public void ExactRenderedSuffix_IsHashChainedAndAuditableBeforePublication()
    {
        var store = new AliAnswerDraftStore("answer-7", ["claim-a"]);
        var initial = store.Snapshot();
        store.Append(new AliAppendAnswerSegmentAction(
            "answer-7",
            0,
            initial.PreviousSegmentHash,
            "Verified answer.",
            ["claim-a"]));
        var composed = store.Finish("answer-7");
        var renderedText = composed.Text
            + "\n\nSources checked:\n- [Example](https://example.com/)";

        var rendered = AliAnswerDraftStore.BindExactRenderedText(composed, renderedText);

        Assert.Equal(renderedText, rendered.Text);
        Assert.Equal(TurnStateIntegrity.Digest(renderedText), rendered.AnswerDigest);
        Assert.Equal(2, rendered.Segments.Count);
        Assert.Equal(
            rendered.Segments[0].SegmentHash,
            rendered.Segments[1].PreviousSegmentHash);
        Assert.Empty(rendered.Segments[1].CoveredClaimIds);
        Assert.Equal(["claim-a"], rendered.CoveredClaimIds);
    }

    private static TemporaryCompletionRequest Request(string immutableRequest)
    {
        var plan = new CompletionPlan(
            "answer-7",
            CompletionKind.Succeeded,
            requiredOutcomeIds: [],
            requiredClaimIds: [],
            bindings: [],
            requestedFormat: "concise");
        return new TemporaryCompletionRequest(
            immutableRequest,
            new AliPlanningTurnInput(11, "{\"status\":\"accepted\"}"),
            new OrchestrationDecision(
                workUpdate: null,
                materialClaims: [],
                nextAction: new BeginCompletionAction(plan)),
            new ChatResponse(new MeaiChatMessage(MeaiChatRole.Assistant, "accepted")),
            requiredOutcomes: [],
            requiredClaims: [],
            citedEvidence: []);
    }
}
