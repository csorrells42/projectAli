using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Mcp;
using Ali.Modules.Permissions;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.Capabilities;

public sealed class AliProductionCapabilityCatalogTests
{
    [Fact]
    public void KnownMetadata_CoversTheExactActiveOrdinaryCatalogOnce()
    {
        var catalogNames = AliCapabilityCatalog.Tools.Select(tool => tool.Name).ToArray();
        var activeCatalogNames = catalogNames
            .Where(name => !AliProductionCapabilityCatalog.IsRetiredToolName(name))
            .ToArray();
        var retiredCatalogNames = catalogNames
            .Where(AliProductionCapabilityCatalog.IsRetiredToolName)
            .ToArray();

        Assert.Equal(catalogNames.Length, catalogNames.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(122, activeCatalogNames.Length);
        Assert.Equal(
            new[]
            {
                AliCapabilityCatalog.CodingAgentExecuteName,
                AliCapabilityCatalog.CodingAgentStatusName
            },
            retiredCatalogNames.Order(StringComparer.Ordinal));
        Assert.Equal(activeCatalogNames.Length, AliProductionCapabilityCatalog.KnownToolNames.Count);
        Assert.True(activeCatalogNames.ToHashSet(StringComparer.Ordinal)
            .SetEquals(AliProductionCapabilityCatalog.KnownToolNames));
        Assert.All(activeCatalogNames, name =>
        {
            Assert.True(AliProductionCapabilityCatalog.TryGetGroupId(name, out var groupId));
            Assert.Contains(groupId, CapabilityGroupIds.All);
        });
        Assert.All(retiredCatalogNames, name =>
        {
            Assert.DoesNotContain(name, AliProductionCapabilityCatalog.KnownToolNames);
            Assert.False(AliProductionCapabilityCatalog.TryGetGroupId(name, out var groupId));
            Assert.Null(groupId);
        });
    }

    [Fact]
    public void NewOrdinaryFamilies_HaveExactHonestMembership()
    {
        AssertGroup(CapabilityGroupIds.CapabilityDiscovery,
            AliCapabilityCatalog.ListAvailableToolsName,
            AliCapabilityCatalog.SemanticDiscoverToolsName);
        AssertGroup(CapabilityGroupIds.PersonalContextAndMemory,
            AliCapabilityCatalog.GetActiveUserProfileName,
            AliCapabilityCatalog.RecallUserMemoryName,
            AliCapabilityCatalog.ForgetCurrentUserMemoryName,
            AliCapabilityCatalog.ListCurrentUserMemoriesName,
            AliCapabilityCatalog.GetAssistantIdentityName,
            AliCapabilityCatalog.GetCurrentLocalTimeName);
        AssertGroup(CapabilityGroupIds.WebResearchAndNavigation,
            AliCapabilityCatalog.SearchCurrentWebName,
            AliCapabilityCatalog.CreateGoogleMapsDirectionsLinkName,
            AliCapabilityCatalog.ResearchWebName,
            AliCapabilityCatalog.SearchLocalLibraryName);
        AssertGroup(CapabilityGroupIds.RemindersAndCalendar,
            AliCapabilityCatalog.CreateCalendarEventName);
        AssertGroup(CapabilityGroupIds.WorkMemory,
            AliCapabilityCatalog.WorkMemoryWriteName,
            AliCapabilityCatalog.WorkMemoryReadName,
            AliCapabilityCatalog.WorkMemoryDeleteName,
            AliCapabilityCatalog.WorkMemoryListName,
            AliCapabilityCatalog.WorkMemorySearchName,
            AliCapabilityCatalog.WorkMemoryReplaceName,
            AliCapabilityCatalog.WorkMemoryReplaceLinesName);
        AssertGroup(CapabilityGroupIds.AgentModesAndSkills,
            AliCapabilityCatalog.GetAgentModeName,
            AliCapabilityCatalog.SetAgentModeName,
            AliCapabilityCatalog.LoadAgentSkillName,
            AliCapabilityCatalog.ReadAgentSkillResourceName,
            AliCapabilityCatalog.RunAgentSkillScriptName);
        AssertGroup(CapabilityGroupIds.SpecialistsAndWorkflows,
            AliCapabilityCatalog.ConsultSoftwareEngineerName,
            AliCapabilityCatalog.ConsultResearcherName,
            AliCapabilityCatalog.ConsultOfficeSpecialistName,
            AliCapabilityCatalog.RunResearchArtifactWorkflowName,
            AliCapabilityCatalog.RunProgrammingGroupChatName,
            AliCapabilityCatalog.RunMagenticOrchestrationName,
            AliCapabilityCatalog.ListRecoverableWorkflowsName,
            AliCapabilityCatalog.ResumeWorkflowCheckpointName);
    }

    [Fact]
    public void ExistingFileAndProgrammingFamilies_HaveExactHonestMembership()
    {
        AssertGroup(CapabilityGroupIds.FilesAndArchives,
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
        AssertGroup(CapabilityGroupIds.ProgrammingCore,
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
        AssertGroup(CapabilityGroupIds.CSharpDotNetRoslyn,
            AliCapabilityCatalog.DotNetCreateProjectName,
            AliCapabilityCatalog.RoslynAnalyzeProjectName,
            AliCapabilityCatalog.RoslynFormatProjectName,
            AliCapabilityCatalog.RoslynFindSymbolName,
            AliCapabilityCatalog.RoslynGetCompletionsName,
            AliCapabilityCatalog.RoslynInspectSolutionName,
            AliCapabilityCatalog.RoslynInspectDocumentName,
            AliCapabilityCatalog.RoslynInspectPositionName,
            AliCapabilityCatalog.RoslynFindReferencesName,
            AliCapabilityCatalog.RoslynPreviewRenameName,
            AliCapabilityCatalog.RoslynApplyRenameName,
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
        AssertGroup(CapabilityGroupIds.NativeCppGcc,
            AliCapabilityCatalog.GnuNativeInspectName,
            AliCapabilityCatalog.GnuNativeExecuteName);
        AssertGroup(CapabilityGroupIds.Arduino,
            AliCapabilityCatalog.ArduinoInspectName,
            AliCapabilityCatalog.ArduinoSearchLibrariesName,
            AliCapabilityCatalog.ArduinoInstallCoreName,
            AliCapabilityCatalog.ArduinoInstallLibraryName,
            AliCapabilityCatalog.ArduinoCreateCompileName,
            AliCapabilityCatalog.ArduinoCompileName,
            AliCapabilityCatalog.ArduinoUploadName,
            AliCapabilityCatalog.ArduinoOpenIdeName);
        AssertGroup(CapabilityGroupIds.RaspberryPi,
            AliCapabilityCatalog.RaspberryPiLibrariesName,
            AliCapabilityCatalog.RaspberryPiProbeName,
            AliCapabilityCatalog.RaspberryPiInspectLibrariesName,
            AliCapabilityCatalog.RaspberryPiSearchPackagesName,
            AliCapabilityCatalog.RaspberryPiDeployName);
        AssertGroup(CapabilityGroupIds.DevOpsArchitectureQuality,
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
        AssertGroup(CapabilityGroupIds.VisualStudio,
            AliCapabilityCatalog.VisualStudioInspectName,
            AliCapabilityCatalog.VisualStudioBuildName,
            AliCapabilityCatalog.VisualStudioOpenName);
        AssertGroup(CapabilityGroupIds.Python);
        AssertGroup(CapabilityGroupIds.WebHtmlCssJavaScriptTypeScript);
        AssertGroup(CapabilityGroupIds.Java);
    }

    [Fact]
    public void Build_UsesEverySuppliedKnownActualSchemaAndValidatesTheRegistry()
    {
        var actualFunctions = AliCapabilityCatalog.Tools
            .Where(tool => !AliProductionCapabilityCatalog.IsRetiredToolName(tool.Name))
            .Select(tool => Function(tool.Name, $"actual schema for {tool.Name}"))
            .ToArray();
        var actualByName = actualFunctions.ToDictionary(function => function.Name, StringComparer.Ordinal);

        var result = AliProductionCapabilityCatalog.Build(actualFunctions);

        Assert.Empty(result.QuarantinedToolNames);
        Assert.Equal(122, actualFunctions.Length);
        Assert.Equal(actualFunctions.Length, result.Registry.Descriptors.Count);
        Assert.Equal(
            new[]
            {
                ("arduino-cli", CapabilityGroupIds.Arduino),
                ("cpp-msvc", CapabilityGroupIds.NativeCppGcc),
                ("dotnet-roslyn", CapabilityGroupIds.CSharpDotNetRoslyn),
                ("java-temurin", CapabilityGroupIds.Java),
                ("python-cpython", CapabilityGroupIds.Python),
                ("web-node", CapabilityGroupIds.WebHtmlCssJavaScriptTypeScript)
            },
            result.Registry.ProviderBindings
                .Select(binding => (binding.ProviderId, binding.GroupId))
                .OrderBy(binding => binding.ProviderId, StringComparer.Ordinal));
        Assert.Equal(AliProductionCapabilityCatalog.ProviderId,
            Assert.Single(result.Registry.Descriptors.Select(descriptor => descriptor.ProviderId).Distinct()));
        foreach (var descriptor in result.Registry.Descriptors)
        {
            var actual = actualByName[descriptor.ToolName];
            Assert.Same(actual, descriptor.SchemaFactory());
            Assert.Equal(CapabilitySchemaIdentity.Calculate(actual), descriptor.SchemaFingerprint);
            Assert.Equal($"actual schema for {descriptor.ToolName}", descriptor.Description);
            if (IsResolvedLanguageTargetTool(descriptor.ToolName))
            {
                Assert.Equal(CapabilityRegistrationKind.LanguageProvider, descriptor.RegistrationKind);
                Assert.Equal(CapabilityProviderGateKind.ResolvedTarget, descriptor.ProviderGate.Kind);
                Assert.Equal(
                    result.Registry.ProviderBindings.Select(binding => binding.ProviderId).Order(StringComparer.Ordinal),
                    descriptor.ProviderGate.SupportedProviderIds.Order(StringComparer.Ordinal));
            }
            else
            {
                Assert.Equal(CapabilityProviderGateKind.OwnerOnly, descriptor.ProviderGate.Kind);
                Assert.Empty(descriptor.ProviderGate.SupportedProviderIds);
            }
            Assert.Equal(
                CanonicalCapabilityCatalog.Presets
                    .Where(preset => preset.GroupIds.Contains(descriptor.GroupId!, StringComparer.Ordinal))
                    .Select(preset => preset.Id)
                    .Order(StringComparer.Ordinal),
                descriptor.PresetIds.Order(StringComparer.Ordinal));
        }
        Assert.Equal(64, result.Registry.RegistryRevision.Length);
    }

    [Fact]
    public void Build_QuarantinesUnknownToolsWithoutMetadataPromotion()
    {
        var known = Function(AliCapabilityCatalog.RoslynInspectDocumentName);
        var unknown = Function("mcp_unassigned_execute", "looks like a coding tool but has no metadata");

        var result = AliProductionCapabilityCatalog.Build([unknown, known]);

        Assert.Equal(new[] { "mcp_unassigned_execute" }, result.QuarantinedToolNames);
        var descriptor = Assert.Single(result.Registry.Descriptors);
        Assert.Equal(AliCapabilityCatalog.RoslynInspectDocumentName, descriptor.ToolName);
        Assert.False(AliProductionCapabilityCatalog.TryGetGroupId(unknown.Name, out var groupId));
        Assert.Null(groupId);
    }

    [Fact]
    public void Build_RejectsDuplicateActualToolIdentities()
    {
        var first = Function(AliCapabilityCatalog.GitStatusName, "first schema");
        var duplicate = Function(AliCapabilityCatalog.GitStatusName, "different schema");

        var exception = Assert.Throws<ArgumentException>(() =>
            AliProductionCapabilityCatalog.Build([first, duplicate]));

        Assert.Contains(AliCapabilityCatalog.GitStatusName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistrationKinds_ReflectFrameworkOwnershipExactly()
    {
        var registry = AliProductionCapabilityCatalog.CreateRegistry(
            AliCapabilityCatalog.Tools.Select(tool => Function(tool.Name)));
        var byName = registry.Descriptors.ToDictionary(descriptor => descriptor.ToolName, StringComparer.Ordinal);

        Assert.Equal(CapabilityRegistrationKind.FrameworkBuiltIn, byName[AliCapabilityCatalog.FileReadName].RegistrationKind);
        Assert.Equal(CapabilityRegistrationKind.FrameworkBuiltIn, byName[AliCapabilityCatalog.WorkMemoryReadName].RegistrationKind);
        Assert.Equal(CapabilityRegistrationKind.FrameworkBuiltIn, byName[AliCapabilityCatalog.GetAgentModeName].RegistrationKind);
        Assert.Equal(CapabilityRegistrationKind.AgentSkill, byName[AliCapabilityCatalog.LoadAgentSkillName].RegistrationKind);
        Assert.Equal(CapabilityRegistrationKind.AgentSkill, byName[AliCapabilityCatalog.RunAgentSkillScriptName].RegistrationKind);
        Assert.Equal(CapabilityRegistrationKind.Native, byName[AliCapabilityCatalog.FileMoveName].RegistrationKind);
        Assert.Equal(CapabilityRegistrationKind.Native, byName[AliCapabilityCatalog.RoslynInspectDocumentName].RegistrationKind);
        Assert.Equal(CapabilityRegistrationKind.Native, byName[AliCapabilityCatalog.RunProgrammingGroupChatName].RegistrationKind);
        Assert.Equal(CapabilityRegistrationKind.LanguageProvider, byName[AliCapabilityCatalog.CodingAnalyzeProjectName].RegistrationKind);
        Assert.Equal(CapabilityRegistrationKind.LanguageProvider, byName[AliCapabilityCatalog.CodingFormatProjectName].RegistrationKind);
        Assert.Equal(CapabilityRegistrationKind.LanguageProvider, byName[AliCapabilityCatalog.CodingBuildProjectName].RegistrationKind);
        Assert.Equal(CapabilityRegistrationKind.LanguageProvider, byName[AliCapabilityCatalog.CodingTestProjectName].RegistrationKind);
        Assert.Equal(CapabilityRegistrationKind.LanguageProvider, byName[AliCapabilityCatalog.CodingRunProjectName].RegistrationKind);
    }

    [Fact]
    public void MutationsAndMcpExposure_AreConservativeAndExact()
    {
        var registry = AliProductionCapabilityCatalog.CreateRegistry(
            AliCapabilityCatalog.Tools.Select(tool => Function(tool.Name)));
        var byName = registry.Descriptors.ToDictionary(descriptor => descriptor.ToolName, StringComparer.Ordinal);

        foreach (var descriptor in registry.Descriptors.Where(descriptor => descriptor.Effect.IsMutation))
        {
            Assert.NotEqual(CapabilityMutationBoundary.None, descriptor.Effect.MutationBoundary);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Effect.ReconcilerId));
            Assert.NotEqual(CapabilityRiskLevel.None, descriptor.Permission.Risk);
        }
        foreach (var policy in McpServerToolCatalog.CreateDefaultPolicies().Where(policy => policy.WritesLocalData))
        {
            if (byName.TryGetValue(policy.Name, out var descriptor))
            {
                Assert.True(descriptor.Effect.IsMutation, policy.Name);
            }
        }

        var expectedMcpNames = McpServerToolCatalog.CreateDefaultPolicies()
            .Select(policy => policy.Name)
            .Intersect(byName.Keys, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var exposedNames = registry.Descriptors
            .Where(descriptor => descriptor.McpExposure.Exposed)
            .Select(descriptor => descriptor.McpExposure.PublishedName!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(expectedMcpNames.SetEquals(exposedNames));
        Assert.All(registry.Descriptors.Where(descriptor => !descriptor.McpExposure.Exposed),
            descriptor => Assert.Null(descriptor.McpExposure.PublishedName));

        var debuggerEvaluation = byName[AliCapabilityCatalog.DotNetDebugEvaluateName];
        Assert.Equal(CapabilityEffectKind.ProcessControl, debuggerEvaluation.Effect.Kind);
        Assert.Equal(
            CapabilityMutationBoundary.PermissionGuarded,
            debuggerEvaluation.Effect.MutationBoundary);
        Assert.True(debuggerEvaluation.Effect.StartsProcesses);
        Assert.True(debuggerEvaluation.Permission.RequiresApproval);
        Assert.NotNull(debuggerEvaluation.Effect.ReconcilerId);

        var localLibrarySearch = byName[AliCapabilityCatalog.SearchLocalLibraryName];
        Assert.Equal(CapabilityEffectKind.ProcessControl, localLibrarySearch.Effect.Kind);
        Assert.Equal(
            CapabilityMutationBoundary.PermissionGuarded,
            localLibrarySearch.Effect.MutationBoundary);
        Assert.True(localLibrarySearch.Effect.ReadsLocalData);
        Assert.True(localLibrarySearch.Effect.WritesLocalData);
        Assert.True(localLibrarySearch.Effect.UsesNetwork);
        Assert.True(localLibrarySearch.Effect.StartsProcesses);
        Assert.False(localLibrarySearch.Permission.RequiresApproval);
        Assert.NotNull(localLibrarySearch.Effect.ReconcilerId);

        var calendarEvent = byName[AliCapabilityCatalog.CreateCalendarEventName];
        Assert.Equal(CapabilityEffectKind.LocalMutation, calendarEvent.Effect.Kind);
        Assert.True(calendarEvent.Effect.StartsProcesses);
        Assert.True(calendarEvent.Effect.ChangesSystemState);

        // Effect metadata describes the task-domain action. Framework audit receipts
        // do not turn a read into a target-domain mutation.
        var fileRead = byName[AliCapabilityCatalog.FileReadName];
        Assert.Equal(CapabilityEffectKind.Read, fileRead.Effect.Kind);
        Assert.True(fileRead.Effect.SupportsIdempotency);
        Assert.False(fileRead.Effect.WritesLocalData);

        var semanticDiscovery = byName[AliCapabilityCatalog.SemanticDiscoverToolsName];
        Assert.Equal(CapabilityEffectKind.Read, semanticDiscovery.Effect.Kind);
        Assert.False(semanticDiscovery.Effect.WritesLocalData);
        Assert.False(semanticDiscovery.Effect.UsesNetwork);
        Assert.False(semanticDiscovery.Effect.StartsProcesses);
        Assert.True(semanticDiscovery.Effect.SupportsIdempotency);
        Assert.False(semanticDiscovery.Permission.RequiresApproval);
        Assert.Null(semanticDiscovery.Effect.ReconcilerId);

        var genericAnalyze = byName[AliCapabilityCatalog.CodingAnalyzeProjectName];
        Assert.Equal(CapabilityEffectKind.ProcessControl, genericAnalyze.Effect.Kind);
        Assert.True(genericAnalyze.Effect.WritesLocalData);
        Assert.True(genericAnalyze.Effect.StartsProcesses);
        Assert.NotNull(genericAnalyze.Effect.ReconcilerId);
    }

    [Fact]
    public void ProviderBackedWebReads_DoNotClaimReplayIdempotency()
    {
        var byName = CreateDescriptorsByName();

        var currentWebSearch = byName[AliCapabilityCatalog.SearchCurrentWebName];
        Assert.Equal(CapabilityEffectKind.LocalMutation, currentWebSearch.Effect.Kind);
        Assert.Equal(CapabilityMutationBoundary.PermissionGuarded, currentWebSearch.Effect.MutationBoundary);
        Assert.True(currentWebSearch.Effect.WritesLocalData);
        Assert.True(currentWebSearch.Effect.UsesNetwork);
        Assert.False(currentWebSearch.Effect.SupportsIdempotency);
        Assert.False(currentWebSearch.Permission.RequiresApproval);
        Assert.False(string.IsNullOrWhiteSpace(currentWebSearch.Effect.ReconcilerId));

        var research = byName[AliCapabilityCatalog.ResearchWebName];
        Assert.Equal(CapabilityEffectKind.ExternalMutation, research.Effect.Kind);
        Assert.Equal(CapabilityMutationBoundary.PermissionGuarded, research.Effect.MutationBoundary);
        Assert.True(research.Effect.ReadsLocalData);
        Assert.True(research.Effect.UsesNetwork);
        Assert.False(research.Effect.SupportsIdempotency);
        Assert.False(string.IsNullOrWhiteSpace(research.Effect.ReconcilerId));

        var benignFileRead = byName[AliCapabilityCatalog.FileReadName];
        Assert.Equal(CapabilityEffectKind.Read, benignFileRead.Effect.Kind);
        Assert.True(benignFileRead.Effect.SupportsIdempotency);
        Assert.False(benignFileRead.Effect.WritesLocalData);
        Assert.False(benignFileRead.Effect.StartsProcesses);
        Assert.False(benignFileRead.Effect.ChangesSystemState);
    }

    [Fact]
    public void RetiredExternalCodingTools_HaveNoProductionDescriptorsKnownNamesOrGroups()
    {
        var retiredNames = new[]
        {
            AliCapabilityCatalog.CodingAgentExecuteName,
            AliCapabilityCatalog.CodingAgentStatusName
        };

        var result = AliProductionCapabilityCatalog.Build(retiredNames.Select(name => Function(name)));

        Assert.Empty(result.Registry.Descriptors);
        Assert.Equal(retiredNames, result.QuarantinedToolNames);
        Assert.All(retiredNames, name =>
        {
            Assert.True(AliProductionCapabilityCatalog.IsRetiredToolName(name));
            Assert.DoesNotContain(name, AliProductionCapabilityCatalog.KnownToolNames);
            Assert.False(AliProductionCapabilityCatalog.TryGetGroupId(name, out var groupId));
            Assert.Null(groupId);
            Assert.Throws<KeyNotFoundException>(() =>
                AliProductionCapabilityCatalog.GetSchemaFactoryId(name));
        });
    }

    [Fact]
    public void MsBuildWorkspaceBackedTools_FailClosedWithTheirFullPotentialEffects()
    {
        var byName = CreateDescriptorsByName();
        var readOrPreviewToolNames = new[]
        {
            AliCapabilityCatalog.RoslynAnalyzeProjectName,
            AliCapabilityCatalog.RoslynFindSymbolName,
            AliCapabilityCatalog.RoslynGetCompletionsName,
            AliCapabilityCatalog.RoslynInspectSolutionName,
            AliCapabilityCatalog.RoslynInspectDocumentName,
            AliCapabilityCatalog.RoslynInspectPositionName,
            AliCapabilityCatalog.RoslynFindReferencesName,
            AliCapabilityCatalog.RoslynPreviewRenameName,
            AliCapabilityCatalog.ArchitectureInspectName,
            AliCapabilityCatalog.ArchitectureCheckName,
            AliCapabilityCatalog.DotNetArchitectureReportName
        };

        foreach (var toolName in readOrPreviewToolNames)
        {
            var descriptor = byName[toolName];
            AssertPermissionGuardedProcess(descriptor);
            Assert.True(descriptor.Effect.ReadsLocalData, toolName);
            Assert.True(descriptor.Effect.WritesLocalData, toolName);
            Assert.True(descriptor.Effect.UsesNetwork, toolName);
            Assert.True(descriptor.Effect.ChangesSystemState, toolName);
        }

        foreach (var toolName in new[]
                 {
                     AliCapabilityCatalog.RoslynFormatProjectName,
                     AliCapabilityCatalog.RoslynApplyRenameName
                 })
        {
            var descriptor = byName[toolName];
            Assert.Equal(CapabilityEffectKind.SourceMutation, descriptor.Effect.Kind);
            Assert.Equal(CapabilityMutationBoundary.PermissionGuarded, descriptor.Effect.MutationBoundary);
            Assert.False(descriptor.Effect.SupportsIdempotency);
            Assert.True(descriptor.Effect.ReadsLocalData);
            Assert.True(descriptor.Effect.WritesLocalData);
            Assert.True(descriptor.Effect.UsesNetwork);
            Assert.True(descriptor.Effect.StartsProcesses);
            Assert.True(descriptor.Effect.ChangesSystemState);
            Assert.True(descriptor.Permission.RequiresApproval);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Effect.ReconcilerId));
        }
    }

    [Fact]
    public void LazyMemoryTools_FailClosedBeforeStartingTheirBackingServices()
    {
        var byName = CreateDescriptorsByName();

        foreach (var toolName in new[]
                 {
                     AliCapabilityCatalog.RecallUserMemoryName,
                     AliCapabilityCatalog.ListCurrentUserMemoriesName
                 })
        {
            var descriptor = byName[toolName];
            AssertPermissionGuardedProcess(descriptor);
            Assert.True(descriptor.Effect.ReadsLocalData);
            Assert.True(descriptor.Effect.WritesLocalData);
            Assert.True(descriptor.Effect.UsesNetwork);
            Assert.False(descriptor.Effect.ChangesSystemState);
        }

        var forget = byName[AliCapabilityCatalog.ForgetCurrentUserMemoryName];
        Assert.Equal(CapabilityEffectKind.Destructive, forget.Effect.Kind);
        Assert.Equal(CapabilityMutationBoundary.PermissionGuarded, forget.Effect.MutationBoundary);
        Assert.False(forget.Effect.SupportsIdempotency);
        Assert.True(forget.Effect.ReadsLocalData);
        Assert.True(forget.Effect.WritesLocalData);
        Assert.True(forget.Effect.UsesNetwork);
        Assert.True(forget.Effect.StartsProcesses);
        Assert.True(forget.Effect.ChangesSystemState);
        Assert.True(forget.Permission.RequiresApproval);
        Assert.False(string.IsNullOrWhiteSpace(forget.Effect.ReconcilerId));
    }

    [Fact]
    public void ObservationalChildProcessTools_FailClosedWithoutInventingTargetWrites()
    {
        var byName = CreateDescriptorsByName();
        var toolNames = new[]
        {
            AliCapabilityCatalog.VisualStudioInspectName,
            AliCapabilityCatalog.GnuNativeInspectName,
            AliCapabilityCatalog.ArduinoInspectName,
            AliCapabilityCatalog.ArduinoSearchLibrariesName,
            AliCapabilityCatalog.RaspberryPiProbeName,
            AliCapabilityCatalog.RaspberryPiInspectLibrariesName,
            AliCapabilityCatalog.RaspberryPiSearchPackagesName,
            AliCapabilityCatalog.ArchiveListName
        };

        foreach (var toolName in toolNames)
        {
            var descriptor = byName[toolName];
            AssertPermissionGuardedProcess(descriptor);
            Assert.False(descriptor.Effect.WritesLocalData, toolName);
            Assert.False(descriptor.Effect.ChangesSystemState, toolName);
        }

        foreach (var toolName in new[]
                 {
                     AliCapabilityCatalog.ArduinoSearchLibrariesName,
                     AliCapabilityCatalog.RaspberryPiProbeName,
                     AliCapabilityCatalog.RaspberryPiInspectLibrariesName,
                     AliCapabilityCatalog.RaspberryPiSearchPackagesName
                 })
        {
            Assert.True(byName[toolName].Effect.UsesNetwork, toolName);
        }

        foreach (var toolName in toolNames.Except(new[]
                 {
                     AliCapabilityCatalog.ArduinoSearchLibrariesName,
                     AliCapabilityCatalog.RaspberryPiProbeName,
                     AliCapabilityCatalog.RaspberryPiInspectLibrariesName,
                     AliCapabilityCatalog.RaspberryPiSearchPackagesName
                 }, StringComparer.Ordinal))
        {
            Assert.False(byName[toolName].Effect.UsesNetwork, toolName);
        }
    }

    [Fact]
    public void ProjectControlledInspection_FailsClosedWithConservativeFullEffects()
    {
        var byName = CreateDescriptorsByName();
        var toolNames = new[]
        {
            AliCapabilityCatalog.GitStatusName,
            AliCapabilityCatalog.GitDiffName,
            AliCapabilityCatalog.GitHistoryName,
            AliCapabilityCatalog.GitBlameName,
            AliCapabilityCatalog.DotNetDependencyInspectName
        };

        foreach (var toolName in toolNames)
        {
            var descriptor = byName[toolName];
            Assert.NotEqual(CapabilityMutationBoundary.None, descriptor.Effect.MutationBoundary);
            Assert.True(descriptor.Effect.ReadsLocalData, toolName);
            Assert.True(descriptor.Effect.WritesLocalData, toolName);
            Assert.True(descriptor.Effect.UsesNetwork, toolName);
            Assert.True(descriptor.Effect.StartsProcesses, toolName);
            Assert.True(descriptor.Effect.ChangesSystemState, toolName);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Effect.ReconcilerId));
        }

        Assert.True(byName[AliCapabilityCatalog.DotNetDependencyInspectName].Permission.RequiresApproval);
        foreach (var toolName in toolNames.Where(name =>
                     name != AliCapabilityCatalog.DotNetDependencyInspectName))
        {
            Assert.False(byName[toolName].Permission.RequiresApproval, toolName);
        }
    }

    [Fact]
    public void GenericSourceCapableFileMutators_AreNotMisclassifiedAsHarmlessLocalWrites()
    {
        var byName = CreateDescriptorsByName();
        var toolNames = new[]
        {
            AliCapabilityCatalog.FileWriteName,
            AliCapabilityCatalog.FileReplaceName,
            AliCapabilityCatalog.FileReplaceLinesName,
            AliCapabilityCatalog.FileMoveName,
            AliCapabilityCatalog.FileCopyName,
            AliCapabilityCatalog.FileCreateDirectoryName,
            AliCapabilityCatalog.ArchiveCreateName,
            AliCapabilityCatalog.ArchiveExtractName,
            AliCapabilityCatalog.RunAgentSkillScriptName
        };

        foreach (var toolName in toolNames)
        {
            var descriptor = byName[toolName];
            Assert.Equal(CapabilityEffectKind.SourceMutation, descriptor.Effect.Kind);
            Assert.Equal(CapabilityMutationBoundary.PermissionGuarded, descriptor.Effect.MutationBoundary);
            Assert.True(descriptor.Effect.WritesLocalData, toolName);
            Assert.False(descriptor.Effect.SupportsIdempotency, toolName);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Effect.ReconcilerId));
        }
    }

    [Fact]
    public void DescriptorApprovalMetadata_PreservesTrustedWorkstationPolicyAndDynamicMagenticChoice()
    {
        var byName = CreateDescriptorsByName();
        var expected = AliToolPermissionPolicy.ProtectedTools
            .Select(tool => tool.ToolName)
            .Where(toolName => toolName != AliCapabilityCatalog.RunMagenticOrchestrationName)
            .Where(AliProductionCapabilityCatalog.KnownToolNames.Contains)
            .ToHashSet(StringComparer.Ordinal);
        var actual = byName.Values
            .Where(descriptor => descriptor.Permission.RequiresApproval)
            .Select(descriptor => descriptor.ToolName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(expected.SetEquals(actual));
        Assert.False(byName[AliCapabilityCatalog.RunMagenticOrchestrationName].Permission.RequiresApproval);
    }

    [Fact]
    public void LockedDownApprovalPolicy_CoversEveryPrivateReaderExceptPrivateWorkMemory()
    {
        var byName = CreateDescriptorsByName();
        var expectedProtectedReaders = byName.Values
            .Where(descriptor => descriptor.Effect.ReadsLocalData)
            .Where(descriptor => !string.Equals(
                descriptor.GroupId,
                CapabilityGroupIds.WorkMemory,
                StringComparison.Ordinal))
            .Select(descriptor => descriptor.ToolName)
            .ToHashSet(StringComparer.Ordinal);
        var protectedNames = AliToolPermissionPolicy
            .ProtectedToolsFor(AgentPermissionProfile.LockedDown)
            .Select(definition => definition.ToolName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            expectedProtectedReaders.IsSubsetOf(protectedNames),
            $"Locked Down is missing private readers: {string.Join(", ", expectedProtectedReaders.Except(protectedNames).Order(StringComparer.Ordinal))}");
        Assert.All(
            byName.Values.Where(descriptor => string.Equals(
                descriptor.GroupId,
                CapabilityGroupIds.WorkMemory,
                StringComparison.Ordinal)),
            descriptor => Assert.False(AliAgentHarnessRunner.RequiresLockedDownPrivateReadApproval(
                descriptor,
                AgentPermissionProfile.LockedDown)));
    }

    [Fact]
    public void SkillWorkflowAndStopMetadata_ReflectsTheirActualLocalEffects()
    {
        var byName = CreateDescriptorsByName();

        foreach (var toolName in new[]
                 {
                     AliCapabilityCatalog.LoadAgentSkillName,
                     AliCapabilityCatalog.ReadAgentSkillResourceName,
                     AliCapabilityCatalog.ListRecoverableWorkflowsName,
                     AliCapabilityCatalog.ResumeWorkflowCheckpointName
                 })
        {
            Assert.True(byName[toolName].Effect.ReadsLocalData, toolName);
        }

        Assert.True(byName[AliCapabilityCatalog.DotNetStopProjectName].Effect.ChangesSystemState);
    }

    [Fact]
    public void PrivateFileAndWorkMemoryReaders_DeclareLocalDataReads()
    {
        var byName = CreateDescriptorsByName();
        var toolNames = new[]
        {
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
            AliCapabilityCatalog.WorkMemoryReplaceLinesName
        };

        foreach (var toolName in toolNames)
        {
            Assert.True(byName[toolName].Effect.ReadsLocalData, toolName);
        }
    }

    [Fact]
    public void ToolchainInspectionAndLocalAgentCalls_DeclareTheirDataBoundaries()
    {
        var byName = CreateDescriptorsByName();

        foreach (var toolName in new[]
                 {
                     AliCapabilityCatalog.CodingListCapabilitiesName,
                     AliCapabilityCatalog.VisualStudioInspectName,
                     AliCapabilityCatalog.GnuNativeInspectName,
                     AliCapabilityCatalog.ArduinoSearchLibrariesName,
                     AliCapabilityCatalog.ArduinoInstallCoreName,
                     AliCapabilityCatalog.ArduinoInstallLibraryName
                 })
        {
            Assert.True(byName[toolName].Effect.ReadsLocalData, toolName);
        }

        foreach (var toolName in new[]
                 {
                     AliCapabilityCatalog.ConsultSoftwareEngineerName,
                     AliCapabilityCatalog.ConsultResearcherName,
                     AliCapabilityCatalog.ConsultOfficeSpecialistName,
                     AliCapabilityCatalog.RunResearchArtifactWorkflowName,
                     AliCapabilityCatalog.RunProgrammingGroupChatName,
                     AliCapabilityCatalog.RunMagenticOrchestrationName,
                     AliCapabilityCatalog.ResumeWorkflowCheckpointName
                 })
        {
            Assert.True(byName[toolName].Effect.UsesNetwork, toolName);
        }

        Assert.True(byName[AliCapabilityCatalog.RunProgrammingGroupChatName].Effect.WritesLocalData);

        foreach (var toolName in new[]
                 {
                     AliCapabilityCatalog.ConsultSoftwareEngineerName,
                     AliCapabilityCatalog.ConsultResearcherName,
                     AliCapabilityCatalog.ConsultOfficeSpecialistName
                 })
        {
            Assert.False(byName[toolName].Effect.SupportsIdempotency, toolName);
        }

        var dependencyApply = byName[AliCapabilityCatalog.DotNetDependencyApplyName];
        Assert.Equal(CapabilityEffectKind.SourceMutation, dependencyApply.Effect.Kind);
        Assert.True(dependencyApply.Effect.ReadsLocalData);
        Assert.True(dependencyApply.Effect.WritesLocalData);
        Assert.False(dependencyApply.Effect.UsesNetwork);
        Assert.False(dependencyApply.Effect.StartsProcesses);
        Assert.False(dependencyApply.Effect.ChangesSystemState);
    }

    [Fact]
    public void EveryKnownImplementationPathThatLaunchesAChildProcess_DeclaresIt()
    {
        var byName = CreateDescriptorsByName();
        var toolNames = new[]
        {
            AliCapabilityCatalog.CodingFormatProjectName,
            AliCapabilityCatalog.DotNetCreateProjectName,
            AliCapabilityCatalog.ArduinoSearchLibrariesName,
            AliCapabilityCatalog.ArduinoInstallCoreName,
            AliCapabilityCatalog.ArduinoInstallLibraryName,
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
            AliCapabilityCatalog.RoslynAnalyzeProjectName,
            AliCapabilityCatalog.RoslynFormatProjectName,
            AliCapabilityCatalog.RoslynFindSymbolName,
            AliCapabilityCatalog.RoslynGetCompletionsName,
            AliCapabilityCatalog.RoslynInspectSolutionName,
            AliCapabilityCatalog.RoslynInspectDocumentName,
            AliCapabilityCatalog.RoslynInspectPositionName,
            AliCapabilityCatalog.RoslynFindReferencesName,
            AliCapabilityCatalog.RoslynPreviewRenameName,
            AliCapabilityCatalog.RoslynApplyRenameName,
            AliCapabilityCatalog.ArchitectureInspectName,
            AliCapabilityCatalog.ArchitectureCheckName,
            AliCapabilityCatalog.DotNetArchitectureReportName
        };

        foreach (var toolName in toolNames)
        {
            var descriptor = byName[toolName];
            Assert.True(descriptor.Effect.StartsProcesses, toolName);
            Assert.True(descriptor.Effect.IsMutation, toolName);
            Assert.NotEqual(CapabilityMutationBoundary.None, descriptor.Effect.MutationBoundary);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Effect.ReconcilerId));
        }
    }

    private static void AssertGroup(string groupId, params string[] expectedToolNames)
    {
        var actual = AliProductionCapabilityCatalog.KnownToolNames
            .Where(name => AliProductionCapabilityCatalog.TryGetGroupId(name, out var actualGroupId)
                && string.Equals(actualGroupId, groupId, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(expectedToolNames.ToHashSet(StringComparer.Ordinal).SetEquals(actual));
    }

    private static bool IsResolvedLanguageTargetTool(string toolName) =>
        toolName is AliCapabilityCatalog.CodingAnalyzeProjectName
            or AliCapabilityCatalog.CodingFormatProjectName
            or AliCapabilityCatalog.CodingBuildProjectName
            or AliCapabilityCatalog.CodingTestProjectName
            or AliCapabilityCatalog.CodingRunProjectName;

    private static IReadOnlyDictionary<string, CapabilityDescriptor> CreateDescriptorsByName() =>
        AliProductionCapabilityCatalog.CreateRegistry(
                AliCapabilityCatalog.Tools.Select(tool => Function(tool.Name)))
            .Descriptors
            .ToDictionary(descriptor => descriptor.ToolName, StringComparer.Ordinal);

    private static void AssertPermissionGuardedProcess(CapabilityDescriptor descriptor)
    {
        Assert.Equal(CapabilityEffectKind.ProcessControl, descriptor.Effect.Kind);
        Assert.Equal(CapabilityMutationBoundary.PermissionGuarded, descriptor.Effect.MutationBoundary);
        Assert.False(descriptor.Effect.SupportsIdempotency);
        Assert.True(descriptor.Effect.StartsProcesses);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Effect.ReconcilerId));
    }

    private static AIFunctionDeclaration Function(string name, string? description = null) =>
        AIFunctionFactory.Create(
            () => "ok",
            name,
            description ?? $"schema for {name}");
}
