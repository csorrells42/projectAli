using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Execution;
using Ali.Modules.Orchestration.State;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class AliDurableInvocationStoreTests
{
    private const string ToolName = "test_exact_effect";
    private const string CapabilityId = "test-capability";
    private const string ReconcilerId = "test-reconciler";
    private const string RootBinding = "test-root-binding";

    [Fact]
    public async Task PreparedWithoutStart_IsDurablyAbsent()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var store = Store(directory.Path);
        var (plan, _) = PlanAndArguments();

        await store.PrepareAsync(plan, TestContext.Current.CancellationToken);

        var snapshot = await store.LoadAsync(
            plan.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationState.Prepared, snapshot.State);
        Assert.Null(snapshot.Receipt);

        var reconciler = new AliDurableInvocationReconciler(
            store,
            plan.ExactIdentity);
        var recovered = await reconciler.ReconcileAsync(
            plan.Id,
            Digest("unused-authorization"),
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationRecoveryDisposition.Absent, recovered.Disposition);
        Assert.Equal("invocation-prepared-not-started", recovered.OutcomeCode);
    }

    [Fact]
    public async Task ExactOneUseGrant_StartsThenCompletesWithAuthorizationReceipt()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var store = Store(directory.Path);
        var (plan, arguments) = PlanAndArguments();
        await store.PrepareAsync(plan, TestContext.Current.CancellationToken);
        var scope = new AliExecutionInvocationScope(Grant(plan));
        var participant = new StoreCompletionParticipant(store, plan.Id);

        await using (var activation = scope.Enter(arguments))
        {
            Assert.False(AliExecutionGrantContext.TryRegisterCurrentCompletionParticipant(
                ToolName,
                CapabilityId,
                ReconcilerId,
                plan.Id,
                plan.RootBinding,
                participant));

            var started = await AliDurableInvocationGrantConsumer.ConsumeAndStartAsync(
                store,
                plan,
                TestContext.Current.CancellationToken);
            Assert.Equal(AliDurableInvocationState.Started, started.State);
            var expectedAuthorization = Assert.IsType<AliDurableInvocationReceipt>(
                started.Receipt).AuthorizationDigest;
            Assert.Equal(
                AliExecutionAuthorizationDigest.Compute(
                    AliDurableInvocationStore.AuthorizationDomain,
                    Grant(plan)),
                expectedAuthorization);
            Assert.False(AliExecutionGrantContext.TryConsumeCurrent(
                ToolName,
                CapabilityId,
                ReconcilerId,
                plan.Id,
                plan.RootBinding,
                out _));
            Assert.False(AliExecutionGrantContext.TryRegisterCurrentCompletionParticipant(
                ToolName + "-other",
                CapabilityId,
                ReconcilerId,
                plan.Id,
                plan.RootBinding,
                participant));
            Assert.False(AliExecutionGrantContext.TryRegisterCurrentCompletionParticipant(
                ToolName,
                CapabilityId,
                ReconcilerId,
                Guid.NewGuid().ToString("N"),
                plan.RootBinding,
                participant));
            Assert.True(AliExecutionGrantContext.TryRegisterCurrentCompletionParticipant(
                ToolName,
                CapabilityId,
                ReconcilerId,
                plan.Id,
                plan.RootBinding,
                participant));
            Assert.False(AliExecutionGrantContext.TryRegisterCurrentCompletionParticipant(
                ToolName,
                CapabilityId,
                ReconcilerId,
                plan.Id,
                plan.RootBinding,
                new StoreCompletionParticipant(store, plan.Id)));

            var startedRecovery = await new AliDurableInvocationReconciler(
                    store,
                    plan.ExactIdentity)
                .ReconcileAsync(
                    plan.Id,
                    expectedAuthorization,
                    TestContext.Current.CancellationToken);
            Assert.Equal(
                AliDurableInvocationRecoveryDisposition.Unknown,
                startedRecovery.Disposition);
            Assert.Equal(
                "invocation-started-no-terminal-receipt",
                startedRecovery.OutcomeCode);

            await activation.CompleteAsync(
                JsonSerializer.SerializeToElement(new { success = true }),
                CancellationToken.None);
        }

        Assert.Equal(1, participant.CompletionCount);
        Assert.Equal(0, participant.FailureCount);
        Assert.Equal(0, participant.InDoubtCount);
        var completed = await store.LoadAsync(
            plan.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationState.Completed, completed.State);
        Assert.Equal(2, completed.Receipt!.Revision);
        Assert.Equal("completed", completed.Receipt.StableOutcomeCode);
        Assert.Equal(Digest("test-result"), completed.Receipt.ResultDigest);
        Assert.Equal(
            AliExecutionAuthorizationDigest.Compute(
                AliDurableInvocationStore.AuthorizationDomain,
                Grant(plan)),
            completed.Receipt.AuthorizationDigest);

        var reconciler = new AliDurableInvocationReconciler(store, plan.ExactIdentity);
        var applied = await reconciler.ReconcileAsync(
            plan.Id,
            completed.Receipt.AuthorizationDigest,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationRecoveryDisposition.Applied, applied.Disposition);
        Assert.Equal("completed", applied.OutcomeCode);
        var mismatched = await reconciler.ReconcileAsync(
            plan.Id,
            Digest("wrong-authorization"),
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationRecoveryDisposition.Unknown, mismatched.Disposition);
        Assert.Equal("invocation-authorization-mismatch", mismatched.OutcomeCode);
    }

    [Fact]
    public async Task IdentityOnlyConsumer_StartsTheExactCurrentPreparedInvocation()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var store = Store(directory.Path);
        var (plan, arguments) = PlanAndArguments("identity-only-consumer");
        await store.PrepareAsync(plan, TestContext.Current.CancellationToken);

        Assert.False(AliExecutionGrantContext.HasCurrentActiveGrant);
        await using (new AliExecutionInvocationScope(Grant(plan)).Enter(arguments))
        {
            Assert.True(AliExecutionGrantContext.HasCurrentActiveGrant);
            Assert.True(AliExecutionGrantContext.TryGetCurrentBinding(
                ToolName,
                CapabilityId,
                ReconcilerId,
                out var preparationIdentity,
                out var rootBinding));
            Assert.Equal(plan.Id, preparationIdentity);
            Assert.Equal(plan.RootBinding, rootBinding);
            Assert.False(AliExecutionGrantContext.TryGetCurrentBinding(
                ToolName,
                CapabilityId + "-other",
                ReconcilerId,
                out _,
                out _));

            var started = await AliDurableInvocationGrantConsumer
                .ConsumeCurrentAndStartAsync(
                    store,
                    plan.ExactIdentity,
                    TestContext.Current.CancellationToken);

            Assert.Equal(plan.Id, started.Plan.Id);
            Assert.Equal(plan.RootBinding, started.Plan.RootBinding);
            Assert.Equal(AliDurableInvocationState.Started, started.State);
            Assert.Equal(1, started.Receipt!.Revision);
            Assert.False(AliExecutionGrantContext.TryConsumeCurrent(
                ToolName,
                CapabilityId,
                ReconcilerId,
                out _));
        }

        Assert.False(AliExecutionGrantContext.HasCurrentActiveGrant);
        Assert.False(AliExecutionGrantContext.TryGetCurrentBinding(
            ToolName,
            CapabilityId,
            ReconcilerId,
            out _,
            out _));
    }

    [Fact]
    public async Task CompletionLifecycle_RecordsFailureAndAbandonmentExactlyOnce()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var store = Store(directory.Path);

        var (failedPlan, failedArguments) = PlanAndArguments("failed");
        await store.PrepareAsync(failedPlan, TestContext.Current.CancellationToken);
        var failedParticipant = new StoreCompletionParticipant(store, failedPlan.Id);
        await using (var activation =
                     new AliExecutionInvocationScope(Grant(failedPlan)).Enter(failedArguments))
        {
            await AliDurableInvocationGrantConsumer.ConsumeAndStartAsync(
                store,
                failedPlan,
                TestContext.Current.CancellationToken);
            Assert.True(AliExecutionGrantContext.TryRegisterCurrentCompletionParticipant(
                ToolName,
                CapabilityId,
                ReconcilerId,
                failedPlan.Id,
                failedPlan.RootBinding,
                failedParticipant));
            await activation.FailAsync(
                new InvalidOperationException("inner invocation failed"),
                CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await activation.CompleteAsync("late-success", CancellationToken.None));
        }

        var failed = await store.LoadAsync(
            failedPlan.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationState.Failed, failed.State);
        Assert.Equal("inner-invocation-failed", failed.Receipt!.FailureCode);
        Assert.Equal(1, failedParticipant.FailureCount);
        Assert.Equal(0, failedParticipant.InDoubtCount);
        var failedWrongAuthorization = await new AliDurableInvocationReconciler(
                store,
                failedPlan.ExactIdentity)
            .ReconcileAsync(
                failedPlan.Id,
                Digest("wrong-failed-authorization"),
                TestContext.Current.CancellationToken);
        Assert.Equal(
            AliDurableInvocationRecoveryDisposition.Unknown,
            failedWrongAuthorization.Disposition);
        Assert.Equal(
            "invocation-authorization-mismatch",
            failedWrongAuthorization.OutcomeCode);

        var (abandonedPlan, abandonedArguments) = PlanAndArguments("abandoned");
        await store.PrepareAsync(abandonedPlan, TestContext.Current.CancellationToken);
        var abandonedParticipant = new StoreCompletionParticipant(store, abandonedPlan.Id);
        await using (new AliExecutionInvocationScope(Grant(abandonedPlan))
                         .Enter(abandonedArguments))
        {
            await AliDurableInvocationGrantConsumer.ConsumeAndStartAsync(
                store,
                abandonedPlan,
                TestContext.Current.CancellationToken);
            Assert.True(AliExecutionGrantContext.TryRegisterCurrentCompletionParticipant(
                ToolName,
                CapabilityId,
                ReconcilerId,
                abandonedPlan.Id,
                abandonedPlan.RootBinding,
                abandonedParticipant));
        }

        var abandoned = await store.LoadAsync(
            abandonedPlan.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationState.InDoubt, abandoned.State);
        Assert.Equal(
            "invocation-activation-disposed-without-terminal",
            abandoned.Receipt!.FailureCode);
        Assert.Equal(1, abandonedParticipant.InDoubtCount);
        var abandonedWrongAuthorization = await new AliDurableInvocationReconciler(
                store,
                abandonedPlan.ExactIdentity)
            .ReconcileAsync(
                abandonedPlan.Id,
                Digest("wrong-abandoned-authorization"),
                TestContext.Current.CancellationToken);
        Assert.Equal(
            AliDurableInvocationRecoveryDisposition.Unknown,
            abandonedWrongAuthorization.Disposition);
        Assert.Equal(
            "invocation-authorization-mismatch",
            abandonedWrongAuthorization.OutcomeCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(
            abandonedPlan.Id,
            abandoned.Receipt.Revision,
            "late-completion",
            Digest("late-result"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SuccessCannotBeSignaledBeforeExactGrantConsumption()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var store = Store(directory.Path);
        var (plan, arguments) = PlanAndArguments("unconsumed");
        await store.PrepareAsync(plan, TestContext.Current.CancellationToken);

        await using (var activation =
                     new AliExecutionInvocationScope(Grant(plan)).Enter(arguments))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await activation.CompleteAsync("not-authorized", CancellationToken.None));
        }

        var snapshot = await store.LoadAsync(
            plan.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationState.Prepared, snapshot.State);
        Assert.Null(snapshot.Receipt);
    }

    [Fact]
    public async Task AsyncActivationDisposal_RestoresTheExactOuterGrantFrame()
    {
        var (outerPlan, outerArguments) = PlanAndArguments("outer-scope");
        var (innerPlan, innerArguments) = PlanAndArguments("inner-scope");

        await using (new AliExecutionInvocationScope(Grant(outerPlan))
                         .Enter(outerArguments))
        {
            await using (new AliExecutionInvocationScope(Grant(innerPlan))
                             .Enter(innerArguments))
            {
                Assert.True(AliExecutionGrantContext.TryConsumeCurrent(
                    innerPlan.ToolName,
                    innerPlan.CapabilityId,
                    innerPlan.ReconcilerId,
                    innerPlan.Id,
                    innerPlan.RootBinding,
                    out _));
            }

            Assert.True(AliExecutionGrantContext.TryConsumeCurrent(
                outerPlan.ToolName,
                outerPlan.CapabilityId,
                outerPlan.ReconcilerId,
                outerPlan.Id,
                outerPlan.RootBinding,
                out _));
        }
    }

    [Fact]
    public async Task TerminalReceiptLoss_RemainsStartedUnknownInsteadOfRegressingToPrepared()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var store = Store(directory.Path);
        var (plan, arguments) = PlanAndArguments("receipt-loss");
        await store.PrepareAsync(plan, TestContext.Current.CancellationToken);
        var participant = new StoreCompletionParticipant(store, plan.Id);
        await using (var activation =
                     new AliExecutionInvocationScope(Grant(plan)).Enter(arguments))
        {
            await AliDurableInvocationGrantConsumer.ConsumeAndStartAsync(
                store,
                plan,
                TestContext.Current.CancellationToken);
            Assert.True(AliExecutionGrantContext.TryRegisterCurrentCompletionParticipant(
                ToolName,
                CapabilityId,
                ReconcilerId,
                plan.Id,
                plan.RootBinding,
                participant));
            await activation.CompleteAsync("completed", CancellationToken.None);
        }

        var completed = await store.LoadAsync(
            plan.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationState.Completed, completed.State);
        var authorizationDigest = completed.Receipt!.AuthorizationDigest;
        File.Delete(Path.Combine(
            directory.Path,
            plan.Id + ".invocation-receipt.protected"));

        var afterLoss = await store.LoadAsync(
            plan.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationState.Started, afterLoss.State);
        Assert.Equal(1, afterLoss.Receipt!.Revision);
        Assert.Equal(authorizationDigest, afterLoss.Receipt.AuthorizationDigest);
        var recovered = await new AliDurableInvocationReconciler(
                store,
                plan.ExactIdentity)
            .ReconcileAsync(
                plan.Id,
                authorizationDigest,
                TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationRecoveryDisposition.Unknown, recovered.Disposition);
        Assert.Equal("invocation-started-no-terminal-receipt", recovered.OutcomeCode);
    }

    [Fact]
    public async Task ValidStartedReceiptReplay_AfterCompletionCannotRegressOrRestartInvocation()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var store = Store(directory.Path);
        var (plan, arguments) = PlanAndArguments("receipt-replay");
        await store.PrepareAsync(plan, TestContext.Current.CancellationToken);
        var receiptPath = Path.Combine(
            directory.Path,
            plan.Id + ".invocation-receipt.protected");
        var participant = new StoreCompletionParticipant(store, plan.Id);
        byte[] validStartedReceipt;
        await using (var activation =
                     new AliExecutionInvocationScope(Grant(plan)).Enter(arguments))
        {
            await AliDurableInvocationGrantConsumer.ConsumeAndStartAsync(
                store,
                plan,
                TestContext.Current.CancellationToken);
            validStartedReceipt = await File.ReadAllBytesAsync(
                receiptPath,
                TestContext.Current.CancellationToken);
            Assert.True(AliExecutionGrantContext.TryRegisterCurrentCompletionParticipant(
                ToolName,
                CapabilityId,
                ReconcilerId,
                plan.Id,
                plan.RootBinding,
                participant));
            await activation.CompleteAsync("completed", CancellationToken.None);
        }

        Assert.Equal(
            AliDurableInvocationState.Completed,
            (await store.LoadAsync(plan.Id, TestContext.Current.CancellationToken)).State);
        await File.WriteAllBytesAsync(
            receiptPath,
            validStartedReceipt,
            TestContext.Current.CancellationToken);

        var replayed = await store.LoadAsync(
            plan.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationState.Started, replayed.State);
        Assert.Equal(1, replayed.Receipt!.Revision);
        var recovery = await new AliDurableInvocationReconciler(
                store,
                plan.ExactIdentity)
            .ReconcileAsync(
                plan.Id,
                replayed.Receipt.AuthorizationDigest,
                TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationRecoveryDisposition.Unknown, recovery.Disposition);

        await using (new AliExecutionInvocationScope(Grant(plan)).Enter(arguments))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AliDurableInvocationGrantConsumer.ConsumeAndStartAsync(
                    store,
                    plan,
                    TestContext.Current.CancellationToken));
        }
        Assert.Equal(
            AliDurableInvocationState.Started,
            (await store.LoadAsync(plan.Id, TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task StartedWithoutTerminal_IsUnknownUnlessExactDomainReconcilerProvesState()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var store = Store(directory.Path);
        var (plan, arguments) = PlanAndArguments();
        await store.PrepareAsync(plan, TestContext.Current.CancellationToken);
        AliDurableInvocationSnapshot started;
        await using (new AliExecutionInvocationScope(Grant(plan)).Enter(arguments))
        {
            started = await AliDurableInvocationGrantConsumer.ConsumeAndStartAsync(
                store,
                plan,
                TestContext.Current.CancellationToken);
        }

        var authorizationDigest = started.Receipt!.AuthorizationDigest;
        var exact = new RecordingDomainReconciler(plan.ExactIdentity);
        Assert.Throws<ArgumentException>(() => new AliDurableInvocationReconciler(
            store,
            plan.ExactIdentity,
            new RecordingDomainReconciler(new AliExactExecutionAdapterIdentity(
                ToolName + "-other",
                CapabilityId,
                ReconcilerId))));
        var reconciler = new AliDurableInvocationReconciler(
            store,
            plan.ExactIdentity,
            exact);

        var wrongAuthorization = await reconciler.ReconcileAsync(
            plan.Id,
            Digest("wrong-authorization"),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            AliDurableInvocationRecoveryDisposition.Unknown,
            wrongAuthorization.Disposition);
        Assert.Equal("invocation-authorization-mismatch", wrongAuthorization.OutcomeCode);
        Assert.Equal(0, exact.CallCount);

        var proven = await reconciler.ReconcileAsync(
            plan.Id,
            authorizationDigest,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationRecoveryDisposition.Applied, proven.Disposition);
        Assert.Equal("domain-proved-applied", proven.OutcomeCode);
        Assert.Equal(1, exact.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedPlanAndReceiptTampering_FailsClosed(bool tamperReceipt)
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var store = Store(directory.Path);
        var (plan, arguments) = PlanAndArguments(tamperReceipt ? "receipt" : "plan");
        await store.PrepareAsync(plan, TestContext.Current.CancellationToken);
        if (tamperReceipt)
        {
            await using (new AliExecutionInvocationScope(Grant(plan)).Enter(arguments))
            {
                await AliDurableInvocationGrantConsumer.ConsumeAndStartAsync(
                    store,
                    plan,
                    TestContext.Current.CancellationToken);
            }
        }

        var artifactPath = Path.Combine(
            directory.Path,
            plan.Id + (tamperReceipt
                ? ".invocation-receipt.protected"
                : ".invocation-plan.protected"));
        var bytes = await File.ReadAllBytesAsync(
            artifactPath,
            TestContext.Current.CancellationToken);
        bytes[bytes.Length / 2] ^= 0x5A;
        await File.WriteAllBytesAsync(
            artifactPath,
            bytes,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(
            plan.Id,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HeldNamespaceSpine_BlocksRootRenameDuringPublication()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var root = Path.Combine(directory.Path, "held-root");
        var movedRoot = Path.Combine(directory.Path, "interposed-root");
        Exception? renameFailure = null;
        var injected = 0;
        var store = Store(root, (checkpoint, _) =>
        {
            if (checkpoint != AliDurableInvocationStoreCheckpoint.NamespaceAcquired
                || Interlocked.Exchange(ref injected, 1) != 0)
            {
                return;
            }

            try
            {
                Directory.Move(root, movedRoot);
            }
            catch (Exception exception)
            {
                renameFailure = exception;
            }
        });
        var (plan, _) = PlanAndArguments("held-root");

        await store.PrepareAsync(plan, TestContext.Current.CancellationToken);

        Assert.NotNull(renameFailure);
        Assert.IsAssignableFrom<IOException>(renameFailure);
        Assert.True(Directory.Exists(root));
        Assert.False(Directory.Exists(movedRoot));
        Assert.Equal(
            AliDurableInvocationState.Prepared,
            (await store.LoadAsync(
                plan.Id,
                TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task SameStoreInstance_RejectsRootReplacementBetweenOperations()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var root = Path.Combine(directory.Path, "bound-root");
        var displaced = Path.Combine(directory.Path, "displaced-bound-root");
        var store = Store(root);
        var (plan, _) = PlanAndArguments("root-replacement");
        await store.PrepareAsync(plan, TestContext.Current.CancellationToken);

        Directory.Move(root, displaced);
        Directory.CreateDirectory(root);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(
            plan.Id,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PlanLeafReplacementAfterIdentityCheck_FailsClosedWithoutOverwrite()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var root = Path.Combine(directory.Path, "leaf-replacement-root");
        var originalStore = Store(root);
        var (plan, arguments) = PlanAndArguments("leaf-replacement");
        await originalStore.PrepareAsync(plan, TestContext.Current.CancellationToken);
        var planPath = Path.Combine(
            root,
            plan.Id + ".invocation-plan.protected");
        var displacedPath = Path.Combine(root, "displaced-plan.protected");
        var attackerBytes = Encoding.UTF8.GetBytes("single-link-interposition");
        var injected = 0;
        var interposedStore = Store(root, (checkpoint, artifactKind) =>
        {
            if (checkpoint != AliDurableInvocationStoreCheckpoint.DestinationIdentityChecked
                || !string.Equals(artifactKind, "plan", StringComparison.Ordinal)
                || Interlocked.Exchange(ref injected, 1) != 0)
            {
                return;
            }

            File.Move(planPath, displacedPath);
            File.WriteAllBytes(planPath, attackerBytes);
        });

        await using (new AliExecutionInvocationScope(Grant(plan)).Enter(arguments))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                AliDurableInvocationGrantConsumer.ConsumeAndStartAsync(
                    interposedStore,
                    plan,
                    TestContext.Current.CancellationToken));
        }

        Assert.Equal(attackerBytes, await File.ReadAllBytesAsync(
            planPath,
            TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(
            root,
            ".invocations.replacement-journal.protected")));
        Assert.Contains(
            Directory.EnumerateFiles(root),
            path => Path.GetFileName(path).EndsWith(
                ".previous",
                StringComparison.Ordinal));
        Assert.Contains(
            Directory.EnumerateFiles(root),
            path => Path.GetFileName(path).EndsWith(
                ".new",
                StringComparison.Ordinal));
        Assert.False(File.Exists(displacedPath));
        File.Delete(planPath);
        Assert.Equal(
            AliDurableInvocationState.Started,
            (await Store(root).LoadAsync(
                plan.Id,
                TestContext.Current.CancellationToken)).State);
        Assert.False(File.Exists(Path.Combine(
            root,
            ".invocations.replacement-journal.protected")));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(root),
            path => Path.GetFileName(path).EndsWith(
                ".previous",
                StringComparison.Ordinal)
                || Path.GetFileName(path).EndsWith(
                    ".new",
                    StringComparison.Ordinal));
    }

    [Theory]
    [InlineData((int)AliDurableInvocationStoreCheckpoint.DestinationDisplaced)]
    [InlineData((int)AliDurableInvocationStoreCheckpoint.BeforeNoReplacePublication)]
    public async Task InjectedLeafAfterDisplacementOrBeforeNoReplace_IsNeverOverwritten(
        int injectionCheckpointValue)
    {
        var injectionCheckpoint = (AliDurableInvocationStoreCheckpoint)injectionCheckpointValue;
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var root = Path.Combine(
            directory.Path,
            "no-replace-" + injectionCheckpoint);
        var initialStore = Store(root);
        var (plan, arguments) = PlanAndArguments(
            "no-replace-" + injectionCheckpoint);
        await initialStore.PrepareAsync(plan, TestContext.Current.CancellationToken);
        var planPath = Path.Combine(
            root,
            plan.Id + ".invocation-plan.protected");
        var attackerBytes = Encoding.UTF8.GetBytes(
            "must-not-overwrite-" + injectionCheckpoint);
        var injected = 0;
        var interposedStore = Store(root, (checkpoint, artifactKind) =>
        {
            if (checkpoint != injectionCheckpoint
                || !string.Equals(artifactKind, "plan", StringComparison.Ordinal)
                || Interlocked.Exchange(ref injected, 1) != 0)
            {
                return;
            }
            File.WriteAllBytes(planPath, attackerBytes);
        });

        await using (new AliExecutionInvocationScope(Grant(plan)).Enter(arguments))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                AliDurableInvocationGrantConsumer.ConsumeAndStartAsync(
                    interposedStore,
                    plan,
                    TestContext.Current.CancellationToken));
        }

        Assert.Equal(attackerBytes, await File.ReadAllBytesAsync(
            planPath,
            TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(
            root,
            ".invocations.replacement-journal.protected")));
        Assert.Contains(
            Directory.EnumerateFiles(root),
            path => Path.GetFileName(path).EndsWith(
                ".previous",
                StringComparison.Ordinal));
        Assert.Contains(
            Directory.EnumerateFiles(root),
            path => Path.GetFileName(path).EndsWith(
                ".new",
                StringComparison.Ordinal));

        File.Delete(planPath);
        var recovered = await Store(root).LoadAsync(
            plan.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationState.Started, recovered.State);
        Assert.False(File.Exists(Path.Combine(
            root,
            ".invocations.replacement-journal.protected")));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(root),
            path => Path.GetFileName(path).EndsWith(
                ".previous",
                StringComparison.Ordinal)
                || Path.GetFileName(path).EndsWith(
                    ".new",
                    StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(
        (int)AliDurableInvocationStoreCheckpoint.ReplacementJournalDurable,
        (int)AliDurableInvocationState.Prepared)]
    [InlineData(
        (int)AliDurableInvocationStoreCheckpoint.DestinationDisplaced,
        (int)AliDurableInvocationState.Started)]
    [InlineData(
        (int)AliDurableInvocationStoreCheckpoint.ReplacementPublishedBeforeCleanup,
        (int)AliDurableInvocationState.Started)]
    public async Task ReplacementCrashBoundaries_ReconcileFromProtectedObjectIdentities(
        int crashCheckpointValue,
        int expectedRecoveredStateValue)
    {
        var crashCheckpoint = (AliDurableInvocationStoreCheckpoint)crashCheckpointValue;
        var expectedRecoveredState = (AliDurableInvocationState)expectedRecoveredStateValue;
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var root = Path.Combine(directory.Path, "crash-" + crashCheckpoint);
        var initialStore = Store(root);
        var (plan, arguments) = PlanAndArguments("crash-" + crashCheckpoint);
        await initialStore.PrepareAsync(plan, TestContext.Current.CancellationToken);
        var injected = 0;
        var crashingStore = Store(root, (checkpoint, artifactKind) =>
        {
            if (checkpoint == crashCheckpoint
                && string.Equals(artifactKind, "plan", StringComparison.Ordinal)
                && Interlocked.Exchange(ref injected, 1) == 0)
            {
                throw new AliDurableInvocationSimulatedCrashException();
            }
        });

        await using (new AliExecutionInvocationScope(Grant(plan)).Enter(arguments))
        {
            await Assert.ThrowsAsync<AliDurableInvocationSimulatedCrashException>(() =>
                AliDurableInvocationGrantConsumer.ConsumeAndStartAsync(
                    crashingStore,
                    plan,
                    TestContext.Current.CancellationToken));
        }

        Assert.True(File.Exists(Path.Combine(
            root,
            ".invocations.replacement-journal.protected")));
        var recovered = await Store(root).LoadAsync(
            plan.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(expectedRecoveredState, recovered.State);
        Assert.False(File.Exists(Path.Combine(
            root,
            ".invocations.replacement-journal.protected")));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(root),
            path => Path.GetFileName(path).EndsWith(
                ".previous",
                StringComparison.Ordinal)
                || Path.GetFileName(path).EndsWith(
                    ".new",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoveryFinalIdentityCheck_PinsRecognizedLeafUntilCleanupCompletes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var root = Path.Combine(directory.Path, "recovery-final-interposition");
        var initialStore = Store(root);
        var (plan, arguments) = PlanAndArguments("recovery-final-interposition");
        await initialStore.PrepareAsync(plan, TestContext.Current.CancellationToken);
        var crashInjected = 0;
        var crashingStore = Store(root, (checkpoint, artifactKind) =>
        {
            if (checkpoint == AliDurableInvocationStoreCheckpoint.ReplacementPublishedBeforeCleanup
                && string.Equals(artifactKind, "plan", StringComparison.Ordinal)
                && Interlocked.Exchange(ref crashInjected, 1) == 0)
            {
                throw new AliDurableInvocationSimulatedCrashException();
            }
        });

        await using (new AliExecutionInvocationScope(Grant(plan)).Enter(arguments))
        {
            await Assert.ThrowsAsync<AliDurableInvocationSimulatedCrashException>(() =>
                AliDurableInvocationGrantConsumer.ConsumeAndStartAsync(
                    crashingStore,
                    plan,
                    TestContext.Current.CancellationToken));
        }

        var planPath = Path.Combine(
            root,
            plan.Id + ".invocation-plan.protected");
        var displacedPath = Path.Combine(root, "recovery-displaced-plan.protected");
        Exception? interpositionFailure = null;
        var interpositionAttempted = 0;
        var recoveringStore = Store(root, (checkpoint, artifactKind) =>
        {
            if (checkpoint != AliDurableInvocationStoreCheckpoint.RecoveryFinalIdentityChecked
                || !string.Equals(artifactKind, "plan", StringComparison.Ordinal)
                || Interlocked.Exchange(ref interpositionAttempted, 1) != 0)
            {
                return;
            }

            try
            {
                File.Move(planPath, displacedPath);
            }
            catch (Exception exception)
            {
                interpositionFailure = exception;
            }
        });

        var recovered = await recoveringStore.LoadAsync(
            plan.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref interpositionAttempted));
        Assert.IsType<IOException>(interpositionFailure);
        Assert.False(File.Exists(displacedPath));
        Assert.Equal(AliDurableInvocationState.Started, recovered.State);
        Assert.False(File.Exists(Path.Combine(
            root,
            ".invocations.replacement-journal.protected")));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(root),
            path => Path.GetFileName(path).EndsWith(
                ".previous",
                StringComparison.Ordinal)
                || Path.GetFileName(path).EndsWith(
                    ".new",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task HardLinkedPlanLeaf_IsRejectedWithoutTouchingExternalIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var root = Path.Combine(directory.Path, "hard-link-root");
        var store = Store(root);
        var (plan, _) = PlanAndArguments("hard-link-plan");
        await store.PrepareAsync(plan, TestContext.Current.CancellationToken);
        var planPath = Path.Combine(
            root,
            plan.Id + ".invocation-plan.protected");
        var savedPath = Path.Combine(root, "saved-plan.protected");
        var externalPath = Path.Combine(directory.Path, "external-protected-artifact");
        File.Move(planPath, savedPath);
        File.Copy(savedPath, externalPath);
        var expectedExternal = await File.ReadAllBytesAsync(
            externalPath,
            TestContext.Current.CancellationToken);
        if (!CreateHardLinkW(planPath, externalPath, IntPtr.Zero))
        {
            Assert.Skip(
                "Hard-link creation is unavailable: Win32 error "
                + Marshal.GetLastWin32Error().ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => Store(root).LoadAsync(
            plan.Id,
            TestContext.Current.CancellationToken));
        Assert.Equal(expectedExternal, await File.ReadAllBytesAsync(
            externalPath,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("*", CapabilityId, ReconcilerId)]
    [InlineData(ToolName, "*", ReconcilerId)]
    [InlineData(ToolName, CapabilityId, "*")]
    public void AdapterRegistry_RejectsWildcardTupleRegistration(
        string toolName,
        string capabilityId,
        string reconcilerId)
    {
        Assert.Throws<ArgumentException>(() => new AliExecutionEffectAdapterRegistry(
            [new StubAdapter(toolName, capabilityId, reconcilerId)]));
    }

    private static AliDurableInvocationStore Store(string root) =>
        new(root, "test-profile-binding");

    private static AliDurableInvocationStore Store(
        string root,
        Action<AliDurableInvocationStoreCheckpoint, string> testHook) =>
        new(root, "test-profile-binding", testHook);

    private static (AliDurableInvocationPlan Plan, AIFunctionArguments Arguments)
        PlanAndArguments(string suffix = "default")
    {
        var arguments = new AIFunctionArguments
        {
            ["path"] = "src/ExactTarget-" + suffix + ".cs"
        };
        var request = new AliExecutionPreparationRequest(
            new TurnIdentity(
                "test-user",
                "durable-invocation-" + suffix,
                "assistant-message"),
            CallId: "call-" + suffix,
            WorkItemId: "work-" + suffix,
            ToolName,
            CapabilityId,
            ReconcilerId,
            JsonSerializer.SerializeToElement(arguments),
            ArgumentsDigest(arguments),
            ActionIdentityFingerprint: Digest("action-" + suffix),
            TargetVersionDigest: Digest("target-" + suffix),
            PermissionReceiptDigest: Digest("permission-" + suffix),
            RegistryRevisionDigest: Digest("registry-revision-" + suffix),
            ExecutionRegistryIdentityDigest: Digest("registry-identity-" + suffix));
        return (
            AliDurableInvocationPlan.Create(
                request,
                RootBinding,
                "domain-preparation-" + suffix,
                Digest("domain-preparation-" + suffix)),
            arguments);
    }

    private static AliExecutionGrant Grant(AliDurableInvocationPlan plan) =>
        new(
            IdempotencyKey: Digest("idempotency-" + plan.Id),
            plan.CallId,
            plan.ToolName,
            plan.CapabilityId,
            plan.CanonicalArgumentsDigest,
            plan.TargetVersionDigest,
            plan.PermissionReceiptDigest,
            plan.ExecutionRegistryIdentityDigest,
            plan.ReconcilerId,
            PreparationIdentity: plan.Id,
            plan.RootBinding);

    private static string ArgumentsDigest(AIFunctionArguments arguments)
    {
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(
            JsonSerializer.SerializeToElement(arguments));
        try
        {
            return TurnStateIntegrity.Digest(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Digest(string value) =>
        TurnStateIntegrity.Digest(Encoding.UTF8.GetBytes(value));

    private sealed class StoreCompletionParticipant(
        AliDurableInvocationStore store,
        string planId) : IAliInvocationCompletionParticipant
    {
        private int _completionCount;
        private int _failureCount;
        private int _inDoubtCount;

        internal int CompletionCount => Volatile.Read(ref _completionCount);

        internal int FailureCount => Volatile.Read(ref _failureCount);

        internal int InDoubtCount => Volatile.Read(ref _inDoubtCount);

        public async ValueTask CompleteAsync(
            object? result,
            CancellationToken cancellationToken)
        {
            _ = result;
            Interlocked.Increment(ref _completionCount);
            await store.CompleteAsync(
                planId,
                expectedRevision: 1,
                "completed",
                Digest("test-result"),
                cancellationToken);
        }

        public async ValueTask FailAsync(
            Exception exception,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(exception);
            Interlocked.Increment(ref _failureCount);
            await store.FailAsync(
                planId,
                expectedRevision: 1,
                "inner-invocation-failed",
                cancellationToken);
        }

        public async ValueTask MarkInDoubtAsync(
            string reasonCode,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _inDoubtCount);
            await store.MarkInDoubtAsync(
                planId,
                expectedRevision: 1,
                reasonCode,
                cancellationToken);
        }
    }

    private sealed class RecordingDomainReconciler(
        AliExactExecutionAdapterIdentity exactIdentity) :
        IAliStartedInvocationDomainReconciler
    {
        private int _callCount;

        public AliExactExecutionAdapterIdentity ExactIdentity { get; } = exactIdentity;

        internal int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<AliDurableInvocationRecoveryResult> ReconcileStartedAsync(
            AliDurableInvocationPlan plan,
            AliDurableInvocationReceipt startedReceipt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(AliDurableInvocationState.Started, startedReceipt.State);
            Assert.Equal(plan.Id, startedReceipt.PlanId);
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(
                AliDurableInvocationRecoveryResult.Applied("domain-proved-applied"));
        }
    }

    private sealed class StubAdapter(
        string toolName,
        string capabilityId,
        string reconcilerId) : IAliExecutionEffectAdapter
    {
        public string ToolName { get; } = toolName;

        public string CapabilityId { get; } = capabilityId;

        public string ReconcilerId { get; } = reconcilerId;

        public ValueTask<AliExecutionPreparation> PrepareAsync(
            AliExecutionPreparationRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ActionReconciliationResult> ReconcileAsync(
            TurnIdentity identity,
            PreparedActionIntent intent,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
