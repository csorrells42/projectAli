using Ali.Modules.Coordinator;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Permissions;

/// <summary>
/// Ali-specific risk classification only. Agent Framework owns approval requests, argument
/// binding, standing rules, and session-scoped approval persistence.
/// </summary>
internal sealed class AliToolPermissionPolicy(Func<CoordinatorTurnContext?> turnAccessor)
{
    private static readonly HashSet<string> ApprovalRequiredTools =
    [
        AliCapabilityCatalog.RememberFactName,
        AliCapabilityCatalog.RememberCurrentUserName,
        AliCapabilityCatalog.CorrectCurrentUserMemoryName,
        AliCapabilityCatalog.ForgetCurrentUserMemoryName,
        AliCapabilityCatalog.ListCurrentUserMemoriesName,
        AliCapabilityCatalog.CreateReminderName,
        AliCapabilityCatalog.ResearchWebName
    ];

    public AIFunction Apply(AIFunction function)
        => Apply(function, ApprovalRequiredTools.Contains(function.Name));

    public AIFunction Apply(AIFunction function, bool requiresApproval)
    {
        var observable = new ActivityReportingAIFunction(function, turnAccessor);
        return requiresApproval
            ? new ApprovalRequiredAIFunction(observable)
            : observable;
    }
}
