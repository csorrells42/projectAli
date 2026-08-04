using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Issues one stable calendar-event identity before the permission-guarded calendar tool runs.
/// A completed invocation is committed from the ordinary trusted tool outcome. After a process
/// interruption, the adapter never guesses whether Windows Task Scheduler accepted the event.
/// </summary>
internal sealed class AliCalendarDurableEffectAdapter : IAliDurableEffectAdapter
{
    internal const string CalendarReconcilerId =
        "ali.reconcile." + AliCapabilityCatalog.CreateCalendarEventName;

    public IReadOnlyCollection<string> ToolNames { get; } =
    [
        AliCapabilityCatalog.CreateCalendarEventName
    ];

    public string ReconcilerId => CalendarReconcilerId;

    public AliDurableEffectPreview Preview(AliDurableEffectPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(
                request.ToolName,
                AliCapabilityCatalog.CreateCalendarEventName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The calendar adapter cannot preview an unregistered tool.");
        }

        var material = string.Join(
            "\n",
            request.TurnIdentity.UserId,
            request.TurnIdentity.ConversationId,
            request.TurnIdentity.AssistantMessageId,
            request.CallId,
            request.ToolName,
            request.CanonicalArgumentsDigest,
            request.TargetVersionDigest);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        try
        {
            return new(
                RequiresPreparedIntent: true,
                OperationId: "cal_" + Convert.ToHexString(hash).ToLowerInvariant(),
                ReconcilerId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public bool ConfirmsAuthoritativeNoEffect(
        PreparedActionIntent intent,
        JsonElement result)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return false;
    }

    public ValueTask<ActionReconciliationResult> ReconcileAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                intent.ToolName,
                AliCapabilityCatalog.CreateCalendarEventName,
                StringComparison.Ordinal)
            || !string.Equals(intent.ReconcilerId, ReconcilerId, StringComparison.Ordinal)
            || !string.Equals(intent.IdempotencyKey, intent.RootBinding, StringComparison.Ordinal)
            || !intent.IdempotencyKey.StartsWith("cal_", StringComparison.Ordinal))
        {
            return ValueTask.FromResult(ActionReconciliationResult.Unknown(
                "calendar-effect-identity-mismatch"));
        }

        // The current calendar store publishes an .ics file and a Windows scheduled task before
        // its JSON inventory is committed. A restart cannot prove all three targets from the turn
        // journal alone, so it must never infer absence or repeat the operation automatically.
        return ValueTask.FromResult(ActionReconciliationResult.Unknown(
            "calendar-effect-explicit-inspection-required"));
    }
}
