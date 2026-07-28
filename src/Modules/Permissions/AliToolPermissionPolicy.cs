using Ali.Modules.Coordinator;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Permissions;

/// <summary>
/// Ali-specific risk classification only. Agent Framework owns approval requests, argument
/// binding, standing rules, and session-scoped approval persistence.
/// </summary>
internal sealed class AliToolPermissionPolicy(
    Func<CoordinatorTurnContext?> turnAccessor,
    Func<AgentPermissionProfile>? profileAccessor = null)
{
    private static IReadOnlyList<AgentToolPermissionDefinition> TrustedWorkstationTools { get; } =
    [
        new(AliCapabilityCatalog.RememberFactName, "Writes shared local memory"),
        new(AliCapabilityCatalog.RememberCurrentUserName, "Writes personal long-term memory"),
        new(AliCapabilityCatalog.CorrectCurrentUserMemoryName, "Changes personal long-term memory"),
        new(AliCapabilityCatalog.ForgetCurrentUserMemoryName, "Deletes personal long-term memory"),
        new(AliCapabilityCatalog.ListCurrentUserMemoriesName, "Reads private personal memory"),
        new(AliCapabilityCatalog.CreateReminderName, "Creates a persistent reminder"),
        new(AliCapabilityCatalog.ResearchWebName, "Starts metered deep web research")
    ];

    private static IReadOnlyList<AgentToolPermissionDefinition> LockedDownAdditionalTools { get; } =
    [
        new(AliCapabilityCatalog.SearchMemoryName, "Reads shared local memory"),
        new(AliCapabilityCatalog.RecallUserMemoryName, "Reads private personal memory"),
        new(AliCapabilityCatalog.SearchCurrentWebName, "Transmits a query to configured web sources"),
        new(AliCapabilityCatalog.SearchLocalLibraryName, "Reads indexed local documents")
    ];

    internal static IReadOnlyList<AgentToolPermissionDefinition> ProtectedTools => TrustedWorkstationTools;

    internal static IReadOnlyList<AgentToolPermissionDefinition> ProtectedToolsFor(AgentPermissionProfile profile) =>
        profile == AgentPermissionProfile.LockedDown
            ? TrustedWorkstationTools.Concat(LockedDownAdditionalTools).ToArray()
            : TrustedWorkstationTools;

    private static readonly HashSet<string> ApprovalRequiredTools =
        TrustedWorkstationTools.Select(tool => tool.ToolName).ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> LockedDownApprovalRequiredTools =
        ProtectedToolsFor(AgentPermissionProfile.LockedDown)
            .Select(tool => tool.ToolName)
            .ToHashSet(StringComparer.Ordinal);

    public AIFunction Apply(AIFunction function)
        => Apply(
            function,
            RequiresApproval(
                function.Name,
                profileAccessor?.Invoke() ?? AgentPermissionProfile.TrustedWorkstation));

    internal static bool RequiresApproval(string toolName) =>
        ApprovalRequiredTools.Contains(toolName);

    internal static bool RequiresApproval(string toolName, AgentPermissionProfile profile) =>
        profile == AgentPermissionProfile.LockedDown
            ? LockedDownApprovalRequiredTools.Contains(toolName)
            : ApprovalRequiredTools.Contains(toolName);

    public AIFunction Apply(AIFunction function, bool requiresApproval)
    {
        var observable = new ActivityReportingAIFunction(function, turnAccessor);
        return requiresApproval
            ? new ApprovalRequiredAIFunction(observable)
            : observable;
    }
}

public sealed record AgentToolPermissionDefinition(string ToolName, string Reason);
