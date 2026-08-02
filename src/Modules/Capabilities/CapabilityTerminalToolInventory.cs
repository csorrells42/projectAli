using Microsoft.Extensions.AI;

namespace Ali.Modules.Capabilities;

public enum CapabilityTerminalToolIssueCode
{
    UnknownTool,
    SchemaIdentityMismatch,
    DuplicateToolName,
    NonInvokableDeclaration,
    UnsupportedTool
}

public sealed record CapabilityTerminalToolIssue(
    string ToolIdentity,
    CapabilityTerminalToolIssueCode Code,
    string Message);

/// <summary>
/// Immutable, data-only identity inventory for the final tools accumulated by the
/// Agent Framework context-provider pipeline.
/// </summary>
public sealed class CapabilityTerminalToolInventory
{
    private CapabilityTerminalToolInventory(
        string revision,
        int observedToolCount,
        int functionDeclarationCount,
        IEnumerable<CapabilityRuntimeToolRegistration> registrations,
        IEnumerable<CapabilityTerminalToolIssue> issues)
    {
        Revision = revision;
        ObservedToolCount = observedToolCount;
        FunctionDeclarationCount = functionDeclarationCount;
        Registrations = Array.AsReadOnly(registrations.ToArray());
        Issues = Array.AsReadOnly(issues.ToArray());
    }

    public string Revision { get; }

    public int ObservedToolCount { get; }

    public int FunctionDeclarationCount { get; }

    public IReadOnlyList<CapabilityRuntimeToolRegistration> Registrations { get; }

    public IReadOnlyList<CapabilityTerminalToolIssue> Issues { get; }

    public static CapabilityTerminalToolInventory Create(
        IEnumerable<AITool> tools,
        CanonicalCapabilityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return Create(tools, registry.Descriptors);
    }

    public static CapabilityTerminalToolInventory Create(
        IEnumerable<AITool> tools,
        CapabilityRegistryRevisionSnapshot registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return Create(tools, registry.Descriptors);
    }

    private static CapabilityTerminalToolInventory Create(
        IEnumerable<AITool> tools,
        IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var observed = tools.ToArray();
        if (observed.Any(tool => tool is null))
        {
            throw new ArgumentException("Terminal tool inventory cannot contain null tools.", nameof(tools));
        }

        var descriptorsByName = descriptors.ToDictionary(
            descriptor => descriptor.ToolName,
            StringComparer.Ordinal);
        var declarations = observed
            .Select((tool, index) => new ObservedDeclaration(index, tool as AIFunctionDeclaration))
            .Where(item => item.Declaration is not null)
            .Select(item => item with { Declaration = item.Declaration! })
            .ToArray();
        var registrations = new List<CapabilityRuntimeToolRegistration>();
        var issues = new List<CapabilityTerminalToolIssue>();

        foreach (var item in observed.Select((tool, index) => (Tool: tool, Index: index))
                     .Where(item => item.Tool is not AIFunctionDeclaration))
        {
            var identity = $"tool[{item.Index}]/{item.Tool.GetType().FullName ?? item.Tool.GetType().Name}";
            issues.Add(new CapabilityTerminalToolIssue(
                identity,
                CapabilityTerminalToolIssueCode.UnsupportedTool,
                $"Terminal tool '{identity}' is not an AI function declaration and cannot be registered."));
        }

        foreach (var group in declarations
                     .GroupBy(item => item.Declaration!.Name, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var toolName = group.Key;
            if (string.IsNullOrWhiteSpace(toolName))
            {
                issues.Add(new CapabilityTerminalToolIssue(
                    $"function[{group.Min(item => item.Index)}]",
                    CapabilityTerminalToolIssueCode.UnsupportedTool,
                    "A terminal AI function declaration has no tool name."));
                continue;
            }

            var candidates = group
                .Select(item => item.Declaration!)
                .OrderBy(CapabilitySchemaIdentity.Calculate, StringComparer.Ordinal)
                .ToArray();
            var selected = candidates[0];
            var duplicate = candidates.Length > 1;
            descriptorsByName.TryGetValue(toolName, out var descriptor);
            var schemaFactoryId = duplicate
                ? $"ali.runtime.duplicate-schema.{toolName}"
                : descriptor?.SchemaFactoryId ?? $"ali.runtime.unassigned-schema.{toolName}";
            if (!duplicate && selected is not AIFunction)
            {
                schemaFactoryId = $"ali.runtime.non-invokable-schema.{toolName}";
            }

            registrations.Add(CapabilityRuntimeToolRegistration.Create(selected, schemaFactoryId));

            if (duplicate)
            {
                issues.Add(new CapabilityTerminalToolIssue(
                    toolName,
                    CapabilityTerminalToolIssueCode.DuplicateToolName,
                    $"Terminal tool name '{toolName}' occurs {candidates.Length} times and is quarantined."));
            }
            if (descriptor is null)
            {
                issues.Add(new CapabilityTerminalToolIssue(
                    toolName,
                    CapabilityTerminalToolIssueCode.UnknownTool,
                    $"Terminal tool '{toolName}' has no canonical descriptor and is quarantined."));
            }
            else if (!duplicate
                     && !string.Equals(
                         CapabilitySchemaIdentity.Calculate(selected),
                         descriptor.SchemaFingerprint,
                         StringComparison.Ordinal))
            {
                issues.Add(new CapabilityTerminalToolIssue(
                    toolName,
                    CapabilityTerminalToolIssueCode.SchemaIdentityMismatch,
                    $"Terminal tool '{toolName}' does not match canonical schema '{descriptor.SchemaFactoryId}'."));
            }
            if (!duplicate && selected is not AIFunction)
            {
                issues.Add(new CapabilityTerminalToolIssue(
                    toolName,
                    CapabilityTerminalToolIssueCode.NonInvokableDeclaration,
                    $"Terminal tool '{toolName}' is a declaration without an invokable AI function."));
            }
        }

        var frozenRegistrations = registrations
            .OrderBy(registration => registration.ToolName, StringComparer.Ordinal)
            .ToArray();
        var frozenIssues = issues
            .OrderBy(issue => issue.ToolIdentity, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code)
            .ToArray();
        return new CapabilityTerminalToolInventory(
            CalculateRevision(observed, declarations, frozenRegistrations, frozenIssues),
            observed.Length,
            declarations.Length,
            frozenRegistrations,
            frozenIssues);
    }

    private static string CalculateRevision(
        IReadOnlyList<AITool> observed,
        IReadOnlyList<ObservedDeclaration> declarations,
        IReadOnlyList<CapabilityRuntimeToolRegistration> registrations,
        IReadOnlyList<CapabilityTerminalToolIssue> issues)
    {
        using var revision = new CapabilityRevisionBuilder();
        revision.Add("ali-terminal-tool-inventory");
        revision.Add(observed.Count);
        revision.Add(declarations.Count);
        foreach (var declarationGroup in declarations
                     .GroupBy(item => item.Declaration!.Name, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            revision.Add(declarationGroup.Key);
            var fingerprints = declarationGroup
                .Select(item => CapabilitySchemaIdentity.Calculate(item.Declaration!))
                .Order(StringComparer.Ordinal)
                .ToArray();
            revision.Add(fingerprints);
        }
        var unsupportedTypes = observed
            .Where(tool => tool is not AIFunctionDeclaration)
            .Select(tool => tool.GetType().AssemblyQualifiedName ?? tool.GetType().FullName ?? tool.GetType().Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        revision.Add(unsupportedTypes);
        revision.Add(registrations.Count);
        foreach (var registration in registrations)
        {
            revision.Add(registration.ToolName);
            revision.Add(registration.SchemaFactoryId);
            revision.Add(registration.SchemaFingerprint);
        }
        revision.Add(issues.Count);
        foreach (var issue in issues)
        {
            revision.Add(issue.ToolIdentity);
            revision.Add((int)issue.Code);
            revision.Add(issue.Message);
        }
        return revision.Finish();
    }

    private sealed record ObservedDeclaration(int Index, AIFunctionDeclaration? Declaration);
}

/// <summary>
/// Immutable non-tool runtime state combined with a terminal tool inventory to create
/// the single exact runtime-availability publication used by both startup and enforcement.
/// </summary>
public sealed class CapabilityRuntimeStateSnapshot
{
    public CapabilityRuntimeStateSnapshot(
        string activeUserId,
        string providerRevision,
        IEnumerable<string> readyProviderIds,
        CapabilityTargetResolution? targetResolution,
        string permissionRevision,
        IEnumerable<string> allowedPermissionPolicyIds,
        string mcpRevision,
        IEnumerable<string> readyIncomingMcpToolNames,
        IEnumerable<string> enabledOutgoingMcpToolNames,
        string reconcilerRevision,
        IEnumerable<string> availableReconcilerIds,
        bool enforceReconcilerAvailability = false,
        bool enforceResolvedTargetBinding = false)
    {
        var validated = new CapabilityRuntimeAvailability(
            activeUserId,
            "runtime-state-validation",
            registeredTools: [],
            providerRevision,
            readyProviderIds,
            targetResolution,
            permissionRevision,
            allowedPermissionPolicyIds,
            mcpRevision,
            readyIncomingMcpToolNames,
            enabledOutgoingMcpToolNames,
            reconcilerRevision,
            availableReconcilerIds,
            enforceReconcilerAvailability,
            enforceResolvedTargetBinding);
        ActiveUserId = validated.ActiveUserId;
        ProviderRevision = validated.ProviderRevision;
        ReadyProviderIds = validated.ReadyProviderIds;
        TargetResolution = validated.TargetResolution;
        PermissionRevision = validated.PermissionRevision;
        AllowedPermissionPolicyIds = validated.AllowedPermissionPolicyIds;
        McpRevision = validated.McpRevision;
        ReadyIncomingMcpToolNames = validated.ReadyIncomingMcpToolNames;
        EnabledOutgoingMcpToolNames = validated.EnabledOutgoingMcpToolNames;
        ReconcilerRevision = validated.ReconcilerRevision;
        AvailableReconcilerIds = validated.AvailableReconcilerIds;
        EnforceReconcilerAvailability = validated.EnforceReconcilerAvailability;
        EnforceResolvedTargetBinding = validated.EnforceResolvedTargetBinding;
    }

    public string ActiveUserId { get; }

    public string ProviderRevision { get; }

    public IReadOnlySet<string> ReadyProviderIds { get; }

    public CapabilityTargetResolution? TargetResolution { get; }

    public string PermissionRevision { get; }

    public IReadOnlySet<string> AllowedPermissionPolicyIds { get; }

    public string McpRevision { get; }

    public IReadOnlySet<string> ReadyIncomingMcpToolNames { get; }

    public IReadOnlySet<string> EnabledOutgoingMcpToolNames { get; }

    public string ReconcilerRevision { get; }

    public IReadOnlySet<string> AvailableReconcilerIds { get; }

    public bool EnforceReconcilerAvailability { get; }

    public bool EnforceResolvedTargetBinding { get; }
}

public static class CapabilityRuntimeAvailabilityFactory
{
    public static CapabilityRuntimeAvailability Create(
        CapabilityTerminalToolInventory inventory,
        CapabilityRuntimeStateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return Create(inventory.Registrations, state);
    }

    internal static CapabilityRuntimeAvailability Create(
        IEnumerable<CapabilityRuntimeToolRegistration> registrations,
        CapabilityRuntimeStateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(state);
        var exactRegistrations = registrations
            .OrderBy(registration => registration.ToolName, StringComparer.Ordinal)
            .ToArray();
        return new CapabilityRuntimeAvailability(
            state.ActiveUserId,
            CalculateRevision(exactRegistrations, state),
            exactRegistrations,
            state.ProviderRevision,
            state.ReadyProviderIds,
            state.TargetResolution,
            state.PermissionRevision,
            state.AllowedPermissionPolicyIds,
            state.McpRevision,
            state.ReadyIncomingMcpToolNames,
            state.EnabledOutgoingMcpToolNames,
            state.ReconcilerRevision,
            state.AvailableReconcilerIds,
            state.EnforceReconcilerAvailability,
            state.EnforceResolvedTargetBinding);
    }

    private static string CalculateRevision(
        IReadOnlyList<CapabilityRuntimeToolRegistration> registrations,
        CapabilityRuntimeStateSnapshot state)
    {
        using var revision = new CapabilityRevisionBuilder();
        revision.Add("ali-capability-runtime-availability-v2");
        revision.Add(registrations.Count);
        foreach (var registration in registrations)
        {
            revision.Add(registration.ToolName);
            revision.Add(registration.SchemaFactoryId);
            revision.Add(registration.SchemaFingerprint);
        }
        revision.Add(state.ActiveUserId);
        revision.Add(state.ProviderRevision);
        revision.Add(state.ReadyProviderIds.Order(StringComparer.Ordinal).ToArray());
        revision.Add(state.TargetResolution is not null);
        if (state.TargetResolution is not null)
        {
            revision.Add(state.TargetResolution.TargetBindingId);
            revision.Add(state.TargetResolution.ProviderId);
            revision.Add(state.TargetResolution.ResolutionRevision);
            revision.Add(state.TargetResolution.ProviderRevision);
        }
        revision.Add(state.PermissionRevision);
        revision.Add(state.AllowedPermissionPolicyIds.Order(StringComparer.Ordinal).ToArray());
        revision.Add(state.McpRevision);
        revision.Add(state.ReadyIncomingMcpToolNames.Order(StringComparer.Ordinal).ToArray());
        revision.Add(state.EnabledOutgoingMcpToolNames.Order(StringComparer.Ordinal).ToArray());
        revision.Add(state.ReconcilerRevision);
        revision.Add(state.AvailableReconcilerIds.Order(StringComparer.Ordinal).ToArray());
        revision.Add(state.EnforceReconcilerAvailability);
        revision.Add(state.EnforceResolvedTargetBinding);
        return revision.Finish();
    }
}
