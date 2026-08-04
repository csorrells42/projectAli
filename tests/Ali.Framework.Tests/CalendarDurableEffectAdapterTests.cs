using System.Text.Json;
using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Reminders;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests;

public sealed class CalendarDurableEffectAdapterTests
{
    [Fact]
    public void ProductionRegistry_RegistersOnlyTheExactCalendarToolIdentity()
    {
        var registry = AliProductionDurableEffectAdapters.Create();

        Assert.True(registry.TryGet(
            AliCapabilityCatalog.CreateCalendarEventName,
            out var adapter));
        Assert.NotNull(adapter);
        Assert.Equal(AliCalendarDurableEffectAdapter.CalendarReconcilerId, adapter.ReconcilerId);
        Assert.False(registry.TryGet(
            AliCapabilityCatalog.CreateCalendarEventName + "_approximate",
            out _));
    }

    [Fact]
    public void Preview_IssuesAStablePreparedCalendarIdentity()
    {
        var registry = AliProductionDurableEffectAdapters.Create();
        Assert.True(registry.TryGet(
            AliCapabilityCatalog.CreateCalendarEventName,
            out var adapter));
        var turn = Turn();
        var request = new AliDurableEffectPreviewRequest(
            new TurnIdentity("user", "conversation", "assistant-message"),
            turn,
            "call-calendar",
            AliCapabilityCatalog.CreateCalendarEventName,
            "arguments-digest",
            "target-digest");

        var first = adapter!.Preview(request);
        var repeated = adapter.Preview(request);
        var changed = adapter.Preview(request with { CanonicalArgumentsDigest = "changed-digest" });

        Assert.True(first.RequiresPreparedIntent);
        Assert.StartsWith("cal_", first.OperationId, StringComparison.Ordinal);
        Assert.Equal(68, first.OperationId!.Length);
        Assert.Equal(first.OperationId, repeated.OperationId);
        Assert.NotEqual(first.OperationId, changed.OperationId);
        Assert.Equal(AliCalendarDurableEffectAdapter.CalendarReconcilerId, first.ReconcilerId);
    }

    [Fact]
    public async Task ReminderTool_UsesThePreparedCalendarIdentityInsideAliInvocation()
    {
        var store = new RecordingReminderStore();
        var turn = Turn();
        const string callId = "call-calendar";
        const string operationId =
            "cal_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        turn.RegisterToolPlan(Plan(callId));
        turn.RegisterDurableOperationId(callId, operationId);
        turn.RegisterActionExecutionAuthority(new TestAuthority());
        Assert.True(turn.TryEnterActiveToolInvocation(
            callId,
            AliCapabilityCatalog.CreateCalendarEventName,
            out var invocation));

        CoordinatorReminderResult result;
        using (invocation)
        {
            result = await new AliReminderTools(store, () => turn).CreateAsync(
                "Review Ali",
                DateTimeOffset.Now.AddHours(2).ToString("O"),
                CancellationToken.None);
        }

        Assert.True(result.Saved);
        Assert.Equal(operationId, result.ReminderId);
        Assert.Equal(operationId, Assert.Single(store.Saved).ReminderId);
    }

    [Fact]
    public async Task ReminderTool_FailsClosedWhenAliInvocationLostItsDurableIdentity()
    {
        var store = new RecordingReminderStore();
        var turn = Turn();
        const string callId = "call-calendar";
        turn.RegisterToolPlan(Plan(callId));
        turn.RegisterActionExecutionAuthority(new TestAuthority());
        Assert.True(turn.TryEnterActiveToolInvocation(
            callId,
            AliCapabilityCatalog.CreateCalendarEventName,
            out var invocation));

        CoordinatorReminderResult result;
        using (invocation)
        {
            result = await new AliReminderTools(store, () => turn).CreateAsync(
                "Review Ali",
                DateTimeOffset.Now.AddHours(2).ToString("O"),
                CancellationToken.None);
        }

        Assert.False(result.Saved);
        Assert.Contains("durable operation identity", result.Message, StringComparison.Ordinal);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task InterruptedCalendarEffect_RemainsUnknownInsteadOfBeingRepeated()
    {
        var adapter = new AliCalendarDurableEffectAdapter();
        const string operationId =
            "cal_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var intent = new PreparedActionIntent(
            operationId,
            "work-calendar",
            AliCapabilityCatalog.CreateCalendarEventName,
            "ali.tool." + AliCapabilityCatalog.CreateCalendarEventName,
            "arguments-digest",
            "target-digest",
            "permission-digest",
            "registry-digest",
            "execution-registry-digest",
            adapter.ReconcilerId,
            operationId,
            RequiresApproval: true,
            AcceptedCallId: "call-calendar");

        var reconciled = await adapter.ReconcileAsync(
            new TurnIdentity("user", "conversation", "assistant-message"),
            intent,
            CancellationToken.None);

        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
        Assert.Equal("calendar-effect-explicit-inspection-required", reconciled.OutcomeCode);
    }

    private static CoordinatorTurnContext Turn() => new(
        "conversation",
        "user-message",
        "assistant-message",
        "create a calendar event",
        static _ => { });

    private static CoordinatorToolPlan Plan(string callId) => new(
        callId,
        AliCapabilityCatalog.CreateCalendarEventName,
        "assessment",
        "plan",
        "next",
        "selected",
        "returned",
        "{}");

    private sealed class RecordingReminderStore : IReminderStore
    {
        internal List<ReminderEntry> Saved { get; } = [];

        public ReminderListResult List() => new(Saved.ToArray(), []);

        public IReadOnlyList<ReminderEntry> ListDue(DateTimeOffset now) => [];

        public ReminderEntry Save(ReminderEntry reminder)
        {
            Saved.Add(reminder);
            return reminder;
        }

        public ReminderEntry? SetStatus(string reminderId, ReminderStatus status) => null;

        public bool Delete(string reminderId) => false;

        public int Clear()
        {
            var count = Saved.Count;
            Saved.Clear();
            return count;
        }
    }

    private sealed class TestAuthority : ICoordinatorActionExecutionAuthority
    {
        public TurnIdentity DurableIdentity { get; } =
            new("user", "conversation", "assistant-message");

        public ValueTask<CapabilityInvocationAuthorization> PrepareExecutionAsync(
            CapabilityInvocationLease lease,
            string callId,
            AIFunctionArguments arguments,
            bool requiresApproval,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
