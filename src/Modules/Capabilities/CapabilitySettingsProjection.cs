namespace Ali.Modules.Capabilities;

internal static class CapabilitySettingsProjection
{
    public static CapabilitySettingsEnvelope Create(
        CapabilityRegistryRevisionSnapshot registry,
        CapabilityResolutionSnapshot resolution,
        string publicationRevision,
        CapabilityAvailabilityLoadStatus loadStatus,
        string? loadError)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationRevision);
        ValidateMatchingRevisions(registry, resolution);

        var descriptorIds = registry.Descriptors
            .Select(descriptor => descriptor.Id)
            .ToHashSet(StringComparer.Ordinal);
        var callableIds = resolution.EffectiveDescriptors
            .Select(descriptor => descriptor.Id)
            .ToHashSet(StringComparer.Ordinal);
        var unavailableById = resolution.UnavailableDescriptors.ToDictionary(
            unavailable => unavailable.Descriptor.Id,
            StringComparer.Ordinal);
        ValidateDescriptorPartition(descriptorIds, callableIds, unavailableById);

        var knownGroupIds = registry.Groups
            .Select(group => group.Id)
            .ToHashSet(StringComparer.Ordinal);
        var rows = registry.Groups
            .Select(group => CreateRow(group, registry, callableIds, unavailableById))
            .ToArray();
        var presets = registry.Presets
            .Select(preset => CreatePreset(preset, registry.GroupSelections))
            .ToArray();
        var unknownSelections = registry.GroupSelections
            .Where(selection => !knownGroupIds.Contains(selection.Key))
            .OrderBy(selection => selection.Key, StringComparer.Ordinal)
            .Select(selection => new CapabilitySettingsUnknownSelection(selection.Key, selection.Value))
            .ToArray();

        var enabledGroupCount = rows.Count(row => row.Enabled);
        var declaredTaskToolCount = rows.Sum(row => row.DeclaredToolCount);
        var callableTaskToolCount = rows.Sum(row => row.CallableToolCount);
        var unavailableTaskToolCount = rows.Sum(row => row.UnavailableToolCount);
        var protocolDescriptors = registry.Descriptors
            .Where(descriptor => descriptor.Tier == CapabilityTier.Protocol)
            .ToArray();
        var callableProtocolToolCount = protocolDescriptors.Count(
            descriptor => callableIds.Contains(descriptor.Id));
        var unavailableProtocolToolCount = protocolDescriptors.Count(
            descriptor => unavailableById.ContainsKey(descriptor.Id));

        if (loadStatus == CapabilityAvailabilityLoadStatus.FailedClosed
            && enabledGroupCount != 0)
        {
            throw new InvalidOperationException(
                "Failed-closed capability settings cannot expose an enabled group.");
        }

        return new CapabilitySettingsEnvelope(
            new CapabilitySettingsStamp(
                publicationRevision,
                registry.RegistryRevision,
                registry.SettingsRevision,
                resolution.ResolutionRevision),
            resolution.ActiveUserId,
            resolution.RuntimeRevision,
            resolution.ProviderRevision,
            resolution.PermissionRevision,
            resolution.McpRevision,
            resolution.ReconcilerRevision,
            loadStatus,
            loadError,
            rows.Length,
            enabledGroupCount,
            rows.Length - enabledGroupCount,
            declaredTaskToolCount,
            callableTaskToolCount,
            unavailableTaskToolCount,
            callableProtocolToolCount,
            unavailableProtocolToolCount,
            resolution.QuarantinedCapabilities.Count,
            unknownSelections,
            rows,
            presets);
    }

    private static CapabilitySettingsRow CreateRow(
        CapabilityGroupDescriptor group,
        CapabilityRegistryRevisionSnapshot registry,
        IReadOnlySet<string> callableIds,
        IReadOnlyDictionary<string, UnavailableCapability> unavailableById)
    {
        var descriptors = registry.Descriptors
            .Where(descriptor => descriptor.Tier == CapabilityTier.Task)
            .Where(descriptor => string.Equals(descriptor.GroupId, group.Id, StringComparison.Ordinal))
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .ToArray();
        var callableToolCount = descriptors.Count(descriptor => callableIds.Contains(descriptor.Id));
        var unavailableToolCount = descriptors.Count(
            descriptor => unavailableById.ContainsKey(descriptor.Id));
        if (callableToolCount + unavailableToolCount != descriptors.Length)
        {
            throw new InvalidOperationException(
                $"Capability group '{group.Id}' does not have a complete resolution partition.");
        }

        var enabled = registry.GroupSelections.TryGetValue(group.Id, out var selected) && selected;
        var reasons = descriptors
            .Where(descriptor => unavailableById.ContainsKey(descriptor.Id))
            .SelectMany(
                descriptor => unavailableById[descriptor.Id].Reasons.Select(
                    reason => new CapabilitySettingsReason(
                        descriptor.Id,
                        descriptor.ToolName,
                        reason.Code,
                        reason.DependencyId,
                        reason.Message)))
            .ToArray();

        return new CapabilitySettingsRow(
            group.Id,
            group.DisplayName,
            group.Description,
            enabled,
            ResolveStatus(enabled, descriptors.Length, callableToolCount),
            descriptors.Length,
            callableToolCount,
            unavailableToolCount,
            reasons);
    }

    private static CapabilitySettingsPreset CreatePreset(
        CapabilityPresetDescriptor preset,
        IReadOnlyDictionary<string, bool> selections)
    {
        var wouldEnableGroupCount = preset.GroupIds.Count(
            groupId => !selections.TryGetValue(groupId, out var enabled) || !enabled);
        return new CapabilitySettingsPreset(
            preset.Id,
            preset.DisplayName,
            preset.Description,
            preset.GroupIds,
            wouldEnableGroupCount);
    }

    private static CapabilitySettingsRowStatus ResolveStatus(
        bool enabled,
        int declaredToolCount,
        int callableToolCount)
    {
        if (!enabled)
        {
            return CapabilitySettingsRowStatus.Disabled;
        }
        if (declaredToolCount == 0)
        {
            return CapabilitySettingsRowStatus.Empty;
        }
        if (callableToolCount == declaredToolCount)
        {
            return CapabilitySettingsRowStatus.Ready;
        }
        return callableToolCount == 0
            ? CapabilitySettingsRowStatus.Unavailable
            : CapabilitySettingsRowStatus.Degraded;
    }

    private static void ValidateMatchingRevisions(
        CapabilityRegistryRevisionSnapshot registry,
        CapabilityResolutionSnapshot resolution)
    {
        if (!string.Equals(
                registry.RegistryRevision,
                resolution.RegistryRevision,
                StringComparison.Ordinal)
            || !string.Equals(
                registry.SettingsRevision,
                resolution.SettingsRevision,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Capability registry and resolution snapshots do not describe the same publication.",
                nameof(resolution));
        }
    }

    private static void ValidateDescriptorPartition(
        IReadOnlySet<string> descriptorIds,
        IReadOnlySet<string> callableIds,
        IReadOnlyDictionary<string, UnavailableCapability> unavailableById)
    {
        if (callableIds.Any(id => !descriptorIds.Contains(id))
            || unavailableById.Keys.Any(id => !descriptorIds.Contains(id))
            || callableIds.Any(unavailableById.ContainsKey)
            || callableIds.Count + unavailableById.Count != descriptorIds.Count)
        {
            throw new ArgumentException(
                "Capability resolution does not exactly partition the canonical descriptors.",
                nameof(unavailableById));
        }
    }
}
