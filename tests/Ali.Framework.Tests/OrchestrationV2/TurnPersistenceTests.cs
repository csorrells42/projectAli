using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class TurnPersistenceTests
{
    [Fact]
    public async Task TransitionWriter_UsesOneJournalAndExpectedRevisionCas()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var first = Writer(directory.Path);
        using var second = Writer(directory.Path);
        var started = await first.StartAsync(
            identity,
            "Build the requested project.",
            Bindings("initial"),
            "turn-start",
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnTransitionWriteStatus.Committed, started.Status);
        var results = await Task.WhenAll(
            first.AppendSteeringAsync(
                identity,
                1,
                "Use the existing solution.",
                "steering-one",
                TestContext.Current.CancellationToken),
            second.AppendSteeringAsync(
                identity,
                1,
                "Keep the UI unchanged.",
                "steering-two",
                TestContext.Current.CancellationToken));

        Assert.Single(results, result => result.Status == TurnTransitionWriteStatus.Committed);
        Assert.Single(results, result => result.Status == TurnTransitionWriteStatus.RevisionConflict);
        var replay = await first.ReplayAsync(identity, TestContext.Current.CancellationToken);
        Assert.Equal(2, replay.State!.Revision);
        Assert.Equal(2, replay.State.JournalCursor);
        Assert.Equal(2, replay.State.SteeringCursor);
        Assert.Equal(2, replay.Entries.Count);
        Assert.Single(
            Directory.GetFiles(directory.Path, "turn.journal.jsonl", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(directory.Path, "events", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(directory.Path, "state", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TransitionWriter_BoundsJournalCache_AndRehydratesAnEvictedTurn()
    {
        using var directory = new TemporaryDirectory();
        using var writer = Writer(directory.Path);
        var identities = Enumerable.Range(0, 65)
            .Select(index => new TurnIdentity(
                "user",
                $"conversation-{index}",
                $"assistant-message-{index}"))
            .ToArray();

        for (var index = 0; index < identities.Length; index++)
        {
            var started = await writer.StartAsync(
                identities[index],
                $"Request {index}",
                Bindings("initial"),
                $"turn-start-{index}",
                TestContext.Current.CancellationToken);
            Assert.Equal(TurnTransitionWriteStatus.Committed, started.Status);
        }

        Assert.Equal(64, writer.CaptureCachedTurnJournalCount());
        var replay = await writer.ReplayAsync(
            identities[0],
            TestContext.Current.CancellationToken);
        Assert.Equal(1, replay.State!.Revision);
        Assert.Single(replay.Entries);
        Assert.Equal(64, writer.CaptureCachedTurnJournalCount());
    }

    [Fact]
    public async Task Recovery_ReleasesPerTurnGates_AfterConcurrentCallersExit()
    {
        using var directory = new TemporaryDirectory();
        using var writer = Writer(directory.Path);
        var recovery = new TurnRecoveryService(writer, []);
        var identity = Identity();

        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ =>
            recovery.RecoverAsync(
                identity,
                Bindings("initial"),
                explicitlyRequested: false,
                TestContext.Current.CancellationToken)));

        Assert.All(results, result => Assert.Equal(TurnRecoveryStatus.NotFound, result.Status));
        Assert.Equal(0, recovery.CaptureRetainedTurnGateCount());
    }

    [Fact]
    public async Task CorrelationKey_IsIdempotent_AndCannotBeRebound()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var writer = Writer(directory.Path);
        var bindings = Bindings("same");
        var first = await writer.StartAsync(
            identity,
            "Original request",
            bindings,
            "stable-correlation",
            TestContext.Current.CancellationToken);
        var retry = await writer.StartAsync(
            identity,
            "Original request",
            bindings,
            "stable-correlation",
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnTransitionWriteStatus.Committed, first.Status);
        Assert.Equal(TurnTransitionWriteStatus.AlreadyRecorded, retry.Status);
        Assert.Equal(1, retry.State!.Revision);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.StartAsync(
            identity,
            "Different request",
            bindings,
            "stable-correlation",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PlanningDecision_IsCompactIdempotentAndTamperEvident()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        const string decisionProseAndArguments =
            "Use secret argument token=do-not-journal and then explain the result.";
        const string materialClaimProse = "The external target now contains the secret value.";
        var decisionDigest = Digest(decisionProseAndArguments);
        var claimsDigest = Digest(materialClaimProse);
        var acceptedCall = AcceptedCall("call-001", "safe-tool-id");
        using (var writer = Writer(directory.Path))
        {
            await StartAsync(writer, identity);
            var accepted = await writer.RecordPlanningDecisionAcceptedAsync(
                identity,
                1,
                decisionDigest,
                PlanningAcceptedActionKind.CallTool,
                "call-001",
                "safe-tool-id",
                workGraphRevision: 0,
                materialClaimsDigest: claimsDigest,
                correlationKey: "planner-decision-001",
                acceptedCall,
                workGraph: null,
                cancellationToken: TestContext.Current.CancellationToken);
            var retry = await writer.RecordPlanningDecisionAcceptedAsync(
                identity,
                expectedRevision: 1,
                decisionDigest,
                PlanningAcceptedActionKind.CallTool,
                "call-001",
                "safe-tool-id",
                workGraphRevision: 0,
                materialClaimsDigest: claimsDigest,
                correlationKey: "planner-decision-001",
                acceptedCall,
                workGraph: null,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(TurnTransitionWriteStatus.Committed, accepted.Status);
            Assert.Equal(2, accepted.State!.Revision);
            Assert.Equal(TurnTransitionWriteStatus.AlreadyRecorded, retry.Status);
            Assert.Equal(2, retry.State!.Revision);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                writer.RecordPlanningDecisionAcceptedAsync(
                    identity,
                    expectedRevision: 2,
                    Digest("different decision"),
                    PlanningAcceptedActionKind.CallTool,
                    "call-001",
                    "safe-tool-id",
                    workGraphRevision: 0,
                    materialClaimsDigest: claimsDigest,
                    correlationKey: "planner-decision-001",
                    acceptedCall,
                    workGraph: null,
                    cancellationToken: TestContext.Current.CancellationToken));
        }

        var journal = Assert.Single(
            Directory.GetFiles(directory.Path, "turn.journal.jsonl", SearchOption.AllDirectories));
        var text = await File.ReadAllTextAsync(journal, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(decisionProseAndArguments, text, StringComparison.Ordinal);
        Assert.DoesNotContain(materialClaimProse, text, StringComparison.Ordinal);
        Assert.DoesNotContain("accepted-path", text, StringComparison.Ordinal);
        var digestStart = text.IndexOf(
            "\"decisionDigest\":\"" + decisionDigest + "\"",
            StringComparison.Ordinal);
        Assert.True(digestStart >= 0);
        digestStart += "\"decisionDigest\":\"".Length;
        var tampered = text.ToCharArray();
        tampered[digestStart] = tampered[digestStart] == '0' ? '1' : '0';
        await File.WriteAllTextAsync(
            journal,
            new string(tampered),
            TestContext.Current.CancellationToken);
        using var reopened = Writer(directory.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reopened.ReplayAsync(identity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task State_PreservesOriginalRequestIdentityBindingsAndOnlyGrowingCursors()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var bindings = Bindings("bound");
        const string originalRequest = "Keep this exact request.\nDo not summarize it.";
        const string steeringText = "This exact steering event is durable.";
        using var writer = Writer(directory.Path);
        await writer.StartAsync(
            identity,
            originalRequest,
            bindings,
            "start",
            TestContext.Current.CancellationToken);
        await writer.AppendSteeringAsync(
            identity,
            1,
            steeringText,
            "steer",
            TestContext.Current.CancellationToken);

        var state = await writer.ReadAsync(identity, TestContext.Current.CancellationToken);
        Assert.NotNull(state);
        Assert.Equal(identity, state.Identity);
        Assert.Equal(Digest(originalRequest), state.OriginalRequestDigest);
        Assert.Equal(
            originalRequest,
            await writer.ReadOriginalRequestAsync(identity, TestContext.Current.CancellationToken));
        Assert.Equal(bindings, state.Bindings);
        Assert.Equal(2, state.SteeringCursor);
        Assert.Empty(state.PendingActions);
        var replay = await writer.ReplayAsync(identity, TestContext.Current.CancellationToken);
        var steering = Assert.IsType<SteeringAppendedTransition>(replay.Entries[1].Transition);
        Assert.Equal(
            steeringText,
            await writer.ReadSteeringAsync(
                identity,
                steering,
                TestContext.Current.CancellationToken));
        var journal = Assert.Single(
            Directory.GetFiles(directory.Path, "turn.journal.jsonl", SearchOption.AllDirectories));
        var journalText = await File.ReadAllTextAsync(
            journal,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(originalRequest, journalText, StringComparison.Ordinal);
        Assert.DoesNotContain(steeringText, journalText, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Directory.GetFiles(directory.Path, "*.turn-input", SearchOption.AllDirectories).Length);
        Assert.DoesNotContain(
            typeof(TurnState).GetProperties(),
            property => property.Name.Contains("Events", StringComparison.Ordinal)
                        || property.Name.Contains("EvidenceEntries", StringComparison.Ordinal)
                        || property.Name.Contains("SteeringEvents", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TurnStart_ProtectsAcceptedPriorConversationAndRecoversExactSourceIdentity()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        const string first = "Earlier exact user text that must not be re-imported from mutable UI.";
        const string second = "A second accepted message.";
        const string system = "An exact accepted system context row.";
        using var writer = Writer(directory.Path);
        var started = await writer.StartAsync(
            identity,
            "Original request",
            Bindings("initial"),
            "turn-start",
            [
                new AcceptedConversationInput("message-b", 8, second),
                new AcceptedConversationInput("message-a", 3, first),
                new AcceptedConversationInput(
                    "message-system",
                    1,
                    system,
                    AcceptedConversationRole.System)
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnTransitionWriteStatus.Committed, started.Status);
        Assert.Equal([1L, 3L, 8L], started.State!.AcceptedPriorConversation.Select(item => item.Sequence));
        var recovered = await writer.ReadAcceptedPriorConversationAsync(
            identity,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            ["message-system", "message-a", "message-b"],
            recovered.Select(item => item.SourceMessageId));
        Assert.Equal([system, first, second], recovered.Select(item => item.Text));
        Assert.Equal(
            [AcceptedConversationRole.System, AcceptedConversationRole.User, AcceptedConversationRole.User],
            recovered.Select(item => item.Role));

        var journal = Assert.Single(
            Directory.GetFiles(directory.Path, "turn.journal.jsonl", SearchOption.AllDirectories));
        var journalText = await File.ReadAllTextAsync(
            journal,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(first, journalText, StringComparison.Ordinal);
        Assert.DoesNotContain(second, journalText, StringComparison.Ordinal);
        Assert.DoesNotContain(system, journalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterimPublication_IsProtectedCommittedBeforeWaitingAndClearedAfterResume()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        const string prompt = "Which exact project should Ali continue?";
        var digest = Digest(prompt);
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);

        var prepared = await writer.PrepareInterimPublicationAsync(
            identity,
            1,
            "interim-001",
            InterimPublicationKind.AwaitingUser,
            InterimPublicationReason.PlannerAwaitingUser,
            "interim-001",
            prompt,
            digest,
            "interim-prepare",
            TestContext.Current.CancellationToken);
        Assert.Equal(InterimPublicationStatus.Prepared, prepared.State!.InterimPublication!.Status);
        Assert.Equal(
            InterimPublicationReason.PlannerAwaitingUser,
            prepared.State.InterimPublication.Reason);
        Assert.Equal(
            prompt,
            await writer.ReadInterimPublicationTextAsync(
                identity,
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ClearInterimPublicationAsync(
            identity,
            prepared.State.Revision,
            "interim-001",
            InterimPublicationKind.AwaitingUser,
            digest,
            "not-displayed",
            "interim-clear-too-early",
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ChangeControlAsync(
            identity,
            prepared.State.Revision,
            TurnControlState.AwaitingUser,
            "question-not-yet-displayed",
            "interim-wait-too-early",
            TestContext.Current.CancellationToken));

        var committed = await writer.CommitInterimPublicationAsync(
            identity,
            prepared.State.Revision,
            "interim-001",
            InterimPublicationKind.AwaitingUser,
            digest,
            "interim-commit",
            TestContext.Current.CancellationToken);
        var waiting = await writer.ChangeControlAsync(
            identity,
            committed.State!.Revision,
            TurnControlState.AwaitingUser,
            "question-displayed",
            "interim-waiting",
            TestContext.Current.CancellationToken);
        var running = await writer.ChangeControlAsync(
            identity,
            waiting.State!.Revision,
            TurnControlState.Running,
            "explicit-resume",
            "interim-resume",
            TestContext.Current.CancellationToken);
        var cleared = await writer.ClearInterimPublicationAsync(
            identity,
            running.State!.Revision,
            "interim-001",
            InterimPublicationKind.AwaitingUser,
            digest,
            "superseded-by-steering",
            "interim-clear",
            TestContext.Current.CancellationToken);

        Assert.Null(cleared.State!.InterimPublication);
        Assert.Equal(TurnControlState.Running, cleared.State.Control);
        var journal = Assert.Single(
            Directory.GetFiles(directory.Path, "turn.journal.jsonl", SearchOption.AllDirectories));
        Assert.DoesNotContain(
            prompt,
            await File.ReadAllTextAsync(journal, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptedReadCall_RecoversExactArgumentsAndRequiresATerminalBeforeAnswer()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var call = AcceptedCall("call-recover", "read-file");
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        var accepted = await writer.RecordPlanningDecisionAcceptedAsync(
            identity,
            1,
            Digest("decision"),
            PlanningAcceptedActionKind.CallTool,
            call.CallId,
            call.ToolName,
            0,
            Digest("claims"),
            "accepted-call",
            call,
            workGraph: null,
            cancellationToken: TestContext.Current.CancellationToken);

        var recovered = await writer.ReadPendingAcceptedCallAsync(
            identity,
            TestContext.Current.CancellationToken);
        Assert.Equal(call.CallId, recovered.CallId);
        Assert.Equal(call.ArgumentsDigest, recovered.ArgumentsDigest);
        Assert.Equal("accepted-path", recovered.CanonicalArguments.GetProperty("path").GetString());
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.PrepareFinalPublicationAsync(
            identity,
            accepted.State!.Revision,
            "final-before-terminal",
            identity.AssistantMessageId,
            "unsafe",
            Digest("unsafe"),
            "final-before-terminal",
            TestContext.Current.CancellationToken));

        var interrupted = await writer.InterruptAcceptedCallAsync(
            identity,
            accepted.State!.Revision,
            call.CallId,
            "process-ended-before-invocation",
            "accepted-call-interrupted",
            TestContext.Current.CancellationToken);
        Assert.Null(interrupted.State!.PendingAcceptedCall);
    }

    [Fact]
    public async Task AcceptedEffectCall_RequiresMatchingPreparedIntentBeforeTerminalEvidence()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var validator = new ReferenceValidator();
        var call = AcceptedCall("call-effect", "write-file") with
        {
            ExecutionClass = AcceptedCallExecutionClass.PreparedEffectRequired,
            ReconcilerId = "file-write-reconciler"
        };
        using var writer = Writer(directory.Path, validator);
        await StartAsync(writer, identity);
        var accepted = await writer.RecordPlanningDecisionAcceptedAsync(
            identity,
            1,
            Digest("effect-decision"),
            PlanningAcceptedActionKind.CallTool,
            call.CallId,
            call.ToolName,
            0,
            Digest("effect-claims"),
            "accepted-effect",
            call,
            workGraph: null,
            cancellationToken: TestContext.Current.CancellationToken);
        var evidence = Evidence("effect-evidence", 1);
        validator.AcceptEvidence(evidence);

        await Assert.ThrowsAsync<InvalidDataException>(() => writer.RecordAcceptedCallEvidenceAsync(
            identity,
            accepted.State!.Revision,
            evidence,
            actionIdempotencyKey: null,
            callId: call.CallId,
            correlationKey: "effect-evidence-without-intent",
            cancellationToken: TestContext.Current.CancellationToken));

        var intent = Intent("effect-action", requiresApproval: false) with
        {
            WorkItemId = call.WorkItemId,
            ToolName = call.ToolName,
            CanonicalArgumentsDigest = call.ArgumentsDigest,
            ReconcilerId = call.ReconcilerId!,
            AcceptedCallId = call.CallId
        };
        var prepared = await writer.PrepareActionAsync(
            identity,
            accepted.State!.Revision,
            intent,
            "effect-intent",
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.InterruptAcceptedCallAsync(
            identity,
            prepared.State!.Revision,
            call.CallId,
            "unsafe-interruption",
            "effect-interrupted",
            TestContext.Current.CancellationToken));

        var referenced = await writer.RecordAcceptedCallEvidenceAsync(
            identity,
            prepared.State!.Revision,
            evidence,
            intent.IdempotencyKey,
            call.CallId,
            "effect-evidence",
            TestContext.Current.CancellationToken);
        Assert.Null(referenced.State!.PendingAcceptedCall);
        var committed = await writer.CommitActionAsync(
            identity,
            referenced.State.Revision,
            intent.IdempotencyKey,
            evidence.EvidenceId,
            evidence.Cursor,
            "effect-committed",
            TestContext.Current.CancellationToken);
        Assert.Empty(committed.State!.PendingActions);
    }

    [Fact]
    public async Task ProtectedOriginalRequestTamperingFailsClosedDuringRecovery()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        var protectedInput = Assert.Single(
            Directory.GetFiles(directory.Path, "*.turn-input", SearchOption.AllDirectories));
        var envelope = await File.ReadAllBytesAsync(
            protectedInput,
            TestContext.Current.CancellationToken);
        envelope[^1] ^= 0x01;
        await File.WriteAllBytesAsync(
            protectedInput,
            envelope,
            TestContext.Current.CancellationToken);

        var recovery = new TurnRecoveryService(writer, []);
        await Assert.ThrowsAsync<InvalidDataException>(() => recovery.RecoverAsync(
            identity,
            Bindings("initial"),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ActionCommit_RequiresPreparedIntentAndMatchingCommittedEvidence()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var validator = new ReferenceValidator();
        using var writer = Writer(directory.Path, validator);
        await StartAsync(writer, identity);
        var intent = Intent("action-one", requiresApproval: false);
        var prepared = await writer.PrepareActionAsync(
            identity,
            1,
            intent,
            "prepare-action",
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionIntentState.Prepared, prepared.State!.PendingActions.Single().State);

        await Assert.ThrowsAsync<InvalidDataException>(() => writer.CommitActionAsync(
            identity,
            2,
            intent.IdempotencyKey,
            "evidence-one",
            1,
            "commit-too-early",
            TestContext.Current.CancellationToken));

        var evidence = Evidence("evidence-one", 1);
        validator.AcceptEvidence(evidence);
        var recorded = await writer.RecordEvidenceAsync(
            identity,
            2,
            evidence,
            intent.IdempotencyKey,
            "record-evidence",
            TestContext.Current.CancellationToken);
        var committed = await writer.CommitActionAsync(
            identity,
            recorded.State!.Revision,
            intent.IdempotencyKey,
            evidence.EvidenceId,
            evidence.Cursor,
            "commit-action",
            TestContext.Current.CancellationToken);

        Assert.Empty(committed.State!.PendingActions);
        Assert.Equal(1, committed.State.EvidenceCursor);
        var duplicatePreparation = await writer.PrepareActionAsync(
            identity,
            committed.State.Revision,
            intent,
            "prepare-action-again",
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnTransitionWriteStatus.AlreadyRecorded, duplicatePreparation.Status);
        Assert.Equal(committed.State.Revision, duplicatePreparation.State!.Revision);
    }

    [Fact]
    public async Task ReferenceTransitions_FailClosedUntilTheSourceConfirmsCommit()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var validator = new ReferenceValidator();
        using var writer = Writer(directory.Path, validator);
        await StartAsync(writer, identity);
        var evidence = Evidence("evidence", 1);
        var workGraph = new CommittedWorkGraphReference(1, Digest("work-graph"));

        await Assert.ThrowsAsync<InvalidDataException>(() => writer.RecordEvidenceAsync(
            identity,
            1,
            evidence,
            actionIdempotencyKey: null,
            "uncommitted-evidence",
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(
            identity,
            1,
            new WorkGraphReferencedTransition("uncommitted-work", workGraph),
            TestContext.Current.CancellationToken));

        Assert.Equal(1, (await writer.ReadAsync(identity, TestContext.Current.CancellationToken))!.Revision);
    }

    [Fact]
    public async Task Recovery_AppliedIntentCommitsEvidenceWithoutRepeatingTheEffect()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var validator = new ReferenceValidator();
        var intent = Intent("applied-action", requiresApproval: false);
        using (var setup = Writer(directory.Path, validator))
        {
            await StartAsync(setup, identity);
            await setup.PrepareActionAsync(
                identity,
                1,
                intent,
                "prepare",
                TestContext.Current.CancellationToken);
        }

        var evidence = Evidence("applied-evidence", 1);
        validator.AcceptEvidence(evidence);
        var reconciler = new StaticActionReconciler(
            intent.ReconcilerId,
            ActionReconciliationResult.Applied("target-shows-applied", evidence));
        using var writer = Writer(directory.Path, validator);
        var recovery = new TurnRecoveryService(writer, [reconciler]);
        var result = await recovery.RecoverAsync(
            identity,
            Bindings("initial"),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnRecoveryStatus.Ready, result.Status);
        Assert.Empty(result.State!.PendingActions);
        Assert.Equal(1, result.State.EvidenceCursor);
        Assert.Equal(ActionReconciliationDisposition.Applied, Assert.Single(result.Actions).Disposition);
        Assert.Equal(1, reconciler.Calls);

        var second = await recovery.RecoverAsync(
            identity,
            Bindings("initial"),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnRecoveryStatus.Ready, second.Status);
        Assert.Empty(second.Actions);
        Assert.Equal(1, reconciler.Calls);
    }

    [Fact]
    public async Task Recovery_AppliedIntentAfterAcceptedCallTerminalUsesOrdinaryEvidence()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var validator = new ReferenceValidator();
        var acceptedCall = AcceptedCall("call-applied-after-terminal", "tool-name");
        var intent = Intent("applied-after-terminal", requiresApproval: false) with
        {
            WorkItemId = acceptedCall.WorkItemId,
            ToolName = acceptedCall.ToolName,
            CapabilityId = acceptedCall.CapabilityId!,
            CanonicalArgumentsDigest = acceptedCall.ArgumentsDigest,
            RegistryRevisionDigest = acceptedCall.RegistryRevisionDigest,
            AcceptedCallId = acceptedCall.CallId
        };
        acceptedCall = acceptedCall with
        {
            ExecutionClass = AcceptedCallExecutionClass.PreparedEffectRequired,
            ReconcilerId = intent.ReconcilerId
        };
        using (var setup = Writer(directory.Path, validator))
        {
            await StartAsync(setup, identity);
            var accepted = await setup.RecordPlanningDecisionAcceptedAsync(
                identity,
                expectedRevision: 1,
                Digest("applied-after-terminal-decision"),
                PlanningAcceptedActionKind.CallTool,
                acceptedCall.CallId,
                acceptedCall.ToolName,
                workGraphRevision: 0,
                materialClaimsDigest: Digest("applied-after-terminal-claims"),
                correlationKey: "applied-after-terminal-decision",
                acceptedCall,
                workGraph: null,
                cancellationToken: TestContext.Current.CancellationToken);
            var prepared = await setup.PrepareActionAsync(
                identity,
                accepted.State!.Revision,
                intent,
                "applied-after-terminal-prepare",
                TestContext.Current.CancellationToken);
            var terminalEvidence = Evidence("accepted-call-terminal", 1);
            validator.AcceptEvidence(terminalEvidence);
            var terminal = await setup.RecordAcceptedCallEvidenceAsync(
                identity,
                prepared.State!.Revision,
                terminalEvidence,
                intent.IdempotencyKey,
                acceptedCall.CallId,
                "applied-after-terminal-call-evidence",
                TestContext.Current.CancellationToken);
            Assert.Null(terminal.State!.PendingAcceptedCall);
            Assert.Single(terminal.State.PendingActions);
        }

        var recoveryEvidence = Evidence("applied-after-terminal-recovery", 2);
        validator.AcceptEvidence(recoveryEvidence);
        var reconciler = new StaticActionReconciler(
            intent.ReconcilerId,
            ActionReconciliationResult.Applied(
                "target-shows-applied-after-terminal",
                recoveryEvidence));
        using var writer = Writer(directory.Path, validator);
        var recovery = new TurnRecoveryService(writer, [reconciler]);
        var result = await recovery.RecoverAsync(
            identity,
            Bindings("initial"),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnRecoveryStatus.Ready, result.Status);
        Assert.Empty(result.State!.PendingActions);
        Assert.Null(result.State.PendingAcceptedCall);
        Assert.Equal(2, result.State.EvidenceCursor);
        Assert.Equal(ActionReconciliationDisposition.Applied, Assert.Single(result.Actions).Disposition);
        Assert.Equal(1, reconciler.Calls);
    }

    [Fact]
    public async Task Recovery_UnknownIntentWaitsForUserAndNeverBlindlyReplays()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var intent = Intent("unknown-action", requiresApproval: true);
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        await writer.PrepareActionAsync(
            identity,
            1,
            intent,
            "prepare",
            TestContext.Current.CancellationToken);
        var reconciler = new StaticActionReconciler(
            intent.ReconcilerId,
            ActionReconciliationResult.Unknown("provider-cannot-prove-state"));
        var recovery = new TurnRecoveryService(writer, [reconciler]);

        var result = await recovery.RecoverAsync(
            identity,
            Bindings("initial"),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnRecoveryStatus.StructuredResolutionRequired, result.Status);
        Assert.Equal(TurnControlState.Running, result.State!.Control);
        Assert.Equal(ActionIntentState.InDoubt, result.State.PendingActions.Single().State);
        var recovered = Assert.Single(result.Actions);
        Assert.Equal(ActionReconciliationDisposition.Unknown, recovered.Disposition);
        Assert.False(recovered.SafeToRetry);
        Assert.False(recovered.RequiresFreshApproval);
        Assert.Equal(1, reconciler.Calls);
        Assert.Equal(
            InterimPublicationKind.AwaitingUser,
            result.State.InterimPublication!.Kind);
        Assert.Equal(
            InterimPublicationReason.ActionReconciliationRequired,
            result.State.InterimPublication.Reason);
        Assert.Equal(intent.IdempotencyKey, result.State.InterimPublication.SubjectId);
        Assert.Equal(
            InterimPublicationStatus.Prepared,
            result.State.InterimPublication.Status);
        Assert.Equal(
            "Ali could not prove whether the interrupted action was applied. Check the target, then use the recovery buttons to confirm whether the action happened.",
            await writer.ReadInterimPublicationTextAsync(
                identity,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Recovery_AbsentApprovalBearingIntentRequiresFreshApproval()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var intent = Intent("absent-action", requiresApproval: true);
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        await writer.PrepareActionAsync(
            identity,
            1,
            intent,
            "prepare",
            TestContext.Current.CancellationToken);
        var reconciler = new StaticActionReconciler(
            intent.ReconcilerId,
            ActionReconciliationResult.Absent("target-proves-absent"));
        var recovery = new TurnRecoveryService(writer, [reconciler]);

        var result = await recovery.RecoverAsync(
            identity,
            Bindings("initial"),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);

        var recovered = Assert.Single(result.Actions);
        Assert.Equal(ActionReconciliationDisposition.Absent, recovered.Disposition);
        Assert.False(recovered.SafeToRetry);
        Assert.True(recovered.RequiresFreshApproval);
        Assert.Empty(result.State!.PendingActions);
    }

    [Fact]
    public async Task Recovery_RequiresExplicitRequestAndExactEnvironmentBindings()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        var recovery = new TurnRecoveryService(writer, []);

        var notExplicit = await recovery.RecoverAsync(
            identity,
            Bindings("initial"),
            explicitlyRequested: false,
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnRecoveryStatus.ExplicitRequestRequired, notExplicit.Status);
        Assert.Null(notExplicit.OriginalRequest);

        var changed = await recovery.RecoverAsync(
            identity,
            Bindings("changed"),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnRecoveryStatus.RevalidationRequired, changed.Status);
        Assert.Equal(9, changed.ChangedBindings.Count);
        Assert.Equal(1, changed.State!.Revision);
        Assert.Null(changed.OriginalRequest);

        var ready = await recovery.RecoverAsync(
            identity,
            Bindings("initial"),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnRecoveryStatus.Ready, ready.Status);
        Assert.Equal("Original request", ready.OriginalRequest);

        var otherUser = new TurnIdentity("other-user", identity.ConversationId, identity.AssistantMessageId);
        var isolated = await recovery.RecoverAsync(
            otherUser,
            Bindings("initial"),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnRecoveryStatus.NotFound, isolated.Status);
    }

    [Fact]
    public async Task FinalPublicationReceipt_IsIdempotentAndCannotChangeAnswer()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        const string exactAnswer = "exact visible answer";
        var answerDigest = Digest(exactAnswer);
        var prepared = await writer.PrepareFinalPublicationAsync(
            identity,
            1,
            "publication-one",
            identity.AssistantMessageId,
            exactAnswer,
            answerDigest,
            "publication-prepare",
            TestContext.Current.CancellationToken);
        var committed = await writer.CommitFinalPublicationAsync(
            identity,
            prepared.State!.Revision,
            "publication-one",
            identity.AssistantMessageId,
            answerDigest,
            "publication-commit",
            TestContext.Current.CancellationToken);
        var retry = await writer.CommitFinalPublicationAsync(
            identity,
            expectedRevision: 1,
            "publication-one",
            identity.AssistantMessageId,
            answerDigest,
            "publication-commit-retry",
            TestContext.Current.CancellationToken);

        Assert.Equal(FinalPublicationStatus.Committed, committed.State!.FinalPublication!.Status);
        Assert.Equal(TurnTransitionWriteStatus.AlreadyRecorded, retry.Status);
        Assert.Equal(committed.State.Revision, retry.State!.Revision);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.CommitFinalPublicationAsync(
            identity,
            committed.State.Revision,
            "publication-one",
            identity.AssistantMessageId,
            Digest("different answer"),
            "publication-rebound",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FinalPublicationAnswer_IsProtectedAndTamperingFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        const string exactAnswer = "Exact private answer token=do-not-journal.";
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        var prepared = await writer.PrepareFinalPublicationAsync(
            identity,
            expectedRevision: 1,
            "publication-protected",
            identity.AssistantMessageId,
            exactAnswer,
            Digest(exactAnswer),
            "publication-protected-prepare",
            TestContext.Current.CancellationToken);

        Assert.Equal(
            exactAnswer,
            await writer.ReadFinalPublicationAnswerAsync(
                identity,
                TestContext.Current.CancellationToken));
        var publication = Assert.IsType<FinalPublicationState>(prepared.State!.FinalPublication);
        Assert.Equal(publication.AnswerDigest, publication.AnswerPayload.ContentDigest);
        var journal = Assert.Single(
            Directory.GetFiles(directory.Path, "turn.journal.jsonl", SearchOption.AllDirectories));
        Assert.DoesNotContain(
            exactAnswer,
            await File.ReadAllTextAsync(journal, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        var payload = Path.Combine(
            directory.Path,
            "turns",
            identity.StorageKey,
            "inputs",
            publication.AnswerPayload.PayloadReference + ".turn-input");
        var envelope = await File.ReadAllBytesAsync(
            payload,
            TestContext.Current.CancellationToken);
        envelope[^1] ^= 0x01;
        await File.WriteAllBytesAsync(
            payload,
            envelope,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            writer.ReadFinalPublicationAnswerAsync(
                identity,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FinalPublicationPrepare_RejectsDigestMismatchBeforeWritingPayloadOrTransition()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);

        await Assert.ThrowsAsync<InvalidDataException>(() => writer.PrepareFinalPublicationAsync(
            identity,
            expectedRevision: 1,
            "publication-mismatch",
            identity.AssistantMessageId,
            "exact answer",
            Digest("different answer"),
            "publication-mismatch-prepare",
            TestContext.Current.CancellationToken));

        var state = await writer.ReadAsync(identity, TestContext.Current.CancellationToken);
        Assert.Equal(1, state!.Revision);
        Assert.Null(state.FinalPublication);
        Assert.Single(
            Directory.GetFiles(directory.Path, "*.turn-input", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CrashAfterProtectedAnswerBeforePreparedHead_RetryReusesOnePayload()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        const string exactAnswer = "Answer survives a torn publication prepare.";
        using (var setup = Writer(directory.Path))
        {
            await StartAsync(setup, identity);
        }

        var injected = false;
        using (var faulted = new TurnTransitionWriter(
                   directory.Path,
                   "profile",
                   referenceValidator: null,
                   boundary =>
                   {
                       if (!injected && boundary == TurnJournalCommitBoundary.CommitMarkerFlushed)
                       {
                           injected = true;
                           throw new InjectedJournalFault();
                       }
                   }))
        {
            await Assert.ThrowsAsync<InjectedJournalFault>(() => faulted.PrepareFinalPublicationAsync(
                identity,
                expectedRevision: 1,
                "publication-torn",
                identity.AssistantMessageId,
                exactAnswer,
                Digest(exactAnswer),
                "publication-torn-prepare",
                TestContext.Current.CancellationToken));
        }

        Assert.Equal(
            2,
            Directory.GetFiles(directory.Path, "*.turn-input", SearchOption.AllDirectories).Length);
        using var recovered = Writer(directory.Path);
        var retried = await recovered.PrepareFinalPublicationAsync(
            identity,
            expectedRevision: 1,
            "publication-torn",
            identity.AssistantMessageId,
            exactAnswer,
            Digest(exactAnswer),
            "publication-torn-prepare",
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnTransitionWriteStatus.Committed, retried.Status);
        Assert.Equal(
            exactAnswer,
            await recovered.ReadFinalPublicationAnswerAsync(
                identity,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            2,
            Directory.GetFiles(directory.Path, "*.turn-input", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Recovery_ReconcilesAnInDoubtFinalPublicationWithoutDuplicatingIt()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        const string exactAnswer = "answer";
        await writer.PrepareFinalPublicationAsync(
            identity,
            1,
            "publication",
            identity.AssistantMessageId,
            exactAnswer,
            Digest(exactAnswer),
            "prepare-publication",
            TestContext.Current.CancellationToken);
        var publicationReconciler = new StaticPublicationReconciler(
            new PublicationReconciliationResult(
                PublicationReconciliationDisposition.Applied,
                "message-and-hash-exist"));
        var recovery = new TurnRecoveryService(writer, [], publicationReconciler);

        var result = await recovery.RecoverAsync(
            identity,
            Bindings("initial"),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnRecoveryStatus.Ready, result.Status);
        Assert.Equal(FinalPublicationStatus.Committed, result.State!.FinalPublication!.Status);
        Assert.Equal(TurnControlState.Completed, result.State.Control);
        Assert.Equal(
            exactAnswer,
            await writer.ReadFinalPublicationAnswerAsync(
                identity,
                TestContext.Current.CancellationToken));
        Assert.Equal(PublicationReconciliationDisposition.Applied, result.Publication!.Disposition);
        Assert.False(result.Publication.SafeToPublishIdempotently);
        Assert.Equal(1, publicationReconciler.Calls);

        var repeated = await recovery.RecoverAsync(
            identity,
            Bindings("initial"),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnControlState.Completed, repeated.State!.Control);
        Assert.Equal(1, publicationReconciler.Calls);
    }

    [Fact]
    public async Task FinalPublicationAndPendingActions_CannotCoexistInEitherOrder()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var intent = Intent("publication-with-pending-action", requiresApproval: true);
        const string exactAnswer = "An answer that must not complete the pending action.";
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        var preparedAction = await writer.PrepareActionAsync(
            identity,
            expectedRevision: 1,
            intent,
            "prepare-pending-action",
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.PrepareFinalPublicationAsync(
            identity,
            preparedAction.State!.Revision,
            "publication-with-pending-action",
            identity.AssistantMessageId,
            exactAnswer,
            Digest(exactAnswer),
            "prepare-publication-with-pending-action",
            TestContext.Current.CancellationToken));

        var secondIdentity = new TurnIdentity("user", "conversation-two", "assistant-two");
        await StartAsync(writer, secondIdentity);
        var preparedPublication = await writer.PrepareFinalPublicationAsync(
            secondIdentity,
            expectedRevision: 1,
            "publication-before-action",
            secondIdentity.AssistantMessageId,
            exactAnswer,
            Digest(exactAnswer),
            "prepare-publication-before-action",
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.PrepareActionAsync(
            secondIdentity,
            preparedPublication.State!.Revision,
            Intent("action-after-publication", requiresApproval: false),
            "prepare-action-after-publication",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PlanningDecision_CannotAdvanceWhilePreparedActionIsUnresolved()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        var prepared = await writer.PrepareActionAsync(
            identity,
            expectedRevision: 1,
            Intent("decision-while-action-pending", requiresApproval: false),
            "prepare-before-new-decision",
            TestContext.Current.CancellationToken);
        var call = AcceptedCall("call-after-pending-action", "safe-tool-id");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            writer.RecordPlanningDecisionAcceptedAsync(
                identity,
                prepared.State!.Revision,
                Digest("decision-after-pending-action"),
                PlanningAcceptedActionKind.CallTool,
                call.CallId,
                call.ToolName,
                workGraphRevision: 0,
                materialClaimsDigest: Digest("claims-after-pending-action"),
                correlationKey: "decision-after-pending-action",
                call,
                workGraph: null,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Recovery_AppliedPublicationCompletesPausedTurn()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        const string exactAnswer = "The answer was already rendered.";
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        var prepared = await writer.PrepareFinalPublicationAsync(
            identity,
            expectedRevision: 1,
            "publication-interrupted-control",
            identity.AssistantMessageId,
            exactAnswer,
            Digest(exactAnswer),
            "publication-interrupted-control-prepare",
            TestContext.Current.CancellationToken);
        await writer.ChangeControlAsync(
            identity,
            prepared.State!.Revision,
            TurnControlState.Paused,
            "test-interruption",
            "publication-interrupted-control-change",
            TestContext.Current.CancellationToken);
        var publicationReconciler = new StaticPublicationReconciler(
            new PublicationReconciliationResult(
                PublicationReconciliationDisposition.Applied,
                "message-and-hash-exist"));

        var result = await new TurnRecoveryService(writer, [], publicationReconciler).RecoverAsync(
            identity,
            Bindings("initial"),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnControlState.Completed, result.State!.Control);
        Assert.Equal(FinalPublicationStatus.Committed, result.State.FinalPublication!.Status);
        Assert.Equal(1, publicationReconciler.Calls);
    }

    [Theory]
    [InlineData(PublicationReconciliationDisposition.Applied, true)]
    [InlineData(PublicationReconciliationDisposition.Absent, false)]
    public async Task Recovery_FinishesCancellationWithoutCompletingOrRepublishing(
        PublicationReconciliationDisposition disposition,
        bool publicationWasApplied)
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        const string exactAnswer = "A prepared answer interrupted by cancellation.";
        using var writer = Writer(directory.Path);
        await StartAsync(writer, identity);
        var prepared = await writer.PrepareFinalPublicationAsync(
            identity,
            expectedRevision: 1,
            "publication-cancelled",
            identity.AssistantMessageId,
            exactAnswer,
            Digest(exactAnswer),
            "publication-cancelled-prepare",
            TestContext.Current.CancellationToken);
        await writer.ChangeControlAsync(
            identity,
            prepared.State!.Revision,
            TurnControlState.CancelRequested,
            "user-cancelled",
            "publication-cancel-requested",
            TestContext.Current.CancellationToken);
        var publicationReconciler = new StaticPublicationReconciler(
            new PublicationReconciliationResult(
                disposition,
                publicationWasApplied ? "message-and-hash-exist" : "message-absent"));

        var result = await new TurnRecoveryService(writer, [], publicationReconciler)
            .RecoverAsync(
                identity,
                Bindings("initial"),
                explicitlyRequested: true,
                TestContext.Current.CancellationToken);

        Assert.Equal(TurnRecoveryStatus.Ready, result.Status);
        Assert.Equal(TurnControlState.Cancelled, result.State!.Control);
        Assert.Equal(
            publicationWasApplied ? FinalPublicationStatus.Committed : null,
            result.State.FinalPublication?.Status);
        Assert.Equal(1, publicationReconciler.Calls);
        var replay = await writer.ReplayAsync(identity, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(
            replay.Entries.Select(entry => entry.Transition).OfType<ControlChangedTransition>(),
            transition => transition.Control == TurnControlState.Completed);
        Assert.Equal(
            publicationWasApplied ? 0 : 1,
            replay.Entries.Select(entry => entry.Transition)
                .OfType<FinalPublicationAbandonedTransition>()
                .Count());
    }

    [Fact]
    public async Task CrashAfterRecoveredPublicationCommit_CompletesOnNextRecoveryWithoutReconciliation()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        const string exactAnswer = "Already rendered exact answer.";
        using (var setup = Writer(directory.Path))
        {
            await StartAsync(setup, identity);
            var prepared = await setup.PrepareFinalPublicationAsync(
                identity,
                expectedRevision: 1,
                "publication-recovery-crash",
                identity.AssistantMessageId,
                exactAnswer,
                Digest(exactAnswer),
                "publication-recovery-crash-prepare",
                TestContext.Current.CancellationToken);
            await setup.WriteAsync(
                identity,
                prepared.State!.Revision,
                new FinalPublicationMarkedInDoubtTransition(
                    "publication-recovery-crash-in-doubt",
                    "publication-recovery-crash",
                    identity.AssistantMessageId,
                    Digest(exactAnswer)),
                TestContext.Current.CancellationToken);
        }

        var injected = false;
        var firstReconciler = new StaticPublicationReconciler(
            new PublicationReconciliationResult(
                PublicationReconciliationDisposition.Applied,
                "message-and-hash-exist"));
        using (var faulted = new TurnTransitionWriter(
                   directory.Path,
                   "profile",
                   referenceValidator: null,
                   boundary =>
                   {
                       if (!injected && boundary == TurnJournalCommitBoundary.HeadCommitted)
                       {
                           injected = true;
                           throw new InjectedJournalFault();
                       }
                   }))
        {
            var recovery = new TurnRecoveryService(faulted, [], firstReconciler);
            await Assert.ThrowsAsync<InjectedJournalFault>(() => recovery.RecoverAsync(
                identity,
                Bindings("initial"),
                explicitlyRequested: true,
                TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, firstReconciler.Calls);
        using var reopened = Writer(directory.Path);
        var afterCrash = await reopened.ReadAsync(identity, TestContext.Current.CancellationToken);
        Assert.Equal(FinalPublicationStatus.Committed, afterCrash!.FinalPublication!.Status);
        Assert.Equal(TurnControlState.Running, afterCrash.Control);
        var unusedReconciler = new StaticPublicationReconciler(
            new PublicationReconciliationResult(
                PublicationReconciliationDisposition.Unknown,
                "must-not-be-called"));
        var resumed = await new TurnRecoveryService(reopened, [], unusedReconciler).RecoverAsync(
            identity,
            Bindings("initial"),
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnControlState.Completed, resumed.State!.Control);
        Assert.Equal(FinalPublicationStatus.Committed, resumed.State.FinalPublication!.Status);
        Assert.Equal(0, unusedReconciler.Calls);
        var replay = await reopened.ReplayAsync(identity, TestContext.Current.CancellationToken);
        Assert.Single(replay.Entries.Select(entry => entry.Transition)
            .OfType<FinalPublicationCommittedTransition>());
        Assert.Single(
            replay.Entries.Select(entry => entry.Transition)
                .OfType<ControlChangedTransition>(),
            transition => transition.Control == TurnControlState.Completed);
    }

    [Fact]
    public async Task TornTailIsDiscarded_ButCommittedTamperingFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using (var setup = Writer(directory.Path))
        {
            await StartAsync(setup, identity);
        }

        var injected = false;
        using (var faulted = new TurnTransitionWriter(
                   directory.Path,
                   "profile",
                   referenceValidator: null,
                   boundary =>
                   {
                       if (!injected && boundary == TurnJournalCommitBoundary.CommitMarkerFlushed)
                       {
                           injected = true;
                           throw new InjectedJournalFault();
                       }
                   }))
        {
            await Assert.ThrowsAsync<InjectedJournalFault>(() => faulted.AppendSteeringAsync(
                identity,
                1,
                "uncommitted steering",
                "torn-transition",
                TestContext.Current.CancellationToken));
        }

        using (var recovered = Writer(directory.Path))
        {
            var replay = await recovered.ReplayAsync(identity, TestContext.Current.CancellationToken);
            Assert.True(replay.RecoveredUncommittedTail);
            Assert.Equal(1, replay.State!.Revision);
            var committed = await recovered.AppendSteeringAsync(
                identity,
                1,
                "committed steering",
                "torn-transition",
                TestContext.Current.CancellationToken);
            Assert.Equal(TurnTransitionWriteStatus.Committed, committed.Status);
        }

        var journal = Assert.Single(
            Directory.GetFiles(directory.Path, "turn.journal.jsonl", SearchOption.AllDirectories));
        var text = await File.ReadAllTextAsync(journal, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("uncommitted steering", text, StringComparison.Ordinal);
        Assert.DoesNotContain("committed steering", text, StringComparison.Ordinal);
        const string recordMacMarker = "\"recordMac\":\"";
        var recordMacStart = text.IndexOf(recordMacMarker, StringComparison.Ordinal)
                             + recordMacMarker.Length;
        Assert.True(recordMacStart >= recordMacMarker.Length);
        var tampered = text.ToCharArray();
        tampered[recordMacStart] = tampered[recordMacStart] == '0' ? '1' : '0';
        await File.WriteAllTextAsync(
            journal,
            new string(tampered),
            TestContext.Current.CancellationToken);
        using var reopened = Writer(directory.Path);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => reopened.ReplayAsync(identity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CrashAfterAuthenticatedHead_RetryReturnsTheOneCommittedTransition()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var injected = false;
        using (var faulted = new TurnTransitionWriter(
                   directory.Path,
                   "profile",
                   referenceValidator: null,
                   boundary =>
                   {
                       if (!injected && boundary == TurnJournalCommitBoundary.HeadCommitted)
                       {
                           injected = true;
                           throw new InjectedJournalFault();
                       }
                   }))
        {
            await Assert.ThrowsAsync<InjectedJournalFault>(() => faulted.StartAsync(
                identity,
                "request",
                Bindings("initial"),
                "start-once",
                TestContext.Current.CancellationToken));
        }

        using var recovered = Writer(directory.Path);
        var retry = await recovered.StartAsync(
            identity,
            "request",
            Bindings("initial"),
            "start-once",
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnTransitionWriteStatus.AlreadyRecorded, retry.Status);
        Assert.Equal(1, retry.State!.Revision);
        Assert.Single((await recovered.ReplayAsync(identity, TestContext.Current.CancellationToken)).Entries);
    }

    [Fact]
    public async Task FiveHundredAdvancingStepsUseLinearJournalReadsAndOneLogFile()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var writer = Writer(directory.Path);
        var state = (await StartAsync(writer, identity)).State!;
        for (var index = 0; index < 500; index++)
        {
            var appended = await writer.AppendSteeringAsync(
                identity,
                state.Revision,
                "steering-" + index,
                "correlation-" + index,
                TestContext.Current.CancellationToken);
            Assert.Equal(TurnTransitionWriteStatus.Committed, appended.Status);
            state = appended.State!;
        }

        Assert.Equal(501, state.Revision);
        Assert.Equal(501, state.SteeringCursor);
        Assert.Empty(state.PendingActions);
        Assert.True(writer.GetDiagnostics(identity).RecordsReadFromDisk <= 501);
        Assert.Single(
            Directory.GetFiles(directory.Path, "turn.journal.jsonl", SearchOption.AllDirectories));

        using var independentReader = Writer(directory.Path);
        var replay = await independentReader.ReplayAsync(identity, TestContext.Current.CancellationToken);
        Assert.Equal(501, replay.Entries.Count);
        Assert.Equal(501, independentReader.GetDiagnostics(identity).RecordsReadFromDisk);
    }

    private static TurnIdentity Identity() =>
        new("user", "conversation", "assistant-message");

    private static TurnTransitionWriter Writer(
        string path,
        ITurnCommittedReferenceValidator? validator = null) =>
        new(path, "profile", validator);

    private static Task<TurnTransitionWriteResult> StartAsync(
        TurnTransitionWriter writer,
        TurnIdentity identity) =>
        writer.StartAsync(
            identity,
            "Original request",
            Bindings("initial"),
            "turn-start",
            TestContext.Current.CancellationToken);

    private static TurnRuntimeBindings Bindings(string suffix) =>
        new(
            Digest("profile-" + suffix),
            Digest("runtime-" + suffix),
            Digest("model-" + suffix),
            Digest("settings-" + suffix),
            Digest("capabilities-" + suffix),
            Digest("permissions-" + suffix),
            Digest("mcp-" + suffix),
            Digest("attachments-" + suffix),
            Digest("artifacts-" + suffix));

    private static PreparedActionIntent Intent(string key, bool requiresApproval) =>
        new(
            key,
            "work-item",
            "tool-name",
            "capability-id",
            Digest("arguments-" + key),
            Digest("target-" + key),
            Digest("permission-" + key),
            Digest("registry"),
            "reconciler-" + key,
            requiresApproval);

    private static AcceptedCallRecoveryPayload AcceptedCall(string callId, string toolName)
    {
        var arguments = JsonSerializer.SerializeToElement(new { path = "accepted-path" });
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(arguments);
        return new AcceptedCallRecoveryPayload(
            callId,
            "work-item",
            toolName,
            "capability-id",
            Digest("schema"),
            Digest("registry"),
            arguments,
            TurnStateIntegrity.Digest(bytes),
            Digest("action-" + callId),
            Digest("effect-" + callId),
            AcceptedCallExecutionClass.NonMutating,
            ReconcilerId: null,
            RequiresApproval: false);
    }

    private static CommittedEvidenceReference Evidence(string id, long cursor) =>
        new(id, cursor, Digest("evidence-" + id));

    private static string Digest(string value) =>
        TurnStateIntegrity.Digest(Encoding.UTF8.GetBytes(value));

    private sealed class ReferenceValidator : ITurnCommittedReferenceValidator
    {
        private readonly HashSet<CommittedEvidenceReference> _evidence = [];
        private readonly HashSet<CommittedWorkGraphReference> _workGraphs = [];

        public void AcceptEvidence(CommittedEvidenceReference evidence) => _evidence.Add(evidence);

        public void AcceptWorkGraph(CommittedWorkGraphReference workGraph) => _workGraphs.Add(workGraph);

        public ValueTask<bool> IsEvidenceCommittedAsync(
            TurnIdentity identity,
            CommittedEvidenceReference evidence,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_evidence.Contains(evidence));

        public ValueTask<bool> IsWorkGraphCommittedAsync(
            TurnIdentity identity,
            CommittedWorkGraphReference workGraph,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_workGraphs.Contains(workGraph));
    }

    private sealed class StaticActionReconciler(
        string reconcilerId,
        ActionReconciliationResult result) : ITurnActionReconciler
    {
        private int _calls;

        public string ReconcilerId { get; } = reconcilerId;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<ActionReconciliationResult> ReconcileAsync(
            TurnIdentity identity,
            PreparedActionIntent intent,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StaticPublicationReconciler(
        PublicationReconciliationResult result) : ITurnPublicationReconciler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<PublicationReconciliationResult> ReconcileAsync(
            TurnIdentity identity,
            FinalPublicationState publication,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InjectedJournalFault : Exception;

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Ali-TurnState-" + Guid.NewGuid().ToString("N"));
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
