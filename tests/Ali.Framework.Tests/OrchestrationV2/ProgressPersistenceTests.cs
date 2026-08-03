using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class ProgressPersistenceTests
{
    [Fact]
    public async Task ProgressAttempt_RoundTripsAndExactCorrelationIsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var transition = Transition("progress-attempt-1");

        using (var writer = Writer(directory.Path))
        {
            var started = await StartAsync(writer, identity);
            var committed = await writer.WriteAsync(
                identity,
                started.State!.Revision,
                transition,
                TestContext.Current.CancellationToken);
            var retry = await writer.WriteAsync(
                identity,
                started.State.Revision,
                transition with { },
                TestContext.Current.CancellationToken);

            Assert.Equal(TurnTransitionWriteStatus.Committed, committed.Status);
            Assert.Equal(TurnTransitionWriteStatus.AlreadyRecorded, retry.Status);
            Assert.Equal(committed.State, retry.State);
            Assert.Equal(2, retry.State!.Revision);
        }

        using var reopened = Writer(directory.Path);
        var replay = await reopened.ReplayAsync(
            identity,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, replay.Entries.Count);
        Assert.Equal(transition, Assert.IsType<ProgressAttemptRecordedTransition>(
            replay.Entries[1].Transition));
        Assert.Equal(2, replay.State!.Revision);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.WriteAsync(
            identity,
            replay.State.Revision,
            transition with { MateriallyAdvanced = true },
            TestContext.Current.CancellationToken));
        Assert.Equal(
            2,
            (await reopened.ReplayAsync(identity, TestContext.Current.CancellationToken))
            .Entries.Count);
    }

    [Fact]
    public async Task ProgressAttempt_AdvancesOnlyRevisionAndDoesNotBloatCompactState()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var writer = Writer(directory.Path);
        var started = await StartAsync(writer, identity);
        var before = started.State!;

        var recorded = await writer.WriteAsync(
            identity,
            before.Revision,
            Transition("progress-attempt-1"),
            TestContext.Current.CancellationToken);
        var after = recorded.State!;

        Assert.Equal(TurnTransitionWriteStatus.Committed, recorded.Status);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal(after.Revision, after.JournalCursor);
        Assert.Equal(before.OriginalRequest, after.OriginalRequest);
        Assert.Equal(before.Bindings, after.Bindings);
        Assert.Equal(before.SteeringCursor, after.SteeringCursor);
        Assert.Equal(before.EvidenceCursor, after.EvidenceCursor);
        Assert.Equal(before.WorkGraphRevision, after.WorkGraphRevision);
        Assert.Equal(before.WorkGraphReference, after.WorkGraphReference);
        Assert.Equal(before.Control, after.Control);
        Assert.Equal(before.PendingActions, after.PendingActions);
        Assert.Equal(before.FinalPublication, after.FinalPublication);
        Assert.DoesNotContain(
            typeof(TurnState).GetProperties(),
            property => property.Name.Contains("Progress", StringComparison.Ordinal)
                        || property.Name.Contains("Attempt", StringComparison.Ordinal)
                        || property.PropertyType == typeof(ProgressHistory));
    }

    [Fact]
    public async Task ReplayedProgressHistory_BlocksExactAndEquivalentNonAdvancingWorkAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var originalAction = Action(tool: "tool-a");
        var effect = Effect();
        var noEffect = NoEffect("target-unchanged");
        var before = Vector(0, 0, "v1");
        var after = Vector(1, 1, "v1");
        var assessed = ProgressDetector.Assess(
            ProgressHistory.Empty,
            originalAction,
            effect,
            noEffect,
            before,
            after);
        var fingerprint = Assert.Single(assessed.History.Fingerprints);
        Assert.False(fingerprint.MateriallyAdvanced);
        var transition = new ProgressAttemptRecordedTransition(
            "progress-attempt-1",
            fingerprint.ActionFingerprint,
            fingerprint.EffectFingerprint,
            fingerprint.NoEffectFingerprint,
            fingerprint.BeforeMaterialFingerprint,
            fingerprint.AfterMaterialFingerprint,
            fingerprint.MateriallyAdvanced);

        using (var writer = Writer(directory.Path))
        {
            var started = await StartAsync(writer, identity);
            var persisted = await writer.WriteAsync(
                identity,
                started.State!.Revision,
                transition,
                TestContext.Current.CancellationToken);
            var state = persisted.State!;
            for (var index = 0; index < ProgressHistory.MaximumRetainedAttempts + 1; index++)
            {
                var suffix = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                // Keep the same material state while evicting the original diagnostic
                // fingerprint. A materially advancing record intentionally clears the
                // current-state no-progress indexes and therefore cannot exercise this case.
                persisted = await writer.WriteAsync(
                    identity,
                    state.Revision,
                    new ProgressAttemptRecordedTransition(
                        "same-state-progress-" + suffix,
                        fingerprint.ActionFingerprint,
                        Digest("effect-" + suffix),
                        NoEffectFingerprint: null,
                        fingerprint.BeforeMaterialFingerprint,
                        fingerprint.AfterMaterialFingerprint,
                        MateriallyAdvanced: false),
                    TestContext.Current.CancellationToken);
                state = persisted.State!;
            }
        }

        ProgressHistory restored = ProgressHistory.Empty;
        using (var reopened = Writer(directory.Path))
        {
            var replay = await reopened.ReplayAsync(
                identity,
                TestContext.Current.CancellationToken);
            foreach (var persisted in replay.Entries
                         .Select(entry => entry.Transition)
                         .OfType<ProgressAttemptRecordedTransition>())
            {
                restored = restored.Restore(new ProgressAttemptFingerprint(
                    persisted.ActionFingerprint,
                    persisted.EffectFingerprint,
                    persisted.NoEffectFingerprint,
                    persisted.BeforeMaterialFingerprint,
                    persisted.AfterMaterialFingerprint,
                    persisted.MateriallyAdvanced));
            }
        }

        Assert.Empty(restored.Attempts);
        Assert.Equal(ProgressHistory.MaximumRetainedAttempts, restored.Fingerprints.Length);
        Assert.Equal(1, restored.NonAdvancingActionIdentityCount);
        Assert.Equal(1, restored.EquivalentNoEffectIdentityCount);
        var exactRetry = ProgressDetector.AssessPlannedAction(
            restored,
            originalAction,
            after);
        Assert.False(exactRetry.CanExecute);
        Assert.Equal(ProgressReason.ExactActionAlreadyNonAdvancing, exactRetry.Reason);

        var equivalentRetry = ProgressDetector.Assess(
            restored,
            Action(tool: "tool-b"),
            effect,
            NoEffect("target-unchanged", denied: true),
            after,
            Vector(2, 2, "v1"));
        Assert.Equal(ProgressDisposition.ReopenDecomposition, equivalentRetry.Disposition);
        Assert.Equal(ProgressReason.EquivalentNoEffectRepeated, equivalentRetry.Reason);
        Assert.False(equivalentRetry.ChangedMaterialState);
    }

    [Fact]
    public void InMemoryHistory_IsBoundedWithoutLimitingAdvancingWork()
    {
        var history = ProgressHistory.Empty;
        var current = Vector(0, 0, "v0");
        var totalActions = ProgressHistory.MaximumRetainedAttempts + 256;

        for (var index = 0; index < totalActions; index++)
        {
            var action = Action(
                tool: "tool-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(ProgressDetector.AssessPlannedAction(history, action, current).CanExecute);
            var next = Vector(index + 1, index + 1, "v" + (index + 1));
            history = ProgressDetector.Assess(
                history,
                action,
                Effect(),
                Applied("write-confirmed"),
                current,
                next).History;
            current = next;
        }

        Assert.Equal(ProgressHistory.MaximumRetainedAttempts, history.Attempts.Length);
        Assert.Equal(ProgressHistory.MaximumRetainedAttempts, history.Fingerprints.Length);
        Assert.Equal(totalActions, current.EvidenceCursor);
    }

    private static ProgressAttemptRecordedTransition Transition(string correlationKey)
    {
        var assessed = ProgressDetector.Assess(
            ProgressHistory.Empty,
            Action(),
            Effect(),
            NoEffect("target-unchanged"),
            Vector(0, 0, "v1"),
            Vector(1, 1, "v1"));
        var attempt = Assert.Single(assessed.History.Fingerprints);
        return new ProgressAttemptRecordedTransition(
            correlationKey,
            attempt.ActionFingerprint,
            attempt.EffectFingerprint,
            attempt.NoEffectFingerprint,
            attempt.BeforeMaterialFingerprint,
            attempt.AfterMaterialFingerprint,
            attempt.MateriallyAdvanced);
    }

    private static ActionIdentity Action(string tool = "tool-a") =>
        ActionIdentity.Create(
            "registry-1",
            "work-1",
            tool,
            Encoding.UTF8.GetBytes("{\"path\":\"a.cs\"}"),
            new Dictionary<string, string> { ["a.cs"] = "v1" },
            "write-allowed",
            "compiler-ready");

    private static EffectIdentity Effect() =>
        EffectIdentity.Create(
            "file-content-update",
            new Dictionary<string, string> { ["path"] = "a.cs" });

    private static EffectOutcomeIdentity NoEffect(string code, bool denied = false) =>
        EffectOutcomeIdentity.Create(
            denied
                ? ToolInvocationOutcome.Denied("write-denied")
                : ToolInvocationOutcome.Returned("{\"changed\":false}"u8, reportedSuccess: false),
            EffectResultKind.NoEffect,
            code,
            new Dictionary<string, string> { ["target"] = "a.cs" });

    private static EffectOutcomeIdentity Applied(string code) =>
        EffectOutcomeIdentity.Create(
            ToolInvocationOutcome.Returned("{\"changed\":true}"u8, reportedSuccess: true),
            EffectResultKind.Applied,
            code,
            new Dictionary<string, string> { ["target"] = "a.cs" });

    private static ProgressVector Vector(
        long evidenceCursor,
        long graphRevision,
        string artifactVersion) =>
        ProgressVector.Create(
            evidenceCursor,
            graphRevision,
            satisfiedOutcomeIds: ["outcome-1"],
            artifactVersions: new Dictionary<string, string> { ["a.cs"] = artifactVersion },
            diagnosticStates: new Dictionary<string, string> { ["compiler"] = "clear" },
            testStates: new Dictionary<string, string> { ["focused"] = "passed" },
            permissionStates: new Dictionary<string, string> { ["workspace"] = "write-allowed" },
            dependencyStates: new Dictionary<string, string> { ["compiler"] = "ready" });

    private static TurnIdentity Identity() =>
        new("user", "conversation", "assistant-message");

    private static TurnTransitionWriter Writer(string path) =>
        new(path, "profile");

    private static Task<TurnTransitionWriteResult> StartAsync(
        TurnTransitionWriter writer,
        TurnIdentity identity) =>
        writer.StartAsync(
            identity,
            "Original request",
            Bindings(),
            "turn-start",
            TestContext.Current.CancellationToken);

    private static TurnRuntimeBindings Bindings() =>
        new(
            Digest("profile"),
            Digest("runtime"),
            Digest("model"),
            Digest("settings"),
            Digest("capabilities"),
            Digest("permissions"),
            Digest("mcp"),
            Digest("attachments"),
            Digest("artifacts"));

    private static string Digest(string value) =>
        TurnStateIntegrity.Digest(Encoding.UTF8.GetBytes(value));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Ali-ProgressPersistence-" + Guid.NewGuid().ToString("N"));
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
