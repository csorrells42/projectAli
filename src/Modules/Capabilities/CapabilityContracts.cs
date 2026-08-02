using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Capabilities;

public static class CapabilityGroupIds
{
    public const string CapabilityDiscovery = "capability-discovery";
    public const string PersonalContextAndMemory = "personal-context-and-memory";
    public const string WebResearchAndNavigation = "web-research-and-navigation";
    public const string RemindersAndCalendar = "reminders-and-calendar";
    public const string WorkMemory = "work-memory";
    public const string AgentModesAndSkills = "agent-modes-and-skills";
    // Keep the persisted group ID stable while retiring the former nested-agent surface.
    public const string ExternalMcp = "specialists-and-workflows";
    public const string SpecialistsAndWorkflows = ExternalMcp;
    public const string FilesAndArchives = "files-and-archives";
    public const string ProgrammingCore = "programming-core";
    public const string CSharpDotNetRoslyn = "csharp-dotnet-roslyn";
    public const string Python = "python";
    public const string WebHtmlCssJavaScriptTypeScript = "web-html-css-js-ts";
    public const string Java = "java";
    public const string NativeCppGcc = "native-cpp-gcc";
    public const string Arduino = "arduino";
    public const string RaspberryPi = "raspberry-pi";
    public const string DevOpsArchitectureQuality = "devops-architecture-quality";
    public const string VisualStudio = "visual-studio";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly<string>(
    [
        CapabilityDiscovery,
        PersonalContextAndMemory,
        WebResearchAndNavigation,
        RemindersAndCalendar,
        WorkMemory,
        AgentModesAndSkills,
        ExternalMcp,
        FilesAndArchives,
        ProgrammingCore,
        CSharpDotNetRoslyn,
        Python,
        WebHtmlCssJavaScriptTypeScript,
        Java,
        NativeCppGcc,
        Arduino,
        RaspberryPi,
        DevOpsArchitectureQuality,
        VisualStudio
    ]);
}

public static class CapabilityPresetIds
{
    public const string CSharp = "csharp";
    public const string Java = "java";
    public const string Arduino = "arduino";
    public const string FileTools = "file-tools";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly<string>(
    [
        CSharp,
        Java,
        Arduino,
        FileTools
    ]);
}

public static class ProtocolCapabilityToolNames
{
    public static IReadOnlySet<string> All { get; } = new[]
    {
        "submit_orchestration_decision"
    }.ToFrozenSet(StringComparer.Ordinal);
}

public sealed record CapabilityGroupDescriptor(
    string Id,
    string DisplayName,
    string Description,
    bool EnabledByDefault);

public sealed record CapabilityPresetDescriptor(
    string Id,
    string DisplayName,
    string Description,
    IReadOnlyList<string> GroupIds);

public enum CapabilityTier
{
    Protocol,
    Task
}

public enum CapabilityRegistrationKind
{
    Native,
    FrameworkBuiltIn,
    AgentSkill,
    LanguageProvider,
    Mcp
}

public enum CapabilityProviderGateKind
{
    OwnerOnly,
    AnySupported,
    ResolvedTarget
}

public enum CapabilityRiskLevel
{
    None,
    Low,
    Medium,
    High,
    Critical
}

public enum CapabilityEffectKind
{
    None,
    Read,
    ExternalRead,
    LocalMutation,
    SourceMutation,
    ProcessControl,
    ExternalMutation,
    Destructive
}

public enum CapabilityMutationBoundary
{
    None,
    PermissionGuarded,
    StagedWorkspace,
    JournaledExternal
}

public enum CapabilityAvailabilityReasonCode
{
    GroupDisabled,
    PrerequisiteGroupDisabled,
    RuntimeToolMissing,
    RuntimeToolIdentityMismatch,
    ProviderUnavailable,
    TargetProviderUnresolved,
    TargetProviderUnsupported,
    TargetBindingMismatch,
    TargetResolutionStale,
    InvocationLeaseStale,
    PermissionUnavailable,
    McpUnavailable,
    ReconcilerUnavailable
}

public delegate AITool CapabilitySchemaFactory();

public static class CapabilitySchemaIdentity
{
    public static string Calculate(AIFunctionDeclaration function)
    {
        ArgumentNullException.ThrowIfNull(function);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, function.Name);
        Append(hash, function.Description);
        Append(hash, function.JsonSchema.GetRawText());
        Append(hash, function.ReturnJsonSchema?.GetRawText());
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        if (value is null)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, -1);
            hash.AppendData(length);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

public sealed record CapabilityProviderGate(
    CapabilityProviderGateKind Kind,
    IReadOnlyList<string> SupportedProviderIds);

public sealed record CapabilityProviderBinding(
    string ProviderId,
    string GroupId);

public sealed record CapabilityPermissionDescriptor(
    string PolicyId,
    bool RequiresApproval,
    CapabilityRiskLevel Risk);

public sealed record CapabilityMcpExposure(
    bool Exposed,
    string? PublishedName);

/// <summary>
/// Describes the task-domain effect of invoking a capability. Infrastructure-only
/// receipts, audit records, metrics, and traces are deliberately excluded: they may
/// record an invocation but must not alter its target-domain outcome or authority.
/// </summary>
public sealed record CapabilityEffectDescriptor(
    CapabilityEffectKind Kind,
    string Summary,
    CapabilityMutationBoundary MutationBoundary,
    bool SupportsIdempotency,
    string? ReconcilerId,
    bool ReadsLocalData,
    bool WritesLocalData,
    bool UsesNetwork,
    bool StartsProcesses,
    bool ChangesSystemState)
{
    public bool IsMutation => Kind is CapabilityEffectKind.LocalMutation
        or CapabilityEffectKind.SourceMutation
        or CapabilityEffectKind.ProcessControl
        or CapabilityEffectKind.ExternalMutation
        or CapabilityEffectKind.Destructive;
}

public sealed record CapabilityDescriptor(
    string Id,
    string ToolName,
    string DisplayName,
    string Description,
    CapabilityTier Tier,
    string? GroupId,
    string ProviderId,
    CapabilityRegistrationKind RegistrationKind,
    string SchemaFactoryId,
    CapabilitySchemaFactory SchemaFactory,
    string SchemaFingerprint,
    CapabilityProviderGate ProviderGate,
    IReadOnlyList<string> PrerequisiteGroupIds,
    IReadOnlyList<string> PresetIds,
    CapabilityPermissionDescriptor Permission,
    string SemanticSearchText,
    IReadOnlyDictionary<string, string> SemanticMetadata,
    bool VisibleInCapabilityReport,
    bool VisibleInCriticInventory,
    CapabilityMcpExposure McpExposure,
    CapabilityEffectDescriptor Effect)
{
    public static CapabilityDescriptor Create(
        string id,
        string toolName,
        string displayName,
        string description,
        CapabilityTier tier,
        string? groupId,
        string providerId,
        CapabilityRegistrationKind registrationKind,
        string schemaFactoryId,
        CapabilitySchemaFactory schemaFactory,
        CapabilityProviderGate providerGate,
        IEnumerable<string> prerequisiteGroupIds,
        IEnumerable<string> presetIds,
        CapabilityPermissionDescriptor permission,
        string semanticSearchText,
        IReadOnlyDictionary<string, string> semanticMetadata,
        bool visibleInCapabilityReport,
        bool visibleInCriticInventory,
        CapabilityMcpExposure mcpExposure,
        CapabilityEffectDescriptor effect)
    {
        ArgumentNullException.ThrowIfNull(schemaFactory);
        ArgumentNullException.ThrowIfNull(providerGate);
        ArgumentNullException.ThrowIfNull(providerGate.SupportedProviderIds);
        ArgumentNullException.ThrowIfNull(prerequisiteGroupIds);
        ArgumentNullException.ThrowIfNull(presetIds);
        ArgumentNullException.ThrowIfNull(permission);
        ArgumentNullException.ThrowIfNull(semanticMetadata);
        ArgumentNullException.ThrowIfNull(mcpExposure);
        ArgumentNullException.ThrowIfNull(effect);

        return new CapabilityDescriptor(
            id,
            toolName,
            displayName,
            description,
            tier,
            groupId,
            providerId,
            registrationKind,
            schemaFactoryId,
            schemaFactory,
            string.Empty,
            providerGate with
            {
                SupportedProviderIds = Array.AsReadOnly(providerGate.SupportedProviderIds.ToArray())
            },
            Array.AsReadOnly(prerequisiteGroupIds.ToArray()),
            Array.AsReadOnly(presetIds.ToArray()),
            permission with { },
            semanticSearchText,
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(semanticMetadata, StringComparer.Ordinal)),
            visibleInCapabilityReport,
            visibleInCriticInventory,
            mcpExposure with { },
            effect with { });
    }
}
