using Ali.Modules.Conversation;
using Ali.Modules.Coordinator;
using Ali.Modules.Evidence;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Runtime;
using Ali.Modules.Storage;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class ConversationPublicationReconcilerTests
{
    [Fact]
    public async Task ProbeAndReconciler_DistinguishAbsentPresentAndMismatchedPublications()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileConversationStore(directory.Path);
        const string conversationId = "conversation";
        const string messageId = "assistant-message";
        const string exactAnswer = "The exact persisted answer.";
        var answerDigest = TurnStateIntegrity.Digest(exactAnswer);

        var absent = store.ProbeAssistantPublication(
            conversationId,
            messageId,
            answerDigest);
        Assert.Equal(ConversationPublicationProbeStatus.Absent, absent.Status);

        store.Save(Conversation(conversationId, messageId, exactAnswer));
        var present = store.ProbeAssistantPublication(
            conversationId,
            messageId,
            answerDigest);
        Assert.Equal(ConversationPublicationProbeStatus.Present, present.Status);

        var identity = new TurnIdentity("user", conversationId, "original-durable-message");
        var publication = Publication(messageId, answerDigest);
        var reconciler = new ConversationStoreFinalPublicationReconciler(store);
        var applied = await reconciler.ReconcileAsync(
            identity,
            publication,
            TestContext.Current.CancellationToken);
        Assert.Equal(PublicationReconciliationDisposition.Applied, applied.Disposition);

        store.Save(Conversation(conversationId, messageId, "A different answer."));
        var mismatch = store.ProbeAssistantPublication(
            conversationId,
            messageId,
            answerDigest);
        Assert.Equal(ConversationPublicationProbeStatus.Mismatch, mismatch.Status);
        var unknown = await reconciler.ReconcileAsync(
            identity,
            publication,
            TestContext.Current.CancellationToken);
        Assert.Equal(PublicationReconciliationDisposition.Unknown, unknown.Disposition);
    }

    [Fact]
    public async Task UnreadableConversation_IsUnknownAndNeverReportedAbsent()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileConversationStore(directory.Path);
        const string conversationId = "corrupt-conversation";
        const string messageId = "assistant-message";
        var answerDigest = TurnStateIntegrity.Digest("answer");
        Directory.CreateDirectory(store.ConversationsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(store.ConversationsDirectory, conversationId + ".json"),
            "{not-valid-json",
            TestContext.Current.CancellationToken);

        var observed = store.ProbeAssistantPublication(
            conversationId,
            messageId,
            answerDigest);
        Assert.Equal(ConversationPublicationProbeStatus.Unavailable, observed.Status);

        var reconciled = await new ConversationStoreFinalPublicationReconciler(store)
            .ReconcileAsync(
                new TurnIdentity("user", conversationId, messageId),
                Publication(messageId, answerDigest),
                TestContext.Current.CancellationToken);
        Assert.Equal(PublicationReconciliationDisposition.Unknown, reconciled.Disposition);
    }

    private static StoredConversation Conversation(
        string conversationId,
        string messageId,
        string answer)
    {
        var now = DateTimeOffset.UtcNow;
        return new StoredConversation(
            conversationId,
            "Conversation",
            now,
            now,
            [
                new StoredChatMessage(
                    messageId,
                    conversationId,
                    ChatRole.Assistant,
                    answer,
                    now,
                    ChatMessageOrigin.System,
                    EvidenceStatus.Unverified)
            ]);
    }

    private static FinalPublicationState Publication(
        string assistantMessageId,
        string answerDigest)
    {
        const string publicationId = "publication";
        return new FinalPublicationState(
            publicationId,
            assistantMessageId,
            answerDigest,
            new ProtectedTurnInputReference(
                answerDigest,
                TurnStateIntegrity.Digest("payload-reference"),
                TurnInputPurposes.FinalPublicationBinding(
                    publicationId,
                    assistantMessageId,
                    answerDigest),
                Utf8Length: 1),
            FinalPublicationStatus.InDoubt,
            PreparedAtRevision: 1,
            LastTransitionRevision: 2);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Ali-ConversationPublication-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
