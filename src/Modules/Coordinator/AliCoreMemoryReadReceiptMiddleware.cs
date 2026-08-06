using Ali.Modules.Orchestration.Evidence;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coordinator;

/// <summary>
/// The fast core-assistant path intentionally skips the full outcome/receipt
/// middleware for speed (see AliFrameworkProviderOutcomeMiddleware). Participant
/// memory tools require an admitted exact tool permission receipt even so, so
/// this records a narrow, auto-approved receipt for exactly the participant
/// memory tool names and is a no-op for every other tool. The mutate tool's
/// receipt is honestly labeled as a policy auto-approval (Source "auto-policy"),
/// never as "interactive-user" — it does not and must not satisfy a real
/// interactive-once approval anywhere that check is meant literally; it only
/// works here because AliParticipantMemoryTools was extended to recognize this
/// distinctly-labeled fast-path source as a second, deliberate approval route
/// (the owner explicitly chose no-prompt auto-approval for this path).
/// </summary>
internal static class AliCoreMemoryReadReceiptMiddleware
{
    private static readonly IReadOnlySet<string> MemoryReadToolNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            AliCapabilityCatalog.RecallUserMemoryName,
            AliCapabilityCatalog.ListCurrentUserMemoriesName
        };

    internal static AIAgent WithMemoryReadReceipts(
        AIAgent agent,
        Func<CoordinatorTurnContext?> turnAccessor)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(turnAccessor);

        var builder = new AIAgentBuilder(agent);
        builder.Use(async (
            AIAgent _,
            FunctionInvocationContext context,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
            CancellationToken cancellationToken) =>
        {
            var toolName = context.Function.Name;
            var callId = context.CallContent.CallId;
            var isMutate = string.Equals(
                toolName,
                AliCapabilityCatalog.MutateParticipantMemoryName,
                StringComparison.Ordinal);
            if ((!MemoryReadToolNames.Contains(toolName) && !isMutate)
                || string.IsNullOrWhiteSpace(callId))
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }

            var turn = turnAccessor();
            if (turn is null
                || !turn.TryEnterLightweightToolInvocation(callId, toolName, out var scope)
                || scope is null)
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }

            using (scope)
            {
                turn.RecordShadowPermission(
                    callId,
                    new EvidencePermissionMetadata(
                        "approved-policy",
                        isMutate ? "core-memory-write" : "core-memory-read"),
                    source: "auto-policy");
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
        });
        return builder.Build();
    }
}
