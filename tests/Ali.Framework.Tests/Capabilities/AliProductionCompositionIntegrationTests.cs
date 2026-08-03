using System.Runtime.CompilerServices;
using Ali.Modules.AgentWorkMemory;
using Ali.Modules.Coding;
using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Identity;
using Ali.Modules.Internet;
using Ali.Modules.Mcp;
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
    [Fact]
    public async Task ActualProductionComposition_Builds114TaskToolsPlusTheRequiredProtocol_Offline()
    {
        using var fixture = new CompositionFixture();
        var runtime = new DevelopmentLocalModelRuntime();
        var client = new RejectingChatClient();
        var profile = AssistantProfile.Create("Ali capability integration");
        var mcpClients = new McpClientManager(fixture.Root);
        await using var codingModule = new AliCodingModule(fixture.FileAccess);
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
        var activeDeclarations = declarations
            .Where(declaration =>
                !AliProductionCapabilityCatalog.IsRetiredToolName(declaration.Name))
            .Append(OrchestrationProtocolCapability.CreateInvariantFunction())
            .ToArray();

        var production = AliProductionCapabilityCatalog.Build(activeDeclarations);

        Assert.Equal(114, declarations.Length);
        Assert.Equal(114, declarations.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(115, activeDeclarations.Length);
        Assert.Equal(115, production.Registry.Descriptors.Count);
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

        var inventory = CapabilityTerminalToolInventory.Create(activeDeclarations, production.Registry);
        Assert.Equal(115, inventory.FunctionDeclarationCount);
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
                reconcilerRevision: "ali-reconciler-v1:none",
                availableReconcilerIds: []));
        var planning = new CapabilityResolver().ResolvePlanning(
            production.Registry.Freeze(CapabilityAvailabilitySettings.CreateDefault()),
            stagedRuntime);

        Assert.True(planning.TryGetTool(AliCapabilityCatalog.FileReadName, out _));
        Assert.True(planning.TryGetTool(AliCapabilityCatalog.FileWriteName, out _));
        Assert.True(planning.TryGetTool(AliCapabilityCatalog.DotNetBuildName, out _));
        Assert.True(planning.TryGetTool(AliCapabilityCatalog.CodingAnalyzeProjectName, out _));
        Assert.True(planning.TryGetTool(AliCapabilityCatalog.CodingFormatProjectName, out _));
        Assert.True(planning.TryGetTool(AliCapabilityCatalog.CodingBuildProjectName, out _));
        Assert.True(planning.TryGetTool(AliCapabilityCatalog.CodingTestProjectName, out _));
        Assert.True(planning.TryGetTool(AliCapabilityCatalog.CodingRunProjectName, out _));
        Assert.Contains(
            "python-cpython",
            planning.EligibleProviderIdsByToolName[AliCapabilityCatalog.CodingBuildProjectName]);
        Assert.Contains(
            "java-temurin",
            planning.EligibleProviderIdsByToolName[AliCapabilityCatalog.CodingBuildProjectName]);
        Assert.DoesNotContain(planning.UnavailableDescriptors, item =>
            item.Reasons.Any(reason => reason.Code == CapabilityAvailabilityReasonCode.ReconcilerUnavailable));
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
        await using var codingModule = new AliCodingModule(fixture.FileAccess);
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
            new AliAgentWorkMemory(fixture.Root),
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
        Assert.Equal(114, settingsEnvelope.DeclaredTaskToolCount);
        Assert.Equal(1, settingsEnvelope.CallableProtocolToolCount);
        Assert.Equal(0, settingsEnvelope.UnavailableProtocolToolCount);
        Assert.Equal(0, externalMcpRow.DeclaredToolCount);
        Assert.DoesNotContain(
            resolvedToolNames,
            AliProductionCapabilityCatalog.IsRetiredToolName);
        Assert.DoesNotContain(
            capabilityRowReasons,
            reason => AliProductionCapabilityCatalog.IsRetiredToolName(reason.ToolName));
        var retiredNames = new[]
        {
            AliCapabilityCatalog.CodingAgentStatusName,
            AliCapabilityCatalog.CodingAgentExecuteName,
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

    private sealed class CompositionFixture : IDisposable
    {
        public CompositionFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "ProjectAli.ProductionCompositionTests",
                Guid.NewGuid().ToString("N"));
            var workspace = Directory.CreateDirectory(Path.Combine(Root, "Workspace")).FullName;
            Permissions = new AgentToolPermissionStore(Root);
            var store = new AliWorkstationFileStore(
                [new AliWorkstationFileMount("Workspace", workspace)],
                Path.Combine(Root, "RecoverableTrash"));
            FileAccess = new AliWorkstationFileAccess(
                store,
                new AgentFileActionAuditStore(Root, activeUsers: null),
                Permissions);
        }

        public string Root { get; }

        public AgentToolPermissionStore Permissions { get; }

        public AliWorkstationFileAccess FileAccess { get; }

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
