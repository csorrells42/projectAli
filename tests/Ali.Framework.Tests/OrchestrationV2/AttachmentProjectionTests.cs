using System.Runtime.CompilerServices;
using System.Text.Json;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.Work;
using Microsoft.Extensions.AI;
using RuntimeAttachmentKind = Ali.Modules.Runtime.AttachmentKind;
using RuntimeChatAttachment = Ali.Modules.Runtime.ChatAttachment;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class AttachmentProjectionTests
{
    [Fact]
    public async Task CurrentTurnDataContent_IsReplayedExactlyIntoEveryCleanPlanningPass()
    {
        var originalBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x01, 0x02, 0x03 };
        var expectedBytes = originalBytes.ToArray();
        var original = new DataContent(originalBytes, "image/png")
        {
            Name = "diagram.png"
        };
        var projection = AliPlanningAttachmentProjection.Capture([original]);

        originalBytes[0] = 0;
        original.Name = "mutated.png";

        var inner = new RecordingChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "not valid protocol JSON"))
            {
                FinishReason = ChatFinishReason.Stop
            },
            new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                PlanningContractTests.TransportJson(
                    PlanningContractTests.DecisionJson(
                        "{\"kind\":\"answerDirectly\",\"answer\":\"I can inspect it.\"}"))))
            {
                FinishReason = ChatFinishReason.Stop
            });
        using var client = new AliOrchestrationPlanningClient(
            inner,
            () => false,
            PlanningTestModelProfile.GptOss65K);
        using var turnScope = client.BeginTurn(
            CreateTurn(),
            Input(),
            new AcceptingObserver(),
            projection);

        var response = await client.GetResponseAsync(
            [],
            new ChatOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal("I can inspect it.", response.Text);
        Assert.Equal(2, inner.Requests.Count);
        var first = Assert.Single(BinaryContents(inner.Requests[0]));
        var second = Assert.Single(BinaryContents(inner.Requests[1]));
        Assert.Equal(expectedBytes, first.Data.ToArray());
        Assert.Equal(expectedBytes, second.Data.ToArray());
        Assert.Equal("image/png", first.MediaType);
        Assert.Equal("image/png", second.MediaType);
        Assert.Equal("diagram.png", first.Name);
        Assert.Equal("diagram.png", second.Name);
        Assert.NotSame(first, second);

        first.Name = "request-mutated.png";
        Assert.Equal(
            "diagram.png",
            Assert.IsType<DataContent>(projection.Materialize().Single()).Name);

        var base64 = Convert.ToBase64String(expectedBytes);
        Assert.All(
            inner.Requests.SelectMany(request => request.Select(message => message.Text)),
            text => Assert.DoesNotContain(base64, text ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void Projection_IsOutsideTheSerializableAuthoritativeState()
    {
        var canary = new byte[] { 0x41, 0x54, 0x54, 0x41, 0x43, 0x48 };
        var projection = AliPlanningAttachmentProjection.Capture(
            [new DataContent(canary, "application/octet-stream")]);

        var durableStateJson = JsonSerializer.Serialize(Input());

        Assert.Equal(1, projection.Count);
        Assert.DoesNotContain(Convert.ToBase64String(canary), durableStateJson, StringComparison.Ordinal);
        Assert.DoesNotContain("application/octet-stream", durableStateJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Projection_RejectsTooManyAttachmentsWithoutSilentlyDroppingAny()
    {
        var contents = Enumerable.Range(0, AliPlanningAttachmentProjection.MaximumAttachmentCount + 1)
            .Select(_ => new DataContent(new byte[] { 1 }, "image/png"))
            .ToArray();

        var exception = Assert.Throws<InvalidOperationException>(
            () => AliPlanningAttachmentProjection.Capture(contents));

        Assert.Contains("none were silently omitted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DurableAttachmentBinding_AllowsExactReattachmentDespiteVolatileMetadata()
    {
        var payload = Convert.ToBase64String([1, 2, 3, 4]);
        var original = new RuntimeChatAttachment(
            "upload-one",
            RuntimeAttachmentKind.Image,
            "original.png",
            " image/PNG ",
            payload,
            RetainAfterSession: false,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var reattached = original with
        {
            Id = "upload-after-restart",
            FileName = "renamed-by-picker.png",
            ContentType = "image/png",
            RetainAfterSession = true,
            CreatedAt = DateTimeOffset.Parse("2026-08-02T00:00:00Z")
        };

        Assert.Equal(
            AliAgentHarnessRunner.CaptureModelVisibleAttachmentDigest([original]),
            AliAgentHarnessRunner.CaptureModelVisibleAttachmentDigest([reattached]));
    }

    [Fact]
    public void DurableAttachmentBinding_RejectsChangedBytesAndChangedModelVisibleOrder()
    {
        var first = Attachment("first", [1, 2, 3]);
        var second = Attachment("second", [4, 5, 6]);
        var original = AliAgentHarnessRunner.CaptureModelVisibleAttachmentDigest([first, second]);
        var changedBytes = AliAgentHarnessRunner.CaptureModelVisibleAttachmentDigest(
            [first with { Base64Data = Convert.ToBase64String([1, 2, 9]) }, second]);
        var changedOrder = AliAgentHarnessRunner.CaptureModelVisibleAttachmentDigest([second, first]);

        Assert.NotEqual(original, changedBytes);
        Assert.NotEqual(original, changedOrder);
    }

    private static RuntimeChatAttachment Attachment(string id, byte[] bytes) =>
        new(
            id,
            RuntimeAttachmentKind.Image,
            id + ".png",
            "image/png",
            Convert.ToBase64String(bytes),
            RetainAfterSession: false,
            DateTimeOffset.UtcNow);

    private static IReadOnlyList<DataContent> BinaryContents(IReadOnlyList<ChatMessage> messages) =>
        messages.SelectMany(message => message.Contents).OfType<DataContent>().ToArray();

    private static AliPlanningTurnInput Input() => new(
        0,
        "No work has been accepted yet.",
        workGraphRevision: 0,
        authoritativeWorkGraph: WorkGraphSnapshot.Empty);

    private static CoordinatorTurnContext CreateTurn() => new(
        "conversation",
        "user-message",
        "assistant-message",
        "inspect this image",
        _ => { });

    private sealed class RecordingChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        internal List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToArray());
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class AcceptingObserver : IAliPlanningTransitionObserver
    {
        public ValueTask<AliPlanningTransitionReceipt> OnDecisionAcceptedAsync(
            AliPlanningDecisionAcceptedEvent accepted,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AliPlanningTransitionReceipt(accepted.ExpectedStateRevision));

        public ValueTask<AliPlanningEvidenceReceipt> OnToolResultObservedAsync(
            AliPlanningToolResultObservedEvent observed,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<AliPlanningTransitionReceipt> OnPlanningSuspendedAsync(
            AliPlanningSuspendedEvent suspended,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<AliPlanningTransitionReceipt> OnInterimResponsePreparedAsync(
            AliPlanningInterimPreparedEvent prepared,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<AliPlanningPublicationReceipt> OnFinalAnswerPreparedAsync(
            AliPlanningPublicationPreparedEvent prepared,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AliPlanningPublicationReceipt(
                prepared.ExpectedStateRevision + 1,
                prepared.PublicationId,
                prepared.AnswerDigest));
    }
}
