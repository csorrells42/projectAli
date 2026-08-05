using System.Runtime.CompilerServices;
using Ali.Modules.AgentWorkMemory;
using Ali.Modules.Coding;
using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Identity;
using Ali.Modules.Internet;
using Ali.Modules.Mcp;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.Permissions;
using Ali.Modules.Runtime;
using Ali.Modules.Storage;
using Ali.Modules.UserMemory;
using Ali.Modules.WorkstationFiles;
using Microsoft.Extensions.AI;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Ali.Framework.Tests.Capabilities;

public sealed class AliProductionCompositionIntegrationTests
{
    private static readonly IReadOnlySet<string> ExpectedDurableAdapterToolNames =
        new[]
        {
            AliCapabilityCatalog.FileWriteName,
            AliCapabilityCatalog.FileReplaceName,
            AliCapabilityCatalog.FileReplaceLinesName,
            AliCapabilityCatalog.FileDeleteName,
            AliCapabilityCatalog.FileMoveName,
            AliCapabilityCatalog.FileCopyName,
            AliCapabilityCatalog.FileCreateDirectoryName,
            AliCapabilityCatalog.WorkMemoryWriteName,
            AliCapabilityCatalog.WorkMemoryReplaceName,
            AliCapabilityCatalog.WorkMemoryReplaceLinesName,
            AliCapabilityCatalog.WorkMemoryDeleteName,
            AliCapabilityCatalog.RoslynInspectTargetName,
            AliCapabilityCatalog.RoslynListActionsName,
            AliCapabilityCatalog.RoslynPreviewActionName,
            AliCapabilityCatalog.RoslynVerifyChangesetName,
            AliCapabilityCatalog.RoslynApplyActionName,
            AliCapabilityCatalog.RoslynAnalyzeProjectName,
            AliCapabilityCatalog.RoslynFindSymbolName,
            AliCapabilityCatalog.RoslynGetCompletionsName,
            AliCapabilityCatalog.RoslynInspectSolutionName,
            AliCapabilityCatalog.RoslynInspectDocumentName,
            AliCapabilityCatalog.RoslynInspectPositionName,
            AliCapabilityCatalog.RoslynFindReferencesName,
            AliCapabilityCatalog.CodingAnalyzeProjectName,
            AliCapabilityCatalog.CodingBuildProjectName,
            AliCapabilityCatalog.CodingTestProjectName,
            AliCapabilityCatalog.CodingRunProjectName,
            AliCapabilityCatalog.DotNetBuildName,
            AliCapabilityCatalog.DotNetTestName,
            AliCapabilityCatalog.DotNetVerifyName,
            AliCapabilityCatalog.DotNetRunName,
            AliCapabilityCatalog.DotNetStopProjectName,
            AliCapabilityCatalog.DotNetDependencyInspectName,
            AliCapabilityCatalog.GitStatusName,
            AliCapabilityCatalog.GitDiffName,
            AliCapabilityCatalog.GitCreateBranchName,
            AliCapabilityCatalog.GitCommitName,
            AliCapabilityCatalog.GitPushName,
            AliCapabilityCatalog.ArchitectureInspectName,
            AliCapabilityCatalog.ArchitectureCheckName,
            AliCapabilityCatalog.DotNetQualityScanName,
            AliCapabilityCatalog.DotNetApplicationVerifyName,
            AliCapabilityCatalog.DotNetReleasePublishName,
            AliCapabilityCatalog.DotNetDeliveryVerifyName
        }.ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> ExpectedDurableEffectAdapterToolNames =
        new[]
        {
            AliCapabilityCatalog.MutateParticipantMemoryName,
            AliCapabilityCatalog.ConsentParticipantMemoryProposalName,
            AliCapabilityCatalog.ReconcileParticipantMemoryMutationName,
            AliCapabilityCatalog.CreateCalendarEventName
        }.ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> ExpectedRestoredOrdinaryToolNames =
        new[]
        {
            AliCapabilityCatalog.RecallUserMemoryName,
            AliCapabilityCatalog.ListCurrentUserMemoriesName,
            AliCapabilityCatalog.MutateParticipantMemoryName,
            AliCapabilityCatalog.ConsentParticipantMemoryProposalName,
            AliCapabilityCatalog.ReconcileParticipantMemoryMutationName,
            AliCapabilityCatalog.SearchCurrentWebName,
            AliCapabilityCatalog.ResearchWebName,
            AliCapabilityCatalog.SearchLocalLibraryName,
            AliCapabilityCatalog.CreateCalendarEventName
        }.ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> ExpectedDeliberatelyUnavailableToolNames =
        new[]
        {
            AliCapabilityCatalog.ArchiveCreateName,
            AliCapabilityCatalog.ArchiveExtractName,
            AliCapabilityCatalog.ArchiveListName,
            AliCapabilityCatalog.ArduinoCompileName,
            AliCapabilityCatalog.ArduinoCreateCompileName,
            AliCapabilityCatalog.ArduinoInspectName,
            AliCapabilityCatalog.ArduinoInstallCoreName,
            AliCapabilityCatalog.ArduinoInstallLibraryName,
            AliCapabilityCatalog.ArduinoOpenIdeName,
            AliCapabilityCatalog.ArduinoSearchLibrariesName,
            AliCapabilityCatalog.ArduinoUploadName,
            AliCapabilityCatalog.CodingFormatProjectName,
            AliCapabilityCatalog.DotNetArchitectureReportName,
            AliCapabilityCatalog.DotNetCreateProjectName,
            AliCapabilityCatalog.DotNetDependencyApplyName,
            AliCapabilityCatalog.DotNetDebugAttachName,
            AliCapabilityCatalog.DotNetDebugControlName,
            AliCapabilityCatalog.DotNetDebugEvaluateName,
            AliCapabilityCatalog.DotNetDebugLaunchName,
            AliCapabilityCatalog.DotNetDebugBreakpointsName,
            AliCapabilityCatalog.DotNetDebugStopName,
            AliCapabilityCatalog.DotNetPerformanceMeasureName,
            AliCapabilityCatalog.DotNetPerformanceTraceName,
            AliCapabilityCatalog.GitBlameName,
            AliCapabilityCatalog.GitHistoryName,
            AliCapabilityCatalog.SetAgentModeName,
            AliCapabilityCatalog.GnuNativeExecuteName,
            AliCapabilityCatalog.GnuNativeInspectName,
            AliCapabilityCatalog.RaspberryPiDeployName,
            AliCapabilityCatalog.RaspberryPiInspectLibrariesName,
            AliCapabilityCatalog.RaspberryPiProbeName,
            AliCapabilityCatalog.RaspberryPiSearchPackagesName,
            AliCapabilityCatalog.RoslynFormatProjectName,
            AliCapabilityCatalog.RunAgentSkillScriptName,
            AliCapabilityCatalog.VisualStudioBuildName,
            AliCapabilityCatalog.VisualStudioInspectName,
            AliCapabilityCatalog.VisualStudioOpenName
        }.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public async Task ActualProductionComposition_Builds119TaskToolsPlusTheRequiredProtocol_Offline()
    {
        using var fixture = new CompositionFixture();
        var runtime = new DevelopmentLocalModelRuntime();
        var client = new RejectingChatClient();
        var profile = AssistantProfile.Create("Ali capability integration");
        var mcpClients = new McpClientManager(fixture.Root);
        await using var codingModule = new AliCodingModule(
            fixture.FileAccess,
            durableOrchestrationRoot: fixture.DurableRoot,
            assistantProfileBinding: fixture.ProfileBinding);
        Assert.NotEmpty(codingModule.RoslynProviderCatalog.CodeFixProviders);
        Assert.NotEmpty(codingModule.RoslynProviderCatalog.RefactoringProviders);
        var source = new EmptySourceRetriever();
        var webResearch = new McpWebResearchClient(() => new WebSourceBackendSettings());
        var catalog = new AliToolCatalog(
            source,
            source,
            webResearch,
            new FileMemoryStore(fixture.Root),
            new FileReminderStore(fixture.Root),
            profile,
            mcpClients,
            fixture.Permissions,
            fixture.FileAccess,
            codingModule,
            () => null,
            orchestrationSettings: () => new AgentOrchestrationSettings());
        var frameworkTools = AliFrameworkCapabilityProbe.Capture(fixture.FileAccess, () => null);
        var declarations = catalog.Tools
            .Concat(frameworkTools)
            .OfType<AIFunctionDeclaration>()
            .ToArray();
        var unsupportedSchemas = declarations
            .Select(declaration =>
            {
                var accepted = CapabilityJsonSchemaValidator.TryValidateToolArgumentsSchema(
                    declaration.JsonSchema,
                    out var reason);
                return (declaration.Name, Accepted: accepted, Reason: reason);
            })
            .Where(result => !result.Accepted)
            .ToArray();
        var activeDeclarations = declarations
            .Where(declaration =>
                !AliProductionCapabilityCatalog.IsRetiredToolName(declaration.Name))
            .Append(OrchestrationProtocolCapability.CreateInvariantFunction())
            .ToArray();

        var production = AliProductionCapabilityCatalog.Build(activeDeclarations);

        Assert.Equal(119, declarations.Length);
        Assert.True(
            unsupportedSchemas.Length == 0,
            string.Join(
                Environment.NewLine,
                unsupportedSchemas.Select(result => $"{result.Name}: {result.Reason}")));
        Assert.Equal(119, declarations.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(120, activeDeclarations.Length);
        Assert.Equal(120, production.Registry.Descriptors.Count);
        Assert.True(AliProductionCapabilityCatalog.KnownToolNames.SetEquals(
            production.Registry.Descriptors
                .Where(descriptor => descriptor.Tier == CapabilityTier.Task)
                .Select(descriptor => descriptor.ToolName)));
        Assert.True(AliProductionToolOutcomeRegistry.ContractedToolNames.SetEquals(
            production.Registry.Descriptors
                .Where(descriptor => descriptor.Tier == CapabilityTier.Task)
                .Select(descriptor => descriptor.ToolName)));
        Assert.All(
            production.Registry.Descriptors.Where(descriptor => descriptor.Tier == CapabilityTier.Task),
            descriptor => Assert.Equal(
                $"ali-outcome.{descriptor.ToolName}.v1",
                descriptor.SemanticMetadata["outcome-contract"]));
        var protocol = Assert.Single(
            production.Registry.Descriptors,
            descriptor => descriptor.Tier == CapabilityTier.Protocol);
        Assert.Equal(OrchestrationProtocolCapability.ToolName, protocol.ToolName);
        Assert.DoesNotContain(
            production.Registry.Descriptors,
            descriptor => AliProductionCapabilityCatalog.IsRetiredToolName(descriptor.ToolName));
        Assert.Empty(production.QuarantinedToolNames);
        Assert.Equal(0, client.CallCount);

        var effectAdapters = fixture.FileAccess.ExecutionEffectAdapters
            .Concat(fixture.WorkMemory.ExecutionEffectAdapters)
            .Concat(codingModule.ExecutionEffectAdapters)
            .ToArray();
        Assert.Equal(44, effectAdapters.Length);
        Assert.Equal(
            44,
            effectAdapters
                .Select(adapter =>
                    (adapter.ToolName, adapter.CapabilityId, adapter.ReconcilerId))
                .Distinct()
                .Count());
        Assert.True(ExpectedDurableAdapterToolNames.SetEquals(
            effectAdapters.Select(adapter => adapter.ToolName)));

        var effectRegistry = new AliExecutionEffectAdapterRegistry(effectAdapters);
        var durableEffectRegistry = AliProductionDurableEffectAdapters.Create();
        var requiredDurableDescriptors = production.Registry.Descriptors
            .Where(descriptor => descriptor.Tier == CapabilityTier.Task)
            .Where(AliDurablePlanningTurn.RequiresDurableEffectAdapter)
            .ToArray();
        var adapterBackedDescriptors = requiredDurableDescriptors
            .Where(descriptor => effectRegistry.TryResolve(descriptor, out _))
            .ToArray();
        var durableEffectBackedDescriptors = requiredDurableDescriptors
            .Where(descriptor => durableEffectRegistry.TryGet(descriptor.ToolName, out _))
            .ToArray();
        var deliberatelyUnavailableDescriptors = requiredDurableDescriptors
            .Where(descriptor => !effectRegistry.TryResolve(descriptor, out _)
                && !durableEffectRegistry.TryGet(descriptor.ToolName, out _))
            .ToArray();
        Assert.Equal(85, requiredDurableDescriptors.Length);
        Assert.Equal(44, adapterBackedDescriptors.Length);
        Assert.Equal(4, durableEffectBackedDescriptors.Length);
        Assert.Equal(37, deliberatelyUnavailableDescriptors.Length);
        Assert.True(ExpectedDurableAdapterToolNames.SetEquals(
            adapterBackedDescriptors.Select(descriptor => descriptor.ToolName)));
        Assert.True(ExpectedDurableEffectAdapterToolNames.SetEquals(
            durableEffectBackedDescriptors.Select(descriptor => descriptor.ToolName)));
        Assert.True(ExpectedDeliberatelyUnavailableToolNames.SetEquals(
            deliberatelyUnavailableDescriptors.Select(descriptor => descriptor.ToolName)));

        var targetStateRegistry = AliProductionTargetStateAdapters.Create(
            fixture.FileAccess,
            fixture.FileAccess.TargetStateAdapters
                .Concat(fixture.WorkMemory.TargetStateAdapters)
                .Concat(codingModule.TargetStateAdapters));
        foreach (var toolName in ExpectedDurableAdapterToolNames)
        {
            var prepared = targetStateRegistry.Prepare(
                toolName,
                System.Text.Json.JsonSerializer.SerializeToElement(new { }));
            Assert.NotNull(prepared.Adapter);
        }

        var inventory = CapabilityTerminalToolInventory.Create(activeDeclarations, production.Registry);
        Assert.Equal(120, inventory.FunctionDeclarationCount);
        Assert.Empty(inventory.Issues);
        var stagedRuntime = CapabilityRuntimeAvailabilityFactory.Create(
            inventory,
            new CapabilityRuntimeStateSnapshot(
                "selection-required",
                "ali-core-provider-v1",
                AliProductionCapabilityCatalog.RegisteredProviderIds,
                targetResolution: null,
                "ali-tool-permission-v1:TrustedWorkstation",
                ["ali-tool-permission-v1"],
                "mcp-staged-none",
                readyIncomingMcpToolNames: [],
                enabledOutgoingMcpToolNames: [],
                reconcilerRevision: "ali-reconciler-v1:production-composition-test",
                availableReconcilerIds: effectAdapters
                    .Select(adapter => adapter.ReconcilerId)
                    .Concat(durableEffectRegistry.Reconcilers.Select(reconciler => reconciler.ReconcilerId)),
                enforceReconcilerAvailability: true));
        var planning = new CapabilityResolver().ResolvePlanning(
            production.Registry.Freeze(CapabilityAvailabilitySettings.CreateDefault()),
            stagedRuntime);

        Assert.True(planning.TryGetTool(AliCapabilityCatalog.FileReadName, out _));
        Assert.True(planning.TryGetTool(AliCapabilityCatalog.FileWriteName, out _));
        foreach (var toolName in new[]
                 {
                     AliCapabilityCatalog.CodingAnalyzeProjectName,
                     AliCapabilityCatalog.CodingBuildProjectName,
                     AliCapabilityCatalog.CodingTestProjectName,
                     AliCapabilityCatalog.CodingRunProjectName,
                     AliCapabilityCatalog.DotNetBuildName,
                     AliCapabilityCatalog.DotNetTestName,
                     AliCapabilityCatalog.DotNetVerifyName,
                     AliCapabilityCatalog.DotNetRunName,
                     AliCapabilityCatalog.DotNetStopProjectName,
                     AliCapabilityCatalog.DotNetDependencyInspectName
                 })
        {
            Assert.True(planning.TryGetTool(toolName, out _), toolName);
        }
        Assert.False(planning.TryGetTool(AliCapabilityCatalog.CodingFormatProjectName, out _));
        Assert.Contains(
            planning.UnavailableDescriptors,
            item => item.Descriptor.ToolName == AliCapabilityCatalog.CodingFormatProjectName
                && item.Reasons.Any(reason =>
                    reason.Code == CapabilityAvailabilityReasonCode.ReconcilerUnavailable));
        Assert.Contains(
            "python-cpython",
            planning.EligibleProviderIdsByToolName[AliCapabilityCatalog.CodingBuildProjectName]);
        Assert.Contains(
            "java-temurin",
            planning.EligibleProviderIdsByToolName[AliCapabilityCatalog.CodingBuildProjectName]);
        Assert.Contains(planning.UnavailableDescriptors, item =>
            item.Descriptor.ToolName == AliCapabilityCatalog.CodingFormatProjectName
            && item.Reasons.Any(reason => reason.Code == CapabilityAvailabilityReasonCode.ReconcilerUnavailable));
        AssertPlanningReady(planning, ExpectedRestoredOrdinaryToolNames);
    }

    [Fact]
    public async Task PublicComposition_InitializesCanonicalBoundaryWithoutLegacyWorkflowTools()
    {
        using var fixture = new CompositionFixture();
        var checkpointPath = Path.Combine(fixture.Root, "WorkflowCheckpoints");
        Directory.CreateDirectory(checkpointPath);
        var corruptKey = new byte[] { 0x41, 0x6c, 0x69, 0x2d, 0x62, 0x61, 0x64 };
        File.WriteAllBytes(
            Path.Combine(checkpointPath, AliWorkflowCheckpointOwnership.KeyFileName),
            corruptKey);
        var runtime = new DevelopmentLocalModelRuntime();
        var client = new RejectingChatClient();
        var profile = AssistantProfile.Create("Ali legacy workflow isolation integration");
        var source = new EmptySourceRetriever();
        var webResearch = new McpWebResearchClient(() => new WebSourceBackendSettings());
        await using var codingModule = new AliCodingModule(
            fixture.FileAccess,
            durableOrchestrationRoot: fixture.DurableRoot,
            assistantProfileBinding: fixture.ProfileBinding);
        using var coordinator = new AliToolCoordinator(
            runtime,
            client,
            source,
            source,
            webResearch,
            new FileMemoryStore(fixture.Root),
            new FileReminderStore(fixture.Root),
            profile,
            new McpClientManager(fixture.Root),
            fixture.Permissions,
            fixture.FileAccess,
            fixture.WorkMemory,
            capabilitySettingsDataRoot: fixture.Root,
            codingModule: codingModule,
            userMemories: null,
            activeUsers: null,
            memorySettings: null,
            workflowCheckpointPath: checkpointPath,
            orchestrationSettings: () => new AgentOrchestrationSettings(),
            semanticToolCatalog: null);

        var settingsOwner = Assert.IsType<CapabilitySettingsSnapshotOwner>(
            coordinator.CapabilitySettings);
        var resolution = settingsOwner.CapturePlanning().Resolution;
        var settingsEnvelope = settingsOwner.CaptureSettings();
        var resolvedToolNames = resolution.EffectiveDescriptors
            .Select(descriptor => descriptor.ToolName)
            .Concat(resolution.UnavailableDescriptors.Select(item => item.Descriptor.ToolName))
            .ToHashSet(StringComparer.Ordinal);
        var capabilityRowReasons = settingsEnvelope.Rows
            .SelectMany(row => row.Reasons)
            .ToArray();
        var externalMcpRow = Assert.Single(
            settingsEnvelope.Rows,
            row => row.GroupId == CapabilityGroupIds.ExternalMcp);
        Assert.Equal(119, settingsEnvelope.DeclaredTaskToolCount);
        Assert.Equal(1, settingsEnvelope.CallableProtocolToolCount);
        Assert.Equal(0, settingsEnvelope.UnavailableProtocolToolCount);
        Assert.Equal(0, externalMcpRow.DeclaredToolCount);
        var allTaskDescriptors = resolution.EffectiveDescriptors
            .Concat(resolution.UnavailableDescriptors.Select(item => item.Descriptor))
            .Where(descriptor => descriptor.Tier == CapabilityTier.Task)
            .ToArray();
        var requiredDurableDescriptors = allTaskDescriptors
            .Where(descriptor => descriptor.Effect.RequiresDurableEffectAdapter)
            .ToArray();
        var deliberatelyUnavailableToolNames = requiredDurableDescriptors
            .Select(descriptor => descriptor.ToolName)
            .Where(toolName => !ExpectedDurableAdapterToolNames.Contains(toolName)
                && !ExpectedDurableEffectAdapterToolNames.Contains(toolName))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(85, requiredDurableDescriptors.Length);
        Assert.Equal(37, deliberatelyUnavailableToolNames.Count);
        Assert.True(ExpectedDeliberatelyUnavailableToolNames.SetEquals(
            deliberatelyUnavailableToolNames));
        foreach (var toolName in deliberatelyUnavailableToolNames)
        {
            var unavailable = Assert.Single(
                resolution.UnavailableDescriptors,
                item => item.Descriptor.ToolName == toolName);
            Assert.Contains(
                unavailable.Reasons,
                reason => reason.Code == CapabilityAvailabilityReasonCode.ReconcilerUnavailable);
            Assert.Contains(
                capabilityRowReasons,
                reason => reason.ToolName == toolName
                    && reason.Code == CapabilityAvailabilityReasonCode.ReconcilerUnavailable);
        }
        foreach (var toolName in ExpectedDurableAdapterToolNames)
        {
            var unavailable = resolution.UnavailableDescriptors.SingleOrDefault(
                item => item.Descriptor.ToolName == toolName);
            if (unavailable is not null)
            {
                Assert.DoesNotContain(
                    unavailable.Reasons,
                    reason => reason.Code == CapabilityAvailabilityReasonCode.ReconcilerUnavailable);
            }
        }
        AssertPlanningReady(resolution, ExpectedRestoredOrdinaryToolNames);
        Assert.DoesNotContain(
            resolvedToolNames,
            AliProductionCapabilityCatalog.IsRetiredToolName);
        Assert.DoesNotContain(
            capabilityRowReasons,
            reason => AliProductionCapabilityCatalog.IsRetiredToolName(reason.ToolName));
        var retiredNames = new[]
        {
            AliCapabilityCatalog.ConsultSoftwareEngineerName,
            AliCapabilityCatalog.ConsultResearcherName,
            AliCapabilityCatalog.ConsultOfficeSpecialistName,
            AliCapabilityCatalog.RunResearchArtifactWorkflowName,
            AliCapabilityCatalog.RunProgrammingGroupChatName,
            AliCapabilityCatalog.RunMagenticOrchestrationName,
            AliCapabilityCatalog.ListRecoverableWorkflowsName,
            AliCapabilityCatalog.ResumeWorkflowCheckpointName
        };
        foreach (var toolName in retiredNames)
        {
            Assert.False(resolution.TryGetTool(toolName, out _));
            Assert.DoesNotContain(
                resolution.UnavailableDescriptors,
                item => item.Descriptor.ToolName == toolName);
        }

        Assert.True(resolution.TryGetTool(AliCapabilityCatalog.GetCurrentLocalTimeName, out _));
        Assert.Equal(corruptKey, File.ReadAllBytes(
            Path.Combine(checkpointPath, AliWorkflowCheckpointOwnership.KeyFileName)));
        Assert.Equal(0, client.CallCount);
    }

    private static void AssertPlanningReady(
        CapabilityResolutionSnapshot resolution,
        IEnumerable<string> expectedToolNames)
    {
        foreach (var toolName in expectedToolNames.Order(StringComparer.Ordinal))
        {
            var unavailable = resolution.UnavailableDescriptors.SingleOrDefault(
                item => string.Equals(
                    item.Descriptor.ToolName,
                    toolName,
                    StringComparison.Ordinal));
            var reason = unavailable is null
                ? $"Tool '{toolName}' was absent from both effective and unavailable production capability partitions."
                : string.Join(
                    "; ",
                    unavailable.Reasons.Select(item => $"{item.Code}: {item.Message}"));
            Assert.True(resolution.TryGetTool(toolName, out _), reason);
        }
    }

    private sealed class CompositionFixture : IDisposable
    {
        public CompositionFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "ProjectAli.ProductionCompositionTests",
                Guid.NewGuid().ToString("N"));
            DurableRoot = Path.Combine(Root, "OrchestrationV2");
            ProfileBinding = "ali-production-composition-test";
            var workspace = Directory.CreateDirectory(Path.Combine(Root, "Workspace")).FullName;
            Permissions = new AgentToolPermissionStore(Root);
            var store = new AliWorkstationFileStore(
                [new AliWorkstationFileMount("Workspace", workspace)],
                Path.Combine(Root, "RecoverableTrash"));
            FileAccess = new AliWorkstationFileAccess(
                store,
                new AgentFileActionAuditStore(Root, activeUsers: null),
                Permissions,
                DurableRoot,
                ProfileBinding);
            WorkMemory = new AliAgentWorkMemory(
                Root,
                DurableRoot,
                ProfileBinding);
        }

        public string Root { get; }

        public string DurableRoot { get; }

        public string ProfileBinding { get; }

        public AgentToolPermissionStore Permissions { get; }

        public AliWorkstationFileAccess FileAccess { get; }

        public AliAgentWorkMemory WorkMemory { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class EmptySourceRetriever : ISourceRetriever
    {
        public Task<SourceRetrievalResult> RetrieveAsync(
            string userText,
            CancellationToken cancellationToken) =>
            Task.FromResult(SourceRetrievalResult.Empty);
    }

    private sealed class RejectingChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromException<ChatResponse>(
                new InvalidOperationException("Production composition must not call a model."));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
