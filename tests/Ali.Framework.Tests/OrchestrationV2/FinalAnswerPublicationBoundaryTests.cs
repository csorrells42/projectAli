using Ali.Modules.Coordinator;
using Ali.Modules.Evidence;
using Ali.Modules.Orchestration.State;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class FinalAnswerPublicationBoundaryTests
{
    [Fact]
    public void ExactPreparedAnswer_CanCrossThePublicationBoundary()
    {
        var publication = Publication() with
        {
            AnswerDigest = TurnStateIntegrity.Digest("answer")
        };

        var bound = FinalAnswerPublicationBoundary.BindExactPreparedAnswer(
            publication,
            publication.AssistantMessageId,
            publication.AnswerText,
            publication.AnswerDigest);

        Assert.Same(publication, bound);
    }

    [Fact]
    public void FrameworkTextThatDiffersFromPreparedAnswer_FailsBeforeSink()
    {
        var publication = Publication() with
        {
            AnswerText = "different answer",
            AnswerDigest = TurnStateIntegrity.Digest("prepared answer")
        };

        Assert.Throws<InvalidDataException>(() =>
            FinalAnswerPublicationBoundary.BindExactPreparedAnswer(
                publication,
                publication.AssistantMessageId,
                "prepared answer",
                publication.AnswerDigest));
    }

    [Fact]
    public void ExactConversationStoreAcknowledgment_IsAccepted()
    {
        var publication = Publication();

        FinalAnswerPublicationBoundary.RequireExactAcknowledgment(
            publication,
            FinalAnswerPublicationAcknowledgment.Accepted(publication));
    }

    [Fact]
    public void RejectedConversationStoreAcknowledgment_FailsClosed()
    {
        var publication = Publication();

        Assert.Throws<InvalidOperationException>(() =>
            FinalAnswerPublicationBoundary.RequireExactAcknowledgment(
                publication,
                FinalAnswerPublicationAcknowledgment.Rejected(publication)));
    }

    [Fact]
    public void MissingConversationStoreAcknowledgment_FailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FinalAnswerPublicationBoundary.RequireExactAcknowledgment(
                Publication(),
                acknowledgment: null));
    }

    [Theory]
    [InlineData("different-publication", "assistant-message", "answer-digest")]
    [InlineData("publication", "different-message", "answer-digest")]
    [InlineData("publication", "assistant-message", "different-digest")]
    public void MismatchedConversationStoreAcknowledgment_FailsClosed(
        string publicationId,
        string assistantMessageId,
        string answerDigest)
    {
        var publication = Publication();
        var acknowledgment = new FinalAnswerPublicationAcknowledgment(
            publicationId,
            assistantMessageId,
            answerDigest,
            FinalAnswerPublicationDisposition.PersistedByConversationStore);

        Assert.Throws<InvalidDataException>(() =>
            FinalAnswerPublicationBoundary.RequireExactAcknowledgment(
                publication,
                acknowledgment));
    }

    [Fact]
    public async Task Delivery_WaitsUntilTheExactConversationMessageIsPersisted()
    {
        var publication = Publication();
        var delivery = new FinalAnswerPublicationDelivery(publication);
        var waiting = delivery.WaitAsync(TestContext.Current.CancellationToken).AsTask();

        Assert.False(waiting.IsCompleted);
        delivery.AcknowledgePersisted(
            publication.ConversationId,
            publication.AssistantMessageId,
            publication.AnswerText);

        var acknowledgment = await waiting;
        FinalAnswerPublicationBoundary.RequireExactAcknowledgment(publication, acknowledgment);
    }

    [Fact]
    public async Task Delivery_MismatchedPersistedMessage_FailsClosed()
    {
        var publication = Publication();
        var delivery = new FinalAnswerPublicationDelivery(publication);

        delivery.AcknowledgePersisted(
            publication.ConversationId,
            publication.AssistantMessageId,
            "different answer");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            delivery.WaitAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public void Renderer_ProducesOneBoundedDeduplicatedSourceAppendix()
    {
        var now = DateTimeOffset.UtcNow;
        var sources = Enumerable.Range(0, 7)
            .Select(index => new CoordinatorSourceItem(
                $"Source [{index}]",
                "topic",
                $"https://example.test/{index}",
                now,
                "excerpt"))
            .Append(new CoordinatorSourceItem(
                "Duplicate",
                "topic",
                "https://EXAMPLE.test/0",
                now,
                "excerpt"))
            .Append(new CoordinatorSourceItem(
                "Unsafe",
                "topic",
                "file:///private.txt",
                now,
                "excerpt"))
            .ToArray();

        var rendered = FinalAnswerRenderer.Compose("Answer", sources);

        Assert.StartsWith("Answer", rendered, StringComparison.Ordinal);
        Assert.Equal(1, Count(rendered, "Sources checked:"));
        Assert.Equal(FinalAnswerRenderer.MaximumVisibleSources, Count(rendered, "- ["));
        Assert.Contains("[Source (0)](https://example.test/0)", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("file:///private.txt", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("https://example.test/5", rendered, StringComparison.Ordinal);
    }

    private static FinalAnswerPublication Publication() => new(
        "conversation",
        "user-message",
        "assistant-message",
        "publication",
        "answer",
        TurnStateIntegrity.Digest("answer"),
        EvidenceStatus.Unverified,
        FinishReason: null);

    private static int Count(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;
}
