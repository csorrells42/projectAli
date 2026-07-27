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
        AliCapabilityCatalog.CreateReminderName
    ];

    public AIFunction Apply(AIFunction function)
    {
        var observable = new ActivityReportingAIFunction(function, turnAccessor);
        return ApprovalRequiredTools.Contains(function.Name)
            ? new ApprovalRequiredAIFunction(observable)
            : observable;
    }
}
