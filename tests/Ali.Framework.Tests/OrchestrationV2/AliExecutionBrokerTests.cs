using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.State;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class AliExecutionBrokerTests
{
    private const string CallId = "call-effect-1";
    private const string WorkItemId = "work-effect-1";
    private const string ExactPath = "src/ExactTarget.cs";

    [Fact]
    public async Task RegisteredAdapter_DurablyPreparesBeforeExactOneUseInvocation_AndBlocksReplay()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = Identity("durable-before-inner");
        var bindings = Bindings();
        var innerInvocations = 0;
        var durablePreparationObservedInside = false;
        PreparedActionIntent? durableIntentObservedInside = null;
        AliExecutionGrant? grantObservedInside = null;
        AliPlanningStateCoordinator? coordinator = null;
        CapabilityDescriptor? descriptor = null;
        RecordingAdapter? adapter = null;
        var function = AIFunctionFactory.Create(
            (Func<string, Task<string>>)(async path =>
            {
                Interlocked.Increment(ref innerInvocations);
                var exactCoordinator = coordinator
                    ?? throw new InvalidOperationException("The test coordinator is unavailable.");
                var exactDescriptor = descriptor
                    ?? throw new InvalidOperationException("The test descriptor is unavailable.");
                var exactAdapter = adapter
                    ?? throw new InvalidOperationException("The test adapter is unavailable.");
                var snapshot = await exactCoordinator.RecoverTurnAsync(
                    identity,
                    bindings,
                    explicitlyRequested: false,
                    TestContext.Current.CancellationToken);
                var snapshotState = snapshot.State;
                durablePreparationObservedInside = snapshotState?.PendingActions.Length == 1
                    && snapshotState.PendingActions[0].Intent.PreparationIdentity
                        == exactAdapter.PreparationIdentity;
                durableIntentObservedInside = snapshotState?.PendingActions.Length == 1
                    ? snapshotState.PendingActions[0].Intent with { }
                    : null;
                Assert.True(AliExecutionGrantContext.TryConsumeCurrent(
                    exactDescriptor.ToolName,
                    exactDescriptor.Id,
                    exactDescriptor.Effect.ReconcilerId!,
                    exactAdapter.PreparationIdentity,
                    exactAdapter.RootBinding,
                    out var grant));
                grantObservedInside = grant;
                Assert.Equal(CallId, grant!.CallId);
                Assert.Equal(ExactPath, path);
                Assert.False(AliExecutionGrantContext.TryConsumeCurrent(
                    exactDescriptor.ToolName,
                    exactDescriptor.Id,
                    exactDescriptor.Effect.ReconcilerId!,
                    exactAdapter.PreparationIdentity,
                    exactAdapter.RootBinding,
                    out _));
                return "applied";
            }),
            AliCapabilityCatalog.FileDeleteName,
            "Delete one exact file.");
        var registry = AliProductionCapabilityCatalog.CreateRegistry([function]);
        descriptor = Assert.Single(registry.Descriptors);
        adapter = new RecordingAdapter(descriptor);
        coordinator = new AliPlanningStateCoordinator(
            directory.Path,
            "profile",
            executionAdapters: new AliExecutionEffectAdapterRegistry([adapter]));
        using (coordinator)
        await using (var turn = await BeginAcceptedTurnAsync(
                         coordinator,
                         identity,
                         bindings,
                         registry,
                         function))
        {
            var accepted = await coordinator.RecoverTurnAsync(
                identity,
                bindings,
                explicitlyRequested: false,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                AcceptedCallExecutionClass.PreparedEffectRequired,
                accepted.State!.PendingAcceptedCall!.ExecutionClass);
            var acceptedStateRevision = accepted.State.Revision;

            var guarded = await CreateLeaseGuardAsync(
                registry,
                function,
                turn,
                CallId);
            var first = await guarded.InvokeAsync(
                ExactArguments(),
                TestContext.Current.CancellationToken);
            Assert.Equal("applied", Assert.IsType<JsonElement>(first).GetString());
            Assert.True(durablePreparationObservedInside);
            var durableIntent = Assert.IsType<PreparedActionIntent>(durableIntentObservedInside);
            var exactGrant = Assert.IsType<AliExecutionGrant>(grantObservedInside);
            Assert.Equal(adapter.RootBinding, durableIntent.RootBinding);
            Assert.Equal(
                durableIntent.ExecutionRegistryIdentityDigest,
                exactGrant.RegistryIdentityDigest);
            Assert.Equal(durableIntent.RootBinding, exactGrant.RootBinding);
            Assert.Equal(durableIntent.AcceptedCallId, exactGrant.CallId);
            Assert.Equal(durableIntent.PreparationIdentity, exactGrant.PreparationIdentity);
            Assert.Equal(1, adapter.PrepareCount);
            Assert.Equal(1, Volatile.Read(ref innerInvocations));
            Assert.False(AliExecutionGrantContext.TryConsumeCurrent(
                descriptor.ToolName,
                descriptor.Id,
                descriptor.Effect.ReconcilerId!,
                adapter.PreparationIdentity,
                adapter.RootBinding,
                out _));

            var replay = await guarded.InvokeAsync(
                ExactArguments(),
                TestContext.Current.CancellationToken);
            var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(replay);
            Assert.Contains(blocked.Reasons, reason =>
                reason.DependencyId == "action-idempotency");
            Assert.Equal(1, adapter.PrepareCount);
            Assert.Equal(1, Volatile.Read(ref innerInvocations));

            var projection = "The exact mutation returned successfully.";
            var evidence = await turn.OnToolResultObservedAsync(
                new AliPlanningToolResultObservedEvent(
                    identity.ConversationId,
                    identity.AssistantMessageId,
                    acceptedStateRevision,
                    "evidence-effect-1",
                    CallId,
                    function.Name,
                    PlanningToolInvocationStatus.Returned,
                    PlanningToolDomainOutcome.Succeeded,
                    JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>(
                        StringComparer.Ordinal)
                    {
                        ["path"] = JsonSerializer.SerializeToElement(ExactPath)
                    }),
                    JsonSerializer.SerializeToElement(new { success = true }),
                    DateTimeOffset.UtcNow.AddMilliseconds(-10),
                    DateTimeOffset.UtcNow,
                    projection,
                    AliPlanningProjectionSafety.Digest(projection)),
                TestContext.Current.CancellationToken);
            Assert.Equal("evidence-effect-1", evidence.EvidenceId);
            var committed = await coordinator.RecoverTurnAsync(
                identity,
                bindings,
                explicitlyRequested: false,
                TestContext.Current.CancellationToken);
            Assert.Empty(committed.State!.PendingActions);
        }
    }

    [Fact]
    public async Task MismatchedRegistration_LeavesMutationUnavailableAndNeverInvokes()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = Identity("adapter-registration-mismatch");
        var bindings = Bindings();
        var innerInvocations = 0;
        var function = AIFunctionFactory.Create(
            (string path) =>
            {
                Interlocked.Increment(ref innerInvocations);
                return path;
            },
            AliCapabilityCatalog.FileDeleteName,
            "Delete one exact file.");
        var registry = AliProductionCapabilityCatalog.CreateRegistry([function]);
        var descriptor = Assert.Single(registry.Descriptors);
        var mismatched = new RecordingAdapter(
            descriptor,
            capabilityId: descriptor.Id + "-different");
        using var coordinator = new AliPlanningStateCoordinator(
            directory.Path,
            "profile",
            executionAdapters: new AliExecutionEffectAdapterRegistry([mismatched]));
        await using var turn = await BeginAcceptedTurnAsync(
            coordinator,
            identity,
            bindings,
            registry,
            function);

        var accepted = await coordinator.RecoverTurnAsync(
            identity,
            bindings,
            explicitlyRequested: false,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            AcceptedCallExecutionClass.Unavailable,
            accepted.State!.PendingAcceptedCall!.ExecutionClass);
        var guarded = await CreateLeaseGuardAsync(registry, function, turn, CallId);

        var result = await guarded.InvokeAsync(
            ExactArguments(),
            TestContext.Current.CancellationToken);

        var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(result);
        Assert.Contains(blocked.Reasons, reason =>
            reason.Code == nameof(CapabilityAvailabilityReasonCode.ReconcilerUnavailable));
        Assert.Equal(0, mismatched.PrepareCount);
        Assert.Equal(0, Volatile.Read(ref innerInvocations));
    }

    [Fact]
    public async Task AdapterTargetMismatch_FailsClosedBeforeDurablePrepareOrInnerEffect()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = Identity("adapter-target-mismatch");
        var bindings = Bindings();
        var innerInvocations = 0;
        var function = AIFunctionFactory.Create(
            (string path) =>
            {
                Interlocked.Increment(ref innerInvocations);
                return path;
            },
            AliCapabilityCatalog.FileDeleteName,
            "Delete one exact file.");
        var registry = AliProductionCapabilityCatalog.CreateRegistry([function]);
        var descriptor = Assert.Single(registry.Descriptors);
        var adapter = new RecordingAdapter(descriptor, returnWrongTarget: true);
        using var coordinator = new AliPlanningStateCoordinator(
            directory.Path,
            "profile",
            executionAdapters: new AliExecutionEffectAdapterRegistry([adapter]));
        await using var turn = await BeginAcceptedTurnAsync(
            coordinator,
            identity,
            bindings,
            registry,
            function);
        var guarded = await CreateLeaseGuardAsync(registry, function, turn, CallId);

        var result = await guarded.InvokeAsync(
            ExactArguments(),
            TestContext.Current.CancellationToken);

        var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(result);
        Assert.Contains(blocked.Reasons, reason =>
            reason.Code == nameof(CapabilityAvailabilityReasonCode.ReconcilerUnavailable));
        Assert.Equal(1, adapter.PrepareCount);
        Assert.Equal(0, Volatile.Read(ref innerInvocations));
        var snapshot = await coordinator.RecoverTurnAsync(
            identity,
            bindings,
            explicitlyRequested: false,
            TestContext.Current.CancellationToken);
        Assert.Empty(snapshot.State!.PendingActions);
    }

    [Fact]
    public async Task CrashAfterDurablePrepare_ReconcilesThroughInjectedAdapterWithoutReexecution()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = Identity("crash-recovery");
        var bindings = Bindings();
        var innerInvocations = 0;
        var function = AIFunctionFactory.Create(
            (Func<string, string>)(path =>
            {
                _ = path;
                Interlocked.Increment(ref innerInvocations);
                throw new IOException("simulated crash after durable preparation");
            }),
            AliCapabilityCatalog.FileDeleteName,
            "Delete one exact file.");
        var registry = AliProductionCapabilityCatalog.CreateRegistry([function]);
        var descriptor = Assert.Single(registry.Descriptors);
        var adapter = new RecordingAdapter(descriptor);
        var adapters = new AliExecutionEffectAdapterRegistry([adapter]);

        using (var coordinator = new AliPlanningStateCoordinator(
                   directory.Path,
                   "profile",
                   executionAdapters: adapters))
        await using (var turn = await BeginAcceptedTurnAsync(
                         coordinator,
                         identity,
                         bindings,
                         registry,
                         function))
        {
            var guarded = await CreateLeaseGuardAsync(
                registry,
                function,
                turn,
                CallId);
            await Assert.ThrowsAsync<IOException>(() => guarded.InvokeAsync(
                    ExactArguments(),
                    TestContext.Current.CancellationToken)
                .AsTask());
        }

        Assert.Equal(1, Volatile.Read(ref innerInvocations));
        using var reopened = new AliPlanningStateCoordinator(
            directory.Path,
            "profile",
            executionAdapters: adapters);

        var recovered = await reopened.RecoverTurnAsync(
            identity,
            bindings,
            explicitlyRequested: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, adapter.ReconcileCount);
        Assert.Equal(adapter.PreparationIdentity, adapter.LastReconciledIntent!.PreparationIdentity);
        Assert.Equal(adapter.RootBinding, adapter.LastReconciledIntent.RootBinding);
        TurnStateIntegrity.RequireDigest(
            adapter.LastReconciledIntent.ExecutionRegistryIdentityDigest,
            nameof(PreparedActionIntent.ExecutionRegistryIdentityDigest));
        Assert.Equal(ActionReconciliationDisposition.Absent, recovered.Actions.Single().Disposition);
        Assert.Equal(1, Volatile.Read(ref innerInvocations));
        Assert.Empty(recovered.State!.PendingActions);
    }

    [Fact]
    public async Task InvocationScope_IsArgumentBoundExactOneUseAndAsyncLocalSafe()
    {
        var exactArguments = ExactArguments();
        var exactDigest = ArgumentsDigest(exactArguments);
        var grant = Grant(exactDigest, "preparation-a", "root-a");
        var wrongArguments = new AIFunctionArguments { ["path"] = "src/Other.cs" };
        var burnedByMismatch = new AliExecutionInvocationScope(grant);

        Assert.Throws<InvalidOperationException>(() => burnedByMismatch.Enter(wrongArguments));
        Assert.Throws<InvalidOperationException>(() => burnedByMismatch.Enter(exactArguments));

        var oneUse = new AliExecutionInvocationScope(grant);
        using (oneUse.Enter(exactArguments))
        {
            Assert.False(AliExecutionGrantContext.TryConsumeCurrent(
                grant.ToolName,
                grant.CapabilityId,
                grant.ReconcilerId,
                "wrong-preparation",
                grant.RootBinding,
                out _));
            Assert.True(AliExecutionGrantContext.TryConsumeCurrent(
                grant.ToolName,
                grant.CapabilityId,
                grant.ReconcilerId,
                grant.PreparationIdentity,
                grant.RootBinding,
                out var consumed));
            Assert.Equal(grant, consumed);
            Assert.False(AliExecutionGrantContext.TryConsumeCurrent(
                grant.ToolName,
                grant.CapabilityId,
                grant.ReconcilerId,
                grant.PreparationIdentity,
                grant.RootBinding,
                out _));
        }

        var leakedContext = new AliExecutionInvocationScope(
            Grant(exactDigest, "preparation-b", "root-b"));
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> leakedAttempt;
        using (leakedContext.Enter(exactArguments))
        {
            leakedAttempt = Task.Run(async () =>
            {
                await release.Task;
                return AliExecutionGrantContext.TryConsumeCurrent(
                    AliCapabilityCatalog.FileDeleteName,
                    "capability-id",
                    "reconciler-id",
                    "preparation-b",
                    "root-b",
                    out _);
            });
        }

        release.TrySetResult(true);
        Assert.False(await leakedAttempt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PreparedIntent_RejectsMissingAuthorizationRecoveryIdentity(int invalidField)
    {
        var valid = new PreparedActionIntent(
            Digest("idempotency"),
            WorkItemId,
            AliCapabilityCatalog.FileDeleteName,
            "capability-id",
            Digest("arguments"),
            Digest("target"),
            Digest("permission"),
            Digest("registry-revision"),
            Digest("execution-registry"),
            "reconciler-id",
            "root-binding",
            RequiresApproval: true,
            AcceptedCallId: CallId,
            PreparationIdentity: "preparation-id");
        var invalid = invalidField switch
        {
            0 => valid with { ExecutionRegistryIdentityDigest = "not-a-digest" },
            1 => valid with { RootBinding = string.Empty },
            _ => valid with { RootBinding = new string('r', 2049) }
        };

        Assert.Throws<ArgumentException>(() => invalid.Validate());
    }

    private static async Task<AliDurablePlanningTurn> BeginAcceptedTurnAsync(
        AliPlanningStateCoordinator coordinator,
        TurnIdentity identity,
        TurnRuntimeBindings bindings,
        CanonicalCapabilityRegistry registry,
        AIFunction function)
    {
        var turnContext = new CoordinatorTurnContext(
            identity.ConversationId,
            "user-message",
            identity.AssistantMessageId,
            "Apply the exact requested mutation.",
            publish: _ => { },
            observationIdentity: identity);
        turnContext.RecordShadowPermission(
            CallId,
            new EvidencePermissionMetadata("approved-once", "exact-arguments"),
            source: "test",
            approvalRequestId: "approval-1");
        var turn = await coordinator.BeginTurnAsync(
            turnContext,
            bindings,
            acceptedPriorConversation: [],
            capabilityRegistry: registry,
            liveBindingsAccessor: () => bindings,
            TestContext.Current.CancellationToken);
        var decision = new OrchestrationDecision(
            new OrchestrationWorkUpdate(
                0,
                [
                    new OrchestrationWorkItemUpdate(
                        WorkItemId,
                        "Apply the exact requested mutation",
                        OrchestrationWorkStatus.Active)
                ]),
            materialClaims: [],
            new CallToolAction(
                function.Name,
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["path"] = JsonSerializer.SerializeToElement(ExactPath)
                },
                "Apply the exact requested mutation",
                "The exact target becomes accepted evidence"));
        var accepted = await turn.OnDecisionAcceptedAsync(
            new AliPlanningDecisionAcceptedEvent(
                identity.ConversationId,
                identity.AssistantMessageId,
                turn.Input.StateRevision,
                decision,
                CallId,
                function.Name),
            TestContext.Current.CancellationToken);
        Assert.False(accepted.RequiresFreshPlanningPass);
        return turn;
    }

    private static async Task<CapabilityInvocationLeaseAIFunction> CreateLeaseGuardAsync(
        CanonicalCapabilityRegistry registry,
        AIFunction function,
        AliDurablePlanningTurn turn,
        string callId)
    {
        var runtimeState = RuntimeState(registry);
        var inventory = CapabilityTerminalToolInventory.Create([function], registry);
        var owner = new CapabilitySettingsSnapshotOwner(
            registry,
            new CapabilityResolver(),
            CapabilityRuntimeAvailabilityFactory.Create(inventory, runtimeState),
            new MemoryPersistence());
        var terminal = new TerminalCapabilityEnforcementProvider(
            owner,
            () => runtimeState,
            actionExecutionBoundary: (lease, arguments, requiresApproval, cancellationToken) =>
                turn.PrepareExecutionAsync(
                    lease,
                    callId,
                    arguments,
                    requiresApproval,
                    cancellationToken));
        var context = await terminal.ApplyTerminalContextAsync(
            new AIContext { Tools = [function] },
            TestContext.Current.CancellationToken);
        var guarded = Assert.IsAssignableFrom<AIFunction>(Assert.Single(context.Tools!));
        return Assert.IsType<CapabilityInvocationLeaseAIFunction>(
            guarded.GetService<CapabilityInvocationLeaseAIFunction>());
    }

    private static CapabilityRuntimeStateSnapshot RuntimeState(
        CanonicalCapabilityRegistry registry) =>
        new(
            "user",
            "provider-revision",
            [AliProductionCapabilityCatalog.ProviderId],
            targetResolution: null,
            "permission-revision",
            registry.Descriptors
                .Select(descriptor => descriptor.Permission.PolicyId)
                .Distinct(StringComparer.Ordinal),
            "mcp-revision",
            readyIncomingMcpToolNames: [],
            enabledOutgoingMcpToolNames: registry.Descriptors
                .Where(descriptor => descriptor.McpExposure.Exposed)
                .Select(descriptor => descriptor.ToolName),
            "reconciler-revision",
            registry.Descriptors
                .Where(descriptor => descriptor.Effect.ReconcilerId is not null)
                .Select(descriptor => descriptor.Effect.ReconcilerId!)
                .Distinct(StringComparer.Ordinal));

    private static AIFunctionArguments ExactArguments() =>
        new() { ["path"] = ExactPath };

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

    private static AliExecutionGrant Grant(
        string argumentsDigest,
        string preparationIdentity,
        string rootBinding) =>
        new(
            IdempotencyKey: Digest("idempotency"),
            CallId,
            AliCapabilityCatalog.FileDeleteName,
            CapabilityId: "capability-id",
            CanonicalArgumentsDigest: argumentsDigest,
            TargetVersionDigest: Digest("target"),
            PermissionReceiptDigest: Digest("permission-receipt"),
            RegistryIdentityDigest: Digest("registry"),
            ReconcilerId: "reconciler-id",
            PreparationIdentity: preparationIdentity,
            RootBinding: rootBinding);

    private static TurnIdentity Identity(string suffix) =>
        new("user", "execution-broker-" + suffix, "assistant-message");

    private static TurnRuntimeBindings Bindings() =>
        new(
            Digest("profile"),
            Digest("runtime"),
            Digest("model"),
            Digest("generation"),
            Digest("registry"),
            Digest("permission"),
            Digest("mcp"),
            Digest("attachment"),
            Digest("artifact"));

    private static string Digest(string value) =>
        TurnStateIntegrity.Digest(Encoding.UTF8.GetBytes(value));

    private sealed class RecordingAdapter : IAliExecutionEffectAdapter
    {
        private readonly bool _returnWrongTarget;
        private int _prepareCount;
        private int _reconcileCount;

        internal RecordingAdapter(
            CapabilityDescriptor descriptor,
            string? capabilityId = null,
            bool returnWrongTarget = false)
        {
            ToolName = descriptor.ToolName;
            CapabilityId = capabilityId ?? descriptor.Id;
            ReconcilerId = descriptor.Effect.ReconcilerId
                ?? throw new ArgumentException("The test descriptor has no reconciler.", nameof(descriptor));
            PreparationIdentity = Digest("manifest-" + descriptor.ToolName);
            _returnWrongTarget = returnWrongTarget;
        }

        public string ToolName { get; }

        public string CapabilityId { get; }

        public string ReconcilerId { get; }

        internal string PreparationIdentity { get; }

        internal string RootBinding { get; } = "test-root-binding";

        internal int PrepareCount => Volatile.Read(ref _prepareCount);

        internal int ReconcileCount => Volatile.Read(ref _reconcileCount);

        internal PreparedActionIntent? LastReconciledIntent { get; private set; }

        public ValueTask<AliExecutionPreparation> PrepareAsync(
            AliExecutionPreparationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _prepareCount);
            return ValueTask.FromResult(new AliExecutionPreparation(
                PreparationIdentity,
                RootBinding,
                _returnWrongTarget
                    ? Digest("wrong-target")
                    : request.TargetVersionDigest));
        }

        public ValueTask<ActionReconciliationResult> ReconcileAsync(
            TurnIdentity identity,
            PreparedActionIntent intent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(identity);
            LastReconciledIntent = intent with { };
            Interlocked.Increment(ref _reconcileCount);
            return ValueTask.FromResult(
                ActionReconciliationResult.Absent("test-effect-confirmed-absent"));
        }
    }

    private sealed class MemoryPersistence : ICapabilityAvailabilitySettingsPersistence
    {
        private CapabilityAvailabilitySettings _settings =
            CapabilityAvailabilitySettings.CreateDefault();

        public CapabilityAvailabilityLoadResult Load() =>
            CapabilityAvailabilityLoadResult.Loaded(_settings);

        public CapabilityAvailabilitySaveResult Save(
            string expectedRevision,
            CapabilityAvailabilitySettings settings)
        {
            if (!string.Equals(expectedRevision, _settings.Revision, StringComparison.Ordinal))
            {
                return CapabilityAvailabilitySaveResult.Conflict(_settings);
            }

            _settings = new CapabilityAvailabilitySettings(settings.GroupSelections);
            return CapabilityAvailabilitySaveResult.Saved(_settings);
        }
    }
}
