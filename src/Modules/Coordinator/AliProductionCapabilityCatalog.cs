using System.Collections.Frozen;
using Ali.Modules.Capabilities;
using Ali.Modules.Mcp;
using Ali.Modules.Permissions;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coordinator;

public sealed record AliProductionCapabilityCatalogBuild(
    CanonicalCapabilityRegistry Registry,
    IReadOnlyList<string> QuarantinedToolNames);

/// <summary>
/// Exact production metadata for Ali-owned ordinary tools. Membership is deliberately
/// name-based registry data; it does not interpret user text or infer metadata for a
/// function that was not explicitly registered here.
/// </summary>
public static class AliProductionCapabilityCatalog
{
    public const string ProviderId = "ali-core";

    private static readonly CapabilityProviderBinding[] LanguageProviderBindings =
    [
        new("dotnet-roslyn", CapabilityGroupIds.CSharpDotNetRoslyn),
        new("python-cpython", CapabilityGroupIds.Python),
        new("web-node", CapabilityGroupIds.WebHtmlCssJavaScriptTypeScript),
        new("java-temurin", CapabilityGroupIds.Java),
        new("cpp-msvc", CapabilityGroupIds.NativeCppGcc),
        new("arduino-cli", CapabilityGroupIds.Arduino)
    ];

    public static IReadOnlyList<string> RegisteredProviderIds { get; } = Array.AsReadOnly(
        new[] { ProviderId }
            .Concat(LanguageProviderBindings.Select(binding => binding.ProviderId))
            .ToArray());

    private static readonly IReadOnlySet<string> RetiredToolNames = ToolSet(
        AliCapabilityCatalog.CodingAgentStatusName,
        AliCapabilityCatalog.CodingAgentExecuteName,
        AliCapabilityCatalog.ConsultSoftwareEngineerName,
        AliCapabilityCatalog.ConsultResearcherName,
        AliCapabilityCatalog.ConsultOfficeSpecialistName,
        AliCapabilityCatalog.RunResearchArtifactWorkflowName,
        AliCapabilityCatalog.RunProgrammingGroupChatName,
        AliCapabilityCatalog.RunMagenticOrchestrationName,
        AliCapabilityCatalog.ListRecoverableWorkflowsName,
        AliCapabilityCatalog.ResumeWorkflowCheckpointName,
        AliCapabilityCatalog.RoslynPreviewRenameName,
        AliCapabilityCatalog.RoslynApplyRenameName,
        AliCapabilityCatalog.RememberCurrentUserName,
        AliCapabilityCatalog.CorrectCurrentUserMemoryName,
        AliCapabilityCatalog.ForgetCurrentUserMemoryName);

    private static readonly IReadOnlySet<string> ResolvedLanguageTargetToolNames = ToolSet(
        AliCapabilityCatalog.CodingAnalyzeProjectName,
        AliCapabilityCatalog.CodingFormatProjectName,
        AliCapabilityCatalog.CodingBuildProjectName,
        AliCapabilityCatalog.CodingTestProjectName,
        AliCapabilityCatalog.CodingRunProjectName);

    private static readonly IReadOnlyDictionary<string, KnownToolDefinition> Definitions =
        CreateDefinitions();

    private static readonly IReadOnlyDictionary<string, McpServerToolPolicy> McpPolicies =
        McpServerToolCatalog.CreateDefaultPolicies()
            .ToFrozenDictionary(policy => policy.Name, StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> FrameworkBuiltInToolNames = ToolSet(
        AliCapabilityCatalog.FileWriteName,
        AliCapabilityCatalog.FileReadName,
        AliCapabilityCatalog.FileDeleteName,
        AliCapabilityCatalog.FileListName,
        AliCapabilityCatalog.FileSearchName,
        AliCapabilityCatalog.FileReplaceName,
        AliCapabilityCatalog.FileReplaceLinesName,
        AliCapabilityCatalog.WorkMemoryWriteName,
        AliCapabilityCatalog.WorkMemoryReadName,
        AliCapabilityCatalog.WorkMemoryDeleteName,
        AliCapabilityCatalog.WorkMemoryListName,
        AliCapabilityCatalog.WorkMemorySearchName,
        AliCapabilityCatalog.WorkMemoryReplaceName,
        AliCapabilityCatalog.WorkMemoryReplaceLinesName,
        AliCapabilityCatalog.GetAgentModeName,
        AliCapabilityCatalog.SetAgentModeName);

    private static readonly IReadOnlySet<string> AgentSkillToolNames = ToolSet(
        AliCapabilityCatalog.LoadAgentSkillName,
        AliCapabilityCatalog.ReadAgentSkillResourceName,
        AliCapabilityCatalog.RunAgentSkillScriptName);

    private static readonly IReadOnlySet<string> DestructiveToolNames = ToolSet(
        AliCapabilityCatalog.MutateParticipantMemoryName,
        AliCapabilityCatalog.FileDeleteName,
        AliCapabilityCatalog.WorkMemoryDeleteName);

    private static readonly IReadOnlySet<string> SourceMutationToolNames = ToolSet(
        AliCapabilityCatalog.FileWriteName,
        AliCapabilityCatalog.FileReplaceName,
        AliCapabilityCatalog.FileReplaceLinesName,
        AliCapabilityCatalog.FileMoveName,
        AliCapabilityCatalog.FileCopyName,
        AliCapabilityCatalog.FileCreateDirectoryName,
        AliCapabilityCatalog.ArchiveCreateName,
        AliCapabilityCatalog.ArchiveExtractName,
        AliCapabilityCatalog.RunAgentSkillScriptName,
        AliCapabilityCatalog.CodingFormatProjectName,
        AliCapabilityCatalog.ArduinoCreateCompileName,
        AliCapabilityCatalog.DotNetCreateProjectName,
        AliCapabilityCatalog.RoslynFormatProjectName,
        AliCapabilityCatalog.RoslynApplyActionName,
        AliCapabilityCatalog.DotNetDependencyApplyName);

    private static readonly IReadOnlySet<string> ExplicitlyNonIdempotentToolNames = ToolSet(
        AliCapabilityCatalog.SearchCurrentWebName,
        AliCapabilityCatalog.ResearchWebName);

    private static readonly IReadOnlySet<string> MsBuildWorkspaceReadOrPreviewToolNames = ToolSet(
        AliCapabilityCatalog.RoslynAnalyzeProjectName,
        AliCapabilityCatalog.RoslynFindSymbolName,
        AliCapabilityCatalog.RoslynGetCompletionsName,
        AliCapabilityCatalog.RoslynInspectSolutionName,
        AliCapabilityCatalog.RoslynInspectDocumentName,
        AliCapabilityCatalog.RoslynInspectPositionName,
        AliCapabilityCatalog.RoslynFindReferencesName,
        AliCapabilityCatalog.RoslynInspectTargetName,
        AliCapabilityCatalog.RoslynListActionsName,
        AliCapabilityCatalog.RoslynPreviewActionName,
        AliCapabilityCatalog.RoslynVerifyChangesetName,
        AliCapabilityCatalog.ArchitectureInspectName,
        AliCapabilityCatalog.ArchitectureCheckName,
        AliCapabilityCatalog.DotNetArchitectureReportName);

    private static readonly IReadOnlySet<string> MsBuildWorkspaceToolNames =
        MsBuildWorkspaceReadOrPreviewToolNames
            .Append(AliCapabilityCatalog.RoslynFormatProjectName)
            .Append(AliCapabilityCatalog.RoslynApplyActionName)
            .ToFrozenSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> ProjectControlledExecutionToolNames = ToolSet(
        AliCapabilityCatalog.CodingAnalyzeProjectName,
        AliCapabilityCatalog.CodingFormatProjectName,
        AliCapabilityCatalog.CodingBuildProjectName,
        AliCapabilityCatalog.CodingTestProjectName,
        AliCapabilityCatalog.CodingRunProjectName,
        AliCapabilityCatalog.RunAgentSkillScriptName,
        AliCapabilityCatalog.VisualStudioBuildName,
        AliCapabilityCatalog.VisualStudioOpenName,
        AliCapabilityCatalog.GnuNativeExecuteName,
        AliCapabilityCatalog.ArduinoCreateCompileName,
        AliCapabilityCatalog.ArduinoCompileName,
        AliCapabilityCatalog.ArduinoUploadName,
        AliCapabilityCatalog.ArduinoOpenIdeName,
        AliCapabilityCatalog.RaspberryPiDeployName,
        AliCapabilityCatalog.DotNetCreateProjectName,
        AliCapabilityCatalog.DotNetBuildName,
        AliCapabilityCatalog.DotNetRunName,
        AliCapabilityCatalog.DotNetTestName,
        AliCapabilityCatalog.DotNetVerifyName,
        AliCapabilityCatalog.DotNetDebugLaunchName,
        AliCapabilityCatalog.DotNetDebugAttachName,
        AliCapabilityCatalog.DotNetDebugEvaluateName,
        AliCapabilityCatalog.DotNetDebugBreakpointsName,
        AliCapabilityCatalog.DotNetDebugControlName,
        AliCapabilityCatalog.DotNetDebugStopName,
        AliCapabilityCatalog.DotNetDependencyInspectName,
        AliCapabilityCatalog.GitStatusName,
        AliCapabilityCatalog.GitDiffName,
        AliCapabilityCatalog.GitHistoryName,
        AliCapabilityCatalog.GitBlameName,
        AliCapabilityCatalog.GitCreateBranchName,
        AliCapabilityCatalog.GitCommitName,
        AliCapabilityCatalog.GitPushName,
        AliCapabilityCatalog.DotNetQualityScanName,
        AliCapabilityCatalog.DotNetPerformanceMeasureName,
        AliCapabilityCatalog.DotNetPerformanceTraceName,
        AliCapabilityCatalog.DotNetApplicationVerifyName,
        AliCapabilityCatalog.DotNetReleasePublishName,
        AliCapabilityCatalog.DotNetArchitectureReportName,
        AliCapabilityCatalog.DotNetDeliveryVerifyName);

    private static readonly IReadOnlySet<string> ExternalMutationToolNames = ToolSet(
        AliCapabilityCatalog.ResearchWebName,
        AliCapabilityCatalog.RaspberryPiDeployName,
        AliCapabilityCatalog.GitPushName);

    private static readonly IReadOnlySet<string> ProcessControlToolNames = ToolSet(
        AliCapabilityCatalog.SearchLocalLibraryName,
        AliCapabilityCatalog.RecallUserMemoryName,
        AliCapabilityCatalog.ListCurrentUserMemoriesName,
        AliCapabilityCatalog.ReconcileParticipantMemoryMutationName,
        AliCapabilityCatalog.CodingAnalyzeProjectName,
        AliCapabilityCatalog.RunAgentSkillScriptName,
        AliCapabilityCatalog.CodingBuildProjectName,
        AliCapabilityCatalog.CodingTestProjectName,
        AliCapabilityCatalog.CodingRunProjectName,
        AliCapabilityCatalog.VisualStudioBuildName,
        AliCapabilityCatalog.VisualStudioOpenName,
        AliCapabilityCatalog.GnuNativeExecuteName,
        AliCapabilityCatalog.ArduinoCompileName,
        AliCapabilityCatalog.ArduinoUploadName,
        AliCapabilityCatalog.ArduinoOpenIdeName,
        AliCapabilityCatalog.DotNetBuildName,
        AliCapabilityCatalog.DotNetRunName,
        AliCapabilityCatalog.DotNetStopProjectName,
        AliCapabilityCatalog.DotNetTestName,
        AliCapabilityCatalog.DotNetVerifyName,
        AliCapabilityCatalog.DotNetDebugLaunchName,
        AliCapabilityCatalog.DotNetDebugAttachName,
        AliCapabilityCatalog.DotNetDebugEvaluateName,
        AliCapabilityCatalog.DotNetDebugBreakpointsName,
        AliCapabilityCatalog.DotNetDebugControlName,
        AliCapabilityCatalog.DotNetDebugStopName,
        AliCapabilityCatalog.DotNetQualityScanName,
        AliCapabilityCatalog.DotNetPerformanceMeasureName,
        AliCapabilityCatalog.DotNetPerformanceTraceName,
        AliCapabilityCatalog.DotNetApplicationVerifyName,
        AliCapabilityCatalog.DotNetReleasePublishName,
        AliCapabilityCatalog.DotNetDeliveryVerifyName,
        AliCapabilityCatalog.VisualStudioInspectName,
        AliCapabilityCatalog.GnuNativeInspectName,
        AliCapabilityCatalog.ArduinoInspectName,
        AliCapabilityCatalog.ArduinoSearchLibrariesName,
        AliCapabilityCatalog.RaspberryPiProbeName,
        AliCapabilityCatalog.RaspberryPiInspectLibrariesName,
        AliCapabilityCatalog.RaspberryPiSearchPackagesName,
        AliCapabilityCatalog.GitStatusName,
        AliCapabilityCatalog.GitDiffName,
        AliCapabilityCatalog.GitHistoryName,
        AliCapabilityCatalog.GitBlameName,
        AliCapabilityCatalog.ArchiveListName,
        AliCapabilityCatalog.DotNetDependencyInspectName);

    private static readonly IReadOnlySet<string> LocalMutationToolNames = ToolSet(
        AliCapabilityCatalog.SearchCurrentWebName,
        AliCapabilityCatalog.ConsentParticipantMemoryProposalName,
        AliCapabilityCatalog.CreateCalendarEventName,
        AliCapabilityCatalog.WorkMemoryWriteName,
        AliCapabilityCatalog.WorkMemoryReplaceName,
        AliCapabilityCatalog.WorkMemoryReplaceLinesName,
        AliCapabilityCatalog.SetAgentModeName,
        AliCapabilityCatalog.ArduinoInstallCoreName,
        AliCapabilityCatalog.ArduinoInstallLibraryName,
        AliCapabilityCatalog.GitCreateBranchName,
        AliCapabilityCatalog.GitCommitName);

    private static readonly IReadOnlySet<string> ExplicitExternalReadToolNames = ToolSet(
        AliCapabilityCatalog.SearchCurrentWebName,
        AliCapabilityCatalog.ResearchWebName,
        AliCapabilityCatalog.CodingProbeServiceName,
        AliCapabilityCatalog.ArduinoSearchLibrariesName,
        AliCapabilityCatalog.RaspberryPiProbeName,
        AliCapabilityCatalog.RaspberryPiInspectLibrariesName,
        AliCapabilityCatalog.RaspberryPiSearchPackagesName,
        AliCapabilityCatalog.DotNetDependencyInspectName);

    private static readonly IReadOnlySet<string> ChangesSystemStateToolNames = ToolSet(
        AliCapabilityCatalog.CreateCalendarEventName,
        AliCapabilityCatalog.ConsentParticipantMemoryProposalName,
        AliCapabilityCatalog.MutateParticipantMemoryName,
        AliCapabilityCatalog.ReconcileParticipantMemoryMutationName,
        AliCapabilityCatalog.SetAgentModeName,
        AliCapabilityCatalog.ArduinoInstallCoreName,
        AliCapabilityCatalog.ArduinoInstallLibraryName,
        AliCapabilityCatalog.ArduinoUploadName,
        AliCapabilityCatalog.GitCreateBranchName,
        AliCapabilityCatalog.GitCommitName,
        AliCapabilityCatalog.GitPushName,
        AliCapabilityCatalog.DotNetDebugBreakpointsName,
        AliCapabilityCatalog.DotNetDebugControlName,
        AliCapabilityCatalog.DotNetDebugStopName,
        AliCapabilityCatalog.DotNetStopProjectName);

    private static readonly IReadOnlySet<string> AdditionalProcessStartingToolNames = ToolSet(
        AliCapabilityCatalog.CreateCalendarEventName,
        AliCapabilityCatalog.MutateParticipantMemoryName,
        AliCapabilityCatalog.ReconcileParticipantMemoryMutationName,
        AliCapabilityCatalog.CodingFormatProjectName,
        AliCapabilityCatalog.DotNetCreateProjectName,
        AliCapabilityCatalog.ArduinoCreateCompileName,
        AliCapabilityCatalog.ArduinoSearchLibrariesName,
        AliCapabilityCatalog.ArduinoInstallCoreName,
        AliCapabilityCatalog.ArduinoInstallLibraryName,
        AliCapabilityCatalog.RaspberryPiDeployName,
        AliCapabilityCatalog.GitStatusName,
        AliCapabilityCatalog.GitDiffName,
        AliCapabilityCatalog.GitHistoryName,
        AliCapabilityCatalog.GitBlameName,
        AliCapabilityCatalog.GitCreateBranchName,
        AliCapabilityCatalog.GitCommitName,
        AliCapabilityCatalog.GitPushName,
        AliCapabilityCatalog.ArchiveCreateName,
        AliCapabilityCatalog.ArchiveListName,
        AliCapabilityCatalog.ArchiveExtractName,
        AliCapabilityCatalog.RoslynFormatProjectName,
        AliCapabilityCatalog.RoslynApplyRenameName);

    private static readonly IReadOnlySet<string> AdditionalLocalReadingToolNames = ToolSet(
        AliCapabilityCatalog.SearchCurrentWebName,
        AliCapabilityCatalog.ResearchWebName,
        AliCapabilityCatalog.RecallUserMemoryName,
        AliCapabilityCatalog.MutateParticipantMemoryName,
        AliCapabilityCatalog.ListCurrentUserMemoriesName,
        AliCapabilityCatalog.ReconcileParticipantMemoryMutationName,
        AliCapabilityCatalog.FileListName,
        AliCapabilityCatalog.FileSearchName,
        AliCapabilityCatalog.FileReplaceLinesName,
        AliCapabilityCatalog.FileCopyName,
        AliCapabilityCatalog.FileMetadataName,
        AliCapabilityCatalog.ArchiveCreateName,
        AliCapabilityCatalog.ArchiveListName,
        AliCapabilityCatalog.ArchiveExtractName,
        AliCapabilityCatalog.WorkMemoryReadName,
        AliCapabilityCatalog.WorkMemoryListName,
        AliCapabilityCatalog.WorkMemorySearchName,
        AliCapabilityCatalog.WorkMemoryReplaceName,
        AliCapabilityCatalog.WorkMemoryReplaceLinesName,
        AliCapabilityCatalog.CodingListCapabilitiesName,
        AliCapabilityCatalog.VisualStudioInspectName,
        AliCapabilityCatalog.GnuNativeInspectName,
        AliCapabilityCatalog.LoadAgentSkillName,
        AliCapabilityCatalog.ReadAgentSkillResourceName,
        AliCapabilityCatalog.ArduinoSearchLibrariesName,
        AliCapabilityCatalog.ArduinoInstallCoreName,
        AliCapabilityCatalog.ArduinoInstallLibraryName,
        AliCapabilityCatalog.RaspberryPiProbeName,
        AliCapabilityCatalog.RaspberryPiInspectLibrariesName,
        AliCapabilityCatalog.RaspberryPiSearchPackagesName);

    private static readonly IReadOnlySet<string> AdditionalLocalWritingToolNames = ToolSet(
        AliCapabilityCatalog.SearchCurrentWebName,
        AliCapabilityCatalog.SearchLocalLibraryName,
        AliCapabilityCatalog.RecallUserMemoryName,
        AliCapabilityCatalog.ListCurrentUserMemoriesName,
        AliCapabilityCatalog.ReconcileParticipantMemoryMutationName,
        AliCapabilityCatalog.CodingAnalyzeProjectName,
        AliCapabilityCatalog.RunAgentSkillScriptName);

    private static readonly IReadOnlySet<string> AdditionalNetworkUsingToolNames = ToolSet(
        AliCapabilityCatalog.RecallUserMemoryName,
        AliCapabilityCatalog.MutateParticipantMemoryName,
        AliCapabilityCatalog.ListCurrentUserMemoriesName,
        AliCapabilityCatalog.ReconcileParticipantMemoryMutationName);

    public static IReadOnlySet<string> KnownToolNames { get; } =
        Definitions.Keys.ToFrozenSet(StringComparer.Ordinal);

    internal static bool IsRetiredToolName(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return RetiredToolNames.Contains(toolName);
    }

    public static bool TryGetGroupId(string toolName, out string? groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (Definitions.TryGetValue(toolName, out var definition))
        {
            groupId = definition.GroupId;
            return true;
        }

        groupId = null;
        return false;
    }

    public static string GetSchemaFactoryId(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (!Definitions.ContainsKey(toolName))
        {
            throw new KeyNotFoundException(
                $"Tool '{toolName}' has no production capability schema registration.");
        }

        return $"ali.actual-schema.{toolName}";
    }

    public static CanonicalCapabilityRegistry CreateRegistry(
        IEnumerable<AIFunctionDeclaration> actualFunctions) =>
        Build(actualFunctions).Registry;

    public static AliProductionCapabilityCatalogBuild Build(
        IEnumerable<AIFunctionDeclaration> actualFunctions)
    {
        ArgumentNullException.ThrowIfNull(actualFunctions);
        var functions = actualFunctions.ToArray();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var function in functions)
        {
            ArgumentNullException.ThrowIfNull(function);
            if (!seenNames.Add(function.Name))
            {
                throw new ArgumentException(
                    $"Duplicate actual function declaration '{function.Name}'.",
                    nameof(actualFunctions));
            }
        }

        var taskDescriptors = functions
            .Where(function => Definitions.ContainsKey(function.Name)
                && AliProductionToolOutcomeRegistry.HasContract(function.Name))
            .Select(CreateDescriptor)
            .ToArray();
        var protocolFunction = functions
            .SingleOrDefault(function => string.Equals(
                function.Name,
                OrchestrationProtocolCapability.ToolName,
                StringComparison.Ordinal));
        var descriptors = protocolFunction is null
            ? taskDescriptors
            : taskDescriptors
                .Append(CreateProtocolDescriptor(protocolFunction))
                .ToArray();
        var quarantinedNames = functions
            .Where(function => (!Definitions.ContainsKey(function.Name)
                    || !AliProductionToolOutcomeRegistry.HasContract(function.Name))
                && !string.Equals(
                    function.Name,
                    OrchestrationProtocolCapability.ToolName,
                    StringComparison.Ordinal))
            .Select(function => function.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new AliProductionCapabilityCatalogBuild(
            new CanonicalCapabilityRegistry(descriptors, providerBindings: LanguageProviderBindings),
            Array.AsReadOnly(quarantinedNames));
    }

    private static CapabilityDescriptor CreateProtocolDescriptor(
        AIFunctionDeclaration function) =>
        CapabilityDescriptor.Create(
            id: "ali.protocol.orchestration-decision",
            toolName: OrchestrationProtocolCapability.ToolName,
            displayName: "Ali orchestration protocol",
            description: function.Description,
            tier: CapabilityTier.Protocol,
            groupId: null,
            providerId: ProviderId,
            registrationKind: CapabilityRegistrationKind.Native,
            schemaFactoryId: "ali.protocol.orchestration-decision.invariant-v1",
            schemaFactory: () => function,
            providerGate: new CapabilityProviderGate(
                CapabilityProviderGateKind.OwnerOnly,
                []),
            prerequisiteGroupIds: [],
            presetIds: [],
            permission: new CapabilityPermissionDescriptor(
                "ali-orchestration-protocol-v1",
                RequiresApproval: false,
                Risk: CapabilityRiskLevel.None),
            semanticSearchText: "internal orchestration decision protocol",
            semanticMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tier"] = "protocol",
                ["schema-source"] = "dynamic-per-planning-pass"
            },
                visibleInCapabilityReport: true,
                visibleInCriticInventory: true,
            mcpExposure: new CapabilityMcpExposure(false, null),
            effect: new CapabilityEffectDescriptor(
                CapabilityEffectKind.None,
                "Commits one validated orchestration transition without performing a task-domain effect.",
                CapabilityMutationBoundary.None,
                SupportsIdempotency: false,
                ReconcilerId: null,
                ReadsLocalData: false,
                WritesLocalData: false,
                UsesNetwork: false,
                StartsProcesses: false,
                ChangesSystemState: false));

    private static CapabilityDescriptor CreateDescriptor(AIFunctionDeclaration function)
    {
        var definition = Definitions[function.Name];
        if (!AliProductionToolOutcomeRegistry.TryGetContractId(
                function.Name,
                out var outcomeContractId)
            || outcomeContractId is null)
        {
            throw new InvalidOperationException(
                $"Tool '{function.Name}' has no production outcome contract.");
        }

        var effect = CreateEffect(function.Name, definition.GroupId);
        var providerBound = ResolvedLanguageTargetToolNames.Contains(function.Name);
        var registrationKind = providerBound
            ? CapabilityRegistrationKind.LanguageProvider
            : AgentSkillToolNames.Contains(function.Name)
            ? CapabilityRegistrationKind.AgentSkill
            : FrameworkBuiltInToolNames.Contains(function.Name)
                ? CapabilityRegistrationKind.FrameworkBuiltIn
                : CapabilityRegistrationKind.Native;
        var presetIds = CanonicalCapabilityCatalog.Presets
            .Where(preset => preset.GroupIds.Contains(definition.GroupId, StringComparer.Ordinal))
            .Select(preset => preset.Id)
            .ToArray();
        var exposed = McpPolicies.ContainsKey(function.Name);
        var description = string.IsNullOrWhiteSpace(function.Description)
            ? definition.Description
            : function.Description;

        return CapabilityDescriptor.Create(
            id: $"ali.tool.{function.Name}",
            toolName: function.Name,
            displayName: Humanize(function.Name),
            description: description,
            tier: CapabilityTier.Task,
            groupId: definition.GroupId,
            providerId: ProviderId,
            registrationKind: registrationKind,
            schemaFactoryId: GetSchemaFactoryId(function.Name),
            schemaFactory: () => function,
            providerGate: providerBound
                ? new CapabilityProviderGate(
                    CapabilityProviderGateKind.ResolvedTarget,
                    LanguageProviderBindings.Select(binding => binding.ProviderId).ToArray())
                : new CapabilityProviderGate(CapabilityProviderGateKind.OwnerOnly, []),
            prerequisiteGroupIds: [],
            presetIds: presetIds,
            permission: new CapabilityPermissionDescriptor(
                "ali-tool-permission-v1",
                AliToolPermissionPolicy.RequiresApproval(function.Name),
                RiskFor(effect.Kind)),
            semanticSearchText: $"{function.Name} {description} {CanonicalCapabilityCatalog.GetGroup(definition.GroupId).DisplayName}",
            semanticMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["group"] = definition.GroupId,
                ["provider"] = ProviderId,
                ["registration-kind"] = registrationKind.ToString(),
                ["schema-source"] = "actual-runtime-declaration",
                ["outcome-contract"] = outcomeContractId
            },
            visibleInCapabilityReport: true,
            visibleInCriticInventory: true,
            mcpExposure: new CapabilityMcpExposure(exposed, exposed ? function.Name : null),
            effect: effect);
    }

    private static CapabilityEffectDescriptor CreateEffect(string toolName, string groupId)
    {
        McpPolicies.TryGetValue(toolName, out var mcpPolicy);
        var kind = DestructiveToolNames.Contains(toolName)
            ? CapabilityEffectKind.Destructive
            : ExternalMutationToolNames.Contains(toolName)
                ? CapabilityEffectKind.ExternalMutation
                : SourceMutationToolNames.Contains(toolName)
                    ? CapabilityEffectKind.SourceMutation
                    : ProcessControlToolNames.Contains(toolName)
                        || MsBuildWorkspaceReadOrPreviewToolNames.Contains(toolName)
                        ? CapabilityEffectKind.ProcessControl
                        : LocalMutationToolNames.Contains(toolName)
                            || mcpPolicy?.WritesLocalData == true
                            ? CapabilityEffectKind.LocalMutation
                            : ExplicitExternalReadToolNames.Contains(toolName)
                                || mcpPolicy?.UsesNetwork == true
                                ? CapabilityEffectKind.ExternalRead
                                : CapabilityEffectKind.Read;
        var boundary = kind switch
        {
            CapabilityEffectKind.SourceMutation => CapabilityMutationBoundary.StagedWorkspace,
            CapabilityEffectKind.ExternalMutation
                or CapabilityEffectKind.LocalMutation
                or CapabilityEffectKind.ProcessControl
                or CapabilityEffectKind.Destructive => CapabilityMutationBoundary.PermissionGuarded,
            _ => CapabilityMutationBoundary.None
        };
        var isMutation = kind is CapabilityEffectKind.LocalMutation
            or CapabilityEffectKind.SourceMutation
            or CapabilityEffectKind.ProcessControl
            or CapabilityEffectKind.ExternalMutation
            or CapabilityEffectKind.Destructive;
        var readsLocalData = mcpPolicy?.ReadsPrivateData == true
            || AdditionalLocalReadingToolNames.Contains(toolName)
            || MsBuildWorkspaceToolNames.Contains(toolName)
            || ProjectControlledExecutionToolNames.Contains(toolName);
        var writesLocalData = mcpPolicy?.WritesLocalData == true
            || kind is CapabilityEffectKind.LocalMutation
                or CapabilityEffectKind.SourceMutation
                or CapabilityEffectKind.Destructive
            || AdditionalLocalWritingToolNames.Contains(toolName)
            || MsBuildWorkspaceToolNames.Contains(toolName)
            || ProjectControlledExecutionToolNames.Contains(toolName);
        var usesNetwork = mcpPolicy?.UsesNetwork == true
            || kind is CapabilityEffectKind.ExternalRead or CapabilityEffectKind.ExternalMutation
            || AdditionalNetworkUsingToolNames.Contains(toolName)
            || MsBuildWorkspaceToolNames.Contains(toolName)
            || ProjectControlledExecutionToolNames.Contains(toolName);
        var startsProcesses = kind == CapabilityEffectKind.ProcessControl
            || AdditionalProcessStartingToolNames.Contains(toolName)
            || MsBuildWorkspaceToolNames.Contains(toolName)
            || ProjectControlledExecutionToolNames.Contains(toolName);

        return new CapabilityEffectDescriptor(
            kind,
            EffectSummary(kind),
            boundary,
            SupportsIdempotency: !isMutation && !ExplicitlyNonIdempotentToolNames.Contains(toolName),
            ReconcilerId: isMutation ? $"ali.reconcile.{toolName}" : null,
            ReadsLocalData: readsLocalData,
            WritesLocalData: writesLocalData,
            UsesNetwork: usesNetwork,
            StartsProcesses: startsProcesses,
            ChangesSystemState: ChangesSystemStateToolNames.Contains(toolName)
                || MsBuildWorkspaceToolNames.Contains(toolName)
                || ProjectControlledExecutionToolNames.Contains(toolName));
    }

    private static CapabilityRiskLevel RiskFor(CapabilityEffectKind kind) => kind switch
    {
        CapabilityEffectKind.Destructive => CapabilityRiskLevel.High,
        CapabilityEffectKind.SourceMutation
            or CapabilityEffectKind.ProcessControl
            or CapabilityEffectKind.ExternalMutation => CapabilityRiskLevel.High,
        CapabilityEffectKind.LocalMutation => CapabilityRiskLevel.Medium,
        _ => CapabilityRiskLevel.Low
    };

    private static string EffectSummary(CapabilityEffectKind kind) => kind switch
    {
        CapabilityEffectKind.ExternalRead => "Reads data through an explicit network boundary without changing external state.",
        CapabilityEffectKind.LocalMutation => "Changes local durable state through the permission-guarded capability boundary.",
        CapabilityEffectKind.SourceMutation => "Changes source content through the declared mutation boundary.",
        CapabilityEffectKind.ProcessControl => "Starts or controls a process through the permission-guarded capability boundary.",
        CapabilityEffectKind.ExternalMutation => "Changes remote state through the declared mutation boundary.",
        CapabilityEffectKind.Destructive => "Removes or terminates state through the permission-guarded capability boundary.",
        _ => "Reads authoritative capability or environment data without changing state."
    };

    private static string Humanize(string toolName)
    {
        var text = toolName.Replace('_', ' ');
        return text.Length == 0 ? toolName : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static IReadOnlyDictionary<string, KnownToolDefinition> CreateDefinitions()
    {
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        AddGroup(groups, CapabilityGroupIds.CapabilityDiscovery,
            AliCapabilityCatalog.ListAvailableToolsName,
            AliCapabilityCatalog.SemanticDiscoverToolsName);
        AddGroup(groups, CapabilityGroupIds.PersonalContextAndMemory,
            AliCapabilityCatalog.GetActiveUserProfileName,
            AliCapabilityCatalog.RecallUserMemoryName,
            AliCapabilityCatalog.MutateParticipantMemoryName,
            AliCapabilityCatalog.ConsentParticipantMemoryProposalName,
            AliCapabilityCatalog.ReconcileParticipantMemoryMutationName,
            AliCapabilityCatalog.ListCurrentUserMemoriesName,
            AliCapabilityCatalog.GetAssistantIdentityName,
            AliCapabilityCatalog.GetCurrentLocalTimeName);
        AddGroup(groups, CapabilityGroupIds.WebResearchAndNavigation,
            AliCapabilityCatalog.SearchCurrentWebName,
            AliCapabilityCatalog.CreateGoogleMapsDirectionsLinkName,
            AliCapabilityCatalog.ResearchWebName,
            AliCapabilityCatalog.SearchLocalLibraryName);
        AddGroup(groups, CapabilityGroupIds.RemindersAndCalendar,
            AliCapabilityCatalog.CreateCalendarEventName);
        AddGroup(groups, CapabilityGroupIds.FilesAndArchives,
            AliCapabilityCatalog.FileWriteName,
            AliCapabilityCatalog.FileReadName,
            AliCapabilityCatalog.FileDeleteName,
            AliCapabilityCatalog.FileListName,
            AliCapabilityCatalog.FileSearchName,
            AliCapabilityCatalog.FileReplaceName,
            AliCapabilityCatalog.FileReplaceLinesName,
            AliCapabilityCatalog.FileMoveName,
            AliCapabilityCatalog.FileCopyName,
            AliCapabilityCatalog.FileCreateDirectoryName,
            AliCapabilityCatalog.FileMetadataName,
            AliCapabilityCatalog.ArchiveCreateName,
            AliCapabilityCatalog.ArchiveListName,
            AliCapabilityCatalog.ArchiveExtractName);
        AddGroup(groups, CapabilityGroupIds.WorkMemory,
            AliCapabilityCatalog.WorkMemoryWriteName,
            AliCapabilityCatalog.WorkMemoryReadName,
            AliCapabilityCatalog.WorkMemoryDeleteName,
            AliCapabilityCatalog.WorkMemoryListName,
            AliCapabilityCatalog.WorkMemorySearchName,
            AliCapabilityCatalog.WorkMemoryReplaceName,
            AliCapabilityCatalog.WorkMemoryReplaceLinesName);
        AddGroup(groups, CapabilityGroupIds.AgentModesAndSkills,
            AliCapabilityCatalog.GetAgentModeName,
            AliCapabilityCatalog.SetAgentModeName,
            AliCapabilityCatalog.LoadAgentSkillName,
            AliCapabilityCatalog.ReadAgentSkillResourceName,
            AliCapabilityCatalog.RunAgentSkillScriptName);
        AddGroup(groups, CapabilityGroupIds.ProgrammingCore,
            AliCapabilityCatalog.CodingListCapabilitiesName,
            AliCapabilityCatalog.CodingInspectProjectName,
            AliCapabilityCatalog.CodingIndexProjectName,
            AliCapabilityCatalog.CodingSearchSymbolsName,
            AliCapabilityCatalog.CodingAnalyzeProjectName,
            AliCapabilityCatalog.CodingFormatProjectName,
            AliCapabilityCatalog.CodingBuildProjectName,
            AliCapabilityCatalog.CodingTestProjectName,
            AliCapabilityCatalog.CodingRunProjectName,
            AliCapabilityCatalog.CodingInspectArchitectureName,
            AliCapabilityCatalog.CodingBuildContextName,
            AliCapabilityCatalog.CodingProbeServiceName,
            AliCapabilityCatalog.CodingInspectProcessName);
        AddGroup(groups, CapabilityGroupIds.CSharpDotNetRoslyn,
            AliCapabilityCatalog.DotNetCreateProjectName,
            AliCapabilityCatalog.RoslynAnalyzeProjectName,
            AliCapabilityCatalog.RoslynFormatProjectName,
            AliCapabilityCatalog.RoslynFindSymbolName,
            AliCapabilityCatalog.RoslynGetCompletionsName,
            AliCapabilityCatalog.RoslynInspectSolutionName,
            AliCapabilityCatalog.RoslynInspectDocumentName,
            AliCapabilityCatalog.RoslynInspectPositionName,
            AliCapabilityCatalog.RoslynFindReferencesName,
            AliCapabilityCatalog.RoslynInspectTargetName,
            AliCapabilityCatalog.RoslynListActionsName,
            AliCapabilityCatalog.RoslynPreviewActionName,
            AliCapabilityCatalog.RoslynApplyActionName,
            AliCapabilityCatalog.RoslynVerifyChangesetName,
            AliCapabilityCatalog.DotNetBuildName,
            AliCapabilityCatalog.DotNetRunName,
            AliCapabilityCatalog.DotNetStopProjectName,
            AliCapabilityCatalog.DotNetTestName,
            AliCapabilityCatalog.DotNetVerifyName,
            AliCapabilityCatalog.DotNetDebugLaunchName,
            AliCapabilityCatalog.DotNetDebugAttachName,
            AliCapabilityCatalog.DotNetDebugInspectName,
            AliCapabilityCatalog.DotNetDebugEvaluateName,
            AliCapabilityCatalog.DotNetDebugBreakpointsName,
            AliCapabilityCatalog.DotNetDebugControlName,
            AliCapabilityCatalog.DotNetDebugStopName,
            AliCapabilityCatalog.DotNetDebugDiagnosticsHandoffName,
            AliCapabilityCatalog.DotNetDependencyInspectName,
            AliCapabilityCatalog.DotNetDependencyPreviewName,
            AliCapabilityCatalog.DotNetDependencyApplyName);
        AddGroup(groups, CapabilityGroupIds.NativeCppGcc,
            AliCapabilityCatalog.GnuNativeInspectName,
            AliCapabilityCatalog.GnuNativeExecuteName);
        AddGroup(groups, CapabilityGroupIds.Arduino,
            AliCapabilityCatalog.ArduinoInspectName,
            AliCapabilityCatalog.ArduinoSearchLibrariesName,
            AliCapabilityCatalog.ArduinoInstallCoreName,
            AliCapabilityCatalog.ArduinoInstallLibraryName,
            AliCapabilityCatalog.ArduinoCreateCompileName,
            AliCapabilityCatalog.ArduinoCompileName,
            AliCapabilityCatalog.ArduinoUploadName,
            AliCapabilityCatalog.ArduinoOpenIdeName);
        AddGroup(groups, CapabilityGroupIds.RaspberryPi,
            AliCapabilityCatalog.RaspberryPiLibrariesName,
            AliCapabilityCatalog.RaspberryPiProbeName,
            AliCapabilityCatalog.RaspberryPiInspectLibrariesName,
            AliCapabilityCatalog.RaspberryPiSearchPackagesName,
            AliCapabilityCatalog.RaspberryPiDeployName);
        AddGroup(groups, CapabilityGroupIds.DevOpsArchitectureQuality,
            AliCapabilityCatalog.GitStatusName,
            AliCapabilityCatalog.GitDiffName,
            AliCapabilityCatalog.GitHistoryName,
            AliCapabilityCatalog.GitBlameName,
            AliCapabilityCatalog.GitCreateBranchName,
            AliCapabilityCatalog.GitCommitName,
            AliCapabilityCatalog.GitPushName,
            AliCapabilityCatalog.ArchitectureInspectName,
            AliCapabilityCatalog.ArchitectureCheckName,
            AliCapabilityCatalog.DotNetQualityScanName,
            AliCapabilityCatalog.DotNetPerformanceMeasureName,
            AliCapabilityCatalog.DotNetPerformanceCompareName,
            AliCapabilityCatalog.DotNetPerformanceTraceName,
            AliCapabilityCatalog.DotNetApplicationVerifyName,
            AliCapabilityCatalog.DotNetReleasePublishName,
            AliCapabilityCatalog.DotNetArchitectureReportName,
            AliCapabilityCatalog.DotNetDeliveryVerifyName);
        AddGroup(groups, CapabilityGroupIds.VisualStudio,
            AliCapabilityCatalog.VisualStudioInspectName,
            AliCapabilityCatalog.VisualStudioBuildName,
            AliCapabilityCatalog.VisualStudioOpenName);
        var catalogByName = AliCapabilityCatalog.Tools
            .Where(tool => !IsRetiredToolName(tool.Name))
            .ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var catalogNames = catalogByName.Keys.ToHashSet(StringComparer.Ordinal);
        if (!catalogNames.SetEquals(groups.Keys))
        {
            var missing = catalogNames.Except(groups.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var extra = groups.Keys.Except(catalogNames, StringComparer.Ordinal).Order(StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"Production capability metadata is not exact. Missing: [{string.Join(", ", missing)}]. Extra: [{string.Join(", ", extra)}].");
        }

        return groups.ToFrozenDictionary(
            pair => pair.Key,
            pair => new KnownToolDefinition(pair.Value, catalogByName[pair.Key].Description),
            StringComparer.Ordinal);
    }

    private static void AddGroup(
        IDictionary<string, string> groups,
        string groupId,
        params string[] toolNames)
    {
        foreach (var toolName in toolNames)
        {
            if (!groups.TryAdd(toolName, groupId))
            {
                throw new InvalidOperationException(
                    $"Production capability '{toolName}' has more than one group assignment.");
            }
        }
    }

    private static IReadOnlySet<string> ToolSet(params string[] names) =>
        names.ToFrozenSet(StringComparer.Ordinal);

    private sealed record KnownToolDefinition(string GroupId, string Description);
}
