using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Exact-name execution ownership for capabilities whose implementation owns its own
/// compare-and-act and idempotency boundary. This is execution authority, not merely
/// effect normalization metadata.
/// </summary>
internal interface IAliDurableEffectAdapter : ITurnActionReconciler
{
    IReadOnlyCollection<string> ToolNames { get; }

    AliDurableEffectPreview Preview(AliDurableEffectPreviewRequest request);

    bool ConfirmsAuthoritativeNoEffect(
        PreparedActionIntent intent,
        JsonElement result);
}

internal sealed record AliDurableEffectPreviewRequest(
    TurnIdentity TurnIdentity,
    CoordinatorTurnContext Turn,
    string CallId,
    string ToolName,
    string CanonicalArgumentsDigest,
    string TargetVersionDigest);

internal sealed record AliDurableEffectPreview(
    bool RequiresPreparedIntent,
    string? OperationId,
    string ReconcilerId);

/// <summary>
/// Closed exact-name registry. No descriptions, keywords, or argument prose select
/// execution authority.
/// </summary>
internal sealed class AliDurableEffectAdapterRegistry
{
    private readonly FrozenDictionary<string, IAliDurableEffectAdapter> _adapters;
    private readonly IReadOnlyList<ITurnActionReconciler> _reconcilers;

    internal AliDurableEffectAdapterRegistry(
        IEnumerable<IAliDurableEffectAdapter>? adapters = null)
    {
        var byName = new Dictionary<string, IAliDurableEffectAdapter>(StringComparer.Ordinal);
        var byReconciler = new Dictionary<string, ITurnActionReconciler>(StringComparer.Ordinal);
        foreach (var adapter in adapters ?? [])
        {
            ArgumentNullException.ThrowIfNull(adapter);
            ArgumentException.ThrowIfNullOrWhiteSpace(adapter.ReconcilerId);
            byReconciler.TryAdd(adapter.ReconcilerId, adapter);
            foreach (var toolName in adapter.ToolNames ?? [])
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
                if (!byName.TryAdd(toolName, adapter))
                {
                    throw new ArgumentException(
                        $"Tool '{toolName}' has more than one durable effect adapter.",
                        nameof(adapters));
                }
            }
        }

        _adapters = byName.ToFrozenDictionary(StringComparer.Ordinal);
        _reconcilers = byReconciler.Values.ToArray();
    }

    internal static AliDurableEffectAdapterRegistry Empty { get; } = new();

    internal IReadOnlyList<ITurnActionReconciler> Reconcilers => _reconcilers;

    internal bool TryGet(string toolName, out IAliDurableEffectAdapter? adapter) =>
        _adapters.TryGetValue(toolName, out adapter);
}

/// <summary>
/// CP11 participant memory owns a private durable mutation journal keyed by the
/// coordinator-issued operation ID. Reads are replay-safe. Consent capture, mutation,
/// and exact reconciliation require prepared coordinator intents. Consent grants are
/// deliberately process-local and therefore reconcile to authoritative durable absence
/// after restart. Mutation reconciliation may perform a bounded rollback or finalize a
/// staged deletion without reapplying the original operation.
/// </summary>
internal sealed class AliParticipantMemoryDurableEffectAdapter : IAliDurableEffectAdapter
{
    internal const string ParticipantMemoryReconcilerId = "participant-memory-journal-v1";

    public IReadOnlyCollection<string> ToolNames { get; } =
    [
        AliCapabilityCatalog.RecallUserMemoryName,
        AliCapabilityCatalog.ListCurrentUserMemoriesName,
        AliCapabilityCatalog.ConsentParticipantMemoryProposalName,
        AliCapabilityCatalog.ReconcileParticipantMemoryMutationName,
        AliCapabilityCatalog.MutateParticipantMemoryName
    ];

    public string ReconcilerId => ParticipantMemoryReconcilerId;

    public AliDurableEffectPreview Preview(AliDurableEffectPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ToolNames.Contains(request.ToolName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The participant-memory adapter cannot preview an unregistered tool.");
        }

        var isMutation = string.Equals(
            request.ToolName,
            AliCapabilityCatalog.MutateParticipantMemoryName,
            StringComparison.Ordinal);
        var isReconciliation = string.Equals(
            request.ToolName,
            AliCapabilityCatalog.ReconcileParticipantMemoryMutationName,
            StringComparison.Ordinal);
        var isConsent = string.Equals(
            request.ToolName,
            AliCapabilityCatalog.ConsentParticipantMemoryProposalName,
            StringComparison.Ordinal);
        if (!isMutation && !isReconciliation && !isConsent)
        {
            // Recall/list may start the private worker, but their domain operation is
            // an exact bounded read and can be retried without a prepared mutation.
            return new(false, null, ReconcilerId);
        }

        var roster = request.Turn.ParticipantRoster?.Normalize()
            ?? throw new InvalidOperationException(
                "Participant-memory mutation requires an admitted roster preview.");
        var material = string.Join(
            "\n",
            request.TurnIdentity.UserId,
            request.TurnIdentity.ConversationId,
            request.TurnIdentity.AssistantMessageId,
            request.CallId,
            request.ToolName,
            request.CanonicalArgumentsDigest,
            request.TargetVersionDigest,
            roster.TenantId,
            roster.Revision,
            roster.SelectedParticipantReference ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        try
        {
            var issuedUnixSeconds = roster.CapturedUtc.ToUnixTimeSeconds();
            return new(
                true,
                (isMutation
                    ? $"participant-mutation:{issuedUnixSeconds}:"
                    : isReconciliation
                        ? $"participant-reconcile:{issuedUnixSeconds}:"
                        : $"participant-consent:{issuedUnixSeconds}:")
                    + Convert.ToHexString(hash).ToLowerInvariant(),
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
        if (string.Equals(
                intent.ToolName,
                AliCapabilityCatalog.ConsentParticipantMemoryProposalName,
                StringComparison.Ordinal)
            && result.ValueKind == JsonValueKind.Object
            && TryReadBoolean(result, "recorded", "Recorded", out var recorded)
            && !recorded)
        {
            // Every false consent result exits before adding a new process-local grant.
            // A previously consumed approval may still exist, but this exact prepared
            // invocation made no additional state change.
            return true;
        }
        if (!string.Equals(
                intent.ToolName,
                AliCapabilityCatalog.MutateParticipantMemoryName,
                StringComparison.Ordinal)
            || result.ValueKind != JsonValueKind.Object
            || !TryReadBoolean(result, "saved", "Saved", out var saved)
            || saved
            || !TryReadBoolean(
                result,
                "durableCompletionConfirmed",
                "DurableCompletionConfirmed",
                out var completionConfirmed)
            || !completionConfirmed
            || !TryReadString(result, "requestId", "RequestId", out var requestId))
        {
            return false;
        }

        return string.Equals(requestId, intent.IdempotencyKey, StringComparison.Ordinal);
    }

    public ValueTask<ActionReconciliationResult> ReconcileAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(
                intent.ToolName,
                AliCapabilityCatalog.ConsentParticipantMemoryProposalName,
                StringComparison.Ordinal))
        {
            // Consent approvals are intentionally never durable. If execution crossed
            // the prepared boundary and the process restarted before commit, the only
            // authoritative durable observation is that no consent grant survived.
            return ValueTask.FromResult(ActionReconciliationResult.Absent(
                "participant-consent-process-local-grant-absent"));
        }
        // A restart has no right to reconstruct a participant roster, consent, or
        // audience from prose. Keep the action unknown until the exact operation ID
        // is reconciled through the scoped participant-memory tool.
        return ValueTask.FromResult(ActionReconciliationResult.Unknown(
            "participant-memory-explicit-reconciliation-required"));
    }

    private static bool TryReadBoolean(
        JsonElement element,
        string camelName,
        string pascalName,
        out bool value)
    {
        if ((element.TryGetProperty(camelName, out var property)
                || element.TryGetProperty(pascalName, out property))
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryReadString(
        JsonElement element,
        string camelName,
        string pascalName,
        out string? value)
    {
        if ((element.TryGetProperty(camelName, out var property)
                || element.TryGetProperty(pascalName, out property))
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }
}

internal static class AliProductionDurableEffectAdapters
{
    internal static AliDurableEffectAdapterRegistry Create() => new(
    [
        new AliParticipantMemoryDurableEffectAdapter(),
        new AliCalendarDurableEffectAdapter()
    ]);
}
