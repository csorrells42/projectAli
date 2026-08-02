using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Capabilities;

public sealed class CanonicalCapabilityRegistry
{
    private readonly IReadOnlyList<CapabilityGroupDescriptor> _groups;
    private readonly IReadOnlyList<CapabilityPresetDescriptor> _presets;
    private readonly IReadOnlyList<CapabilityProviderBinding> _providerBindings;
    private readonly IReadOnlyList<CapabilityDescriptor> _descriptors;

    public CanonicalCapabilityRegistry(
        IEnumerable<CapabilityDescriptor> descriptors,
        IEnumerable<CapabilityProviderBinding> providerBindings)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(providerBindings);

        _groups = Array.AsReadOnly(
            CanonicalCapabilityCatalog.Groups.Select(group => group with { }).ToArray());
        _presets = Array.AsReadOnly(
            CanonicalCapabilityCatalog.Presets.Select(FreezePreset).ToArray());

        _providerBindings = Array.AsReadOnly(
            providerBindings
                .Select(binding =>
                {
                    ArgumentNullException.ThrowIfNull(binding);
                    return binding with { };
                })
                .OrderBy(binding => binding.ProviderId, StringComparer.Ordinal)
                .ToArray());
        var frozenDescriptors = descriptors
            .Select(FreezeDescriptor)
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .ToArray();
        ValidateCatalog(_groups, _presets);
        ValidateProviderBindings(_providerBindings, _groups);
        ValidateDescriptors(frozenDescriptors, _groups, _presets, _providerBindings);
        _descriptors = Array.AsReadOnly(frozenDescriptors);
        RegistryRevision = CalculateRegistryRevision(
            _groups,
            _presets,
            _providerBindings,
            _descriptors);
    }

    public IReadOnlyList<CapabilityGroupDescriptor> Groups => _groups;

    public IReadOnlyList<CapabilityPresetDescriptor> Presets => _presets;

    public IReadOnlyList<CapabilityProviderBinding> ProviderBindings => _providerBindings;

    public IReadOnlyList<CapabilityDescriptor> Descriptors => _descriptors;

    public string RegistryRevision { get; }

    public CapabilityRegistryRevisionSnapshot Freeze(CapabilityAvailabilitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var frozenSelections = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (groupId, enabled) in settings.GroupSelections)
        {
            frozenSelections.Add(groupId, enabled);
        }

        return new CapabilityRegistryRevisionSnapshot(
            RegistryRevision,
            settings.Revision,
            new ReadOnlyDictionary<string, bool>(
                frozenSelections),
            Array.AsReadOnly(_groups.Select(group => group with { }).ToArray()),
            Array.AsReadOnly(_presets.Select(FreezePreset).ToArray()),
            Array.AsReadOnly(_providerBindings.Select(binding => binding with { }).ToArray()),
            Array.AsReadOnly(_descriptors.Select(FreezeDescriptor).ToArray()));
    }

    private static CapabilityPresetDescriptor FreezePreset(CapabilityPresetDescriptor preset) =>
        preset with
        {
            GroupIds = Array.AsReadOnly(preset.GroupIds.ToArray())
        };

    private static CapabilityDescriptor FreezeDescriptor(CapabilityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(descriptor.SchemaFactory);
        ArgumentNullException.ThrowIfNull(descriptor.ProviderGate);
        ArgumentNullException.ThrowIfNull(descriptor.ProviderGate.SupportedProviderIds);
        ArgumentNullException.ThrowIfNull(descriptor.PrerequisiteGroupIds);
        ArgumentNullException.ThrowIfNull(descriptor.PresetIds);
        ArgumentNullException.ThrowIfNull(descriptor.Permission);
        ArgumentNullException.ThrowIfNull(descriptor.SemanticMetadata);
        ArgumentNullException.ThrowIfNull(descriptor.McpExposure);
        ArgumentNullException.ThrowIfNull(descriptor.Effect);
        var schema = descriptor.SchemaFactory();
        if (schema is not AIFunctionDeclaration function)
        {
            throw new ArgumentException(
                $"Capability '{descriptor.Id}' schema factory must return an AI function declaration.",
                nameof(descriptor));
        }
        if (!string.Equals(function.Name, descriptor.ToolName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Capability '{descriptor.Id}' schema factory returned '{function.Name}' instead of '{descriptor.ToolName}'.",
                nameof(descriptor));
        }
        var schemaFingerprint = CapabilitySchemaIdentity.Calculate(function);
        var semanticMetadata = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in descriptor.SemanticMetadata)
        {
            semanticMetadata.Add(key, value);
        }

        return descriptor with
        {
            SchemaFactory = () => function,
            SchemaFingerprint = schemaFingerprint,
            ProviderGate = descriptor.ProviderGate with
            {
                SupportedProviderIds = Array.AsReadOnly(
                    descriptor.ProviderGate.SupportedProviderIds.Order(StringComparer.Ordinal).ToArray())
            },
            PrerequisiteGroupIds = Array.AsReadOnly(
                descriptor.PrerequisiteGroupIds.Order(StringComparer.Ordinal).ToArray()),
            PresetIds = Array.AsReadOnly(
                descriptor.PresetIds.Order(StringComparer.Ordinal).ToArray()),
            Permission = descriptor.Permission with { },
            SemanticMetadata = new ReadOnlyDictionary<string, string>(
                semanticMetadata),
            McpExposure = descriptor.McpExposure with { },
            Effect = descriptor.Effect with { }
        };
    }

    private static void ValidateCatalog(
        IReadOnlyList<CapabilityGroupDescriptor> groups,
        IReadOnlyList<CapabilityPresetDescriptor> presets)
    {
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            ArgumentNullException.ThrowIfNull(group);
            RequireText(group.Id, nameof(group.Id));
            RequireText(group.DisplayName, $"group '{group.Id}' display name");
            RequireText(group.Description, $"group '{group.Id}' description");
            if (!groupIds.Add(group.Id))
            {
                throw new ArgumentException($"Duplicate capability group ID '{group.Id}'.", nameof(groups));
            }
        }

        if (!groupIds.SetEquals(CapabilityGroupIds.All))
        {
            throw new ArgumentException(
                "The canonical capability groups do not match CapabilityGroupIds.All.",
                nameof(groups));
        }

        var presetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in presets)
        {
            ArgumentNullException.ThrowIfNull(preset);
            RequireText(preset.Id, nameof(preset.Id));
            RequireText(preset.DisplayName, $"preset '{preset.Id}' display name");
            RequireText(preset.Description, $"preset '{preset.Id}' description");
            ArgumentNullException.ThrowIfNull(preset.GroupIds);
            if (!presetIds.Add(preset.Id))
            {
                throw new ArgumentException($"Duplicate capability preset ID '{preset.Id}'.", nameof(presets));
            }

            ValidateUniqueTextValues(preset.GroupIds, $"preset '{preset.Id}' group IDs");
            if (preset.GroupIds.Count == 0)
            {
                throw new ArgumentException(
                    $"Capability preset '{preset.Id}' requires at least one group.",
                    nameof(presets));
            }
            foreach (var groupId in preset.GroupIds)
            {
                if (!groupIds.Contains(groupId))
                {
                    throw new ArgumentException(
                        $"Capability preset '{preset.Id}' uses unknown group '{groupId}'.",
                        nameof(presets));
                }
            }
        }

        if (!presetIds.SetEquals(CapabilityPresetIds.All))
        {
            throw new ArgumentException(
                "The canonical capability presets do not match CapabilityPresetIds.All.",
                nameof(presets));
        }
    }

    private static void ValidateDescriptors(
        IReadOnlyList<CapabilityDescriptor> descriptors,
        IReadOnlyList<CapabilityGroupDescriptor> groups,
        IReadOnlyList<CapabilityPresetDescriptor> presets,
        IReadOnlyList<CapabilityProviderBinding> providerBindings)
    {
        var groupIds = groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        var presetsById = presets.ToDictionary(preset => preset.Id, StringComparer.Ordinal);
        var providerBindingsById = providerBindings.ToDictionary(
            binding => binding.ProviderId,
            StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var toolNames = new HashSet<string>(StringComparer.Ordinal);
        var schemaFactoryIds = new HashSet<string>(StringComparer.Ordinal);
        var publishedMcpNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var descriptor in descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            RequireText(descriptor.Id, nameof(descriptor.Id));
            RequireText(descriptor.ToolName, $"capability '{descriptor.Id}' tool name");
            RequireText(descriptor.DisplayName, $"capability '{descriptor.Id}' display name");
            RequireText(descriptor.Description, $"capability '{descriptor.Id}' description");
            RequireText(descriptor.ProviderId, $"capability '{descriptor.Id}' provider ID");
            RequireText(descriptor.SchemaFactoryId, $"capability '{descriptor.Id}' schema factory ID");
            RequireText(descriptor.SchemaFingerprint, $"capability '{descriptor.Id}' schema fingerprint");
            if (!Enum.IsDefined(descriptor.Tier))
            {
                throw new ArgumentException(
                    $"Capability '{descriptor.Id}' has an invalid tier.",
                    nameof(descriptors));
            }
            if (!Enum.IsDefined(descriptor.RegistrationKind))
            {
                throw new ArgumentException(
                    $"Capability '{descriptor.Id}' has an invalid registration kind.",
                    nameof(descriptors));
            }
            if (!ids.Add(descriptor.Id))
            {
                throw new ArgumentException($"Duplicate capability ID '{descriptor.Id}'.", nameof(descriptors));
            }
            if (!toolNames.Add(descriptor.ToolName))
            {
                throw new ArgumentException(
                    $"Duplicate capability tool name '{descriptor.ToolName}'.",
                    nameof(descriptors));
            }
            if (!schemaFactoryIds.Add(descriptor.SchemaFactoryId))
            {
                throw new ArgumentException(
                    $"Duplicate capability schema factory ID '{descriptor.SchemaFactoryId}'.",
                    nameof(descriptors));
            }

            ValidateTierAndGroup(descriptor, groupIds, descriptors);
            ValidateProviderGate(descriptor, providerBindingsById, descriptors);
            ValidateUniqueTextValues(
                descriptor.PrerequisiteGroupIds,
                $"capability '{descriptor.Id}' prerequisite group IDs");
            ValidatePrerequisites(descriptor, groupIds, descriptors);
            ValidateUniqueTextValues(
                descriptor.PresetIds,
                $"capability '{descriptor.Id}' preset IDs");
            ValidatePresetMembership(descriptor, presetsById, descriptors);
            ValidatePermission(descriptor, descriptors);
            ValidateSemanticMetadata(descriptor, descriptors);
            if (!descriptor.VisibleInCapabilityReport || !descriptor.VisibleInCriticInventory)
            {
                throw new ArgumentException(
                    $"Capability '{descriptor.Id}' must be visible in both capability and critic inventories.",
                    nameof(descriptors));
            }
            ValidateMcpExposure(descriptor, publishedMcpNames, descriptors);
            ValidateEffect(descriptor, descriptors);
        }
    }

    private static void ValidateProviderBindings(
        IReadOnlyList<CapabilityProviderBinding> providerBindings,
        IReadOnlyList<CapabilityGroupDescriptor> groups)
    {
        var knownGroupIds = groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        var providerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in providerBindings)
        {
            RequireText(binding.ProviderId, nameof(binding.ProviderId));
            RequireText(binding.GroupId, $"provider '{binding.ProviderId}' group ID");
            if (!providerIds.Add(binding.ProviderId))
            {
                throw new ArgumentException(
                    $"Duplicate capability provider binding '{binding.ProviderId}'.",
                    nameof(providerBindings));
            }
            if (!knownGroupIds.Contains(binding.GroupId))
            {
                throw new ArgumentException(
                    $"Provider '{binding.ProviderId}' uses unknown group '{binding.GroupId}'.",
                    nameof(providerBindings));
            }
        }
    }

    private static void ValidateProviderGate(
        CapabilityDescriptor descriptor,
        IReadOnlyDictionary<string, CapabilityProviderBinding> providerBindingsById,
        IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        var gate = descriptor.ProviderGate;
        if (!Enum.IsDefined(gate.Kind))
        {
            throw new ArgumentException(
                $"Capability '{descriptor.Id}' has an invalid provider gate.",
                nameof(descriptors));
        }

        ValidateUniqueTextValues(
            gate.SupportedProviderIds,
            $"capability '{descriptor.Id}' supported provider IDs");
        if (gate.Kind == CapabilityProviderGateKind.OwnerOnly)
        {
            if (gate.SupportedProviderIds.Count > 0)
            {
                throw new ArgumentException(
                    $"Owner-only capability '{descriptor.Id}' cannot list supported providers.",
                    nameof(descriptors));
            }
            if (descriptor.RegistrationKind == CapabilityRegistrationKind.LanguageProvider)
            {
                throw new ArgumentException(
                    $"Language-provider capability '{descriptor.Id}' requires an explicit provider gate.",
                    nameof(descriptors));
            }
            return;
        }

        if (gate.SupportedProviderIds.Count == 0)
        {
            throw new ArgumentException(
                $"Capability '{descriptor.Id}' requires at least one supported provider.",
                nameof(descriptors));
        }
        foreach (var providerId in gate.SupportedProviderIds)
        {
            if (!providerBindingsById.ContainsKey(providerId))
            {
                throw new ArgumentException(
                    $"Capability '{descriptor.Id}' uses unbound provider '{providerId}'.",
                    nameof(descriptors));
            }
        }
    }

    private static void ValidateTierAndGroup(
        CapabilityDescriptor descriptor,
        IReadOnlySet<string> groupIds,
        IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        if (descriptor.Tier == CapabilityTier.Protocol)
        {
            if (!ProtocolCapabilityToolNames.All.Contains(descriptor.ToolName))
            {
                throw new ArgumentException(
                    $"Capability '{descriptor.Id}' cannot use the reserved protocol tier.",
                    nameof(descriptors));
            }
            if (descriptor.RegistrationKind is CapabilityRegistrationKind.AgentSkill
                or CapabilityRegistrationKind.LanguageProvider
                or CapabilityRegistrationKind.Mcp)
            {
                throw new ArgumentException(
                    $"Dynamic capability '{descriptor.Id}' cannot join the protocol tier.",
                    nameof(descriptors));
            }
            if (descriptor.GroupId is not null)
            {
                throw new ArgumentException(
                    $"Protocol capability '{descriptor.Id}' cannot belong to a task group.",
                    nameof(descriptors));
            }
            if (descriptor.PrerequisiteGroupIds.Count > 0 || descriptor.PresetIds.Count > 0)
            {
                throw new ArgumentException(
                    $"Protocol capability '{descriptor.Id}' cannot declare task prerequisites or presets.",
                    nameof(descriptors));
            }
            if (descriptor.ProviderGate.Kind != CapabilityProviderGateKind.OwnerOnly)
            {
                throw new ArgumentException(
                    $"Protocol capability '{descriptor.Id}' cannot use a task-provider gate.",
                    nameof(descriptors));
            }
            return;
        }

        if (ProtocolCapabilityToolNames.All.Contains(descriptor.ToolName))
        {
            throw new ArgumentException(
                $"Reserved protocol tool '{descriptor.ToolName}' must use the protocol tier.",
                nameof(descriptors));
        }

        RequireText(descriptor.GroupId, $"capability '{descriptor.Id}' group ID");
        if (!groupIds.Contains(descriptor.GroupId!))
        {
            throw new ArgumentException(
                $"Capability '{descriptor.Id}' uses unknown group '{descriptor.GroupId}'.",
                nameof(descriptors));
        }
    }

    private static void ValidatePrerequisites(
        CapabilityDescriptor descriptor,
        IReadOnlySet<string> groupIds,
        IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        foreach (var prerequisiteGroupId in descriptor.PrerequisiteGroupIds)
        {
            if (!groupIds.Contains(prerequisiteGroupId))
            {
                throw new ArgumentException(
                    $"Capability '{descriptor.Id}' requires unknown group '{prerequisiteGroupId}'.",
                    nameof(descriptors));
            }
            if (string.Equals(prerequisiteGroupId, descriptor.GroupId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Capability '{descriptor.Id}' cannot require its own group '{prerequisiteGroupId}'.",
                    nameof(descriptors));
            }
        }
    }

    private static void ValidatePresetMembership(
        CapabilityDescriptor descriptor,
        IReadOnlyDictionary<string, CapabilityPresetDescriptor> presetsById,
        IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        var expectedPresetIds = descriptor.GroupId is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : presetsById.Values
                .Where(preset => preset.GroupIds.Contains(descriptor.GroupId, StringComparer.Ordinal))
                .Select(preset => preset.Id)
                .ToHashSet(StringComparer.Ordinal);
        if (!expectedPresetIds.SetEquals(descriptor.PresetIds))
        {
            throw new ArgumentException(
                $"Capability '{descriptor.Id}' preset memberships do not match its canonical group presets.",
                nameof(descriptors));
        }

        foreach (var presetId in descriptor.PresetIds)
        {
            if (!presetsById.TryGetValue(presetId, out var preset))
            {
                throw new ArgumentException(
                    $"Capability '{descriptor.Id}' uses unknown preset '{presetId}'.",
                    nameof(descriptors));
            }
            if (descriptor.GroupId is null || !preset.GroupIds.Contains(descriptor.GroupId, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"Capability '{descriptor.Id}' is assigned to preset '{presetId}', but its group is not in that preset.",
                    nameof(descriptors));
            }
        }
    }

    private static void ValidatePermission(
        CapabilityDescriptor descriptor,
        IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        RequireText(descriptor.Permission.PolicyId, $"capability '{descriptor.Id}' permission policy ID");
        if (!Enum.IsDefined(descriptor.Permission.Risk))
        {
            throw new ArgumentException(
                $"Capability '{descriptor.Id}' has an invalid permission risk.",
                nameof(descriptors));
        }
    }

    private static void ValidateSemanticMetadata(
        CapabilityDescriptor descriptor,
        IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        if (descriptor.SemanticMetadata.Count == 0)
        {
            throw new ArgumentException(
                $"Capability '{descriptor.Id}' requires semantic metadata.",
                nameof(descriptors));
        }
        RequireText(descriptor.SemanticSearchText, $"capability '{descriptor.Id}' semantic search text");

        foreach (var (key, value) in descriptor.SemanticMetadata)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    $"Capability '{descriptor.Id}' has empty semantic metadata.",
                    nameof(descriptors));
            }
        }
    }

    private static void ValidateMcpExposure(
        CapabilityDescriptor descriptor,
        ISet<string> publishedMcpNames,
        IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        if (descriptor.McpExposure.Exposed)
        {
            RequireText(
                descriptor.McpExposure.PublishedName,
                $"capability '{descriptor.Id}' published MCP name");
            if (!publishedMcpNames.Add(descriptor.McpExposure.PublishedName!))
            {
                throw new ArgumentException(
                    $"Duplicate published MCP name '{descriptor.McpExposure.PublishedName}'.",
                    nameof(descriptors));
            }
            return;
        }

        if (descriptor.McpExposure.PublishedName is not null)
        {
            throw new ArgumentException(
                $"Capability '{descriptor.Id}' cannot declare an MCP name while MCP exposure is disabled.",
                nameof(descriptors));
        }
    }

    private static void ValidateEffect(
        CapabilityDescriptor descriptor,
        IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        var effect = descriptor.Effect;
        if (!Enum.IsDefined(effect.Kind) || !Enum.IsDefined(effect.MutationBoundary))
        {
            throw new ArgumentException(
                $"Capability '{descriptor.Id}' has invalid effect metadata.",
                nameof(descriptors));
        }
        RequireText(effect.Summary, $"capability '{descriptor.Id}' effect summary");

        if (effect.Kind == CapabilityEffectKind.None
            && (effect.SupportsIdempotency
                || effect.ReadsLocalData
                || effect.WritesLocalData
                || effect.UsesNetwork
                || effect.StartsProcesses
                || effect.ChangesSystemState))
        {
            throw new ArgumentException(
                $"Capability '{descriptor.Id}' with no effect cannot declare effect flags.",
                nameof(descriptors));
        }

        if (effect.Kind == CapabilityEffectKind.LocalMutation && !effect.WritesLocalData)
        {
            throw new ArgumentException(
                $"Local mutation capability '{descriptor.Id}' must declare local writes.",
                nameof(descriptors));
        }
        if (effect.Kind == CapabilityEffectKind.ExternalRead && !effect.UsesNetwork)
        {
            throw new ArgumentException(
                $"External-read capability '{descriptor.Id}' must declare network use.",
                nameof(descriptors));
        }
        if (effect.Kind == CapabilityEffectKind.SourceMutation
            && (!effect.WritesLocalData || effect.MutationBoundary != CapabilityMutationBoundary.StagedWorkspace))
        {
            throw new ArgumentException(
                $"Source mutation capability '{descriptor.Id}' must write locally through a staged workspace.",
                nameof(descriptors));
        }
        if (effect.Kind == CapabilityEffectKind.Destructive
            && (!effect.WritesLocalData
                && !effect.UsesNetwork
                && !effect.StartsProcesses
                && !effect.ChangesSystemState))
        {
            throw new ArgumentException(
                $"Destructive capability '{descriptor.Id}' must declare what it can change.",
                nameof(descriptors));
        }
        if (effect.IsMutation && descriptor.Permission.Risk == CapabilityRiskLevel.None)
        {
            throw new ArgumentException(
                $"Mutating capability '{descriptor.Id}' cannot declare no risk.",
                nameof(descriptors));
        }
        if (effect.Kind == CapabilityEffectKind.Destructive
            && descriptor.Permission.Risk is not CapabilityRiskLevel.High
                and not CapabilityRiskLevel.Critical)
        {
            throw new ArgumentException(
                $"Destructive capability '{descriptor.Id}' requires high or critical risk metadata.",
                nameof(descriptors));
        }
        if (effect.Kind == CapabilityEffectKind.ProcessControl && !effect.StartsProcesses)
        {
            throw new ArgumentException(
                $"Process-control capability '{descriptor.Id}' must declare process activity.",
                nameof(descriptors));
        }
        if (effect.Kind == CapabilityEffectKind.ExternalMutation
            && (!effect.UsesNetwork || effect.MutationBoundary != CapabilityMutationBoundary.JournaledExternal))
        {
            throw new ArgumentException(
                $"External mutation capability '{descriptor.Id}' must use a journaled network boundary.",
                nameof(descriptors));
        }

        if (effect.IsMutation)
        {
            if (effect.MutationBoundary == CapabilityMutationBoundary.None)
            {
                throw new ArgumentException(
                    $"Mutating capability '{descriptor.Id}' requires a mutation boundary.",
                    nameof(descriptors));
            }
            RequireText(effect.ReconcilerId, $"mutating capability '{descriptor.Id}' reconciler ID");
            return;
        }

        if (effect.MutationBoundary != CapabilityMutationBoundary.None
            || effect.ReconcilerId is not null
            || effect.WritesLocalData
            || effect.StartsProcesses
            || effect.ChangesSystemState)
        {
            throw new ArgumentException(
                $"Non-mutating capability '{descriptor.Id}' cannot declare mutation metadata.",
                nameof(descriptors));
        }
    }

    private static void ValidateUniqueTextValues(IEnumerable<string> values, string fieldName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            RequireText(value, fieldName);
            if (!seen.Add(value))
            {
                throw new ArgumentException($"{fieldName} contains duplicate '{value}'.", fieldName);
            }
        }
    }

    private static void RequireText(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{fieldName} is required.", fieldName);
        }
    }

    private static string CalculateRegistryRevision(
        IReadOnlyList<CapabilityGroupDescriptor> groups,
        IReadOnlyList<CapabilityPresetDescriptor> presets,
        IReadOnlyList<CapabilityProviderBinding> providerBindings,
        IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        using var revision = new CapabilityRevisionBuilder();
        revision.Add("ali-capability-registry");
        revision.Add(groups.Count);
        foreach (var group in groups)
        {
            revision.Add(group.Id);
            revision.Add(group.DisplayName);
            revision.Add(group.Description);
            revision.Add(group.EnabledByDefault);
        }

        revision.Add(presets.Count);
        foreach (var preset in presets)
        {
            revision.Add(preset.Id);
            revision.Add(preset.DisplayName);
            revision.Add(preset.Description);
            revision.Add(preset.GroupIds.Count);
            foreach (var groupId in preset.GroupIds)
            {
                revision.Add(groupId);
            }
        }

        revision.Add(providerBindings.Count);
        foreach (var binding in providerBindings)
        {
            revision.Add(binding.ProviderId);
            revision.Add(binding.GroupId);
        }

        revision.Add(descriptors.Count);
        foreach (var descriptor in descriptors)
        {
            revision.Add(descriptor.Id);
            revision.Add(descriptor.ToolName);
            revision.Add(descriptor.DisplayName);
            revision.Add(descriptor.Description);
            revision.Add((int)descriptor.Tier);
            revision.Add(descriptor.GroupId);
            revision.Add(descriptor.ProviderId);
            revision.Add((int)descriptor.RegistrationKind);
            revision.Add(descriptor.SchemaFactoryId);
            revision.Add(descriptor.SchemaFingerprint);
            revision.Add((int)descriptor.ProviderGate.Kind);
            revision.Add(descriptor.ProviderGate.SupportedProviderIds);
            revision.Add(descriptor.PrerequisiteGroupIds);
            revision.Add(descriptor.PresetIds);
            revision.Add(descriptor.Permission.PolicyId);
            revision.Add(descriptor.Permission.RequiresApproval);
            revision.Add((int)descriptor.Permission.Risk);
            revision.Add(descriptor.SemanticSearchText);
            revision.Add(descriptor.SemanticMetadata.Count);
            foreach (var (key, value) in descriptor.SemanticMetadata)
            {
                revision.Add(key);
                revision.Add(value);
            }
            revision.Add(descriptor.VisibleInCapabilityReport);
            revision.Add(descriptor.VisibleInCriticInventory);
            revision.Add(descriptor.McpExposure.Exposed);
            revision.Add(descriptor.McpExposure.PublishedName);
            revision.Add((int)descriptor.Effect.Kind);
            revision.Add(descriptor.Effect.Summary);
            revision.Add((int)descriptor.Effect.MutationBoundary);
            revision.Add(descriptor.Effect.SupportsIdempotency);
            revision.Add(descriptor.Effect.ReconcilerId);
            revision.Add(descriptor.Effect.ReadsLocalData);
            revision.Add(descriptor.Effect.WritesLocalData);
            revision.Add(descriptor.Effect.UsesNetwork);
            revision.Add(descriptor.Effect.StartsProcesses);
            revision.Add(descriptor.Effect.ChangesSystemState);
        }

        return revision.Finish();
    }
}

public sealed class CapabilityRegistryRevisionSnapshot
{
    internal CapabilityRegistryRevisionSnapshot(
        string registryRevision,
        string settingsRevision,
        IReadOnlyDictionary<string, bool> groupSelections,
        IReadOnlyList<CapabilityGroupDescriptor> groups,
        IReadOnlyList<CapabilityPresetDescriptor> presets,
        IReadOnlyList<CapabilityProviderBinding> providerBindings,
        IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        RegistryRevision = registryRevision;
        SettingsRevision = settingsRevision;
        GroupSelections = groupSelections;
        Groups = groups;
        Presets = presets;
        ProviderBindings = providerBindings;
        Descriptors = descriptors;
    }

    public string RegistryRevision { get; }

    public string SettingsRevision { get; }

    public IReadOnlyDictionary<string, bool> GroupSelections { get; }

    public IReadOnlyList<CapabilityGroupDescriptor> Groups { get; }

    public IReadOnlyList<CapabilityPresetDescriptor> Presets { get; }

    public IReadOnlyList<CapabilityProviderBinding> ProviderBindings { get; }

    public IReadOnlyList<CapabilityDescriptor> Descriptors { get; }
}

internal sealed class CapabilityRevisionBuilder : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _finished;

    public void Add(string? value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        if (value is null)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, -1);
            _hash.AppendData(length);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        _hash.AppendData(length);
        _hash.AppendData(bytes);
    }

    public void Add(bool value) => _hash.AppendData(value ? [1] : [0]);

    public void Add(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        _hash.AppendData(bytes);
    }

    public void Add(IReadOnlyList<string> values)
    {
        Add(values.Count);
        foreach (var value in values)
        {
            Add(value);
        }
    }

    public string Finish()
    {
        if (_finished)
        {
            throw new InvalidOperationException("The capability revision has already been finalized.");
        }

        _finished = true;
        return Convert.ToHexString(_hash.GetHashAndReset());
    }

    public void Dispose() => _hash.Dispose();
}
