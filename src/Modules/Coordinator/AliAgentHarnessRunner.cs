#pragma warning disable MAAI001 // Agent Framework file-access provider is intentionally enabled by Ali's workstation-file module.

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.AgentWorkMemory;
using Ali.Modules.Capabilities;
using Ali.Modules.Evidence;
using Ali.Modules.WorkstationFiles;
using Ali.Modules.Identity;
using Ali.Modules.Mcp;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Completion;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Observation;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.Permissions;
using Ali.Modules.Runtime;
using Ali.Modules.Runtime.Models;
using Ali.Modules.UserMemory;
using Ali.Modules.ToolDiscovery;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;
using RuntimeChatMessage = Ali.Modules.Runtime.ChatMessage;
using RuntimeChatRole = Ali.Modules.Runtime.ChatRole;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Owns Agent Framework sessions, iterative execution, and framework approval responses.
/// Conversation orchestration and Ali's capability implementations remain outside this class.
/// </summary>
internal sealed class AliAgentHarnessRunner : IDisposable
{
    // Substantial jobs may legitimately require hundreds of distinct atomic steps.
    // Exact repeated tool/argument plans are stopped by the connector; this high
    // ceiling remains only as a final finite-run safety boundary.
    internal const int MaximumToolIterations = int.MaxValue;
    private readonly IReadOnlyList<AITool> _baseTools;
    private readonly AIFunction _protocolTool;
    private readonly IChatClient _modelClient;
    private readonly AliPlanningStateCoordinator _planningStateCoordinator;
    private readonly ILocalModelRuntime _runtime;
    private readonly AssistantProfile _assistantProfile;
    private readonly Func<AgentOrchestrationSettings> _orchestrationSettings;
    private readonly McpClientManager _mcpClients;
    private readonly AgentToolPermissionStore _toolPermissions;
    private readonly AliWorkstationFileAccess _fileAccess;
    private readonly AliAgentWorkMemory _workMemory;
    private readonly IActiveUserSession? _activeUsers;
    private readonly Func<CoordinatorTurnContext?> _turnAccessor;
    private readonly ISemanticToolCatalog _semanticToolCatalog;
    private readonly IShadowToolObserver? _shadowObserver;
    private readonly AliToolPermissionPolicy _capabilityPermissionPolicy;
    private readonly string? _capabilitySettingsDataRoot;
    private readonly CanonicalCapabilityRegistry? _baseCapabilityRegistry;
    private readonly IReadOnlyList<AITool> _frameworkCapabilityTools;
    private readonly CapabilitySettingsSnapshotOwner? _capabilitySettings;
    private readonly TerminalCapabilityEnforcementProvider? _capabilityEnforcementProvider;
    private readonly AliFrameworkToolOutcomeSidecar _toolOutcomes;
    private readonly AliProductionToolOutcomeRegistry _toolOutcomeRegistry;
    private readonly AliExecutionEffectAdapterRegistry _executionAdapters;
    private readonly IReadOnlyList<string> _executionReconcilerIds;
    private readonly string _executionReconcilerRevision;
    private readonly ConcurrentDictionary<string, PendingApproval> _pendingApprovals = new(StringComparer.Ordinal);
    private readonly object _lifetimeSync = new();
    private AgentToolPermissionSnapshot? _projectionPermissionSnapshot;
    private int _activeRuns;
    private bool _planningStateCoordinatorDisposed;
    private int _disposed;

    public AliAgentHarnessRunner(
        IChatClient chatClient,
        ILocalModelRuntime runtime,
        AssistantProfile assistantProfile,
        AliToolCatalog catalog,
        McpClientManager mcpClients,
        AgentToolPermissionStore toolPermissions,
        AliWorkstationFileAccess fileAccess,
        AliAgentWorkMemory workMemory,
        IActiveUserSession? activeUsers,
        Func<CoordinatorTurnContext?> turnAccessor,
        string checkpointPath,
        Func<AgentOrchestrationSettings> orchestrationSettings,
        ISemanticToolCatalog? semanticToolCatalog = null,
        IShadowToolObserver? shadowObserver = null,
        string? capabilitySettingsDataRoot = null,
        ITurnPublicationReconciler? publicationReconciler = null,
        EffectNormalizationRegistry? effectNormalizations = null,
        TargetStateRegistry? targetStates = null,
        AliExecutionEffectAdapterRegistry? executionAdapters = null,
        AliFrameworkToolOutcomeSidecar? toolOutcomes = null)
    {
        _modelClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _runtime = runtime;
        _assistantProfile = assistantProfile.Normalize();
        _semanticToolCatalog = semanticToolCatalog ?? new RegistryOnlySemanticToolCatalog();
        _toolOutcomes = toolOutcomes ?? new AliFrameworkToolOutcomeSidecar();
        _toolOutcomeRegistry = new AliProductionToolOutcomeRegistry(_toolOutcomes);
        _executionAdapters = executionAdapters ?? AliExecutionEffectAdapterRegistry.Empty;
        _executionReconcilerIds = _executionAdapters.Reconcilers
            .Select(reconciler => reconciler.ReconcilerId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        _executionReconcilerRevision = ReconcilerRevision(_executionReconcilerIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointPath);
        _planningStateCoordinator = new AliPlanningStateCoordinator(
            Path.Combine(Path.GetFullPath(checkpointPath), "OrchestrationV2"),
            _assistantProfile.AssistantName,
            publicationReconciler,
            effectNormalizations ?? AliProductionEffectNormalizations.Create(),
            targetStates ?? AliProductionTargetStateAdapters.Create(fileAccess),
            _executionAdapters);
        try
        {
            _baseTools = catalog.Tools.ToArray();
            _protocolTool = OrchestrationProtocolCapability.CreateInvariantFunction();
            _orchestrationSettings = orchestrationSettings;
            _mcpClients = mcpClients;
            _toolPermissions = toolPermissions;
            _fileAccess = fileAccess;
            _workMemory = workMemory;
            _activeUsers = activeUsers;
            _turnAccessor = turnAccessor;
            _shadowObserver = shadowObserver;
            _capabilityPermissionPolicy = new AliToolPermissionPolicy(
                _turnAccessor,
                () => _toolPermissions.CurrentProfile,
                _shadowObserver);

            if (string.IsNullOrWhiteSpace(capabilitySettingsDataRoot))
            {
                _capabilitySettingsDataRoot = null;
                _baseCapabilityRegistry = null;
                _frameworkCapabilityTools = [];
                _capabilitySettings = null;
                _capabilityEnforcementProvider = null;
            }
            else
            {
                var normalizedDataRoot = Path.GetFullPath(capabilitySettingsDataRoot);
                _capabilitySettingsDataRoot = normalizedDataRoot;
                _frameworkCapabilityTools = AliFrameworkCapabilityProbe.Capture(
                        _fileAccess,
                        _turnAccessor)
                    .ToArray();
                var allDeclarations = _baseTools
                    .Concat(_frameworkCapabilityTools)
                    .Append(_protocolTool)
                    .OfType<AIFunctionDeclaration>()
                    .ToArray();
                var productionDeclarations = allDeclarations
                    .Where(declaration =>
                        !AliProductionCapabilityCatalog.IsRetiredToolName(declaration.Name))
                    .ToArray();
                var productionCatalog = AliProductionCapabilityCatalog.Build(productionDeclarations);
                if (productionCatalog.QuarantinedToolNames.Count > 0
                    || productionCatalog.Registry.Descriptors.Count
                    != AliProductionCapabilityCatalog.KnownToolNames.Count
                        + ProtocolCapabilityToolNames.All.Count)
                {
                    throw new InvalidOperationException(
                        "Ali's production capability catalog does not exactly match the registered tool schemas.");
                }
                _baseCapabilityRegistry = productionCatalog.Registry;

                var initialTools = BuildPolicyTools(_orchestrationSettings().Normalize())
                    .Concat(_frameworkCapabilityTools)
                    .ToArray();
                var initialInventory = CapabilityTerminalToolInventory.Create(
                    initialTools,
                    productionCatalog.Registry);
                var initialRuntime = CapabilityRuntimeAvailabilityFactory.Create(
                    initialInventory,
                    CaptureCapabilityRuntimeState(normalizedDataRoot));
                _capabilitySettings = CapabilitySettingsSnapshotOwner.Open(
                    normalizedDataRoot,
                    productionCatalog.Registry,
                    initialRuntime);
                _capabilityEnforcementProvider = new TerminalCapabilityEnforcementProvider(
                    _capabilitySettings,
                    () => CaptureCapabilityRuntimeState(normalizedDataRoot),
                    targetBindingIdAccessor: null,
                    report => ReportCapabilityIssues(report, initialTools),
                    ProjectEffectiveTool,
                    CaptureLiveActiveUserId,
                    CapturePermissionRevision,
                    CaptureLiveActiveUserRevision,
                    actionExecutionBoundary: PrepareDurableExecutionAsync);
            }
        }
        catch
        {
            _planningStateCoordinator.Dispose();
            throw;
        }
    }

    internal CapabilitySettingsSnapshotOwner? CapabilitySettings => _capabilitySettings;

    internal AIFunction ProjectEffectiveTool(
        AIFunction function,
        CapabilityResolutionSnapshot resolution)
    {
        var inventoryProjection = ProjectEffectiveInventory(function, resolution);
        if (!ReferenceEquals(inventoryProjection, function))
        {
            return inventoryProjection;
        }

        var permissionSnapshot = Volatile.Read(ref _projectionPermissionSnapshot);
        if (permissionSnapshot is null
            || !string.Equals(
                permissionSnapshot.Revision,
                resolution.PermissionRevision,
                StringComparison.Ordinal))
        {
            permissionSnapshot = CapturePermissionSnapshot();
        }

        var profile = string.Equals(
                permissionSnapshot.Revision,
                resolution.PermissionRevision,
                StringComparison.Ordinal)
            ? permissionSnapshot.Profile
            : AgentPermissionProfile.LockedDown;
        var requiresPrivateReadApproval = resolution.TryGetTool(function.Name, out var descriptor)
            && RequiresLockedDownPrivateReadApproval(descriptor, profile);
        if (function is CapabilityPermissionProjectionAIFunction permissionProjection)
        {
            return permissionProjection.Project(profile, requiresPrivateReadApproval);
        }

        var requiresApprovalBoundary = requiresPrivateReadApproval
            || descriptor.Permission.RequiresApproval
            || function.GetService<ApprovalRequiredAIFunction>() is not null;
        return _capabilityPermissionPolicy.Apply(function, requiresApprovalBoundary);
    }

    internal static bool RequiresLockedDownPrivateReadApproval(
        CapabilityDescriptor descriptor,
        AgentPermissionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return profile == AgentPermissionProfile.LockedDown
            && descriptor.Effect.ReadsLocalData
            && !string.Equals(
                descriptor.GroupId,
                CapabilityGroupIds.WorkMemory,
                StringComparison.Ordinal);
    }

    internal static AIFunction ProjectEffectiveInventory(
        AIFunction function,
        CapabilityResolutionSnapshot resolution)
    {
        if (!string.Equals(
                function.Name,
                AliCapabilityCatalog.ListAvailableToolsName,
                StringComparison.Ordinal))
        {
            return function;
        }

        var tools = resolution.EffectiveDescriptors
            .OrderBy(descriptor => descriptor.ToolName, StringComparer.Ordinal)
            .Select(descriptor => new CoordinatorCapability(
                descriptor.ToolName,
                descriptor.Description,
                descriptor.RegistrationKind switch
                {
                    CapabilityRegistrationKind.FrameworkBuiltIn => "Microsoft Agent Framework",
                    CapabilityRegistrationKind.AgentSkill => "Agent Skill",
                    CapabilityRegistrationKind.Mcp => "External MCP",
                    _ => descriptor.GroupId is null
                        ? "Ali orchestration protocol"
                        : $"Ali: {CanonicalCapabilityCatalog.GetGroup(descriptor.GroupId).DisplayName}"
                }))
            .ToArray();
        var result = new CoordinatorCapabilityResult(
            $"Ali has {tools.Length} effective model-callable tools in the current capability snapshot. "
            + $"{resolution.UnavailableDescriptors.Count} registered tool(s) are currently unavailable and "
            + $"{resolution.QuarantinedCapabilities.Count} runtime tool(s) are quarantined.",
            Array.AsReadOnly(tools));
        return AIFunctionFactory.Create(
            (Func<CoordinatorCapabilityResult>)(() => result),
            function.Name,
            function.Description);
    }

    private AIAgent CreateAgent(
        IChatClient planningClient,
        ModelProfile profile,
        IReadOnlyList<AITool> tools,
        AgentOrchestrationSettings orchestrationSettings,
        TerminalCapabilityEnforcementProvider? capabilityEnforcementProvider = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var skillsRoot = Path.Combine(AppContext.BaseDirectory, "skills");
        var skillsSource = new AliOutcomeReportingAgentSkillsSource(
            new AgentFileSkillsSource(skillsRoot),
            _toolOutcomes);
        var effectiveCapabilityEnforcement = capabilityEnforcementProvider
            ?? _capabilityEnforcementProvider;
        AIAgent agent = planningClient.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = _assistantProfile.AssistantName,
            Description = "Local personal assistant with memory, current web, local library, reminders, identity, clock, private work memory, and approved workstation file tools.",
            MaximumIterationsPerRequest = MaximumToolIterations,
#pragma warning disable MAAI001 // Agent Framework compaction controls are preview in Harness 1.15.
            MaxContextWindowTokens = profile.ContextTokens,
            MaxOutputTokens = profile.OutputTokenLimit,
#pragma warning restore MAAI001
            DisableWebSearch = true,
            DisableFileMemory = false,
            DisableAgentSkillsProvider = false,
            AgentSkillsSource = skillsSource,
            DisableOpenTelemetry = false,
            OpenTelemetrySourceName = "ProjectAli.AgentFramework",
            // Ali already exposes live progress through CoordinatorTurnContext and keeps
            // private multi-step state in scoped file memory. Harness todo lists made the
            // model narrate an internal plan on ordinary turns and repeatedly surfaced an
            // unfinished list, so keep that overlapping provider out of the conversation.
            DisableTodoProvider = true,
            FileMemoryStore = _workMemory.Store,
            FileAccessStore = _fileAccess.FrameworkStore,
            FileAccessProviderOptions = new FileAccessProviderOptions
            {
                Instructions = _fileAccess.Instructions,
                DisableWriteTools = false,
                DisableReadOnlyToolApproval = false,
                DisableWriteToolApproval = false
            },
            ToolApprovalAgentOptions = new ToolApprovalAgentOptions
            {
                AutoApprovalRules = [ShouldAutoApproveAndRecordAsync]
            },
            AIContextProviders = effectiveCapabilityEnforcement is null
                ? []
                : [effectiveCapabilityEnforcement],
            ChatOptions = new ChatOptions
            {
                Instructions = AliToolCatalog.BuildInstructions(
                    _assistantProfile.AssistantName,
                    orchestrationSettings),
                Tools = tools.ToList(),
                ToolMode = ChatToolMode.Auto,
                AllowMultipleToolCalls = false,
                MaxOutputTokens = profile.OutputTokenLimit
            }
        });
        agent = AliFrameworkProviderOutcomeMiddleware.WithOutcomeReporting(
            agent,
            _toolOutcomes,
            _turnAccessor,
            skillsSource);
        return AliAgentFrameworkMiddleware.WithVisibleLifecycle(agent, _turnAccessor, "Ali");
    }

    private CapabilityRuntimeStateSnapshot CaptureCapabilityRuntimeState(string dataRoot)
    {
        var outgoingToolNames = LoadEnabledOutgoingToolNames(dataRoot);
        return new CapabilityRuntimeStateSnapshot(
            CaptureActiveUserId(),
            providerRevision: "ali-core-provider-v1",
            readyProviderIds: AliProductionCapabilityCatalog.RegisteredProviderIds,
            targetResolution: null,
            permissionRevision: CapturePermissionRevision(),
            allowedPermissionPolicyIds:
            [
                "ali-tool-permission-v1",
                "ali-orchestration-protocol-v1"
            ],
            mcpRevision: McpCapabilityPublicationGate.CalculateMcpRevision([], outgoingToolNames),
            readyIncomingMcpToolNames: [],
            enabledOutgoingMcpToolNames: outgoingToolNames,
            reconcilerRevision: _executionReconcilerRevision,
            availableReconcilerIds: _executionReconcilerIds,
            enforceReconcilerAvailability: true);
    }

    private static string ReconcilerRevision(IReadOnlyList<string> reconcilerIds)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", reconcilerIds));
        try
        {
            return "ali-reconciler-v1:" + Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private string CaptureActiveUserId()
    {
        var selection = _turnAccessor()?.CapturedUserSelection
            ?? _activeUsers?.CaptureSelectionSnapshot();
        return selection?.IsResolved == true
            ? selection.SelectedUser!.StableId
            : "selection-required";
    }

    private string CaptureLiveActiveUserId()
    {
        var selection = _activeUsers?.CaptureSelectionSnapshot();
        if (selection is null)
        {
            return CaptureActiveUserId();
        }

        return selection.IsResolved
            ? selection.SelectedUser!.StableId
            : "selection-required";
    }

    private string CaptureLiveActiveUserRevision() =>
        _activeUsers?.CaptureSelectionRevision()
        ?? $"legacy-active-user:{CaptureLiveActiveUserId()}";

    private AgentToolPermissionSnapshot CapturePermissionSnapshot()
    {
        var snapshot = _toolPermissions.CaptureSnapshot();
        Volatile.Write(ref _projectionPermissionSnapshot, snapshot);
        return snapshot;
    }

    private string CapturePermissionRevision() => CapturePermissionSnapshot().Revision;

    private static IReadOnlySet<string> LoadEnabledOutgoingToolNames(string dataRoot)
    {
        try
        {
            var settings = McpServerSettingsStore.LoadOrDefault(dataRoot);
            return settings.Enabled
                ? settings.Tools
                    .Where(policy => policy.Enabled)
                    .Select(policy => policy.Name)
                    .ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private void ReportCapabilityIssues(
        CapabilityTerminalIssueReport report,
        IReadOnlyList<AITool> activeTools)
    {
        var turn = _turnAccessor();
        var reportKey = $"{report.InventoryRevision}:{report.RuntimeRevision}";
        if (turn is null || !turn.TryRegisterCapabilityIssueReport(reportKey))
        {
            return;
        }

        var names = report.Issues
            .Select(issue => issue.ToolIdentity)
            .Concat(report.QuarantinedCapabilities.Select(item => item.ToolName))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name => ResolveCapabilityIssueDisplayName(activeTools, name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var displayedNames = names.Take(4).ToArray();
        var total = names.Length;
        var detail = displayedNames.Length == 0
            ? "Withheld an incomplete capability declaration from this turn."
            : $"Withheld {total} incomplete capability declaration(s): {string.Join(", ", displayedNames)}.";
        turn.Report(
            AgentActivityKind.Warning,
            "Blocked incomplete capability tools",
            detail);
    }

    private TerminalCapabilityEnforcementProvider CreateIncomingMcpTurnEnforcer(
        IncomingMcpCapabilityCatalog incomingCatalog,
        IReadOnlyList<AITool> activeTools)
    {
        if (_capabilitySettings is null || _capabilitySettingsDataRoot is null)
        {
            throw new InvalidOperationException(
                "Incoming MCP tools require the canonical capability settings owner.");
        }

        var initialState = incomingCatalog.CreateRuntimeState(
            CaptureCapabilityRuntimeState(_capabilitySettingsDataRoot));
        var initialInventory = CapabilityTerminalToolInventory.Create(
            activeTools.Concat(_frameworkCapabilityTools),
            incomingCatalog.Registry);
        var initialRuntime = CapabilityRuntimeAvailabilityFactory.Create(
            initialInventory,
            initialState);
        var turnOwner = CapabilitySettingsSnapshotOwner.Open(
            _capabilitySettingsDataRoot,
            incomingCatalog.Registry,
            initialRuntime);

        CapabilityRuntimeStateSnapshot CaptureTurnState()
        {
            SynchronizeTurnCapabilitySettings(turnOwner);
            return incomingCatalog.CreateRuntimeState(
                CaptureCapabilityRuntimeState(_capabilitySettingsDataRoot));
        }

        return new TerminalCapabilityEnforcementProvider(
            turnOwner,
            CaptureTurnState,
            targetBindingIdAccessor: null,
            report => ReportCapabilityIssues(report, activeTools),
            ProjectEffectiveTool,
            CaptureLiveActiveUserId,
            CapturePermissionRevision,
            CaptureLiveActiveUserRevision,
            invocationBoundaryRevisionAccessor: () =>
                _capabilitySettings.CaptureSettings().Stamp.PublicationRevision,
            invocationBoundaryDependencyId: "capability-settings-publication",
            invocationBoundaryChangedMessage:
                "Capability settings changed after this external tool was planned.",
            invocationBoundaryUnavailableMessage:
                "The live capability-settings publication could not be verified",
            actionExecutionBoundary: PrepareDurableExecutionAsync);
    }

    private TerminalCapabilityEnforcementProvider CreateBaseTurnEnforcer(
        IReadOnlyList<AITool> activeTools)
    {
        if (_capabilitySettings is null
            || _capabilitySettingsDataRoot is null
            || _baseCapabilityRegistry is null)
        {
            throw new InvalidOperationException(
                "A turn-scoped capability boundary requires the canonical settings owner.");
        }

        var initialInventory = CapabilityTerminalToolInventory.Create(
            activeTools.Concat(_frameworkCapabilityTools),
            _baseCapabilityRegistry);
        var initialRuntime = CapabilityRuntimeAvailabilityFactory.Create(
            initialInventory,
            CaptureCapabilityRuntimeState(_capabilitySettingsDataRoot));
        var turnOwner = CapabilitySettingsSnapshotOwner.Open(
            _capabilitySettingsDataRoot,
            _baseCapabilityRegistry,
            initialRuntime);

        CapabilityRuntimeStateSnapshot CaptureTurnState()
        {
            SynchronizeTurnCapabilitySettings(turnOwner);
            return CaptureCapabilityRuntimeState(_capabilitySettingsDataRoot);
        }

        return new TerminalCapabilityEnforcementProvider(
            turnOwner,
            CaptureTurnState,
            targetBindingIdAccessor: null,
            report => ReportCapabilityIssues(report, activeTools),
            ProjectEffectiveTool,
            CaptureLiveActiveUserId,
            CapturePermissionRevision,
            CaptureLiveActiveUserRevision,
            invocationBoundaryRevisionAccessor: () =>
                _capabilitySettings.CaptureSettings().Stamp.PublicationRevision,
            invocationBoundaryDependencyId: "capability-settings-publication",
            invocationBoundaryChangedMessage:
                "Capability settings changed after this tool was planned.",
            invocationBoundaryUnavailableMessage:
                "The live capability-settings publication could not be verified",
            actionExecutionBoundary: PrepareDurableExecutionAsync);
    }

    private void SynchronizeTurnCapabilitySettings(
        CapabilitySettingsSnapshotOwner turnOwner)
    {
        if (_capabilitySettings is null)
        {
            return;
        }

        var shared = _capabilitySettings.CaptureSettings();
        var current = turnOwner.CaptureSettings();
        if (!string.Equals(
                shared.SettingsRevision,
                current.SettingsRevision,
                StringComparison.Ordinal)
            || shared.LoadStatus != current.LoadStatus)
        {
            turnOwner.Reload();
        }
    }

    private static void ReportIncomingMcpIssues(
        CoordinatorTurnContext turn,
        IReadOnlyList<IncomingMcpCapabilityIssue> issues)
    {
        if (issues.Count == 0)
        {
            return;
        }

        var categories = issues
            .GroupBy(issue => issue.Code)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}: {group.Count()}")
            .Take(6)
            .ToArray();
        var examples = issues
            .Select(issue => issue.Message.ReplaceLineEndings(" ").Trim())
            .Where(message => message.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Select(message => message.Length <= 220 ? message : message[..220] + "...")
            .ToArray();
        turn.Report(
            AgentActivityKind.Warning,
            "Withheld unsafe or stale MCP declarations",
            $"Withheld {issues.Count} external declaration(s) without affecting valid tools. {string.Join("; ", categories)}. {string.Join(" ", examples)}");
    }

    private IReadOnlyList<AITool> BuildPolicyTools(AgentOrchestrationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return _baseTools
            .Where(tool => tool is not AIFunctionDeclaration function
                || !AliProductionCapabilityCatalog.IsRetiredToolName(function.Name))
            .Append(_protocolTool)
            .ToArray();
    }

    public bool ResolveToolApproval(AgentToolApprovalDecision decision) =>
        _pendingApprovals.TryGetValue(decision.RequestId, out var pending)
        && pending.Completion.TrySetResult(decision.Choice);

    internal Task<TurnIdentity?> FindPausedTurnAsync(
        CoordinatorTurnContext visibleTurn,
        CancellationToken cancellationToken) =>
        _planningStateCoordinator.FindPausedTurnAsync(visibleTurn, cancellationToken);

    public async Task<AgentHarnessRunResult> RunAsync(
        CoordinatorTurnContext turn,
        string userText,
        IReadOnlyList<RuntimeChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        Func<FinalAnswerPublication, CancellationToken,
            ValueTask<FinalAnswerPublicationAcknowledgment>> publishFinal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publishFinal);
        EnterRun();
        try
        {
            return await RunCoreAsync(
                    turn,
                    userText,
                    history,
                    attachments,
                    publishFinal,
                    resumeRequest: null,
                    structuredRecoveryRequest: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ExitRun();
        }
    }

    public async Task<AgentHarnessRunResult> ResumeAsync(
        CoordinatorTurnContext turn,
        TurnIdentity durableIdentity,
        string steeringText,
        IReadOnlyList<RuntimeChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        Func<FinalAnswerPublication, CancellationToken,
            ValueTask<FinalAnswerPublicationAcknowledgment>> publishFinal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(durableIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(steeringText);
        ArgumentNullException.ThrowIfNull(publishFinal);
        EnterRun();
        try
        {
            return await RunCoreAsync(
                    turn,
                    steeringText,
                    history,
                    attachments,
                    publishFinal,
                    new AliHarnessResumeRequest(
                        durableIdentity,
                        turn.UserMessageId,
                        steeringText),
                    structuredRecoveryRequest: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ExitRun();
        }
    }

    public async Task<AgentHarnessRunResult> ResolveRecoveryAsync(
        CoordinatorTurnContext turn,
        AgentRecoveryDecision decision,
        IReadOnlyList<RuntimeChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        Func<FinalAnswerPublication, CancellationToken,
            ValueTask<FinalAnswerPublicationAcknowledgment>> publishFinal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(publishFinal);
        decision.Validate();
        EnterRun();
        try
        {
            return await RunCoreAsync(
                    turn,
                    userText: string.Empty,
                    history,
                    attachments,
                    publishFinal,
                    resumeRequest: null,
                    structuredRecoveryRequest: new AliHarnessStructuredRecoveryRequest(decision),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ExitRun();
        }
    }

    public async Task CancelRecoveryAsync(
        CoordinatorTurnContext turn,
        AgentRecoveryPrompt prompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(prompt);
        prompt.Validate();
        EnterRun();
        try
        {
            await _planningStateCoordinator.CancelStructuredRecoveryAsync(
                turn,
                prompt,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitRun();
        }
    }

    private async Task<AgentHarnessRunResult> RunCoreAsync(
        CoordinatorTurnContext turn,
        string userText,
        IReadOnlyList<RuntimeChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        Func<FinalAnswerPublication, CancellationToken,
            ValueTask<FinalAnswerPublicationAcknowledgment>> publishFinal,
        AliHarnessResumeRequest? resumeRequest,
        AliHarnessStructuredRecoveryRequest? structuredRecoveryRequest,
        CancellationToken cancellationToken)
    {
        var userSelection = turn.CapturedUserSelection
            ?? _activeUsers?.CaptureSelectionSnapshot();
        var workMemoryUser = userSelection?.IsResolved == true
            ? userSelection.SelectedUser
            : null;
        using var workMemoryScope = _workMemory.EnterScope(turn.ConversationId, workMemoryUser);
        var configuredIncomingMcpToolCount = _mcpClients.CountEnabledConfiguredToolPolicies();
        if (configuredIncomingMcpToolCount > 0)
        {
            turn.Report(
                AgentActivityKind.Status,
                "Connecting configured MCP tools",
                $"Checking {configuredIncomingMcpToolCount} enabled external tool configuration(s) and their exact saved policies within a fixed {McpClientManager.DefaultTurnSetupBudget.TotalSeconds:0}-second setup budget for this turn.");
        }
        await using var mcpSession = await _mcpClients
            .CreateEnabledToolSessionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var warning in mcpSession.Warnings)
        {
            turn.Report(
                AgentActivityKind.Warning,
                $"Incoming MCP warning: {warning.ServerName}",
                warning.Message);
        }

        var orchestrationSettings = _orchestrationSettings().Normalize();
        IReadOnlyList<AITool> activeTools = BuildPolicyTools(orchestrationSettings);
        var activeCapabilityRegistry = _baseCapabilityRegistry;
        var capabilityEnforcementProvider = _baseCapabilityRegistry is not null
            && _capabilitySettings is not null
            && _capabilitySettingsDataRoot is not null
            ? CreateBaseTurnEnforcer(activeTools)
            : _capabilityEnforcementProvider;
        if (mcpSession.Tools.Count > 0)
        {
            if (_baseCapabilityRegistry is null
                || _capabilitySettings is null
                || _capabilitySettingsDataRoot is null)
            {
                turn.Report(
                    AgentActivityKind.Warning,
                    "External MCP tools withheld",
                    "Ali connected to configured MCP tools, but the canonical terminal capability boundary is unavailable for this run.");
            }
            else
            {
                var incomingCatalog = IncomingMcpCapabilityCatalog.Build(
                    _baseCapabilityRegistry,
                    mcpSession);
                activeCapabilityRegistry = incomingCatalog.Registry;
                ReportIncomingMcpIssues(turn, incomingCatalog.Issues);
                if (incomingCatalog.Tools.Count > 0)
                {
                    var incomingTools = incomingCatalog.Tools
                        .Select(tool => (AITool)_capabilityPermissionPolicy.Apply(
                            tool.Function,
                            tool.Descriptor.Permission.RequiresApproval,
                            tool.Descriptor.DisplayName))
                        .ToArray();
                    activeTools = activeTools.Concat(incomingTools).ToArray();
                    capabilityEnforcementProvider = CreateIncomingMcpTurnEnforcer(
                        incomingCatalog,
                        activeTools);
                    turn.Report(
                        AgentActivityKind.Status,
                        "Loaded configured MCP tools",
                        $"Added {incomingCatalog.Tools.Count} canonical external tool(s) for this turn; each remains bound to its server, saved policy, schema, active user, and live capability settings.");
                }
            }
        }
        var input = structuredRecoveryRequest is null
            ? BuildInitialInput(history, userText, attachments).ToList()
            : new List<MeaiChatMessage> { BuildUserMessage(string.Empty, attachments) };
        var attachmentProjection = AliPlanningAttachmentProjection.Capture(
            input[^1].Contents.OfType<DataContent>());
        var initialModelDispatch = CaptureBoundModelDispatch();
        var bindings = BuildTurnRuntimeBindings(
            initialModelDispatch,
            activeTools,
            activeCapabilityRegistry,
            mcpSession,
            attachments);
        var priorConversation = history
            .Select((message, index) => (Message: message, Sequence: (long)index))
            .Select(item => new AcceptedConversationInput(
                item.Message.Id,
                item.Sequence,
                item.Message.Text,
                item.Message.Role switch
                {
                    RuntimeChatRole.User => AcceptedConversationRole.User,
                    RuntimeChatRole.Assistant => AcceptedConversationRole.Assistant,
                    RuntimeChatRole.System => AcceptedConversationRole.System,
                    _ => throw new InvalidDataException(
                        "The runtime conversation contains an unsupported role.")
                }))
            .ToArray();
        var liveBindingsAccessor = () => BuildTurnRuntimeBindings(
            CaptureBoundModelDispatch(),
            activeTools,
            activeCapabilityRegistry,
            mcpSession,
            attachments);
        AliPlanningResumeAttempt? resumeAttempt = null;
        if (structuredRecoveryRequest is not null)
        {
            var decision = structuredRecoveryRequest.Decision;
            var resolution = await _planningStateCoordinator.ResolveStructuredRecoveryAsync(
                turn,
                decision,
                cancellationToken).ConfigureAwait(false);
            if (decision.Choice == AgentRecoveryDecisionChoice.ConfirmDisplayed)
            {
                return new AgentHarnessRunResult(
                    WroteAnswer: false,
                    FinishReason: "recovery-display-confirmed");
            }

            resumeAttempt = decision.Choice switch
            {
                AgentRecoveryDecisionChoice.ConfirmApplied =>
                    await _planningStateCoordinator.ResumeResolvedActionAsync(
                        turn,
                        decision.Prompt.DurableIdentity,
                        bindings,
                        resolution.SourceCommandId,
                        resolution.State.Revision,
                        decision.Prompt.SubjectId,
                        decision.Prompt.SubjectPreparedRevision,
                        ActionUserResolution.ConfirmApplied,
                        activeCapabilityRegistry,
                        liveBindingsAccessor,
                        cancellationToken).ConfigureAwait(false),
                AgentRecoveryDecisionChoice.ConfirmAbsent =>
                    await _planningStateCoordinator.ResumeResolvedActionAsync(
                        turn,
                        decision.Prompt.DurableIdentity,
                        bindings,
                        resolution.SourceCommandId,
                        resolution.State.Revision,
                        decision.Prompt.SubjectId,
                        decision.Prompt.SubjectPreparedRevision,
                        ActionUserResolution.ConfirmAbsent,
                        activeCapabilityRegistry,
                        liveBindingsAccessor,
                        cancellationToken).ConfigureAwait(false),
                AgentRecoveryDecisionChoice.ConfirmNotDisplayed =>
                    await _planningStateCoordinator.RecoverResolvedFinalPublicationAsync(
                        turn,
                        decision.Prompt.DurableIdentity,
                        bindings,
                        resolution.SourceCommandId,
                        resolution.State.Revision,
                        decision.Prompt.SubjectId,
                        decision.Prompt.SubjectPreparedRevision,
                        FinalPublicationUserResolution.ConfirmNotDisplayed,
                        cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(structuredRecoveryRequest))
            };

            if (resumeAttempt.RecoveredPublication is { } resolvedPublication)
            {
                return await PublishRecoveredFinalAsync(
                    turn,
                    resolvedPublication,
                    publishFinal,
                    cancellationToken).ConfigureAwait(false);
            }

            if (decision.Choice == AgentRecoveryDecisionChoice.ConfirmNotDisplayed)
            {
                throw StructuredRecoveryFailure(resumeAttempt);
            }
        }
        else if (resumeRequest is not null)
        {
            resumeAttempt = await _planningStateCoordinator.ResumeTurnAsync(
                turn,
                resumeRequest.DurableIdentity,
                bindings,
                resumeRequest.SteeringText,
                resumeRequest.SourceMessageId,
                activeCapabilityRegistry,
                liveBindingsAccessor,
                cancellationToken).ConfigureAwait(false);
            if (resumeAttempt.RecoveredPublication is { } recoveredPublication)
            {
                return await PublishRecoveredFinalAsync(
                    turn,
                    recoveredPublication,
                    publishFinal,
                    cancellationToken).ConfigureAwait(false);
            }
            if (resumeAttempt.RecoveredInterimPublication is { } recoveredInterim)
            {
                return await PublishRecoveredInterimAsync(
                    turn,
                    recoveredInterim,
                    cancellationToken).ConfigureAwait(false);
            }

            if (resumeAttempt.Recovery.State?.Control is TurnControlState.Completed
                or TurnControlState.Cancelled)
            {
                await _planningStateCoordinator.ClearRecoverableTurnAsync(
                    resumeRequest.DurableIdentity,
                    CancellationToken.None).ConfigureAwait(false);
                resumeRequest = null;
                resumeAttempt = null;
            }
        }

        await using var durableTurn = resumeRequest is null && structuredRecoveryRequest is null
            ? await _planningStateCoordinator.BeginTurnAsync(
                turn,
                bindings,
                priorConversation,
                activeCapabilityRegistry,
                liveBindingsAccessor,
                cancellationToken).ConfigureAwait(false)
            : RequireResumedTurn(resumeAttempt!);
        var dispatchBindingsFactory = (BoundModelDispatchSnapshot dispatch) =>
            BuildTurnRuntimeBindings(
                dispatch,
                activeTools,
                activeCapabilityRegistry,
                mcpSession,
                attachments);
        var completionComposer = new AliCompletionComposer(
            CaptureBoundModelDispatch,
            dispatchBindingsFactory,
            durableTurn.AuthorizeCompletionDispatchAsync);
        var completionCritic = new AliCompletionCritic(
            CaptureBoundModelDispatch,
            dispatchBindingsFactory,
            durableTurn.AuthorizeCompletionDispatchAsync);
        using var planningClient = new AliOrchestrationPlanningClient(
            _modelClient,
            () => _runtime.ActiveProfile.SupportsToolCalls,
            () => _runtime.ActiveProfile,
            _semanticToolCatalog,
            completionBridge: TemporaryCompletionBridge.FromComposer(
                completionComposer.ComposeAsync),
            toolArgumentNormalizer: _fileAccess.NormalizeProviderToolArguments,
            completedToolOutcomeClassifier: _toolOutcomeRegistry.Classify,
            finalAnswerRenderer: static (activeTurn, answer) =>
                FinalAnswerRenderer.Compose(answer, activeTurn.WebSources),
            boundDispatchAccessor: CaptureBoundModelDispatch,
            dispatchBindingsFactory: dispatchBindingsFactory,
            completionCritic: completionCritic);
        using var planningTurnScope = planningClient.BeginTurn(
            turn,
            durableTurn.Input,
            durableTurn,
            attachmentProjection,
            durableTurn.DurableIdentity,
            durableTurn.ImmutableOriginalRequest);
        var activeAgent = CreateAgent(
            planningClient,
            initialModelDispatch.Profile,
            activeTools,
            orchestrationSettings,
            capabilityEnforcementProvider);

        if (attachments.Count > 0)
        {
            turn.Report(
                AgentActivityKind.Status,
                "Inspecting attachments through the agent",
                $"Loaded {attachments.Count} attachment(s) without bypassing tools or approvals.");
        }

        // The UI conversation history is the canonical state. A fresh Harness session per
        // visible turn prevents an unfinished high-effort tool loop from leaking into the
        // user's next message while preserving one session across this turn's tool calls.
        var session = await activeAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        string? finishReason = null;
        var renderedAnswer = new StringBuilder();
        var pendingShadowCalls = new PendingShadowCallTracker();
        var pendingStandingPermissions = new PendingStandingPermissionTracker();
        turn.RegisterActionExecutionAuthority(durableTurn);

        try
        {
            while (true)
            {
                ToolApprovalRequestContent? approvalRequest = null;
                await foreach (var update in activeAgent.RunStreamingAsync(
                                   input,
                                   session,
                                   options: null,
                                   cancellationToken).ConfigureAwait(false))
                {
                    finishReason = update.FinishReason?.ToString() ?? finishReason;
                    foreach (var content in update.Contents)
                    {
                        switch (content)
                        {
                            case ToolApprovalRequestContent approval:
                                TrackPendingShadowCall(turn, pendingShadowCalls, approval.ToolCall);
                                approvalRequest = approval;
                                break;
                            case FunctionCallContent functionCall when !functionCall.InformationalOnly:
                                TrackPendingShadowCall(turn, pendingShadowCalls, functionCall);
                                if (!turn.TryGetToolPlan(functionCall.CallId, out _))
                                {
                                    var displayName = ResolveUserFacingToolName(
                                        activeTools,
                                        functionCall.Name);
                                    turn.Report(
                                        AgentActivityKind.ToolCall,
                                        $"Requested {displayName}",
                                        $"Selected tool: {displayName}");
                                }
                                break;
                            case FunctionResultContent functionResult:
                                CompleteStandingPermission(
                                    turn,
                                    pendingStandingPermissions,
                                    functionResult);
                                TryObserveFrameworkResult(
                                    _shadowObserver,
                                    turn,
                                    pendingShadowCalls,
                                    functionResult);
                                if (!turn.TryGetToolPlan(functionResult.CallId, out _)
                                    && ShouldReportGenericReturnedResult(functionResult))
                                {
                                    turn.Report(
                                        AgentActivityKind.ToolResult,
                                        "Tool returned; Ali is evaluating the result.",
                                        "The tool returned; Ali is evaluating the returned evidence.");
                                }
                                turn.RequestToolPlanRetirement(functionResult.CallId);
                                break;
                            case TextContent textContent when textContent.Text is { Length: > 0 }:
                                renderedAnswer.Append(textContent.Text);
                                break;
                        }
                    }
                }

                if (approvalRequest is null)
                {
                    break;
                }

                var response = await RequestApprovalAsync(
                    turn,
                    approvalRequest,
                    activeTools,
                    pendingStandingPermissions,
                    cancellationToken).ConfigureAwait(false);
                input = [new MeaiChatMessage(MeaiChatRole.User, [response])];
                }

            var exactAnswer = renderedAnswer.ToString();
            if (string.IsNullOrWhiteSpace(exactAnswer))
            {
                throw new InvalidOperationException(
                    "The agent run ended without an exact prepared final answer; nothing was published.");
            }

            // The planning client returns only an exact prepared final or interim text at this
            // boundary. Earlier native protocol/tool iterations may have reported ToolCalls, and
            // some providers omit a finish reason on an otherwise complete answer. Never let that
            // earlier protocol metadata leak into the user-visible terminal publication.
            finishReason = ChatFinishReason.Stop.ToString();

            if (planningClient.PreparedInterimResponse is { } interim)
            {
                var exactDigest = TurnStateIntegrity.Digest(exactAnswer);
                if (!string.Equals(exactDigest, interim.AnswerDigest, StringComparison.Ordinal)
                    || !string.Equals(exactAnswer, interim.AnswerText, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The streamed interim response differs from the exact planning pause response.");
                }

                // Make the exact user/conversation -> durable-turn lookup crash durable before
                // displaying a prompt which invites the user's next explicit message.
                await _planningStateCoordinator.RecordRecoverableTurnAsync(
                        interim.DurableIdentity,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                turn.PublishInterimResponse(exactAnswer, finishReason);
                await durableTurn.CommitInterimPublicationAsync(
                    interim,
                    CancellationToken.None).ConfigureAwait(false);
                return new AgentHarnessRunResult(
                    WroteAnswer: true,
                    FinishReason: finishReason,
                    Paused: true,
                    ResumeIdentity: interim.DurableIdentity);
            }

            var prepared = planningClient.RequirePreparedFinalPublication();
            var publication = FinalAnswerPublicationBoundary.BindExactPreparedAnswer(
                new FinalAnswerPublication(
                    turn.ConversationId,
                    turn.UserMessageId,
                    turn.AssistantMessageId,
                    prepared.PublicationId,
                    exactAnswer,
                    prepared.AnswerDigest,
                    turn.UsedEvidenceTool ? EvidenceStatus.Verified : EvidenceStatus.Unverified,
                    finishReason),
                prepared.AssistantMessageId,
                prepared.AnswerText,
                prepared.AnswerDigest);
            var acknowledgment = await publishFinal(publication, cancellationToken)
                .ConfigureAwait(false);
            FinalAnswerPublicationBoundary.RequireExactAcknowledgment(
                publication,
                acknowledgment);
            // Once the conversation boundary accepts the exact answer, caller cancellation
            // cannot revoke the obligation to durably record that publication.
            await durableTurn.CommitFinalPublicationAsync(
                exactAnswer,
                CancellationToken.None).ConfigureAwait(false);
            await _planningStateCoordinator.ClearRecoverableTurnAsync(
                    durableTurn.DurableIdentity,
                    CancellationToken.None)
                .ConfigureAwait(false);

            return new AgentHarnessRunResult(
                WroteAnswer: true,
                FinishReason: finishReason,
                Paused: false,
                ResumeIdentity: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                var control = await durableTurn.RequestCancellationAsync().ConfigureAwait(false);
                if (control == TurnControlState.Cancelled)
                {
                    await _planningStateCoordinator.ClearRecoverableTurnAsync(
                            durableTurn.DurableIdentity,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                else if (control == TurnControlState.CancelRequested)
                {
                    await _planningStateCoordinator.RecordRecoverableTurnAsync(
                            durableTurn.DurableIdentity,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // Preserve the caller's cancellation. Recovery will fail closed on the last
                // durable revision if the best-effort cancellation transition could not commit.
            }

            throw;
        }
        finally
        {
            turn.ClearActionExecutionAuthority(durableTurn);
            pendingStandingPermissions.Clear();
        }
    }

    private static AliDurablePlanningTurn RequireResumedTurn(
        AliPlanningResumeAttempt attempt)
    {
        if (attempt.IsReady)
        {
            return attempt.Turn!;
        }

        var changedBindings = attempt.Recovery.ChangedBindings.Count == 0
            ? string.Empty
            : " Changed bindings: " + string.Join(", ", attempt.Recovery.ChangedBindings) + ".";
        var attachmentGuidance = attempt.Recovery.ChangedBindings.Contains(
            "attachments",
            StringComparer.Ordinal)
            ? " Reattach the exact original attachment bytes before explicitly resuming; Ali will not silently omit or replace them."
            : string.Empty;
        throw new InvalidOperationException(
            "Ali could not explicitly resume the preserved turn ("
            + (attempt.FailureCode ?? attempt.Recovery.Status.ToString())
            + ")." + changedBindings + attachmentGuidance);
    }

    private static InvalidOperationException StructuredRecoveryFailure(
        AliPlanningResumeAttempt attempt)
    {
        var changedBindings = attempt.Recovery.ChangedBindings.Count == 0
            ? string.Empty
            : " Changed bindings: " + string.Join(", ", attempt.Recovery.ChangedBindings) + ".";
        return new InvalidOperationException(
            "Ali could not apply the exact structured recovery decision ("
            + (attempt.FailureCode ?? attempt.Recovery.Status.ToString())
            + ")." + changedBindings);
    }

    private async Task<AgentHarnessRunResult> PublishRecoveredFinalAsync(
        CoordinatorTurnContext visibleTurn,
        AliRecoveredFinalPublication recovered,
        Func<FinalAnswerPublication, CancellationToken,
            ValueTask<FinalAnswerPublicationAcknowledgment>> publishFinal,
        CancellationToken cancellationToken)
    {
        var publication = FinalAnswerPublicationBoundary.BindExactPreparedAnswer(
            new FinalAnswerPublication(
                visibleTurn.ConversationId,
                visibleTurn.UserMessageId,
                recovered.AssistantMessageId,
                recovered.PublicationId,
                recovered.AnswerText,
                recovered.AnswerDigest,
                EvidenceStatus.Unknown,
                FinishReason: "recovered-publication"),
            recovered.AssistantMessageId,
            recovered.AnswerText,
            recovered.AnswerDigest);
        var acknowledgment = await publishFinal(publication, cancellationToken)
            .ConfigureAwait(false);
        FinalAnswerPublicationBoundary.RequireExactAcknowledgment(publication, acknowledgment);
        await _planningStateCoordinator.CommitRecoveredFinalPublicationAsync(
            recovered,
            CancellationToken.None).ConfigureAwait(false);
        return new AgentHarnessRunResult(
            WroteAnswer: true,
            FinishReason: publication.FinishReason,
            Paused: false,
            ResumeIdentity: null);
    }

    private async Task<AgentHarnessRunResult> PublishRecoveredInterimAsync(
        CoordinatorTurnContext visibleTurn,
        AliRecoveredInterimPublication recovered,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        visibleTurn.PublishInterimResponse(
            recovered.Text,
            finishReason: "recovered-interim-publication");
        var committedState = await _planningStateCoordinator.CommitRecoveredInterimPublicationAsync(
            recovered,
            CancellationToken.None).ConfigureAwait(false);
        await _planningStateCoordinator.RecordRecoverableTurnAsync(
            recovered.DurableIdentity,
            CancellationToken.None).ConfigureAwait(false);
        var recoveryPrompt = CreateRecoveryPrompt(recovered, committedState);
        if (recoveryPrompt is not null)
        {
            visibleTurn.Report(
                AgentActivityKind.Status,
                "Recovery decision required",
                recoveryPrompt.Kind == AgentRecoveryPromptKind.ActionReconciliation
                    ? "Confirm whether the interrupted action happened. Use a recovery button; chat text will not be interpreted as the answer."
                    : "Confirm whether the recovered answer was already shown. Use a recovery button; chat text will not be interpreted as the answer.",
                recoveryPrompt: recoveryPrompt);
        }
        return new AgentHarnessRunResult(
            WroteAnswer: true,
            FinishReason: "recovered-interim-publication",
            Paused: true,
            ResumeIdentity: recovered.DurableIdentity,
            StructuredRecoveryRequired: recoveryPrompt is not null);
    }

    private static AgentRecoveryPrompt? CreateRecoveryPrompt(
        AliRecoveredInterimPublication recovered,
        TurnState committedState)
    {
        var kind = recovered.Reason switch
        {
            InterimPublicationReason.ActionReconciliationRequired =>
                AgentRecoveryPromptKind.ActionReconciliation,
            InterimPublicationReason.FinalPublicationReconciliationRequired =>
                AgentRecoveryPromptKind.FinalPublicationReconciliation,
            _ => (AgentRecoveryPromptKind?)null
        };
        if (kind is null)
        {
            return null;
        }

        var prompt = new AgentRecoveryPrompt(
            recovered.DurableIdentity,
            committedState.Revision,
            recovered.PublicationId,
            recovered.TextDigest,
            recovered.SubjectId,
            recovered.SubjectPreparedRevision,
            kind.Value);
        prompt.Validate();
        return prompt;
    }

    private async ValueTask<CapabilityInvocationAuthorization> PrepareDurableExecutionAsync(
        CapabilityInvocationLease lease,
        AIFunctionArguments arguments,
        bool requiresApproval,
        CancellationToken cancellationToken)
    {
        var turn = _turnAccessor();
        if (turn is null
            || !turn.TryGetActiveToolCallId(lease.ToolName, out var callId)
            || string.IsNullOrWhiteSpace(callId)
            || !turn.TryGetActionExecutionAuthority(out var authority)
            || authority is null)
        {
            return CapabilityInvocationAuthorization.Block(
                new CapabilityAvailabilityReason(
                    CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                    "durable-action-boundary",
                    "No exact current turn/call action boundary is available."));
        }

        return await authority.PrepareExecutionAsync(
                lease,
                callId,
                arguments,
                requiresApproval,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private BoundModelDispatchSnapshot CaptureBoundModelDispatch()
    {
        if (_runtime is not IBoundModelDispatchSource boundSource)
        {
            throw new InvalidOperationException(
                "The active model runtime cannot expose the exact client and settings envelope required for durable orchestration.");
        }

        var dispatch = boundSource.CaptureBoundModelDispatch()
            ?? throw new InvalidOperationException(
                "The active model runtime returned no bound dispatch snapshot.");
        ArgumentNullException.ThrowIfNull(dispatch.ChatClient);
        ArgumentNullException.ThrowIfNull(dispatch.Profile);
        ArgumentNullException.ThrowIfNull(dispatch.RuntimeBinding);
        ArgumentNullException.ThrowIfNull(dispatch.ModelBinding);
        ArgumentNullException.ThrowIfNull(dispatch.GenerationSettingsBinding);
        return dispatch;
    }

    private TurnRuntimeBindings BuildTurnRuntimeBindings(
        BoundModelDispatchSnapshot modelDispatch,
        IReadOnlyList<AITool> activeTools,
        CanonicalCapabilityRegistry? capabilityRegistry,
        McpToolSession mcpSession,
        IReadOnlyList<ChatAttachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(modelDispatch);
        ArgumentNullException.ThrowIfNull(modelDispatch.Profile);
        ArgumentNullException.ThrowIfNull(modelDispatch.RuntimeBinding);
        ArgumentNullException.ThrowIfNull(modelDispatch.ModelBinding);
        ArgumentNullException.ThrowIfNull(modelDispatch.GenerationSettingsBinding);
        var permission = _toolPermissions.CaptureSnapshot();
        var declarations = activeTools
            .OfType<AIFunctionDeclaration>()
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .Select(tool => new
            {
                tool.Name,
                tool.Description,
                schema = tool.JsonSchema.GetRawText(),
                returnSchema = tool.ReturnJsonSchema?.GetRawText()
            })
            .ToArray();
        var semanticIndexFingerprint = _semanticToolCatalog.CaptureBindingFingerprint(
            activeTools.OfType<AIFunctionDeclaration>().ToArray());
        var mcpBoundary = mcpSession.CaptureSnapshot();
        var capabilitySettings = _capabilitySettings?.CaptureSettings();

        return new TurnRuntimeBindings(
            DigestCanonical(new
            {
                _assistantProfile.ProfileId,
                _assistantProfile.AssistantName,
                createdAtUtc = _assistantProfile.CreatedAt.ToUniversalTime()
            }),
            DigestCanonical(new
            {
                modelDispatch.RuntimeBinding.Engine,
                modelDispatch.RuntimeBinding.Implementation,
                modelDispatch.RuntimeBinding.RuntimeKind,
                modelDispatch.RuntimeBinding.RuntimeLocation,
                modelDispatch.RuntimeBinding.RuntimeEndpoint,
                modelDispatch.RuntimeBinding.ProtocolIdentity,
                modelDispatch.RuntimeBinding.CapabilityProfileIdentity
            }),
            DigestCanonical(new
            {
                modelDispatch.ModelBinding.ProfileId,
                modelDispatch.ModelBinding.PackageId,
                modelDispatch.ModelBinding.Family,
                modelDispatch.ModelBinding.Size,
                modelDispatch.ModelBinding.Quantization,
                modelDispatch.ModelBinding.SupportsVision,
                modelDispatch.ModelBinding.SupportsToolCalls,
                modelDispatch.ModelBinding.CapabilityProfileIdentity
            }),
            DigestCanonical(new
            {
                modelDispatch.GenerationSettingsBinding.ContextTokens,
                modelDispatch.GenerationSettingsBinding.OutputTokenLimit,
                modelDispatch.GenerationSettingsBinding.Temperature,
                modelDispatch.GenerationSettingsBinding.TopP,
                modelDispatch.GenerationSettingsBinding.StreamingEnabled,
                modelDispatch.GenerationSettingsBinding.ThinkingControl,
                modelDispatch.GenerationSettingsBinding.ThinkingEnabled,
                modelDispatch.GenerationSettingsBinding.ReasoningEffort,
                modelDispatch.GenerationSettingsBinding.TokenizerIdentity,
                modelDispatch.GenerationSettingsBinding.RollingWindowMode,
                modelDispatch.GenerationSettingsBinding.ProtocolIdentity
            }),
            DigestCanonical(new
            {
                registryRevision = capabilityRegistry?.RegistryRevision ?? "registry-unavailable",
                activeUserId = CaptureActiveUserId(),
                declarations,
                semanticIndexFingerprint,
                settings = capabilitySettings is null
                    ? null
                    : new
                    {
                        capabilitySettings.Stamp.PublicationRevision,
                        capabilitySettings.Stamp.RegistryRevision,
                        capabilitySettings.Stamp.SettingsRevision,
                        capabilitySettings.Stamp.ResolutionRevision,
                        capabilitySettings.RuntimeRevision,
                        capabilitySettings.ProviderRevision,
                        capabilitySettings.PermissionRevision,
                        capabilitySettings.McpRevision,
                        capabilitySettings.ReconcilerRevision,
                        capabilitySettings.LoadStatus,
                        rows = capabilitySettings.Rows
                            .OrderBy(row => row.GroupId, StringComparer.Ordinal)
                            .Select(row => new
                            {
                                row.GroupId,
                                row.Enabled,
                                row.Status,
                                row.DeclaredToolCount,
                                row.CallableToolCount,
                                row.UnavailableToolCount
                            })
                            .ToArray()
                    }
            }),
            DigestCanonical(new
            {
                permission.Profile,
                permission.Revision
            }),
            DigestCanonical(new
            {
                mcpSession.SettingsRevision,
                mcpSession.SessionRevision,
                boundary = mcpBoundary,
                tools = mcpSession.Tools
                    .OrderBy(tool => tool.Function.Name, StringComparer.Ordinal)
                    .Select(tool => new
                    {
                        tool.Function.Name,
                        tool.ServerId,
                        tool.ConfiguredDeclarationFingerprint,
                        tool.SchemaFingerprint,
                        tool.RequiresApproval
                    })
                    .ToArray()
            }),
            CaptureModelVisibleAttachmentDigest(attachments),
            TurnStateIntegrity.EmptyDigest);
    }

    internal static string CaptureModelVisibleAttachmentDigest(
        IReadOnlyList<ChatAttachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        var projected = new List<object>();
        for (var index = 0; index < attachments.Count; index++)
        {
            var attachment = attachments[index]
                ?? throw new InvalidDataException("An attachment entry cannot be null.");
            if (attachment.Kind != AttachmentKind.Image)
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(attachment.Base64Data);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"The attached image '{attachment.FileName}' did not contain valid image data.",
                    ex);
            }

            try
            {
                projected.Add(new
                {
                    originalIndex = index,
                    kind = attachment.Kind.ToString(),
                    mediaType = NormalizeAttachmentMediaType(attachment.ContentType),
                    payloadDigest = TurnStateIntegrity.Digest(bytes)
                });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        return DigestCanonical(projected);
    }

    private static string NormalizeAttachmentMediaType(string contentType) =>
        string.IsNullOrWhiteSpace(contentType)
            ? "image/png"
            : contentType.Trim().ToLowerInvariant();

    private async ValueTask<bool> ShouldAutoApproveAndRecordAsync(
        ToolAutoApprovalRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var approved = await _fileAccess.ShouldAutoApproveAsync(context).ConfigureAwait(false);
        if (!approved)
        {
            return false;
        }

        var call = context.FunctionCallContent;
        var turn = _turnAccessor();
        if (turn is not null && !string.IsNullOrWhiteSpace(call.CallId))
        {
            turn.RecordShadowPermission(
                call.CallId,
                new EvidencePermissionMetadata("approved-policy", "policy"),
                source: "auto-policy");
        }

        return true;
    }

    private static string DigestCanonical<T>(T value)
    {
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(value);
        try
        {
            return TurnStateIntegrity.Digest(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static IReadOnlyList<MeaiChatMessage> BuildInitialInput(
        IReadOnlyList<RuntimeChatMessage> history,
        string userText,
        IReadOnlyList<ChatAttachment> attachments)
    {
        var userMessage = BuildUserMessage(userText, attachments);
        var messages = history.Select(ToExtensionsAiMessage).ToList();
        messages.Add(userMessage);
        return messages;
    }

    private static MeaiChatMessage BuildUserMessage(
        string userText,
        IReadOnlyList<ChatAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return new MeaiChatMessage(MeaiChatRole.User, userText);
        }

        var contents = new List<AIContent>
        {
            new TextContent(userText)
        };
        foreach (var attachment in attachments.Where(item => item.Kind == AttachmentKind.Image))
        {
            try
            {
                contents.Add(new DataContent(
                    Convert.FromBase64String(attachment.Base64Data),
                    NormalizeAttachmentMediaType(attachment.ContentType)));
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"The attached image '{attachment.FileName}' did not contain valid image data.",
                    ex);
            }
        }

        return new MeaiChatMessage(MeaiChatRole.User, contents);
    }

    private async Task<AIContent> RequestApprovalAsync(
        CoordinatorTurnContext turn,
        ToolApprovalRequestContent request,
        IReadOnlyList<AITool> activeTools,
        PendingStandingPermissionTracker pendingStandingPermissions,
        CancellationToken cancellationToken)
    {
        var approvalRequestedAtUtc = DateTimeOffset.UtcNow;
        var functionCall = request.ToolCall as FunctionCallContent;
        var toolName = functionCall?.Name ?? request.ToolCall.GetType().Name;
        var toolDisplayName = ResolveUserFacingToolName(activeTools, toolName);
        var arguments = functionCall is null ? "{}" : CompactArguments(functionCall.Arguments, 1200);
        var description = activeTools
            .OfType<AIFunctionDeclaration>()
            .FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal))?
            .Description ?? "Ali requested permission to run this tool.";

        if (functionCall is not null
            && TryGetActiveUser(turn, out var activeUser)
            && _toolPermissions.TryMatch(activeUser, toolName, functionCall.Arguments, out var savedGrant)
            && savedGrant is not null)
        {
            RecordStandingShadowPermission(
                turn,
                functionCall,
                savedGrant.Scope,
                request.RequestId);
            turn.Report(
                AgentActivityKind.Status,
                $"Used saved permission for {toolDisplayName}",
                savedGrant.Scope == AgentToolPermissionScope.Tool
                    ? $"{activeUser.DisplayName} previously allowed this tool."
                    : $"{activeUser.DisplayName} previously allowed these exact arguments.");
            return CreateOneCallApprovalResponse(
                request,
                savedGrant.Scope == AgentToolPermissionScope.Tool
                    ? "Approved for this call by the current user's saved tool rule."
                    : "Approved for this call by the current user's saved exact-arguments rule.");
        }

        var prompt = new AgentToolApprovalPrompt(
            request.RequestId,
            toolDisplayName,
            arguments,
            description);
        var completion = new TaskCompletionSource<AgentToolApprovalChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingApprovals.TryAdd(request.RequestId, new PendingApproval(completion)))
        {
            throw new InvalidOperationException("Ali received a duplicate framework approval request.");
        }

        turn.Report(
            AgentActivityKind.Approval,
            $"Permission needed for {toolDisplayName}",
            arguments,
            approvalPrompt: prompt);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        AgentToolApprovalChoice choice;
        try
        {
            choice = await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            TryObserveApprovalCancelled(
                _shadowObserver,
                turn,
                functionCall,
                toolName,
                ex,
                approvalRequestedAtUtc,
                DateTimeOffset.UtcNow);
            if (functionCall is not null)
            {
                turn.RequestToolPlanRetirement(functionCall.CallId);
            }
            throw;
        }
        finally
        {
            _pendingApprovals.TryRemove(request.RequestId, out _);
        }

        RecordInteractiveShadowPermission(
            turn,
            functionCall,
            choice,
            request.RequestId);
        turn.Report(
            choice == AgentToolApprovalChoice.Deny ? AgentActivityKind.Warning : AgentActivityKind.Status,
            choice == AgentToolApprovalChoice.Deny ? "Permission denied" : "Permission granted",
            choice.ToString());
        turn.RecordPermissionDecision(choice);
        if (choice == AgentToolApprovalChoice.Deny)
        {
            TryObserveApprovalDenied(
                _shadowObserver,
                turn,
                functionCall,
                toolName,
                approvalRequestedAtUtc,
                DateTimeOffset.UtcNow);
            if (functionCall is not null)
            {
                turn.RequestToolPlanRetirement(functionCall.CallId);
            }
        }

        if (choice is AgentToolApprovalChoice.AlwaysAllowArguments or AgentToolApprovalChoice.AlwaysAllowTool)
        {
            QueueStandingPermission(
                turn,
                pendingStandingPermissions,
                choice,
                functionCall);
        }

        return choice switch
        {
            AgentToolApprovalChoice.AllowOnce =>
                CreateOneCallApprovalResponse(request, "Approved once by the user."),
            AgentToolApprovalChoice.AlwaysAllowArguments =>
                CreateOneCallApprovalResponse(
                    request,
                    "Approved for this call; the exact-arguments rule will be saved only after the call returns."),
            AgentToolApprovalChoice.AlwaysAllowTool =>
                CreateOneCallApprovalResponse(
                    request,
                    "Approved for this call; the tool rule will be saved only after the call returns."),
            _ => request.CreateResponse(false, "Denied by the user.")
        };
    }

    internal static AIContent CreateOneCallApprovalResponse(
        ToolApprovalRequestContent request,
        string message)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.CreateResponse(true, message);
    }

    // This controls non-authoritative UI wording only. Domain success is decided
    // exclusively by AliProductionToolOutcomeRegistry in the planning evidence path.
    internal static bool ShouldReportGenericReturnedResult(
        FunctionResultContent functionResult) =>
        FrameworkToolResultClassifier.Classify(functionResult)
            == FrameworkToolResultDisposition.CompletedReturn;

    private void QueueStandingPermission(
        CoordinatorTurnContext turn,
        PendingStandingPermissionTracker pendingStandingPermissions,
        AgentToolApprovalChoice choice,
        FunctionCallContent? functionCall)
    {
        if (!TryGetActiveUser(turn, out var activeUser))
        {
            turn.Report(
                AgentActivityKind.Warning,
                "Standing permission was not saved",
                "Select the active user profile first. This approval still applies to the current agent run.");
            return;
        }

        if (!pendingStandingPermissions.TryQueue(
                activeUser,
                choice,
                functionCall,
                out var reason))
        {
            turn.Report(
                AgentActivityKind.Warning,
                "Standing permission was not saved",
                reason);
            return;
        }

        turn.Report(
            AgentActivityKind.Status,
            "Standing permission will be saved after this call",
            "Ali captured the approved call exactly and will persist the revocable rule only if the matching tool result returns without a capability block.");
    }

    private void CompleteStandingPermission(
        CoordinatorTurnContext turn,
        PendingStandingPermissionTracker pendingStandingPermissions,
        FunctionResultContent functionResult)
    {
        var completion = pendingStandingPermissions.Complete(functionResult);
        if (completion.Status == PendingStandingPermissionCompletionStatus.None)
        {
            return;
        }

        if (completion.Status != PendingStandingPermissionCompletionStatus.ReadyToSave
            || completion.Permission is null)
        {
            turn.Report(
                AgentActivityKind.Warning,
                "Standing permission was not saved",
                completion.Status == PendingStandingPermissionCompletionStatus.CapabilityBlocked
                    ? "The matching tool call was blocked because its capability lease became stale. No standing rule was created."
                    : "The matching tool call did not reach a returned invocation state. No standing rule was created.");
            return;
        }

        try
        {
            var pending = completion.Permission;
            _toolPermissions.Save(
                pending.ActiveUser,
                pending.ToolName,
                pending.Scope,
                pending.Arguments.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal));
            turn.Report(
                AgentActivityKind.Status,
                "Saved revocable permission",
                pending.Scope == AgentToolPermissionScope.Tool
                    ? $"{pending.ActiveUser.DisplayName} allowed this tool until the rule is revoked in Settings."
                    : $"{pending.ActiveUser.DisplayName} allowed these exact arguments until the rule is revoked in Settings.");
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or System.Security.SecurityException)
        {
            turn.Report(
                AgentActivityKind.Warning,
                "Standing permission was not saved",
                $"The current run remains approved, but the permission file could not be updated: {ex.Message}");
        }
    }

    private void TrackPendingShadowCall(
        CoordinatorTurnContext turn,
        PendingShadowCallTracker pendingCalls,
        ToolCallContent toolCall)
    {
        if (_shadowObserver is null)
        {
            return;
        }

        try
        {
            pendingCalls.TryTrack(toolCall, DateTimeOffset.UtcNow);
        }
        catch
        {
            // Stream tracking is shadow-only and cannot affect Agent Framework output.
        }
    }

    internal static void TryObserveFrameworkResult(
        IShadowToolObserver? shadowObserver,
        CoordinatorTurnContext turn,
        PendingShadowCallTracker pendingCalls,
        FunctionResultContent functionResult)
    {
        if (shadowObserver is null
            || !CoordinatorTurnContext.IsBoundedShadowCallId(functionResult.CallId))
        {
            return;
        }

        try
        {
            if (turn.WasShadowObserved(functionResult.CallId))
            {
                pendingCalls.TryTake(functionResult.CallId, out _);
                return;
            }

            if (turn.TryGetPendingExplicitShadowTerminal(
                    functionResult.CallId,
                    out var explicitTerminal)
                && explicitTerminal is not null)
            {
                pendingCalls.TryTake(functionResult.CallId, out _);
                TryObservePendingExplicitTerminal(
                    shadowObserver,
                    turn,
                    explicitTerminal,
                    functionResult.Exception);
                return;
            }

            var hasTrackedCall = pendingCalls.TryGet(functionResult.CallId, out var pending);
            if (!hasTrackedCall || pending is null)
            {
                if (!turn.TryGetToolPlan(functionResult.CallId, out var plan) || plan is null)
                {
                    return;
                }

                pending = new PendingShadowCall(
                    plan.ToolName,
                    DateTimeOffset.UtcNow);
            }

            if (!CoordinatorTurnContext.IsBoundedShadowToolName(pending.ToolName))
            {
                return;
            }

            var completedAtUtc = DateTimeOffset.UtcNow;
            var permission = turn.TryGetShadowPermission(functionResult.CallId, out var recordedPermission)
                && recordedPermission is not null
                    ? recordedPermission
                    : turn.TryGetShadowStandingPermission(pending.ToolName, out var standingPermission)
                      && standingPermission is not null
                        ? standingPermission
                        : new EvidencePermissionMetadata("unknown", "unknown");
            bool accepted;
            if (functionResult.Exception is Exception exception)
            {
                accepted = shadowObserver.TryObserveThrew(
                    turn.ObservationIdentity,
                    functionResult.CallId,
                    pending.ToolName,
                    null,
                    exception,
                    pending.StartedAtUtc,
                    completedAtUtc,
                    permission);
            }
            else
            {
                accepted = shadowObserver.TryObserveReturned(
                    turn.ObservationIdentity,
                    functionResult.CallId,
                    pending.ToolName,
                    null,
                    functionResult.Result,
                    pending.StartedAtUtc,
                    completedAtUtc,
                    permission);
            }

            if (accepted)
            {
                if (hasTrackedCall)
                {
                    pendingCalls.TryTake(functionResult.CallId, out _);
                }
                turn.MarkShadowObserved(functionResult.CallId);
            }
        }
        catch
        {
            // Framework stream observation is supplementary and failure-isolated.
        }
    }

    private static void TryObservePendingExplicitTerminal(
        IShadowToolObserver shadowObserver,
        CoordinatorTurnContext turn,
        PendingExplicitShadowTerminal terminal,
        Exception? frameworkException)
    {
        try
        {
            var accepted = terminal.Kind switch
            {
                ExplicitShadowTerminalKind.Denied => shadowObserver.TryObserveDenied(
                    turn.ObservationIdentity,
                    terminal.CallId,
                    terminal.ToolName,
                    null,
                    terminal.FailureCode,
                    terminal.StartedAtUtc,
                    terminal.CompletedAtUtc,
                    terminal.Permission),
                ExplicitShadowTerminalKind.Cancelled => shadowObserver.TryObserveCancelled(
                    turn.ObservationIdentity,
                    terminal.CallId,
                    terminal.ToolName,
                    null,
                    frameworkException as OperationCanceledException
                        ?? new OperationCanceledException("The approval wait was cancelled."),
                    terminal.StartedAtUtc,
                    terminal.CompletedAtUtc,
                    terminal.Permission),
                _ => false
            };
            if (accepted)
            {
                turn.ClearPendingExplicitShadowTerminal(terminal.CallId);
                turn.MarkShadowObserved(terminal.CallId);
            }
        }
        catch
        {
            // A rejected or failed retry remains pending as the same typed terminal.
        }
    }

    internal static void TryObserveApprovalDenied(
        IShadowToolObserver? shadowObserver,
        CoordinatorTurnContext turn,
        FunctionCallContent? functionCall,
        string toolName,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc)
    {
        if (shadowObserver is null || functionCall is null)
        {
            return;
        }

        var callId = functionCall.CallId;
        if (string.IsNullOrWhiteSpace(callId) || turn.WasShadowObserved(callId))
        {
            return;
        }

        var permission = new EvidencePermissionMetadata("denied", "none");
        turn.RecordPendingExplicitShadowTerminal(new PendingExplicitShadowTerminal(
            callId,
            toolName,
            ExplicitShadowTerminalKind.Denied,
            "user-denied",
            startedAtUtc,
            completedAtUtc,
            permission));
        try
        {
            if (shadowObserver.TryObserveDenied(
                turn.ObservationIdentity,
                callId,
                toolName,
                functionCall.Arguments,
                "user-denied",
                startedAtUtc,
                completedAtUtc,
                permission))
            {
                turn.ClearPendingExplicitShadowTerminal(callId);
                turn.MarkShadowObserved(callId);
            }
        }
        catch
        {
            // The user's denial remains authoritative even if shadow storage fails.
        }
    }

    internal static void TryObserveApprovalCancelled(
        IShadowToolObserver? shadowObserver,
        CoordinatorTurnContext turn,
        FunctionCallContent? functionCall,
        string toolName,
        OperationCanceledException exception,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc)
    {
        if (shadowObserver is null
            || functionCall is null
            || !CoordinatorTurnContext.IsBoundedShadowCallId(functionCall.CallId)
            || turn.WasShadowObserved(functionCall.CallId))
        {
            return;
        }

        var permission = new EvidencePermissionMetadata("unknown", "unknown");
        turn.RecordPendingExplicitShadowTerminal(new PendingExplicitShadowTerminal(
            functionCall.CallId,
            toolName,
            ExplicitShadowTerminalKind.Cancelled,
            null,
            startedAtUtc,
            completedAtUtc,
            permission));
        try
        {
            if (shadowObserver.TryObserveCancelled(
                    turn.ObservationIdentity,
                    functionCall.CallId,
                    toolName,
                    functionCall.Arguments,
                    exception,
                    startedAtUtc,
                    completedAtUtc,
                    permission))
            {
                turn.ClearPendingExplicitShadowTerminal(functionCall.CallId);
                turn.MarkShadowObserved(functionCall.CallId);
            }
        }
        catch
        {
            // Approval cancellation must retain its original exception and stack.
        }
    }

    private bool TryGetActiveUser(
        CoordinatorTurnContext turn,
        out ActiveUser activeUser)
    {
        var selection = turn.CapturedUserSelection
            ?? _activeUsers?.CaptureSelectionSnapshot();
        if (selection?.IsResolved != true || selection.SelectedUser is null)
        {
            activeUser = null!;
            return false;
        }

        activeUser = selection.SelectedUser;
        return true;
    }

    internal static void RecordInteractiveShadowPermission(
        CoordinatorTurnContext turn,
        FunctionCallContent? functionCall,
        AgentToolApprovalChoice choice,
        string? approvalRequestId = null)
    {
        if (functionCall is null || string.IsNullOrWhiteSpace(functionCall.CallId))
        {
            return;
        }

        var permission = choice switch
        {
            AgentToolApprovalChoice.AllowOnce =>
                new EvidencePermissionMetadata("approved-once", "once"),
            AgentToolApprovalChoice.AlwaysAllowArguments =>
                new EvidencePermissionMetadata("approved-standing", "exact-arguments"),
            AgentToolApprovalChoice.AlwaysAllowTool =>
                new EvidencePermissionMetadata("approved-standing", "tool"),
            _ => new EvidencePermissionMetadata("denied", "none")
        };
        turn.RecordShadowPermission(
            functionCall.CallId,
            permission,
            source: "interactive-user",
            approvalRequestId: approvalRequestId);
        if (choice is AgentToolApprovalChoice.AlwaysAllowArguments
            or AgentToolApprovalChoice.AlwaysAllowTool)
        {
            turn.RecordShadowStandingPermission(functionCall.Name, permission);
        }
    }

    internal static void RecordStandingShadowPermission(
        CoordinatorTurnContext turn,
        FunctionCallContent? functionCall,
        AgentToolPermissionScope scope,
        string? approvalRequestId = null)
    {
        if (functionCall is null || string.IsNullOrWhiteSpace(functionCall.CallId))
        {
            return;
        }

        var permission = scope == AgentToolPermissionScope.Tool
            ? new EvidencePermissionMetadata("approved-standing", "tool")
            : new EvidencePermissionMetadata("approved-standing", "exact-arguments");
        turn.RecordShadowPermission(
            functionCall.CallId,
            permission,
            source: "saved-user-rule",
            approvalRequestId: approvalRequestId);
        turn.RecordShadowStandingPermission(functionCall.Name, permission);
    }

    private static MeaiChatMessage ToExtensionsAiMessage(RuntimeChatMessage message) =>
        new(
            message.Role switch
            {
                RuntimeChatRole.System => MeaiChatRole.System,
                RuntimeChatRole.Assistant => MeaiChatRole.Assistant,
                _ => MeaiChatRole.User
            },
            message.Text);

    private static string CompactArguments(IDictionary<string, object?>? arguments, int maximumCharacters = 520) =>
        CompactValue(arguments ?? new Dictionary<string, object?>(), maximumCharacters);

    private static string CompactValue(object? value, int maximumCharacters = 520)
    {
        var text = value switch
        {
            null => "No details",
            string stringValue => stringValue,
            JsonElement element => element.GetRawText(),
            _ => JsonSerializer.Serialize(value)
        };
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= maximumCharacters ? text : text[..maximumCharacters] + "...";
    }

    private static string Humanize(string toolName) => toolName.Replace('_', ' ').Trim();

    private static string ResolveUserFacingToolName(
        IReadOnlyList<AITool> activeTools,
        string toolName)
    {
        try
        {
            var displayName = activeTools
                .OfType<AIFunction>()
                .FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal))?
                .GetService<ActivityReportingAIFunction>()?
                .UserFacingDisplayName;
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }
        }
        catch
        {
            // User-facing enrichment must never affect exact tool execution identity.
        }

        return Humanize(toolName);
    }

    internal static string ResolveCapabilityIssueDisplayName(
        IReadOnlyList<AITool> activeTools,
        string toolName)
    {
        try
        {
            var function = activeTools
                .OfType<AIFunction>()
                .FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));
            if (function is not null)
            {
                var displayName = function
                    .GetService<ActivityReportingAIFunction>()?
                    .UserFacingDisplayName;
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    return displayName;
                }

                return Humanize(function.Name);
            }
        }
        catch
        {
            // Human display enrichment cannot affect capability quarantine.
        }

        return "unavailable capability";
    }

    public void Dispose()
    {
        bool disposePlanningCoordinator;
        lock (_lifetimeSync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            disposePlanningCoordinator = TryClaimPlanningCoordinatorDisposalUnderLock();
        }

        foreach (var pending in _pendingApprovals.Values)
        {
            pending.Completion.TrySetCanceled();
        }
        _pendingApprovals.Clear();
        if (disposePlanningCoordinator)
        {
            _planningStateCoordinator.Dispose();
        }
    }

    private void EnterRun()
    {
        lock (_lifetimeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            checked
            {
                _activeRuns++;
            }
        }
    }

    private void ExitRun()
    {
        bool disposePlanningCoordinator;
        lock (_lifetimeSync)
        {
            if (_activeRuns <= 0)
            {
                throw new InvalidOperationException("Ali's active-run lifetime count is inconsistent.");
            }

            _activeRuns--;
            disposePlanningCoordinator = TryClaimPlanningCoordinatorDisposalUnderLock();
        }

        if (disposePlanningCoordinator)
        {
            _planningStateCoordinator.Dispose();
        }
    }

    private bool TryClaimPlanningCoordinatorDisposalUnderLock()
    {
        if (_disposed == 0 || _activeRuns != 0 || _planningStateCoordinatorDisposed)
        {
            return false;
        }

        _planningStateCoordinatorDisposed = true;
        return true;
    }

    private sealed record PendingApproval(TaskCompletionSource<AgentToolApprovalChoice> Completion);
}

internal enum PendingStandingPermissionCompletionStatus
{
    None,
    ReadyToSave,
    CapabilityBlocked,
    ToolFailed
}

internal sealed record PendingStandingPermission(
    ActiveUser ActiveUser,
    AgentToolApprovalChoice Choice,
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments)
{
    public AgentToolPermissionScope Scope => Choice == AgentToolApprovalChoice.AlwaysAllowTool
        ? AgentToolPermissionScope.Tool
        : AgentToolPermissionScope.ExactArguments;
}

internal sealed record PendingStandingPermissionCompletion(
    PendingStandingPermissionCompletionStatus Status,
    PendingStandingPermission? Permission = null);

internal sealed class PendingStandingPermissionTracker
{
    internal const int DefaultCapacity = 256;

    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly Dictionary<string, PendingStandingPermission> _pending = new(StringComparer.Ordinal);

    public PendingStandingPermissionTracker(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    internal int Count
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    internal bool TryQueue(
        ActiveUser activeUser,
        AgentToolApprovalChoice choice,
        FunctionCallContent? functionCall,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(activeUser);
        if (choice is not (AgentToolApprovalChoice.AlwaysAllowArguments
            or AgentToolApprovalChoice.AlwaysAllowTool))
        {
            reason = "Only an explicit standing-permission choice can be queued.";
            return false;
        }

        if (functionCall is null
            || !CoordinatorTurnContext.IsBoundedShadowCallId(functionCall.CallId)
            || !CoordinatorTurnContext.IsBoundedShadowToolName(functionCall.Name))
        {
            reason = "The framework did not provide a safe call identity for this standing permission.";
            return false;
        }

        IReadOnlyDictionary<string, object?> arguments;
        try
        {
            arguments = SnapshotArguments(functionCall.Arguments);
        }
        catch (Exception ex) when (ex is JsonException
                                   or NotSupportedException
                                   or InvalidOperationException)
        {
            reason = "The framework arguments could not be captured exactly enough to save this standing permission safely.";
            return false;
        }

        var pending = new PendingStandingPermission(
            activeUser.Normalize(),
            choice,
            functionCall.Name,
            arguments);
        lock (_sync)
        {
            if (_pending.Count >= _capacity)
            {
                reason = "Too many standing permissions are waiting for matching tool results in this turn.";
                return false;
            }

            if (!_pending.TryAdd(functionCall.CallId, pending))
            {
                reason = "The framework reused a call identity, so this standing permission was not queued.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    internal PendingStandingPermissionCompletion Complete(FunctionResultContent functionResult)
    {
        ArgumentNullException.ThrowIfNull(functionResult);
        PendingStandingPermission? pending;
        lock (_sync)
        {
            if (!_pending.Remove(functionResult.CallId, out pending))
            {
                return new PendingStandingPermissionCompletion(
                    PendingStandingPermissionCompletionStatus.None);
            }
        }

        // Standing-permission persistence tracks whether the already-approved invocation
        // reached a return boundary. It does not create evidence, complete work, or classify
        // the tool's domain result; those remain exclusively registry-owned.
        var disposition = FrameworkToolResultClassifier.Classify(functionResult);
        if (disposition is FrameworkToolResultDisposition.ExternalOutcomeUnknown
            or FrameworkToolResultDisposition.InvocationFailed)
        {
            return new PendingStandingPermissionCompletion(
                PendingStandingPermissionCompletionStatus.ToolFailed);
        }

        if (disposition == FrameworkToolResultDisposition.CapabilityBlockedBeforeInvocation)
        {
            return new PendingStandingPermissionCompletion(
                PendingStandingPermissionCompletionStatus.CapabilityBlocked);
        }

        return new PendingStandingPermissionCompletion(
            PendingStandingPermissionCompletionStatus.ReadyToSave,
            pending);
    }

    internal void Clear()
    {
        lock (_sync)
        {
            _pending.Clear();
        }
    }

    private static IReadOnlyDictionary<string, object?> SnapshotArguments(
        IDictionary<string, object?>? arguments)
    {
        var serialized = JsonSerializer.SerializeToElement(
            arguments ?? new Dictionary<string, object?>());
        if (serialized.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Tool arguments did not serialize as an object.");
        }

        return new ReadOnlyDictionary<string, object?>(
            serialized.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object?)property.Value.Clone(),
                StringComparer.Ordinal));
    }

}

internal sealed class PendingShadowCallTracker
{
    internal const int DefaultCapacity = 256;

    private readonly int _capacity;
    private readonly Dictionary<string, PendingShadowEntry> _calls = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _oldestFirst = new();

    public PendingShadowCallTracker(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    internal int Count => _calls.Count;

    internal bool TryTrack(
        ToolCallContent? toolCall,
        DateTimeOffset observedAtUtc)
    {
        if (toolCall is not FunctionCallContent functionCall
            || functionCall.InformationalOnly
            || !CoordinatorTurnContext.IsBoundedShadowCallId(functionCall.CallId)
            || !CoordinatorTurnContext.IsBoundedShadowToolName(functionCall.Name)
            || _calls.ContainsKey(functionCall.CallId))
        {
            return false;
        }

        if (_calls.Count >= _capacity)
        {
            var oldest = _oldestFirst.First
                ?? throw new InvalidOperationException("The pending shadow-call tracker is inconsistent.");
            _oldestFirst.RemoveFirst();
            _calls.Remove(oldest.Value);
        }

        var node = _oldestFirst.AddLast(functionCall.CallId);
        _calls.Add(functionCall.CallId, new PendingShadowEntry(
            new PendingShadowCall(functionCall.Name, observedAtUtc),
            node));
        return true;
    }

    internal bool TryTake(string callId, out PendingShadowCall? pending)
    {
        if (string.IsNullOrWhiteSpace(callId) || !_calls.Remove(callId, out var entry))
        {
            pending = null;
            return false;
        }

        _oldestFirst.Remove(entry.Node);
        pending = entry.Pending;
        return true;
    }

    internal bool TryGet(string callId, out PendingShadowCall? pending)
    {
        if (!CoordinatorTurnContext.IsBoundedShadowCallId(callId)
            || !_calls.TryGetValue(callId, out var entry))
        {
            pending = null;
            return false;
        }

        pending = entry.Pending;
        return true;
    }

    private sealed record PendingShadowEntry(
        PendingShadowCall Pending,
        LinkedListNode<string> Node);
}

internal sealed record PendingShadowCall(
    string ToolName,
    DateTimeOffset StartedAtUtc);

internal sealed record AgentHarnessRunResult(
    bool WroteAnswer,
    string? FinishReason,
    bool Paused = false,
    TurnIdentity? ResumeIdentity = null,
    bool StructuredRecoveryRequired = false);

internal sealed record AliHarnessResumeRequest(
    TurnIdentity DurableIdentity,
    string SourceMessageId,
    string SteeringText);

internal sealed record AliHarnessStructuredRecoveryRequest(
    AgentRecoveryDecision Decision);
