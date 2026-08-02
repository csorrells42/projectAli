namespace Ali.Modules.Capabilities;

public readonly record struct CapabilitySettingsStamp
{
    public CapabilitySettingsStamp(
        string publicationRevision,
        string registryRevision,
        string settingsRevision,
        string resolutionRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(registryRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolutionRevision);
        PublicationRevision = publicationRevision;
        RegistryRevision = registryRevision;
        SettingsRevision = settingsRevision;
        ResolutionRevision = resolutionRevision;
    }

    public string PublicationRevision { get; }

    public string RegistryRevision { get; }

    public string SettingsRevision { get; }

    public string ResolutionRevision { get; }
}

public enum CapabilitySettingsRowStatus
{
    Disabled,
    Empty,
    Ready,
    Degraded,
    Unavailable
}

public sealed class CapabilitySettingsReason
{
    internal CapabilitySettingsReason(
        string capabilityId,
        string toolName,
        CapabilityAvailabilityReasonCode code,
        string dependencyId,
        string message)
    {
        CapabilityId = capabilityId;
        ToolName = toolName;
        Code = code;
        DependencyId = dependencyId;
        Message = message;
    }

    public string CapabilityId { get; }

    public string ToolName { get; }

    public CapabilityAvailabilityReasonCode Code { get; }

    public string DependencyId { get; }

    public string Message { get; }
}

public sealed class CapabilitySettingsRow
{
    internal CapabilitySettingsRow(
        string groupId,
        string capability,
        string description,
        bool enabled,
        CapabilitySettingsRowStatus status,
        int declaredToolCount,
        int callableToolCount,
        int unavailableToolCount,
        IEnumerable<CapabilitySettingsReason> reasons)
    {
        GroupId = groupId;
        Capability = capability;
        Description = description;
        Enabled = enabled;
        Status = status;
        DeclaredToolCount = declaredToolCount;
        CallableToolCount = callableToolCount;
        UnavailableToolCount = unavailableToolCount;
        Reasons = Array.AsReadOnly(reasons.ToArray());
    }

    public string GroupId { get; }

    public string Capability { get; }

    public string Description { get; }

    public bool Enabled { get; }

    public CapabilitySettingsRowStatus Status { get; }

    public int DeclaredToolCount { get; }

    public int CallableToolCount { get; }

    public int UnavailableToolCount { get; }

    public IReadOnlyList<CapabilitySettingsReason> Reasons { get; }
}

public sealed class CapabilitySettingsPreset
{
    internal CapabilitySettingsPreset(
        string id,
        string displayName,
        string description,
        IEnumerable<string> groupIds,
        int wouldEnableGroupCount)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        GroupIds = Array.AsReadOnly(groupIds.ToArray());
        WouldEnableGroupCount = wouldEnableGroupCount;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public IReadOnlyList<string> GroupIds { get; }

    public int WouldEnableGroupCount { get; }

    public bool IsFullyApplied => WouldEnableGroupCount == 0;
}

public sealed class CapabilitySettingsUnknownSelection
{
    internal CapabilitySettingsUnknownSelection(string groupId, bool enabled)
    {
        GroupId = groupId;
        Enabled = enabled;
    }

    public string GroupId { get; }

    public bool Enabled { get; }
}

public sealed class CapabilitySettingsEnvelope
{
    internal CapabilitySettingsEnvelope(
        CapabilitySettingsStamp stamp,
        string activeUserId,
        string runtimeRevision,
        string providerRevision,
        string permissionRevision,
        string mcpRevision,
        string reconcilerRevision,
        CapabilityAvailabilityLoadStatus loadStatus,
        string? loadError,
        int knownGroupCount,
        int enabledGroupCount,
        int disabledGroupCount,
        int declaredTaskToolCount,
        int callableTaskToolCount,
        int unavailableTaskToolCount,
        int callableProtocolToolCount,
        int unavailableProtocolToolCount,
        int quarantinedRuntimeToolCount,
        IEnumerable<CapabilitySettingsUnknownSelection> unknownSelections,
        IEnumerable<CapabilitySettingsRow> rows,
        IEnumerable<CapabilitySettingsPreset> presets)
    {
        Stamp = stamp;
        ActiveUserId = activeUserId;
        RuntimeRevision = runtimeRevision;
        ProviderRevision = providerRevision;
        PermissionRevision = permissionRevision;
        McpRevision = mcpRevision;
        ReconcilerRevision = reconcilerRevision;
        LoadStatus = loadStatus;
        LoadError = loadError;
        KnownGroupCount = knownGroupCount;
        EnabledGroupCount = enabledGroupCount;
        DisabledGroupCount = disabledGroupCount;
        DeclaredTaskToolCount = declaredTaskToolCount;
        CallableTaskToolCount = callableTaskToolCount;
        UnavailableTaskToolCount = unavailableTaskToolCount;
        CallableProtocolToolCount = callableProtocolToolCount;
        UnavailableProtocolToolCount = unavailableProtocolToolCount;
        QuarantinedRuntimeToolCount = quarantinedRuntimeToolCount;
        UnknownSelections = Array.AsReadOnly(unknownSelections.ToArray());
        Rows = Array.AsReadOnly(rows.ToArray());
        Presets = Array.AsReadOnly(presets.ToArray());
    }

    public CapabilitySettingsStamp Stamp { get; }

    public string PublicationRevision => Stamp.PublicationRevision;

    public string RegistryRevision => Stamp.RegistryRevision;

    public string SettingsRevision => Stamp.SettingsRevision;

    public string ResolutionRevision => Stamp.ResolutionRevision;

    public string ActiveUserId { get; }

    public string RuntimeRevision { get; }

    public string ProviderRevision { get; }

    public string PermissionRevision { get; }

    public string McpRevision { get; }

    public string ReconcilerRevision { get; }

    public CapabilityAvailabilityLoadStatus LoadStatus { get; }

    public string? LoadError { get; }

    public int KnownGroupCount { get; }

    public int EnabledGroupCount { get; }

    public int DisabledGroupCount { get; }

    public int DeclaredTaskToolCount { get; }

    public int CallableTaskToolCount { get; }

    public int UnavailableTaskToolCount { get; }

    public int CallableProtocolToolCount { get; }

    public int UnavailableProtocolToolCount { get; }

    public int QuarantinedRuntimeToolCount { get; }

    public int UnknownSelectionCount => UnknownSelections.Count;

    public IReadOnlyList<CapabilitySettingsUnknownSelection> UnknownSelections { get; }

    public IReadOnlyList<CapabilitySettingsRow> Rows { get; }

    public IReadOnlyList<CapabilitySettingsPreset> Presets { get; }
}

public enum CapabilitySettingsMutationStatus
{
    Saved,
    NoChange,
    Conflict,
    Busy,
    InvalidRequest,
    WriteFailed,
    FailedClosed
}

public sealed class CapabilitySettingsMutationResult
{
    internal CapabilitySettingsMutationResult(
        CapabilitySettingsMutationStatus status,
        CapabilitySettingsEnvelope current,
        string? error)
    {
        Status = status;
        Current = current;
        Error = error;
    }

    public CapabilitySettingsMutationStatus Status { get; }

    public CapabilitySettingsEnvelope Current { get; }

    public string? Error { get; }

    public bool Success => Status is CapabilitySettingsMutationStatus.Saved
        or CapabilitySettingsMutationStatus.NoChange;
}
