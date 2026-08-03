using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Work;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class WorkProgressTests
{
    [Fact]
    public void ActionIdentity_CanonicalizesJsonNumbersPropertiesAndTargetMaps()
    {
        var first = Action(
            arguments: "{\"b\":2.0,\"a\":{\"z\":1e0,\"y\":true}}",
            targets: new Dictionary<string, string>
            {
                ["z-file"] = "v2",
                ["a-file"] = "v1"
            });
        var second = Action(
            arguments: " { \"a\" : { \"y\" : true, \"z\" : 1 }, \"b\" : 2 } ",
            targets: new Dictionary<string, string>
            {
                ["a-file"] = "v1",
                ["z-file"] = "v2"
            });

        Assert.Equal(first.ArgumentsDigest, second.ArgumentsDigest);
        Assert.Equal(first.TargetVersionsDigest, second.TargetVersionsDigest);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void ActionIdentity_RejectsAmbiguousDuplicateJsonProperties()
    {
        Assert.Throws<JsonException>(() => Action(arguments: "{\"path\":\"a\",\"path\":\"b\"}"));
    }

    [Fact]
    public void ActionIdentity_ChangesForEveryExecutionRelevantDimension()
    {
        var baseline = Action();
        var variants = new[]
        {
            Action(registry: "registry-2"),
            Action(workItem: "work-2"),
            Action(tool: "tool-b"),
            Action(arguments: "{\"path\":\"b.cs\"}"),
            Action(targets: new Dictionary<string, string> { ["a.cs"] = "v2" }),
            Action(permission: "write-denied"),
            Action(dependency: "compiler-unavailable")
        };

        Assert.All(variants, variant => Assert.NotEqual(baseline.Fingerprint, variant.Fingerprint));
        Assert.Equal(variants.Length, variants.Select(variant => variant.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void CursorAndGraphRevisionMovementAloneAreNotMaterialProgress()
    {
        var before = Vector(evidenceCursor: 10, graphRevision: 4, artifactVersion: "v1");
        var after = Vector(evidenceCursor: 11, graphRevision: 5, artifactVersion: "v1");

        var assessed = ProgressDetector.Assess(
            ProgressHistory.Empty,
            Action(),
            Effect(),
            NoEffect("target-unchanged"),
            before,
            after);

        Assert.Equal(ProgressDisposition.ReplanRequired, assessed.Disposition);
        Assert.Equal(ProgressReason.NoValidatedStateChange, assessed.Reason);
        Assert.True(assessed.AddedEvidence);
        Assert.True(assessed.ChangedWorkGraphRevision);
        Assert.False(assessed.ChangedMaterialState);
    }

    [Fact]
    public void ExactNonAdvancingActionIsBlockedBeforeAnotherInvocationDespiteCursorAdvance()
    {
        var action = Action();
        var first = ProgressDetector.Assess(
            ProgressHistory.Empty,
            action,
            Effect(),
            NoEffect("target-unchanged"),
            Vector(0, 0, "v1"),
            Vector(1, 1, "v1"));

        var planned = ProgressDetector.AssessPlannedAction(
            first.History,
            action,
            Vector(1, 1, "v1"));

        Assert.False(planned.CanExecute);
        Assert.Equal(ProgressReason.ExactActionAlreadyNonAdvancing, planned.Reason);
        Assert.Equal(action.Fingerprint, planned.PriorActionFingerprint);
    }

    [Fact]
    public void AlternatingActionsWithTheSameEquivalentNoEffectReopenDecomposition()
    {
        var effect = Effect();
        var first = ProgressDetector.Assess(
            ProgressHistory.Empty,
            Action(tool: "tool-a"),
            effect,
            NoEffect("target-unchanged"),
            Vector(0, 0, "v1"),
            Vector(1, 1, "v1"));

        var second = ProgressDetector.Assess(
            first.History,
            Action(tool: "tool-b"),
            effect,
            NoEffect("target-unchanged", denied: true),
            Vector(1, 1, "v1"),
            Vector(2, 2, "v1"));

        Assert.Equal(ProgressDisposition.ReopenDecomposition, second.Disposition);
        Assert.Equal(ProgressReason.EquivalentNoEffectRepeated, second.Reason);
        Assert.False(second.ChangedMaterialState);
    }

    [Fact]
    public void NormalizedNoEffectIdentityIgnoresRawTimestampNoise()
    {
        var firstInvocation = ToolInvocationOutcome.Returned(
            "{\"timestamp\":\"2026-08-02T01:00:00Z\",\"changed\":false}"u8,
            reportedSuccess: false);
        var secondInvocation = ToolInvocationOutcome.Returned(
            "{\"timestamp\":\"2026-08-02T01:00:01Z\",\"changed\":false}"u8,
            reportedSuccess: false);
        var stableFields = new Dictionary<string, string> { ["target"] = "a.cs" };

        var first = EffectOutcomeIdentity.Create(
            firstInvocation,
            EffectResultKind.NoEffect,
            "target-unchanged",
            stableFields);
        var second = EffectOutcomeIdentity.Create(
            secondInvocation,
            EffectResultKind.NoEffect,
            "target-unchanged",
            stableFields);

        Assert.NotEqual(firstInvocation.Fingerprint(), secondInvocation.Fingerprint());
        Assert.Equal(first.OutcomeFingerprint, second.OutcomeFingerprint);
        Assert.Equal(first.NoEffectFingerprint, second.NoEffectFingerprint);
    }

    [Fact]
    public void ValidatedMaterialStateChangeCountsAsProgress()
    {
        var assessed = ProgressDetector.Assess(
            ProgressHistory.Empty,
            Action(),
            Effect(),
            Applied("write-confirmed"),
            Vector(0, 0, "v1"),
            Vector(1, 1, "v2"));

        Assert.Equal(ProgressDisposition.Advanced, assessed.Disposition);
        Assert.Equal(ProgressReason.MaterialStateChanged, assessed.Reason);
        Assert.True(assessed.ChangedMaterialState);
    }

    [Fact]
    public void ReportedEffectWithoutValidatedStateChangeRequiresReplanning()
    {
        var assessed = ProgressDetector.Assess(
            ProgressHistory.Empty,
            Action(),
            Effect(),
            Applied("write-confirmed"),
            Vector(0, 0, "v1"),
            Vector(1, 1, "v1"));

        Assert.Equal(ProgressDisposition.ReplanRequired, assessed.Disposition);
        Assert.Equal(ProgressReason.ReportedEffectWithoutValidatedStateChange, assessed.Reason);
    }

    [Fact]
    public void RegistryTargetPermissionOrDependencyChangeAllowsAPreviouslyBlockedActionShape()
    {
        var action = Action();
        var current = Vector(1, 1, "v1");
        var first = ProgressDetector.Assess(
            ProgressHistory.Empty,
            action,
            Effect(),
            NoEffect("target-unchanged"),
            Vector(0, 0, "v1"),
            current);

        var changedIdentities = new[]
        {
            Action(registry: "registry-2"),
            Action(targets: new Dictionary<string, string> { ["a.cs"] = "v2" }),
            Action(permission: "write-denied"),
            Action(dependency: "compiler-unavailable")
        };

        Assert.All(
            changedIdentities,
            changed => Assert.True(
                ProgressDetector.AssessPlannedAction(first.History, changed, current).CanExecute));
    }

    [Fact]
    public void FiveHundredUniqueAdvancingActionsAreAllowedWithoutAStepCap()
    {
        var history = ProgressHistory.Empty;
        var current = Vector(0, 0, "v0");

        for (var index = 0; index < 500; index++)
        {
            var action = Action(
                workItem: $"work-{index:D3}",
                arguments: $"{{\"index\":{index}}}",
                targets: new Dictionary<string, string>
                {
                    ["a.cs"] = $"v{index}"
                });
            Assert.True(ProgressDetector.AssessPlannedAction(history, action, current).CanExecute);

            var next = Vector(index + 1, index + 1, $"v{index + 1}");
            var assessed = ProgressDetector.Assess(
                history,
                action,
                Effect(),
                Applied("write-confirmed"),
                current,
                next);

            Assert.Equal(ProgressDisposition.Advanced, assessed.Disposition);
            history = assessed.History;
            current = next;
        }

        Assert.Equal(500, history.Attempts.Length);
        Assert.Equal(0, history.NonAdvancingActionIdentityCount);
        Assert.Equal(0, history.EquivalentNoEffectIdentityCount);
    }

    [Fact]
    public void DistinctNonAdvancingIdentityIndexFailsClosedAtItsPerStateBound()
    {
        var history = ProgressHistory.Empty;
        var current = Vector(0, 0, "v1");

        for (var index = 0;
             index < ProgressHistory.MaximumDistinctNoProgressIdentitiesPerMaterialState;
             index++)
        {
            var action = Action(
                workItem: $"work-{index:D3}",
                arguments: $"{{\"index\":{index}}}");
            Assert.True(ProgressDetector.AssessPlannedAction(history, action, current).CanExecute);

            var assessed = ProgressDetector.Assess(
                history,
                action,
                Effect(),
                NoEffect($"unchanged-{index:D3}"),
                current,
                Vector(index + 1, index + 1, "v1"));
            Assert.Equal(ProgressDisposition.ReplanRequired, assessed.Disposition);
            history = assessed.History;
            current = Vector(index + 1, index + 1, "v1");
        }

        var blocked = ProgressDetector.AssessPlannedAction(
            history,
            Action(workItem: "work-over-limit", arguments: "{\"index\":999}"),
            current);

        Assert.False(blocked.CanExecute);
        Assert.Equal(ProgressReason.DistinctNoProgressLimitReached, blocked.Reason);
        Assert.Equal(
            ProgressHistory.MaximumDistinctNoProgressIdentitiesPerMaterialState,
            history.NonAdvancingActionIdentityCount);
        Assert.True(
            history.EquivalentNoEffectIdentityCount
            <= ProgressHistory.MaximumDistinctNoProgressIdentitiesPerMaterialState);
    }

    [Fact]
    public void MaterialStateChangeClearsNoProgressIndexAndPermitsFurtherWork()
    {
        var firstAction = Action(workItem: "stalled-work");
        var stalled = ProgressDetector.Assess(
            ProgressHistory.Empty,
            firstAction,
            Effect(),
            NoEffect("target-unchanged"),
            Vector(0, 0, "v1"),
            Vector(1, 1, "v1"));

        var advancingAction = Action(
            workItem: "different-work",
            targets: new Dictionary<string, string> { ["a.cs"] = "v1" });
        var advanced = ProgressDetector.Assess(
            stalled.History,
            advancingAction,
            Effect(),
            Applied("write-confirmed"),
            Vector(1, 1, "v1"),
            Vector(2, 2, "v2"));

        Assert.Equal(ProgressDisposition.Advanced, advanced.Disposition);
        Assert.Equal(0, advanced.History.NonAdvancingActionIdentityCount);
        Assert.Equal(0, advanced.History.EquivalentNoEffectIdentityCount);
        Assert.True(
            ProgressDetector.AssessPlannedAction(
                advanced.History,
                firstAction,
                Vector(2, 2, "v2"))
            .CanExecute);
    }

    private static ActionIdentity Action(
        string registry = "registry-1",
        string workItem = "work-1",
        string tool = "tool-a",
        string arguments = "{\"path\":\"a.cs\"}",
        IReadOnlyDictionary<string, string>? targets = null,
        string permission = "write-allowed",
        string dependency = "compiler-ready") =>
        ActionIdentity.Create(
            registry,
            workItem,
            tool,
            Encoding.UTF8.GetBytes(arguments),
            targets ?? new Dictionary<string, string> { ["a.cs"] = "v1" },
            permission,
            dependency);

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
}
