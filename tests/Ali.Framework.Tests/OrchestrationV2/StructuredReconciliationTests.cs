using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.State;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class StructuredReconciliationTests
{
    [Theory]
    [InlineData(ActionUserResolution.ConfirmApplied)]
    [InlineData(ActionUserResolution.ConfirmAbsent)]
    public async Task UnknownAction_RequiresExactTypedResolution_AndReplays(
        ActionUserResolution resolution)
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity("action");
        var intent = Intent("effect-001");
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        await writer.PrepareActionAsync(
            identity,
            expectedRevision: 1,
            intent,
            "action-prepare",
            TestContext.Current.CancellationToken);
        var recovery = await new TurnRecoveryService(
                writer,
                [new UnknownActionReconciler(intent.ReconcilerId)])
            .RecoverAsync(
                identity,
                Bindings(),
                explicitlyRequested: true,
                TestContext.Current.CancellationToken);

        Assert.Equal(TurnRecoveryStatus.StructuredResolutionRequired, recovery.Status);
        var waiting = await DisplayPreparedInterimAsync(
            writer,
            identity,
            recovery.State!,
            TestContext.Current.CancellationToken);
        var prompt = waiting.InterimPublication!;
        var pending = Assert.Single(waiting.PendingActions);

        var ordinaryInputs = new[] { "yes", "no", "retry", "I saw it" };
        for (var index = 0; index < ordinaryInputs.Length; index++)
        {
            var input = ordinaryInputs[index];
            await Assert.ThrowsAsync<InvalidDataException>(() => writer.AppendSteeringAsync(
                identity,
                waiting.Revision,
                input,
                "ordinary-text-cannot-resolve-" + index,
                TestContext.Current.CancellationToken));
        }
        Assert.DoesNotContain(
            (await writer.ReplayAsync(identity, TestContext.Current.CancellationToken)).Entries,
            entry => entry.Transition is SteeringAppendedTransition);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ChangeControlAsync(
            identity,
            waiting.Revision,
            TurnControlState.Running,
            "ordinary-resume",
            "ordinary-control-cannot-resolve",
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ClearInterimPublicationAsync(
            identity,
            waiting.Revision,
            prompt.PublicationId,
            prompt.Kind,
            prompt.TextDigest,
            "ordinary-clear",
            "ordinary-clear-cannot-resolve",
            TestContext.Current.CancellationToken));

        const string resolutionCorrelation = "typed-action-resolution";
        var resolved = await writer.ResolveUnknownActionAsync(
            identity,
            waiting.Revision,
            "user-command-001",
            prompt.PublicationId,
            prompt.TextDigest,
            prompt.SubjectId,
            pending.PreparedAtRevision,
            resolution,
            resolutionCorrelation,
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnTransitionWriteStatus.Committed, resolved.Status);
        var resolvedState = Assert.IsType<TurnState>(resolved.State);
        Assert.Equal(TurnControlState.Running, resolvedState.Control);
        Assert.Empty(resolvedState.PendingActions);
        Assert.Null(resolvedState.PendingAcceptedCall);
        Assert.Null(resolvedState.InterimPublication);

        var duplicate = await writer.ResolveUnknownActionAsync(
            identity,
            waiting.Revision,
            "user-command-001",
            prompt.PublicationId,
            prompt.TextDigest,
            prompt.SubjectId,
            pending.PreparedAtRevision,
            resolution,
            resolutionCorrelation,
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnTransitionWriteStatus.AlreadyRecorded, duplicate.Status);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ResolveUnknownActionAsync(
            identity,
            waiting.Revision,
            "user-command-001",
            prompt.PublicationId,
            prompt.TextDigest,
            prompt.SubjectId,
            pending.PreparedAtRevision,
            resolution == ActionUserResolution.ConfirmApplied
                ? ActionUserResolution.ConfirmAbsent
                : ActionUserResolution.ConfirmApplied,
            resolutionCorrelation,
            TestContext.Current.CancellationToken));

        using var reopened = Writer(directory.Path);
        var replayed = await reopened.ReadAsync(
            identity,
            TestContext.Current.CancellationToken);
        Assert.Equal(resolvedState.Revision, replayed!.Revision);
        Assert.Equal(resolvedState.Control, replayed.Control);
        Assert.Empty(replayed.PendingActions);
        Assert.Null(replayed.InterimPublication);
        var resumeProjection = await reopened.ReadResumeProjectionAsync(
            identity,
            TestContext.Current.CancellationToken);
        var projectedResolution = Assert.Single(resumeProjection.UserResolutions);
        Assert.Equal(resolvedState.Revision, projectedResolution.StateRevision);
        Assert.Equal("user-command-001", projectedResolution.SourceCommandId);
        Assert.Equal(prompt.PublicationId, projectedResolution.PromptPublicationId);
        Assert.Equal(prompt.TextDigest, projectedResolution.PromptTextDigest);
        Assert.Equal(TurnUserResolutionKind.Action, projectedResolution.Kind);
        Assert.Equal(
            InterimPublicationReason.ActionReconciliationRequired,
            projectedResolution.Reason);
        Assert.Equal(prompt.SubjectId, projectedResolution.SubjectId);
        Assert.Equal(pending.PreparedAtRevision, projectedResolution.SubjectPreparedRevision);
        Assert.Equal(
            resolution == ActionUserResolution.ConfirmApplied
                ? TurnUserResolutionOutcome.ActionConfirmedApplied
                : TurnUserResolutionOutcome.ActionConfirmedAbsent,
            projectedResolution.Outcome);
        if (resolution == ActionUserResolution.ConfirmApplied)
        {
            var duplicatePreparation = await reopened.PrepareActionAsync(
                identity,
                replayed.Revision,
                intent,
                "applied-action-cannot-repeat",
                TestContext.Current.CancellationToken);
            Assert.Equal(TurnTransitionWriteStatus.AlreadyRecorded, duplicatePreparation.Status);
            Assert.Equal(replayed.Revision, duplicatePreparation.State!.Revision);
        }
        else
        {
            var retried = await reopened.PrepareActionAsync(
                identity,
                replayed.Revision,
                intent,
                "confirmed-absent-action-can-retry",
                TestContext.Current.CancellationToken);
            Assert.Equal(TurnTransitionWriteStatus.Committed, retried.Status);
        }
    }

    [Fact]
    public async Task UnknownAction_WrongSubjectOrPreparedRevision_FailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity("wrong-action");
        var intent = Intent("effect-002");
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        await writer.PrepareActionAsync(
            identity,
            expectedRevision: 1,
            intent,
            "action-prepare",
            TestContext.Current.CancellationToken);
        var recovery = await new TurnRecoveryService(
                writer,
                [new UnknownActionReconciler(intent.ReconcilerId)])
            .RecoverAsync(
                identity,
                Bindings(),
                explicitlyRequested: true,
                TestContext.Current.CancellationToken);
        var waiting = await DisplayPreparedInterimAsync(
            writer,
            identity,
            recovery.State!,
            TestContext.Current.CancellationToken);
        var prompt = waiting.InterimPublication!;
        var pending = Assert.Single(waiting.PendingActions);

        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ResolveUnknownActionAsync(
            identity,
            waiting.Revision,
            "user-command-wrong-subject",
            prompt.PublicationId,
            prompt.TextDigest,
            "different-action",
            pending.PreparedAtRevision,
            ActionUserResolution.ConfirmAbsent,
            "wrong-subject-resolution",
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ResolveUnknownActionAsync(
            identity,
            waiting.Revision,
            "user-command-stale-revision",
            prompt.PublicationId,
            prompt.TextDigest,
            prompt.SubjectId,
            pending.PreparedAtRevision + 1,
            ActionUserResolution.ConfirmAbsent,
            "wrong-prepared-revision",
            TestContext.Current.CancellationToken));
        var stale = await writer.ResolveUnknownActionAsync(
            identity,
            waiting.Revision - 1,
            "user-command-stale-state",
            prompt.PublicationId,
            prompt.TextDigest,
            prompt.SubjectId,
            pending.PreparedAtRevision,
            ActionUserResolution.ConfirmAbsent,
            "stale-state-resolution",
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnTransitionWriteStatus.RevisionConflict, stale.Status);

        var unchanged = await writer.ReadAsync(identity, TestContext.Current.CancellationToken);
        Assert.Equal(waiting.Revision, unchanged!.Revision);
        Assert.Equal(TurnControlState.AwaitingUser, unchanged.Control);
        Assert.Equal(prompt.PublicationId, unchanged.InterimPublication!.PublicationId);
        Assert.Equal(intent.IdempotencyKey, Assert.Single(unchanged.PendingActions).Intent.IdempotencyKey);
    }

    [Fact]
    public async Task UnknownFinalPublication_ConfirmDisplayed_CompletesAtomically()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity("publication-applied");
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        const string answer = "The exact answer the user confirms was displayed.";
        var answerDigest = Digest(answer);
        await writer.PrepareFinalPublicationAsync(
            identity,
            expectedRevision: 1,
            "publication-001",
            identity.AssistantMessageId,
            answer,
            answerDigest,
            "publication-prepare",
            TestContext.Current.CancellationToken);
        var publicationReconciler = new UnknownPublicationReconciler();
        var recovery = await new TurnRecoveryService(writer, [], publicationReconciler)
            .RecoverAsync(
                identity,
                Bindings(),
                explicitlyRequested: true,
                TestContext.Current.CancellationToken);
        var waiting = await DisplayPreparedInterimAsync(
            writer,
            identity,
            recovery.State!,
            TestContext.Current.CancellationToken);
        var prompt = waiting.InterimPublication!;
        var publication = waiting.FinalPublication!;

        var resolved = await writer.ResolveUnknownFinalPublicationAsync(
            identity,
            waiting.Revision,
            "user-command-saw-answer",
            prompt.PublicationId,
            prompt.TextDigest,
            prompt.SubjectId,
            publication.PreparedAtRevision,
            FinalPublicationUserResolution.ConfirmDisplayed,
            "typed-publication-displayed",
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnTransitionWriteStatus.Committed, resolved.Status);
        var resolvedState = Assert.IsType<TurnState>(resolved.State);
        Assert.Equal(TurnControlState.Completed, resolvedState.Control);
        Assert.Null(resolvedState.InterimPublication);
        Assert.Equal(
            FinalPublicationStatus.Committed,
            resolvedState.FinalPublication!.Status);
        using var reopened = Writer(directory.Path);
        var replayed = await reopened.ReadAsync(
            identity,
            TestContext.Current.CancellationToken);
        Assert.Equal(resolvedState.Revision, replayed!.Revision);
        Assert.Equal(TurnControlState.Completed, replayed.Control);
        Assert.Equal(
            FinalPublicationStatus.Committed,
            replayed.FinalPublication!.Status);
        var resumeProjection = await reopened.ReadResumeProjectionAsync(
            identity,
            TestContext.Current.CancellationToken);
        var projectedResolution = Assert.Single(resumeProjection.UserResolutions);
        Assert.Equal(resolvedState.Revision, projectedResolution.StateRevision);
        Assert.Equal("user-command-saw-answer", projectedResolution.SourceCommandId);
        Assert.Equal(TurnUserResolutionKind.FinalPublication, projectedResolution.Kind);
        Assert.Equal(publication.PublicationId, projectedResolution.SubjectId);
        Assert.Equal(publication.PreparedAtRevision, projectedResolution.SubjectPreparedRevision);
        Assert.Equal(
            TurnUserResolutionOutcome.FinalPublicationConfirmedDisplayed,
            projectedResolution.Outcome);
    }

    [Fact]
    public async Task UnknownFinalPublication_ConfirmNotDisplayed_RemainsDurablyRepublishable()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity("publication-absent");
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        const string answer = "The exact answer that must survive retargeting.";
        var answerDigest = Digest(answer);
        await writer.PrepareFinalPublicationAsync(
            identity,
            expectedRevision: 1,
            "publication-old",
            identity.AssistantMessageId,
            answer,
            answerDigest,
            "publication-prepare",
            TestContext.Current.CancellationToken);
        var publicationReconciler = new UnknownPublicationReconciler();
        var recovery = await new TurnRecoveryService(writer, [], publicationReconciler)
            .RecoverAsync(
                identity,
                Bindings(),
                explicitlyRequested: true,
                TestContext.Current.CancellationToken);
        var waiting = await DisplayPreparedInterimAsync(
            writer,
            identity,
            recovery.State!,
            TestContext.Current.CancellationToken);
        var prompt = waiting.InterimPublication!;
        var publication = waiting.FinalPublication!;

        var resolved = await writer.ResolveUnknownFinalPublicationAsync(
            identity,
            waiting.Revision,
            "user-command-did-not-see-answer",
            prompt.PublicationId,
            prompt.TextDigest,
            prompt.SubjectId,
            publication.PreparedAtRevision,
            FinalPublicationUserResolution.ConfirmNotDisplayed,
            "typed-publication-absent",
            TestContext.Current.CancellationToken);
        var resolvedState = Assert.IsType<TurnState>(resolved.State);
        Assert.Equal(TurnControlState.Running, resolvedState.Control);
        Assert.Equal(
            FinalPublicationStatus.ConfirmedAbsent,
            resolvedState.FinalPublication!.Status);
        Assert.Null(resolvedState.InterimPublication);
        var resumeProjection = await writer.ReadResumeProjectionAsync(
            identity,
            TestContext.Current.CancellationToken);
        var projectedResolution = Assert.Single(resumeProjection.UserResolutions);
        Assert.Equal(resolvedState.Revision, projectedResolution.StateRevision);
        Assert.Equal(
            TurnUserResolutionOutcome.FinalPublicationConfirmedNotDisplayed,
            projectedResolution.Outcome);

        var postResolution = await new TurnRecoveryService(writer, [], publicationReconciler)
            .RecoverAsync(
                identity,
                Bindings(),
                explicitlyRequested: true,
                TestContext.Current.CancellationToken);
        Assert.Equal(TurnRecoveryStatus.Ready, postResolution.Status);
        Assert.True(postResolution.Publication!.SafeToPublishIdempotently);
        Assert.Equal(1, publicationReconciler.Calls);

        var retargeted = await writer.RetargetFinalPublicationAsync(
            identity,
            resolvedState.Revision,
            publication.PublicationId,
            publication.AssistantMessageId,
            "publication-new",
            "assistant-message-new",
            answerDigest,
            "publication-retarget",
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnTransitionWriteStatus.Committed, retargeted.Status);
        var retargetedState = Assert.IsType<TurnState>(retargeted.State);
        Assert.Equal(
            FinalPublicationStatus.Prepared,
            retargetedState.FinalPublication!.Status);
        Assert.Equal("publication-new", retargetedState.FinalPublication.PublicationId);
        Assert.Equal("assistant-message-new", retargetedState.FinalPublication.AssistantMessageId);
        Assert.Equal(
            answer,
            await writer.ReadFinalPublicationAnswerAsync(
                identity,
                TestContext.Current.CancellationToken));

        using var reopened = Writer(directory.Path);
        var replayed = await reopened.ReadAsync(identity, TestContext.Current.CancellationToken);
        Assert.Equal(retargetedState.Revision, replayed!.Revision);
        Assert.Equal(
            FinalPublicationStatus.Prepared,
            replayed.FinalPublication!.Status);
        Assert.Equal("publication-new", replayed.FinalPublication.PublicationId);
        Assert.Equal(
            answer,
            await reopened.ReadFinalPublicationAnswerAsync(
                identity,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownAction_CancellationTerminatesWithoutClaimingOutcome()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity("action-cancel");
        var intent = Intent("effect-cancel");
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        await writer.PrepareActionAsync(
            identity,
            expectedRevision: 1,
            intent,
            "action-prepare",
            TestContext.Current.CancellationToken);
        var reconciler = new UnknownActionReconciler(intent.ReconcilerId);
        var recoveryService = new TurnRecoveryService(writer, [reconciler]);
        var recovery = await recoveryService.RecoverAsync(
            identity,
            Bindings(),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);
        var waiting = await DisplayPreparedInterimAsync(
            writer,
            identity,
            recovery.State!,
            TestContext.Current.CancellationToken);
        var cancelRequested = await writer.ChangeControlAsync(
            identity,
            waiting.Revision,
            TurnControlState.CancelRequested,
            "user-cancelled",
            "action-cancel-requested",
            TestContext.Current.CancellationToken);

        var cancelled = await recoveryService.RecoverAsync(
            identity,
            Bindings(),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnRecoveryStatus.Ready, cancelled.Status);
        Assert.Equal(TurnControlState.Cancelled, cancelled.State!.Control);
        Assert.Empty(cancelled.State.PendingActions);
        Assert.Null(cancelled.State.PendingAcceptedCall);
        Assert.Null(cancelled.State.InterimPublication);
        Assert.Null(cancelled.State.FinalPublication);
        Assert.Equal(
            cancelRequested.State!.Revision + 1,
            cancelled.State.Revision);
        var replay = await writer.ReplayAsync(identity, TestContext.Current.CancellationToken);
        Assert.Single(replay.Entries.Select(entry => entry.Transition)
            .OfType<InDoubtActionCancelledTransition>());
    }

    [Fact]
    public async Task UnknownFinalPublication_CancellationTerminatesWithoutClaimingDisplay()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity("publication-cancel");
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        const string answer = "An answer whose display state remains unknown at cancellation.";
        var answerDigest = Digest(answer);
        await writer.PrepareFinalPublicationAsync(
            identity,
            expectedRevision: 1,
            "publication-cancel",
            identity.AssistantMessageId,
            answer,
            answerDigest,
            "publication-prepare",
            TestContext.Current.CancellationToken);
        var reconciler = new UnknownPublicationReconciler();
        var recoveryService = new TurnRecoveryService(writer, [], reconciler);
        var recovery = await recoveryService.RecoverAsync(
            identity,
            Bindings(),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);
        var waiting = await DisplayPreparedInterimAsync(
            writer,
            identity,
            recovery.State!,
            TestContext.Current.CancellationToken);
        await writer.ChangeControlAsync(
            identity,
            waiting.Revision,
            TurnControlState.CancelRequested,
            "user-cancelled",
            "publication-cancel-requested",
            TestContext.Current.CancellationToken);

        var cancelled = await recoveryService.RecoverAsync(
            identity,
            Bindings(),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnRecoveryStatus.Ready, cancelled.Status);
        Assert.Equal(TurnControlState.Cancelled, cancelled.State!.Control);
        Assert.Empty(cancelled.State.PendingActions);
        Assert.Null(cancelled.State.InterimPublication);
        Assert.Null(cancelled.State.FinalPublication);
        Assert.False(cancelled.Publication!.SafeToPublishIdempotently);
        var replay = await writer.ReplayAsync(identity, TestContext.Current.CancellationToken);
        Assert.Single(replay.Entries.Select(entry => entry.Transition)
            .OfType<InDoubtFinalPublicationCancelledTransition>());
    }

    [Fact]
    public async Task StructuredActionPrompt_IsClearedAtomicallyWhenObservationLaterProvesAbsent()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity("action-observed-absent");
        var intent = Intent("effect-observed-absent");
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        await writer.PrepareActionAsync(
            identity,
            expectedRevision: 1,
            intent,
            "action-prepare",
            TestContext.Current.CancellationToken);
        var first = await new TurnRecoveryService(
                writer,
                [new UnknownActionReconciler(intent.ReconcilerId)])
            .RecoverAsync(
                identity,
                Bindings(),
                explicitlyRequested: true,
                TestContext.Current.CancellationToken);
        _ = await DisplayPreparedInterimAsync(
            writer,
            identity,
            first.State!,
            TestContext.Current.CancellationToken);

        var resolved = await new TurnRecoveryService(
                writer,
                [new AbsentActionReconciler(intent.ReconcilerId)])
            .RecoverAsync(
                identity,
                Bindings(),
                explicitlyRequested: true,
                TestContext.Current.CancellationToken);

        Assert.Equal(TurnRecoveryStatus.Ready, resolved.Status);
        Assert.Equal(TurnControlState.Running, resolved.State!.Control);
        Assert.Empty(resolved.State.PendingActions);
        Assert.Null(resolved.State.InterimPublication);
        Assert.Equal(ActionReconciliationDisposition.Absent, Assert.Single(resolved.Actions).Disposition);
    }

    [Fact]
    public async Task StructuredPublicationPrompt_IsClearedAtomicallyWhenObservationLaterProvesAbsent()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity("publication-observed-absent");
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        const string answer = "An answer later proven absent from the conversation store.";
        var answerDigest = Digest(answer);
        await writer.PrepareFinalPublicationAsync(
            identity,
            expectedRevision: 1,
            "publication-observed-absent",
            identity.AssistantMessageId,
            answer,
            answerDigest,
            "publication-prepare",
            TestContext.Current.CancellationToken);
        var first = await new TurnRecoveryService(
                writer,
                [],
                new UnknownPublicationReconciler())
            .RecoverAsync(
                identity,
                Bindings(),
                explicitlyRequested: true,
                TestContext.Current.CancellationToken);
        _ = await DisplayPreparedInterimAsync(
            writer,
            identity,
            first.State!,
            TestContext.Current.CancellationToken);

        var resolved = await new TurnRecoveryService(
                writer,
                [],
                new AbsentPublicationReconciler())
            .RecoverAsync(
                identity,
                Bindings(),
                explicitlyRequested: true,
                TestContext.Current.CancellationToken);

        Assert.Equal(TurnRecoveryStatus.Ready, resolved.Status);
        Assert.Equal(TurnControlState.Running, resolved.State!.Control);
        Assert.Null(resolved.State.InterimPublication);
        Assert.Equal(
            FinalPublicationStatus.ConfirmedAbsent,
            resolved.State.FinalPublication!.Status);
        Assert.True(resolved.Publication!.SafeToPublishIdempotently);
        var replay = await writer.ReplayAsync(identity, TestContext.Current.CancellationToken);
        Assert.Single(replay.Entries.Select(entry => entry.Transition)
            .OfType<FinalPublicationConfirmedAbsentTransition>());
    }

    private static async Task<TurnState> DisplayPreparedInterimAsync(
        TurnTransitionWriter writer,
        TurnIdentity identity,
        TurnState state,
        CancellationToken cancellationToken)
    {
        var interim = state.InterimPublication
            ?? throw new InvalidDataException("The recovery prompt was not prepared.");
        Assert.Equal(InterimPublicationStatus.Prepared, interim.Status);
        Assert.Equal(TurnControlState.Running, state.Control);
        var committed = await writer.CommitInterimPublicationAsync(
            identity,
            state.Revision,
            interim.PublicationId,
            interim.Kind,
            interim.TextDigest,
            "recovery-prompt-display-commit-" + interim.PublicationId,
            cancellationToken);
        var waiting = await writer.ChangeControlAsync(
            identity,
            committed.State!.Revision,
            TurnControlState.AwaitingUser,
            "structured-reconciliation-prompt-displayed",
            "recovery-prompt-waiting-" + interim.PublicationId,
            cancellationToken);
        return waiting.State!;
    }

    private static async Task StartAsync(
        TurnTransitionWriter writer,
        TurnIdentity identity)
    {
        var started = await writer.StartAsync(
            identity,
            "Perform the requested durable operation.",
            Bindings(),
            "turn-start",
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnTransitionWriteStatus.Committed, started.Status);
    }

    private static TurnTransitionWriter Writer(string path) => new(path, "profile-a");

    private static TurnIdentity Identity(string suffix) => new(
        "user-a",
        "conversation-" + suffix,
        "assistant-message-" + suffix);

    private static PreparedActionIntent Intent(string idempotencyKey) => new(
        idempotencyKey,
        "work-001",
        "write_file",
        "filesystem.write",
        Digest("arguments"),
        Digest("target-version"),
        Digest("permission-receipt"),
        Digest("registry"),
        "filesystem-observer",
        RequiresApproval: true);

    private static TurnRuntimeBindings Bindings() => new(
        Digest("assistant-profile"),
        Digest("runtime"),
        Digest("model"),
        Digest("generation"),
        Digest("registry"),
        Digest("permissions"),
        Digest("mcp"),
        Digest("attachments"),
        Digest("artifacts"));

    private static string Digest(string value) => TurnStateIntegrity.Digest(value);

    private sealed class UnknownActionReconciler(string reconcilerId) : ITurnActionReconciler
    {
        public string ReconcilerId { get; } = reconcilerId;

        public ValueTask<ActionReconciliationResult> ReconcileAsync(
            TurnIdentity identity,
            PreparedActionIntent intent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ActionReconciliationResult.Unknown("target-state-unknown"));
    }

    private sealed class AbsentActionReconciler(string reconcilerId) : ITurnActionReconciler
    {
        public string ReconcilerId { get; } = reconcilerId;

        public ValueTask<ActionReconciliationResult> ReconcileAsync(
            TurnIdentity identity,
            PreparedActionIntent intent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ActionReconciliationResult.Absent("target-proved-absent"));
    }

    private sealed class UnknownPublicationReconciler : ITurnPublicationReconciler
    {
        public int Calls { get; private set; }

        public ValueTask<PublicationReconciliationResult> ReconcileAsync(
            TurnIdentity identity,
            FinalPublicationState publication,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new PublicationReconciliationResult(
                PublicationReconciliationDisposition.Unknown,
                "conversation-state-unknown"));
        }
    }

    private sealed class AbsentPublicationReconciler : ITurnPublicationReconciler
    {
        public ValueTask<PublicationReconciliationResult> ReconcileAsync(
            TurnIdentity identity,
            FinalPublicationState publication,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PublicationReconciliationResult(
                PublicationReconciliationDisposition.Absent,
                "conversation-proved-absent"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ali-structured-reconciliation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup is best-effort only.
            }
        }
    }
}
