using System.Collections.Frozen;
using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Capabilities;

public enum CapabilityQuarantineReasonCode
{
    MissingCanonicalDescriptor,
    SchemaIdentityMismatch
}

public sealed class CapabilityRuntimeToolRegistration
{
    private CapabilityRuntimeToolRegistration(
        string toolName,
        string schemaFactoryId,
        string schemaFingerprint)
    {
        ToolName = toolName;
        SchemaFactoryId = schemaFactoryId;
        SchemaFingerprint = schemaFingerprint;
    }

    public string ToolName { get; }

    public string SchemaFactoryId { get; }

    public string SchemaFingerprint { get; }

    public static CapabilityRuntimeToolRegistration Create(
        AITool tool,
        string schemaFactoryId)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaFactoryId);
        if (tool is not AIFunctionDeclaration function)
        {
            throw new ArgumentException(
                "A runtime capability registration must contain an AI function declaration.",
                nameof(tool));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(function.Name);

        return new CapabilityRuntimeToolRegistration(
            function.Name,
            schemaFactoryId,
            CapabilitySchemaIdentity.Calculate(function));
    }
}

public sealed class CapabilityTargetResolution
{
    public CapabilityTargetResolution(
        string targetBindingId,
        string providerId,
        string resolutionRevision,
        string providerRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBindingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolutionRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRevision);
        TargetBindingId = targetBindingId;
        ProviderId = providerId;
        ResolutionRevision = resolutionRevision;
        ProviderRevision = providerRevision;
    }

    public string TargetBindingId { get; }

    public string ProviderId { get; }

    public string ResolutionRevision { get; }

    public string ProviderRevision { get; }
}

public sealed record QuarantinedCapability(
    string ToolName,
    CapabilityQuarantineReasonCode Code,
    string Message);

public sealed class CapabilityRuntimeAvailability
{
    public CapabilityRuntimeAvailability(
        string activeUserId,
        string runtimeRevision,
        IEnumerable<CapabilityRuntimeToolRegistration> registeredTools,
        string providerRevision,
        IEnumerable<string> readyProviderIds,
        CapabilityTargetResolution? targetResolution,
        string permissionRevision,
        IEnumerable<string> allowedPermissionPolicyIds,
        string mcpRevision,
        IEnumerable<string> readyIncomingMcpToolNames,
        IEnumerable<string> enabledOutgoingMcpToolNames,
        string reconcilerRevision,
        IEnumerable<string> availableReconcilerIds)
    {
        ActiveUserId = RequireText(activeUserId, nameof(activeUserId));
        RuntimeRevision = RequireText(runtimeRevision, nameof(runtimeRevision));
        ProviderRevision = RequireText(providerRevision, nameof(providerRevision));
        PermissionRevision = RequireText(permissionRevision, nameof(permissionRevision));
        McpRevision = RequireText(mcpRevision, nameof(mcpRevision));
        ReconcilerRevision = RequireText(reconcilerRevision, nameof(reconcilerRevision));
        RegisteredToolsByName = FreezeRegistrations(registeredTools);
        ReadyProviderIds = FreezeSet(readyProviderIds, nameof(readyProviderIds));
        TargetResolution = targetResolution;
        AllowedPermissionPolicyIds = FreezeSet(
            allowedPermissionPolicyIds,
            nameof(allowedPermissionPolicyIds));
        ReadyIncomingMcpToolNames = FreezeSet(
            readyIncomingMcpToolNames,
            nameof(readyIncomingMcpToolNames));
        EnabledOutgoingMcpToolNames = FreezeSet(
            enabledOutgoingMcpToolNames,
            nameof(enabledOutgoingMcpToolNames));
        AvailableReconcilerIds = FreezeSet(
            availableReconcilerIds,
            nameof(availableReconcilerIds));
    }

    public string ActiveUserId { get; }

    public string RuntimeRevision { get; }

    public IReadOnlyDictionary<string, CapabilityRuntimeToolRegistration> RegisteredToolsByName { get; }

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

    private static IReadOnlyDictionary<string, CapabilityRuntimeToolRegistration> FreezeRegistrations(
        IEnumerable<CapabilityRuntimeToolRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var copy = new Dictionary<string, CapabilityRuntimeToolRegistration>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.ToolName);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.SchemaFactoryId);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.SchemaFingerprint);
            if (!copy.TryAdd(registration.ToolName, registration))
            {
                throw new ArgumentException(
                    $"Runtime registrations contain duplicate tool '{registration.ToolName}'.",
                    nameof(registrations));
            }
        }

        return copy.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    private static IReadOnlySet<string> FreezeSet(
        IEnumerable<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var copy = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
            if (!copy.Add(value))
            {
                throw new ArgumentException(
                    $"{parameterName} contains duplicate '{value}'.",
                    parameterName);
            }
        }

        return copy.ToFrozenSet(StringComparer.Ordinal);
    }
}

public sealed record CapabilityAvailabilityReason(
    CapabilityAvailabilityReasonCode Code,
    string DependencyId,
    string Message);

public sealed record UnavailableCapability(
    CapabilityDescriptor Descriptor,
    IReadOnlyList<CapabilityAvailabilityReason> Reasons);

public sealed class CapabilityInvocationLease
{
    internal CapabilityInvocationLease(
        string toolName,
        string capabilityId,
        string schemaFactoryId,
        string schemaFingerprint,
        string planningResolutionRevision,
        string registryRevision,
        string settingsRevision,
        string activeUserId,
        string runtimeRevision,
        string providerRevision,
        string permissionRevision,
        string mcpRevision,
        string reconcilerRevision,
        IReadOnlyList<string> eligibleProviderIds,
        bool requiresTargetResolution)
    {
        ToolName = toolName;
        CapabilityId = capabilityId;
        SchemaFactoryId = schemaFactoryId;
        SchemaFingerprint = schemaFingerprint;
        PlanningResolutionRevision = planningResolutionRevision;
        RegistryRevision = registryRevision;
        SettingsRevision = settingsRevision;
        ActiveUserId = activeUserId;
        RuntimeRevision = runtimeRevision;
        ProviderRevision = providerRevision;
        PermissionRevision = permissionRevision;
        McpRevision = mcpRevision;
        ReconcilerRevision = reconcilerRevision;
        EligibleProviderIds = eligibleProviderIds;
        RequiresTargetResolution = requiresTargetResolution;
    }

    public string ToolName { get; }

    public string CapabilityId { get; }

    public string SchemaFactoryId { get; }

    public string SchemaFingerprint { get; }

    public string PlanningResolutionRevision { get; }

    public string RegistryRevision { get; }

    public string SettingsRevision { get; }

    public string ActiveUserId { get; }

    public string RuntimeRevision { get; }

    public string ProviderRevision { get; }

    public string PermissionRevision { get; }

    public string McpRevision { get; }

    public string ReconcilerRevision { get; }

    public IReadOnlyList<string> EligibleProviderIds { get; }

    public bool RequiresTargetResolution { get; }
}

public sealed class CapabilityInvocationValidation
{
    internal CapabilityInvocationValidation(
        string currentResolutionRevision,
        CapabilityDescriptor? descriptor,
        IReadOnlyList<CapabilityAvailabilityReason> reasons)
    {
        CurrentResolutionRevision = currentResolutionRevision;
        Descriptor = descriptor;
        Reasons = reasons;
    }

    public string CurrentResolutionRevision { get; }

    public CapabilityDescriptor? Descriptor { get; }

    public IReadOnlyList<CapabilityAvailabilityReason> Reasons { get; }

    public bool Success => Descriptor is not null && Reasons.Count == 0;
}

public sealed class CapabilityResolver
{
    public CapabilityResolutionSnapshot ResolvePlanning(
        CapabilityRegistryRevisionSnapshot registry,
        CapabilityRuntimeAvailability runtime)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runtime);

        var enabledGroups = registry.Groups
            .Where(group => IsGroupEnabled(registry, group.Id))
            .Select(group => group with { })
            .ToArray();
        var descriptorsByToolName = registry.Descriptors.ToDictionary(
            descriptor => descriptor.ToolName,
            StringComparer.Ordinal);
        var providerBindingsById = registry.ProviderBindings.ToDictionary(
            binding => binding.ProviderId,
            StringComparer.Ordinal);
        var effective = new List<CapabilityDescriptor>();
        var unavailable = new List<UnavailableCapability>();
        var eligibleProvidersByToolName = new SortedDictionary<string, IReadOnlyList<string>>(
            StringComparer.Ordinal);
        var requiresTargetResolution = new List<string>();

        foreach (var descriptor in registry.Descriptors.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var reasons = new List<CapabilityAvailabilityReason>();
            ResolveTaskGroupReasons(descriptor, registry, reasons);
            ResolveRuntimeIdentityReasons(descriptor, runtime, reasons);
            ResolveOwnerProviderReason(descriptor, runtime, reasons);
            var eligibleProviders = ResolveProviderGateReasons(
                descriptor,
                registry,
                runtime,
                providerBindingsById,
                reasons);
            eligibleProvidersByToolName.Add(
                descriptor.ToolName,
                Array.AsReadOnly(eligibleProviders.ToArray()));
            ResolvePermissionReason(descriptor, runtime, reasons);
            ResolveMcpReason(descriptor, runtime, reasons);
            ResolveReconcilerReason(descriptor, runtime, reasons);

            if (reasons.Count == 0)
            {
                effective.Add(descriptor);
                if (descriptor.ProviderGate.Kind == CapabilityProviderGateKind.ResolvedTarget)
                {
                    requiresTargetResolution.Add(descriptor.ToolName);
                }
            }
            else
            {
                unavailable.Add(
                    new UnavailableCapability(
                        descriptor,
                        Array.AsReadOnly(reasons.ToArray())));
            }
        }

        var quarantined = ResolveQuarantine(runtime, descriptorsByToolName);
        var effectiveByToolName = new ReadOnlyDictionary<string, CapabilityDescriptor>(
            effective.ToDictionary(descriptor => descriptor.ToolName, StringComparer.Ordinal));
        var outgoingMcpDescriptors = effective
            .Where(descriptor => descriptor.McpExposure.Exposed)
            .Where(descriptor => runtime.EnabledOutgoingMcpToolNames.Contains(descriptor.ToolName))
            .OrderBy(descriptor => descriptor.McpExposure.PublishedName, StringComparer.Ordinal)
            .ToArray();
        var frozenEligibleProviders = new ReadOnlyDictionary<string, IReadOnlyList<string>>(
            eligibleProvidersByToolName);
        var resolutionRevision = CalculateResolutionRevision(
            registry,
            runtime,
            effective,
            unavailable,
            quarantined,
            outgoingMcpDescriptors,
            frozenEligibleProviders,
            requiresTargetResolution);

        return new CapabilityResolutionSnapshot(
            resolutionRevision,
            registry.RegistryRevision,
            registry.SettingsRevision,
            runtime.ActiveUserId,
            runtime.RuntimeRevision,
            runtime.ProviderRevision,
            runtime.PermissionRevision,
            runtime.McpRevision,
            runtime.ReconcilerRevision,
            Array.AsReadOnly(enabledGroups),
            Array.AsReadOnly(effective.ToArray()),
            Array.AsReadOnly(unavailable.ToArray()),
            Array.AsReadOnly(quarantined.ToArray()),
            Array.AsReadOnly(outgoingMcpDescriptors),
            frozenEligibleProviders,
            Array.AsReadOnly(requiresTargetResolution.Order(StringComparer.Ordinal).ToArray()),
            effectiveByToolName);
    }

    public CapabilityInvocationValidation ValidateInvocation(
        CapabilityRegistryRevisionSnapshot registry,
        CapabilityRuntimeAvailability runtime,
        CapabilityInvocationLease lease,
        string? boundTargetBindingId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(lease);

        var current = ResolvePlanning(registry, runtime);
        var reasons = new List<CapabilityAvailabilityReason>();
        AddStaleReason(reasons, "registry", lease.RegistryRevision, current.RegistryRevision);
        AddStaleReason(reasons, "settings", lease.SettingsRevision, current.SettingsRevision);
        AddStaleReason(reasons, "active-user", lease.ActiveUserId, current.ActiveUserId);
        AddStaleReason(reasons, "runtime", lease.RuntimeRevision, current.RuntimeRevision);
        AddStaleReason(reasons, "providers", lease.ProviderRevision, current.ProviderRevision);
        AddStaleReason(reasons, "permissions", lease.PermissionRevision, current.PermissionRevision);
        AddStaleReason(reasons, "mcp", lease.McpRevision, current.McpRevision);
        AddStaleReason(reasons, "reconcilers", lease.ReconcilerRevision, current.ReconcilerRevision);
        AddStaleReason(
            reasons,
            "planning-resolution",
            lease.PlanningResolutionRevision,
            current.ResolutionRevision);

        CapabilityDescriptor? descriptor = null;
        if (!current.TryGetTool(lease.ToolName, out descriptor!))
        {
            AddReason(
                reasons,
                CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                lease.ToolName,
                $"Tool '{lease.ToolName}' is no longer callable.");
            var unavailable = current.UnavailableDescriptors.FirstOrDefault(
                item => string.Equals(item.Descriptor.ToolName, lease.ToolName, StringComparison.Ordinal));
            if (unavailable is not null)
            {
                foreach (var reason in unavailable.Reasons)
                {
                    AddReason(reasons, reason.Code, reason.DependencyId, reason.Message);
                }
            }
        }
        else
        {
            if (!string.Equals(descriptor.Id, lease.CapabilityId, StringComparison.Ordinal)
                || !string.Equals(descriptor.SchemaFactoryId, lease.SchemaFactoryId, StringComparison.Ordinal)
                || !string.Equals(descriptor.SchemaFingerprint, lease.SchemaFingerprint, StringComparison.Ordinal))
            {
                AddReason(
                    reasons,
                    CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                    lease.ToolName,
                    $"Tool '{lease.ToolName}' no longer matches the planned capability schema.");
            }

            var currentEligible = current.EligibleProviderIdsByToolName[lease.ToolName];
            if (!currentEligible.SequenceEqual(lease.EligibleProviderIds, StringComparer.Ordinal))
            {
                AddReason(
                    reasons,
                    CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                    "eligible-providers",
                    "The eligible provider set changed after planning.");
            }

            if (descriptor.ProviderGate.Kind == CapabilityProviderGateKind.ResolvedTarget)
            {
                ValidateExactTarget(
                    registry,
                    runtime,
                    lease,
                    descriptor,
                    currentEligible,
                    boundTargetBindingId,
                    reasons);
            }
        }

        return new CapabilityInvocationValidation(
            current.ResolutionRevision,
            reasons.Count == 0 ? descriptor : null,
            Array.AsReadOnly(reasons.ToArray()));
    }

    private static void ValidateExactTarget(
        CapabilityRegistryRevisionSnapshot registry,
        CapabilityRuntimeAvailability runtime,
        CapabilityInvocationLease lease,
        CapabilityDescriptor descriptor,
        IReadOnlyList<string> currentEligibleProviderIds,
        string? boundTargetBindingId,
        ICollection<CapabilityAvailabilityReason> reasons)
    {
        var target = runtime.TargetResolution;
        if (target is null)
        {
            AddReason(
                reasons,
                CapabilityAvailabilityReasonCode.TargetProviderUnresolved,
                descriptor.ToolName,
                $"Tool '{descriptor.ToolName}' requires an exact target provider before invocation.");
            return;
        }
        if (string.IsNullOrWhiteSpace(boundTargetBindingId)
            || !string.Equals(target.TargetBindingId, boundTargetBindingId, StringComparison.Ordinal))
        {
            AddReason(
                reasons,
                CapabilityAvailabilityReasonCode.TargetBindingMismatch,
                descriptor.ToolName,
                $"The resolved target does not match the target bound to '{descriptor.ToolName}'.");
        }
        if (!string.Equals(target.ProviderRevision, runtime.ProviderRevision, StringComparison.Ordinal)
            || !string.Equals(target.ProviderRevision, lease.ProviderRevision, StringComparison.Ordinal))
        {
            AddReason(
                reasons,
                CapabilityAvailabilityReasonCode.TargetResolutionStale,
                target.ResolutionRevision,
                "The target provider resolution was produced under a stale provider revision.");
        }
        if (!descriptor.ProviderGate.SupportedProviderIds.Contains(
                target.ProviderId,
                StringComparer.Ordinal))
        {
            AddReason(
                reasons,
                CapabilityAvailabilityReasonCode.TargetProviderUnsupported,
                target.ProviderId,
                $"Resolved target provider '{target.ProviderId}' is not supported by '{descriptor.ToolName}'.");
            return;
        }
        if (!currentEligibleProviderIds.Contains(target.ProviderId, StringComparer.Ordinal)
            || !lease.EligibleProviderIds.Contains(target.ProviderId, StringComparer.Ordinal))
        {
            var binding = registry.ProviderBindings.First(item =>
                string.Equals(item.ProviderId, target.ProviderId, StringComparison.Ordinal));
            if (!IsGroupEnabled(registry, binding.GroupId))
            {
                AddReason(
                    reasons,
                    CapabilityAvailabilityReasonCode.PrerequisiteGroupDisabled,
                    binding.GroupId,
                    $"Resolved provider capability group '{binding.GroupId}' is disabled.");
            }
            if (!runtime.ReadyProviderIds.Contains(target.ProviderId))
            {
                AddReason(
                    reasons,
                    CapabilityAvailabilityReasonCode.ProviderUnavailable,
                    target.ProviderId,
                    $"Resolved target provider '{target.ProviderId}' is unavailable.");
            }
            if (currentEligibleProviderIds.Contains(target.ProviderId, StringComparer.Ordinal)
                && !lease.EligibleProviderIds.Contains(target.ProviderId, StringComparer.Ordinal))
            {
                AddReason(
                    reasons,
                    CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                    target.ProviderId,
                    $"Resolved target provider '{target.ProviderId}' was not eligible when the tool was planned.");
            }
        }
    }

    private static void ResolveTaskGroupReasons(
        CapabilityDescriptor descriptor,
        CapabilityRegistryRevisionSnapshot registry,
        ICollection<CapabilityAvailabilityReason> reasons)
    {
        if (descriptor.Tier != CapabilityTier.Task)
        {
            return;
        }

        if (!IsGroupEnabled(registry, descriptor.GroupId!))
        {
            AddReason(
                reasons,
                CapabilityAvailabilityReasonCode.GroupDisabled,
                descriptor.GroupId!,
                $"Capability group '{descriptor.GroupId}' is disabled.");
        }

        foreach (var prerequisiteGroupId in descriptor.PrerequisiteGroupIds)
        {
            if (!IsGroupEnabled(registry, prerequisiteGroupId))
            {
                AddReason(
                    reasons,
                    CapabilityAvailabilityReasonCode.PrerequisiteGroupDisabled,
                    prerequisiteGroupId,
                    $"Prerequisite capability group '{prerequisiteGroupId}' is disabled.");
            }
        }
    }

    private static void ResolveRuntimeIdentityReasons(
        CapabilityDescriptor descriptor,
        CapabilityRuntimeAvailability runtime,
        ICollection<CapabilityAvailabilityReason> reasons)
    {
        if (!runtime.RegisteredToolsByName.TryGetValue(descriptor.ToolName, out var registration))
        {
            AddReason(
                reasons,
                CapabilityAvailabilityReasonCode.RuntimeToolMissing,
                descriptor.ToolName,
                $"Runtime tool '{descriptor.ToolName}' is not registered.");
            return;
        }

        if (!string.Equals(registration.SchemaFactoryId, descriptor.SchemaFactoryId, StringComparison.Ordinal)
            || !string.Equals(
                registration.SchemaFingerprint,
                descriptor.SchemaFingerprint,
                StringComparison.Ordinal))
        {
            AddReason(
                reasons,
                CapabilityAvailabilityReasonCode.RuntimeToolIdentityMismatch,
                descriptor.ToolName,
                $"Runtime tool '{descriptor.ToolName}' does not match canonical schema '{descriptor.SchemaFactoryId}'.");
        }
    }

    private static void ResolveOwnerProviderReason(
        CapabilityDescriptor descriptor,
        CapabilityRuntimeAvailability runtime,
        ICollection<CapabilityAvailabilityReason> reasons)
    {
        if (!runtime.ReadyProviderIds.Contains(descriptor.ProviderId))
        {
            AddReason(
                reasons,
                CapabilityAvailabilityReasonCode.ProviderUnavailable,
                descriptor.ProviderId,
                $"Provider '{descriptor.ProviderId}' is unavailable.");
        }
    }

    private static IReadOnlyList<string> ResolveProviderGateReasons(
        CapabilityDescriptor descriptor,
        CapabilityRegistryRevisionSnapshot registry,
        CapabilityRuntimeAvailability runtime,
        IReadOnlyDictionary<string, CapabilityProviderBinding> providerBindingsById,
        ICollection<CapabilityAvailabilityReason> reasons)
    {
        if (descriptor.ProviderGate.Kind == CapabilityProviderGateKind.OwnerOnly)
        {
            return Array.Empty<string>();
        }

        var eligible = descriptor.ProviderGate.SupportedProviderIds
            .Where(providerId => runtime.ReadyProviderIds.Contains(providerId))
            .Where(providerId => IsGroupEnabled(registry, providerBindingsById[providerId].GroupId))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (eligible.Length > 0)
        {
            return eligible;
        }

        foreach (var providerId in descriptor.ProviderGate.SupportedProviderIds)
        {
            var binding = providerBindingsById[providerId];
            if (!IsGroupEnabled(registry, binding.GroupId))
            {
                AddReason(
                    reasons,
                    CapabilityAvailabilityReasonCode.PrerequisiteGroupDisabled,
                    binding.GroupId,
                    $"Provider capability group '{binding.GroupId}' is disabled.");
            }
            if (!runtime.ReadyProviderIds.Contains(providerId))
            {
                AddReason(
                    reasons,
                    CapabilityAvailabilityReasonCode.ProviderUnavailable,
                    providerId,
                    $"Provider '{providerId}' is unavailable.");
            }
        }

        return eligible;
    }

    private static void ResolvePermissionReason(
        CapabilityDescriptor descriptor,
        CapabilityRuntimeAvailability runtime,
        ICollection<CapabilityAvailabilityReason> reasons)
    {
        if (!runtime.AllowedPermissionPolicyIds.Contains(descriptor.Permission.PolicyId))
        {
            AddReason(
                reasons,
                CapabilityAvailabilityReasonCode.PermissionUnavailable,
                descriptor.Permission.PolicyId,
                $"Permission policy '{descriptor.Permission.PolicyId}' is unavailable for the active user.");
        }
    }

    private static void ResolveMcpReason(
        CapabilityDescriptor descriptor,
        CapabilityRuntimeAvailability runtime,
        ICollection<CapabilityAvailabilityReason> reasons)
    {
        if (descriptor.RegistrationKind == CapabilityRegistrationKind.Mcp
            && !runtime.ReadyIncomingMcpToolNames.Contains(descriptor.ToolName))
        {
            AddReason(
                reasons,
                CapabilityAvailabilityReasonCode.McpUnavailable,
                descriptor.ToolName,
                $"Incoming MCP tool '{descriptor.ToolName}' is unavailable.");
        }
    }

    private static void ResolveReconcilerReason(
        CapabilityDescriptor descriptor,
        CapabilityRuntimeAvailability runtime,
        ICollection<CapabilityAvailabilityReason> reasons)
    {
        if (descriptor.Effect.IsMutation
            && !runtime.AvailableReconcilerIds.Contains(descriptor.Effect.ReconcilerId!))
        {
            AddReason(
                reasons,
                CapabilityAvailabilityReasonCode.ReconcilerUnavailable,
                descriptor.Effect.ReconcilerId!,
                $"Reconciler '{descriptor.Effect.ReconcilerId}' is unavailable.");
        }
    }

    private static IReadOnlyList<QuarantinedCapability> ResolveQuarantine(
        CapabilityRuntimeAvailability runtime,
        IReadOnlyDictionary<string, CapabilityDescriptor> descriptorsByToolName)
    {
        var quarantined = new List<QuarantinedCapability>();
        foreach (var registration in runtime.RegisteredToolsByName.Values.OrderBy(
                     item => item.ToolName,
                     StringComparer.Ordinal))
        {
            if (!descriptorsByToolName.TryGetValue(registration.ToolName, out var descriptor))
            {
                quarantined.Add(
                    new QuarantinedCapability(
                        registration.ToolName,
                        CapabilityQuarantineReasonCode.MissingCanonicalDescriptor,
                        $"Runtime tool '{registration.ToolName}' has no complete canonical descriptor."));
            }
            else if (!string.Equals(
                         registration.SchemaFactoryId,
                         descriptor.SchemaFactoryId,
                         StringComparison.Ordinal)
                     || !string.Equals(
                         registration.SchemaFingerprint,
                         descriptor.SchemaFingerprint,
                         StringComparison.Ordinal))
            {
                quarantined.Add(
                    new QuarantinedCapability(
                        registration.ToolName,
                        CapabilityQuarantineReasonCode.SchemaIdentityMismatch,
                        $"Runtime tool '{registration.ToolName}' does not match canonical schema '{descriptor.SchemaFactoryId}'."));
            }
        }

        return quarantined;
    }

    private static void AddStaleReason(
        ICollection<CapabilityAvailabilityReason> reasons,
        string dependencyId,
        string expected,
        string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            AddReason(
                reasons,
                CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                dependencyId,
                $"The {dependencyId} revision changed after planning.");
        }
    }

    private static void AddReason(
        ICollection<CapabilityAvailabilityReason> reasons,
        CapabilityAvailabilityReasonCode code,
        string dependencyId,
        string message)
    {
        if (reasons.Any(reason => reason.Code == code
                                  && string.Equals(
                                      reason.DependencyId,
                                      dependencyId,
                                      StringComparison.Ordinal)))
        {
            return;
        }

        reasons.Add(new CapabilityAvailabilityReason(code, dependencyId, message));
    }

    private static bool IsGroupEnabled(
        CapabilityRegistryRevisionSnapshot registry,
        string groupId) =>
        registry.GroupSelections.TryGetValue(groupId, out var enabled) && enabled;

    private static string CalculateResolutionRevision(
        CapabilityRegistryRevisionSnapshot registry,
        CapabilityRuntimeAvailability runtime,
        IReadOnlyList<CapabilityDescriptor> effective,
        IReadOnlyList<UnavailableCapability> unavailable,
        IReadOnlyList<QuarantinedCapability> quarantined,
        IReadOnlyList<CapabilityDescriptor> outgoingMcpDescriptors,
        IReadOnlyDictionary<string, IReadOnlyList<string>> eligibleProvidersByToolName,
        IReadOnlyList<string> requiresTargetResolution)
    {
        using var revision = new CapabilityRevisionBuilder();
        revision.Add("ali-capability-resolution");
        revision.Add(registry.RegistryRevision);
        revision.Add(registry.SettingsRevision);
        revision.Add(runtime.ActiveUserId);
        revision.Add(runtime.RuntimeRevision);
        revision.Add(runtime.RegisteredToolsByName.Count);
        foreach (var registration in runtime.RegisteredToolsByName.Values.OrderBy(
                     item => item.ToolName,
                     StringComparer.Ordinal))
        {
            revision.Add(registration.ToolName);
            revision.Add(registration.SchemaFactoryId);
            revision.Add(registration.SchemaFingerprint);
        }
        revision.Add(runtime.ProviderRevision);
        revision.Add(runtime.ReadyProviderIds.Order(StringComparer.Ordinal).ToArray());
        revision.Add(runtime.PermissionRevision);
        revision.Add(runtime.AllowedPermissionPolicyIds.Order(StringComparer.Ordinal).ToArray());
        revision.Add(runtime.McpRevision);
        revision.Add(runtime.ReadyIncomingMcpToolNames.Order(StringComparer.Ordinal).ToArray());
        revision.Add(runtime.EnabledOutgoingMcpToolNames.Order(StringComparer.Ordinal).ToArray());
        revision.Add(runtime.ReconcilerRevision);
        revision.Add(runtime.AvailableReconcilerIds.Order(StringComparer.Ordinal).ToArray());

        revision.Add(effective.Count);
        foreach (var descriptor in effective)
        {
            revision.Add(descriptor.Id);
        }

        revision.Add(unavailable.Count);
        foreach (var item in unavailable)
        {
            revision.Add(item.Descriptor.Id);
            revision.Add(item.Reasons.Count);
            foreach (var reason in item.Reasons)
            {
                revision.Add((int)reason.Code);
                revision.Add(reason.DependencyId);
                revision.Add(reason.Message);
            }
        }

        revision.Add(quarantined.Count);
        foreach (var item in quarantined)
        {
            revision.Add(item.ToolName);
            revision.Add((int)item.Code);
            revision.Add(item.Message);
        }
        revision.Add(outgoingMcpDescriptors.Select(item => item.Id).ToArray());
        revision.Add(eligibleProvidersByToolName.Count);
        foreach (var (toolName, providerIds) in eligibleProvidersByToolName)
        {
            revision.Add(toolName);
            revision.Add(providerIds);
        }
        revision.Add(requiresTargetResolution);
        return revision.Finish();
    }
}

public sealed class CapabilityResolutionSnapshot
{
    internal CapabilityResolutionSnapshot(
        string resolutionRevision,
        string registryRevision,
        string settingsRevision,
        string activeUserId,
        string runtimeRevision,
        string providerRevision,
        string permissionRevision,
        string mcpRevision,
        string reconcilerRevision,
        IReadOnlyList<CapabilityGroupDescriptor> enabledGroups,
        IReadOnlyList<CapabilityDescriptor> effectiveDescriptors,
        IReadOnlyList<UnavailableCapability> unavailableDescriptors,
        IReadOnlyList<QuarantinedCapability> quarantinedCapabilities,
        IReadOnlyList<CapabilityDescriptor> outgoingMcpDescriptors,
        IReadOnlyDictionary<string, IReadOnlyList<string>> eligibleProviderIdsByToolName,
        IReadOnlyList<string> requiresTargetResolutionToolNames,
        IReadOnlyDictionary<string, CapabilityDescriptor> byToolName)
    {
        ResolutionRevision = resolutionRevision;
        RegistryRevision = registryRevision;
        SettingsRevision = settingsRevision;
        ActiveUserId = activeUserId;
        RuntimeRevision = runtimeRevision;
        ProviderRevision = providerRevision;
        PermissionRevision = permissionRevision;
        McpRevision = mcpRevision;
        ReconcilerRevision = reconcilerRevision;
        EnabledGroups = enabledGroups;
        EffectiveDescriptors = effectiveDescriptors;
        UnavailableDescriptors = unavailableDescriptors;
        QuarantinedCapabilities = quarantinedCapabilities;
        OutgoingMcpDescriptors = outgoingMcpDescriptors;
        EligibleProviderIdsByToolName = eligibleProviderIdsByToolName;
        RequiresTargetResolutionToolNames = requiresTargetResolutionToolNames;
        ByToolName = byToolName;
    }

    public string ResolutionRevision { get; }

    public string RegistryRevision { get; }

    public string SettingsRevision { get; }

    public string ActiveUserId { get; }

    public string RuntimeRevision { get; }

    public string ProviderRevision { get; }

    public string PermissionRevision { get; }

    public string McpRevision { get; }

    public string ReconcilerRevision { get; }

    public IReadOnlyList<CapabilityGroupDescriptor> EnabledGroups { get; }

    public IReadOnlyList<CapabilityDescriptor> EffectiveDescriptors { get; }

    public IReadOnlyList<UnavailableCapability> UnavailableDescriptors { get; }

    public IReadOnlyList<QuarantinedCapability> QuarantinedCapabilities { get; }

    public IReadOnlyList<CapabilityDescriptor> OutgoingMcpDescriptors { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> EligibleProviderIdsByToolName { get; }

    public IReadOnlyList<string> RequiresTargetResolutionToolNames { get; }

    public IReadOnlyDictionary<string, CapabilityDescriptor> ByToolName { get; }

    public bool TryGetTool(string toolName, out CapabilityDescriptor descriptor) =>
        ByToolName.TryGetValue(toolName, out descriptor!);

    public CapabilityInvocationLease CreateInvocationLease(string toolName)
    {
        if (!TryGetTool(toolName, out var descriptor))
        {
            throw new InvalidOperationException($"Tool '{toolName}' is not callable in this planning snapshot.");
        }

        var eligibleProviderIds = EligibleProviderIdsByToolName.TryGetValue(toolName, out var eligible)
            ? eligible
            : Array.Empty<string>();
        return new CapabilityInvocationLease(
            toolName,
            descriptor.Id,
            descriptor.SchemaFactoryId,
            descriptor.SchemaFingerprint,
            ResolutionRevision,
            RegistryRevision,
            SettingsRevision,
            ActiveUserId,
            RuntimeRevision,
            ProviderRevision,
            PermissionRevision,
            McpRevision,
            ReconcilerRevision,
            Array.AsReadOnly(eligibleProviderIds.ToArray()),
            RequiresTargetResolutionToolNames.Contains(toolName, StringComparer.Ordinal));
    }
}
