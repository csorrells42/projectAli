using System.Runtime.CompilerServices;
using Ali.Modules.AgentWorkMemory;
using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.Capabilities;

public sealed class AliFrameworkCapabilityProbeTests
{
    private static readonly string[] ExpectedFrameworkToolNames =
    [
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
        AliCapabilityCatalog.SetAgentModeName,
        AliCapabilityCatalog.LoadAgentSkillName,
        AliCapabilityCatalog.ReadAgentSkillResourceName,
        AliCapabilityCatalog.RunAgentSkillScriptName
    ];

    [Fact]
    public void Capture_UsesRealFrameworkProviders_AndReturnsTheExactSupportedToolSet()
    {
        using var fixture = new ProviderFixture();

        var captured = AliFrameworkCapabilityProbe.Capture(
            fixture.FileAccess,
            () => null);

        Assert.Equal(19, captured.Count);
        Assert.Equal(
            ExpectedFrameworkToolNames.Order(StringComparer.Ordinal),
            captured.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.Equal(19, captured.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(captured, declaration =>
        {
            Assert.False(string.IsNullOrWhiteSpace(declaration.Description));
            Assert.NotEqual(System.Text.Json.JsonValueKind.Undefined, declaration.JsonSchema.ValueKind);
        });
    }

    [Fact]
    public async Task TerminalProvider_RunsAfterAllRealFrameworkProviders_WithoutAModelCall()
    {
        using var fixture = new ProviderFixture();
        using var scope = fixture.WorkMemory.EnterScope("terminal-provider-order", activeUser: null);
        var declarations = AliFrameworkCapabilityProbe.Capture(
            fixture.FileAccess,
            () => null);
        var registry = AliProductionCapabilityCatalog.CreateRegistry(declarations);
        var state = CreateRuntimeState(registry);
        var inventory = CapabilityTerminalToolInventory.Create(declarations, registry);
        var owner = new CapabilitySettingsSnapshotOwner(
            registry,
            new CapabilityResolver(),
            CapabilityRuntimeAvailabilityFactory.Create(inventory, state),
            new MemoryPersistence());
        var terminal = new TerminalCapabilityEnforcementProvider(owner, () => state);
        var client = new CapturingChatClient();
        var agent = CreateRealProviderAgent(client, fixture, terminal);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        _ = await agent.RunAsync(
            "Return without calling a tool.",
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, client.CallCount);
        Assert.Equal(
            ExpectedFrameworkToolNames.Order(StringComparer.Ordinal),
            client.CapturedTools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.All(client.CapturedTools, tool =>
            Assert.NotNull(tool.GetService<CapabilityInvocationLeaseAIFunction>()));
        var planning = owner.CapturePlanning();
        Assert.Equal(19, planning.Runtime.RegisteredToolsByName.Count);
        Assert.Empty(planning.Resolution.QuarantinedCapabilities);
    }

    private static AIAgent CreateRealProviderAgent(
        IChatClient client,
        ProviderFixture fixture,
        TerminalCapabilityEnforcementProvider terminal) =>
        client.AsHarnessAgent(new HarnessAgentOptions
        {
            MaximumIterationsPerRequest = 1,
            DisableWebSearch = true,
            DisableFileMemory = false,
            DisableAgentSkillsProvider = false,
            AgentSkillsSource = new AgentFileSkillsSource(Path.Combine(AppContext.BaseDirectory, "skills")),
            DisableOpenTelemetry = true,
            DisableTodoProvider = true,
            FileMemoryStore = fixture.WorkMemory.Store,
            FileAccessStore = fixture.FileAccess.Store,
            FileAccessProviderOptions = new FileAccessProviderOptions
            {
                Instructions = fixture.FileAccess.Instructions,
                DisableWriteTools = false,
                DisableReadOnlyToolApproval = false,
                DisableWriteToolApproval = false
            },
            ToolApprovalAgentOptions = new ToolApprovalAgentOptions
            {
                AutoApprovalRules = [fixture.FileAccess.ShouldAutoApproveAsync]
            },
            AIContextProviders = [terminal],
            ChatOptions = new ChatOptions
            {
                Instructions = "Verify terminal capability enforcement provider ordering.",
                Tools = [],
                ToolMode = ChatToolMode.Auto,
                AllowMultipleToolCalls = false
            }
        });

    private static CapabilityRuntimeStateSnapshot CreateRuntimeState(
        CanonicalCapabilityRegistry registry) =>
        new(
            "test-user",
            "ali-core-provider-test",
            [AliProductionCapabilityCatalog.ProviderId],
            targetResolution: null,
            "ali-tool-permission-test",
            registry.Descriptors
                .Select(descriptor => descriptor.Permission.PolicyId)
                .Distinct(StringComparer.Ordinal),
            "mcp-test",
            readyIncomingMcpToolNames: [],
            enabledOutgoingMcpToolNames: registry.Descriptors
                .Where(descriptor => descriptor.McpExposure.Exposed)
                .Select(descriptor => descriptor.ToolName),
            "reconcilers-test",
            registry.Descriptors
                .Where(descriptor => descriptor.Effect.ReconcilerId is not null)
                .Select(descriptor => descriptor.Effect.ReconcilerId!)
                .Distinct(StringComparer.Ordinal));

    private sealed class CapturingChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<AIFunction> CapturedTools { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Capture(options);
            return Task.FromResult(StopResponse());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Capture(options);
            foreach (var update in StopResponse().ToChatResponseUpdates())
            {
                yield return update;
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private void Capture(ChatOptions? options)
        {
            CallCount++;
            CapturedTools = (options?.Tools ?? []).OfType<AIFunction>().ToArray();
        }

        private static ChatResponse StopResponse() =>
            new(new ChatMessage(ChatRole.Assistant, "Provider ordering captured."))
            {
                FinishReason = ChatFinishReason.Stop
            };
    }

    private sealed class MemoryPersistence : ICapabilityAvailabilitySettingsPersistence
    {
        private CapabilityAvailabilitySettings _settings = CapabilityAvailabilitySettings.CreateDefault();

        public CapabilityAvailabilityLoadResult Load() =>
            CapabilityAvailabilityLoadResult.Loaded(_settings);

        public CapabilityAvailabilitySaveResult Save(
            string expectedRevision,
            CapabilityAvailabilitySettings settings)
        {
            if (!string.Equals(expectedRevision, _settings.Revision, StringComparison.Ordinal))
            {
                return CapabilityAvailabilitySaveResult.Conflict(_settings);
            }

            _settings = new CapabilityAvailabilitySettings(settings.GroupSelections);
            return CapabilityAvailabilitySaveResult.Saved(_settings);
        }
    }

    private sealed class ProviderFixture : IDisposable
    {
        private readonly string _root;

        public ProviderFixture()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "ProjectAli.FrameworkCapabilityProbeTests",
                Guid.NewGuid().ToString("N"));
            var workspace = Directory.CreateDirectory(Path.Combine(_root, "Workspace")).FullName;
            var permissions = new AgentToolPermissionStore(_root);
            var store = new AliWorkstationFileStore(
                [new AliWorkstationFileMount("Workspace", workspace)],
                Path.Combine(_root, "RecoverableTrash"));
            FileAccess = new AliWorkstationFileAccess(
                store,
                new AgentFileActionAuditStore(_root, activeUsers: null),
                permissions);
            WorkMemory = new AliAgentWorkMemory(_root);
        }

        public AliWorkstationFileAccess FileAccess { get; }

        public AliAgentWorkMemory WorkMemory { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
