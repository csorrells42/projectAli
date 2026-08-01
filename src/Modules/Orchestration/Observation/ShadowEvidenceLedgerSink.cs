using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Orchestration.Observation;

internal interface IShadowObservationSink
{
    ValueTask PersistAsync(
        ShadowToolObservation observation,
        CancellationToken cancellationToken);
}

internal sealed class ShadowEvidenceLedgerSink : IShadowObservationSink
{
    private const string ObserverRevision = "checkpoint-1c";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement OmittedArguments = CreateOmissionSentinel("arguments");
    private static readonly JsonElement OmittedResult = CreateOmissionSentinel("result");
    private readonly EvidenceLedger _ledger;

    public ShadowEvidenceLedgerSink(EvidenceLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledger = ledger;
    }

    public async ValueTask PersistAsync(
        ShadowToolObservation observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var draft = CreateDraft(observation);
        await _ledger.AppendAsync(
            observation.Identity,
            draft,
            cancellationToken).ConfigureAwait(false);
    }

    internal static EvidenceDraft CreateDraft(ShadowToolObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var (outcome, stableOutcomeCode) = CreateOutcome(observation);
        var normalizedTarget = JsonSerializer.SerializeToElement(
            new { targetNormalization = "unavailable-in-shadow" },
            JsonOptions);
        var normalizedEffectResult = JsonSerializer.SerializeToElement(
            new
            {
                stableOutcomeCode,
                invocationStatus = observation.InvocationStatus.ToString()
            },
            JsonOptions);
        var permissionReceipt = JsonSerializer.SerializeToElement(
            new
            {
                observation.Permission.Decision,
                observation.Permission.Scope
            },
            JsonOptions);
        var provenance = JsonSerializer.SerializeToElement(
            new
            {
                observer = "checkpoint-1c-shadow",
                plannerVisible = false
            },
            JsonOptions);

        return new EvidenceDraft
        {
            CallId = observation.CallId,
            ToolName = observation.ToolName,
            CapabilityGroup = "shadow-observation",
            ProviderId = "agent-framework",
            RegistryRevision = ObserverRevision,
            EffectKind = "mixed",
            Arguments = OmittedArguments,
            Result = OmittedResult,
            NormalizedTarget = normalizedTarget,
            NormalizedEffectResult = normalizedEffectResult,
            Outcome = outcome,
            StableOutcomeCode = stableOutcomeCode,
            StartedAtUtc = observation.StartedAtUtc.ToUniversalTime(),
            CompletedAtUtc = observation.CompletedAtUtc.ToUniversalTime(),
            Artifacts = [],
            Permission = observation.Permission with { },
            ProtectedPermissionReceipt = permissionReceipt,
            Source = new EvidenceSourceMetadata(
                "tool",
                "agent-framework",
                "unknown",
                observation.CompletedAtUtc.ToUniversalTime(),
                ObserverRevision),
            ProtectedProvenance = provenance
        };
    }

    private static (ToolInvocationOutcome Outcome, string StableCode) CreateOutcome(
        ShadowToolObservation observation)
    {
        return observation.InvocationStatus switch
        {
            InvocationStatus.Returned => CreateReturnedOutcome(observation.ReportedSuccess),
            InvocationStatus.Denied => (
                ToolInvocationOutcome.Denied(EmptyToNull(observation.FailureCode)),
                "denied"),
            InvocationStatus.Threw => (
                ToolInvocationOutcome.ThrewType(
                    observation.ExceptionType
                    ?? throw new InvalidOperationException("A thrown observation has no exception type.")),
                "threw"),
            InvocationStatus.Cancelled => (ToolInvocationOutcome.Cancelled(), "cancelled"),
            _ => throw new InvalidOperationException("The observation has an unsupported terminal status.")
        };
    }

    private static (ToolInvocationOutcome Outcome, string StableCode) CreateReturnedOutcome(
        bool? reportedSuccess)
    {
        var stableCode = reportedSuccess switch
        {
            true => "returned-succeeded",
            false => "returned-failed",
            null => "returned-unreported"
        };
        var redactedDigestMaterial = Encoding.UTF8.GetBytes(stableCode);
        return (ToolInvocationOutcome.Returned(redactedDigestMaterial, reportedSuccess), stableCode);
    }

    private static JsonElement CreateOmissionSentinel(string field) =>
        JsonSerializer.SerializeToElement(
            new
            {
                capture = "omitted-in-shadow",
                field,
                reason = "live-path-isolation"
            },
            JsonOptions);

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
