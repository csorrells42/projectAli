using System.Text;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.State;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class CompletionPauseRecoveryTests
{
    [Theory]
    [InlineData(
        AliPlanningInterimKind.CompletionInputNotAdmitted,
        InterimPublicationReason.CompletionInputNotAdmitted)]
    [InlineData(
        AliPlanningInterimKind.CompletionDispatchBindingsChanged,
        InterimPublicationReason.CompletionDispatchBindingsChanged)]
    [InlineData(
        AliPlanningInterimKind.CompletionOutputIncomplete,
        InterimPublicationReason.CompletionOutputIncomplete)]
    public async Task TypedCompletionPause_SurvivesRestartAndExplicitSameSettingsResume(
        AliPlanningInterimKind planningKind,
        InterimPublicationReason durableReason)
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity(
            "user",
            "completion-restart-conversation",
            "completion-restart-turn-" + planningKind);
        var bindings = Bindings("accepted");
        const string originalRequest = "Complete the exact accepted work without losing evidence.";
        const string acceptedHistory = "The prior accepted assistant result remains exact.";

        using (var initialCoordinator = new AliPlanningStateCoordinator(
                   directory.Path,
                   "profile"))
        {
            await using var turn = await initialCoordinator.BeginTurnAsync(
                Turn(identity, originalRequest),
                bindings,
                [new AcceptedConversationInput(
                    "accepted-assistant-message",
                    0,
                    acceptedHistory,
                    AcceptedConversationRole.Assistant)],
                capabilityRegistry: null,
                liveBindingsAccessor: () => bindings,
                TestContext.Current.CancellationToken);
            await PrepareAndCommitPauseAsync(turn, planningKind);
        }

        using var restartedCoordinator = new AliPlanningStateCoordinator(
            directory.Path,
            "profile");
        var recovered = await restartedCoordinator.RecoverTurnAsync(
            identity,
            bindings,
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);
        Assert.Equal(originalRequest, recovered.OriginalRequest);
        Assert.Equal(TurnControlState.SuspendedRuntime, recovered.State!.Control);
        Assert.Equal(bindings, recovered.State.Bindings);
        Assert.Equal(InterimPublicationStatus.Committed, recovered.State.InterimPublication!.Status);
        Assert.Equal(durableReason, recovered.State.InterimPublication.Reason);
        Assert.Null(recovered.State.FinalPublication);
        Assert.Empty(recovered.State.PendingActions);
        Assert.Null(recovered.State.PendingAcceptedCall);

        var resumed = await restartedCoordinator.ResumeTurnAsync(
            VisibleTurn(identity, "visible-same-settings-" + planningKind),
            identity,
            bindings,
            "Retry the exact preserved completion with the same settings.",
            "same-settings-steering-" + planningKind,
            capabilityRegistry: null,
            liveBindingsAccessor: () => bindings,
            TestContext.Current.CancellationToken);

        Assert.True(resumed.IsReady, resumed.FailureCode);
        await using var resumedTurn = Assert.IsType<AliDurablePlanningTurn>(resumed.Turn);
        Assert.Equal(originalRequest, resumedTurn.ImmutableOriginalRequest);
        Assert.Equal(acceptedHistory, resumedTurn.Input.AcceptedPriorConversation[0].Text);
        Assert.True(resumedTurn.Input.AcceptedPriorConversation[^1].IsSteering);
        var afterResume = await restartedCoordinator.RecoverTurnAsync(
            identity,
            bindings,
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnControlState.Running, afterResume.State!.Control);
        Assert.Null(afterResume.State.InterimPublication);
        Assert.Equal(bindings, afterResume.State.Bindings);
        Assert.Null(afterResume.State.FinalPublication);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CompletionPause_ExplicitlyAdoptsOnlyModelOrGenerationChanges(
        bool changeModel,
        bool changeGeneration)
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity(
            "user",
            "completion-settings-conversation",
            $"completion-settings-{changeModel}-{changeGeneration}");
        var initialBindings = Bindings("accepted");
        var newBindings = initialBindings with
        {
            ModelDigest = changeModel
                ? Digest("changed-model")
                : initialBindings.ModelDigest,
            GenerationSettingsDigest = changeGeneration
                ? Digest("changed-generation")
                : initialBindings.GenerationSettingsDigest
        };
        using var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile");
        await using (var turn = await coordinator.BeginTurnAsync(
                         Turn(identity, "Complete the accepted result."),
                         initialBindings,
                         acceptedPriorConversation: [],
                         capabilityRegistry: null,
                         liveBindingsAccessor: () => initialBindings,
                         TestContext.Current.CancellationToken))
        {
            await PrepareAndCommitPauseAsync(
                turn,
                AliPlanningInterimKind.CompletionOutputIncomplete);
        }

        var resumed = await coordinator.ResumeTurnAsync(
            VisibleTurn(identity, "visible-settings"),
            identity,
            newBindings,
            "Retry with the deliberately changed model configuration.",
            "model-configuration-steering",
            capabilityRegistry: null,
            liveBindingsAccessor: () => newBindings,
            TestContext.Current.CancellationToken);

        Assert.True(resumed.IsReady, resumed.FailureCode);
        await using var resumedTurn = Assert.IsType<AliDurablePlanningTurn>(resumed.Turn);
        var recovered = await coordinator.RecoverTurnAsync(
            identity,
            newBindings,
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);
        Assert.Equal(newBindings, recovered.State!.Bindings);
        Assert.Equal(TurnControlState.Running, recovered.State.Control);
        Assert.Null(recovered.State.InterimPublication);
    }

    [Fact]
    public async Task CompletionPause_UnrelatedBindingChangeIsRejectedWithoutClearingPause()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity(
            "user",
            "completion-unrelated-conversation",
            "completion-unrelated-turn");
        var initialBindings = Bindings("accepted");
        var changedBindings = initialBindings with
        {
            PermissionDigest = Digest("changed-permissions")
        };
        using var coordinator = new AliPlanningStateCoordinator(directory.Path, "profile");
        await using (var turn = await coordinator.BeginTurnAsync(
                         Turn(identity, "Complete the accepted result."),
                         initialBindings,
                         acceptedPriorConversation: [],
                         capabilityRegistry: null,
                         liveBindingsAccessor: () => initialBindings,
                         TestContext.Current.CancellationToken))
        {
            await PrepareAndCommitPauseAsync(
                turn,
                AliPlanningInterimKind.CompletionDispatchBindingsChanged);
        }

        var attempt = await coordinator.ResumeTurnAsync(
            VisibleTurn(identity, "visible-unrelated"),
            identity,
            changedBindings,
            "Do not adopt unrelated settings.",
            "unrelated-steering",
            capabilityRegistry: null,
            liveBindingsAccessor: () => changedBindings,
            TestContext.Current.CancellationToken);

        Assert.False(attempt.IsReady);
        Assert.Equal(
            "model-input-admission-unrelated-bindings-changed",
            attempt.FailureCode);
        Assert.Equal(["permissions"], attempt.Recovery.ChangedBindings);
        Assert.Equal(initialBindings, attempt.Recovery.State!.Bindings);
        Assert.Equal(TurnControlState.SuspendedRuntime, attempt.Recovery.State.Control);
        Assert.Equal(0, attempt.Recovery.State.SteeringCursor);
        Assert.Equal(
            InterimPublicationReason.CompletionDispatchBindingsChanged,
            attempt.Recovery.State.InterimPublication!.Reason);
        Assert.Equal(InterimPublicationStatus.Committed, attempt.Recovery.State.InterimPublication.Status);
        Assert.Null(attempt.Recovery.State.FinalPublication);
    }

    private static async Task PrepareAndCommitPauseAsync(
        AliDurablePlanningTurn turn,
        AliPlanningInterimKind kind)
    {
        var publicationId = "interim-" + kind;
        var text = "Safe typed completion pause: " + kind;
        var digest = TurnStateIntegrity.Digest(text);
        var expectedStateRevision = turn.Input.StateRevision;
        var prepared = await turn.OnInterimResponsePreparedAsync(
            new AliPlanningInterimPreparedEvent(
                turn.DurableIdentity.ConversationId,
                turn.DurableIdentity.AssistantMessageId,
                expectedStateRevision,
                publicationId,
                kind,
                text,
                digest),
            TestContext.Current.CancellationToken);
        Assert.True(prepared.StateRevision > expectedStateRevision);
        await turn.CommitInterimPublicationAsync(
            new AliPreparedInterimResponse(
                turn.DurableIdentity,
                publicationId,
                text,
                digest,
                kind),
            TestContext.Current.CancellationToken);
    }

    private static CoordinatorTurnContext Turn(
        TurnIdentity identity,
        string originalRequest) =>
        new(
            identity.ConversationId,
            "user-message",
            identity.AssistantMessageId,
            originalRequest,
            _ => { },
            observationIdentity: identity);

    private static CoordinatorTurnContext VisibleTurn(
        TurnIdentity durableIdentity,
        string assistantMessageId)
    {
        var visibleIdentity = new TurnIdentity(
            durableIdentity.UserId,
            durableIdentity.ConversationId,
            assistantMessageId);
        return new CoordinatorTurnContext(
            durableIdentity.ConversationId,
            "visible-user-message",
            assistantMessageId,
            "Explicitly resume the preserved completion.",
            _ => { },
            observationIdentity: visibleIdentity);
    }

    private static TurnRuntimeBindings Bindings(string suffix) => new(
        Digest("profile-" + suffix),
        Digest("runtime-" + suffix),
        Digest("model-" + suffix),
        Digest("generation-" + suffix),
        Digest("registry-" + suffix),
        Digest("permissions-" + suffix),
        Digest("mcp-" + suffix),
        Digest("attachments-" + suffix),
        Digest("artifacts-" + suffix));

    private static string Digest(string value) =>
        TurnStateIntegrity.Digest(Encoding.UTF8.GetBytes(value));
}
