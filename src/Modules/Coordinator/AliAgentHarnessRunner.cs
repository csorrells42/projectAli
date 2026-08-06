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
using Ali.Modules.Serena;
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
    private const int MinimumMessageToolIterations = 65;
    private static readonly IReadOnlySet<string> MinimumCSharpToolNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            AliCapabilityCatalog.CodingInspectProjectName,
            AliCapabilityCatalog.DotNetCreateProjectName,
            AliCapabilityCatalog.RoslynAnalyzeProjectName,
            AliCapabilityCatalog.RoslynFormatProjectName,
            AliCapabilityCatalog.DotNetBuildName,
            AliCapabilityCatalog.DotNetTestName,
            AliCapabilityCatalog.DotNetRunName,
            AliCapabilityCatalog.DotNetStopProjectName
        };
    private static readonly IReadOnlySet<string> CoreRecoveryToolNames =
        new HashSet<string>(MinimumCSharpToolNames, StringComparer.Ordinal)
        {
            AliCapabilityCatalog.SearchCurrentWebName,
            AliCapabilityCatalog.RecallUserMemoryName,
            AliCapabilityCatalog.ListCurrentUserMemoriesName
        };
    private static readonly IReadOnlySet<string> CoreBehaviorRepairToolNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            AliCapabilityCatalog.FileReadName,
            AliCapabilityCatalog.FileWriteName,
            AliCapabilityCatalog.FileReplaceLinesName,
            AliCapabilityCatalog.RoslynAnalyzeProjectName,
            AliCapabilityCatalog.RoslynFormatProjectName,
            AliCapabilityCatalog.DotNetBuildName,
            AliCapabilityCatalog.DotNetTestName,
            AliCapabilityCatalog.DotNetRunName,
            AliCapabilityCatalog.DotNetStopProjectName
        };
    private static readonly IReadOnlySet<string> CoreBehaviorRewriteToolNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            AliCapabilityCatalog.FileReadName,
            AliCapabilityCatalog.FileWriteName
        };
    private readonly IReadOnlyList<AITool> _baseTools;
    private readonly IReadOnlyList<AITool> _startupAssistantTools;
    private readonly IReadOnlyList<AITool> _startupPolicyTools;
    private readonly AIFunction _protocolTool;
    private readonly IChatClient _modelClient;
    private readonly AliPlanningStateCoordinator _planningStateCoordinator;
    private readonly ILocalModelRuntime _runtime;
    private readonly AssistantProfile _assistantProfile;
    private readonly AgentOrchestrationSettings _orchestrationSettings;
    private readonly McpClientManager _mcpClients;
    private readonly AgentToolPermissionStore _toolPermissions;
    private readonly AliWorkstationFileAccess _fileAccess;
    private readonly SerenaCodingService? _serenaCoding;
    private readonly McpSourceFileTools _coreSourceFileTools;
    private readonly IReadOnlyList<AITool> _coreFileTools;
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
        AliDurableEffectAdapterRegistry? durableEffects = null,
        AliFrameworkToolOutcomeSidecar? toolOutcomes = null,
        SerenaCodingService? serenaCoding = null)
    {
        _modelClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _runtime = runtime;
        _assistantProfile = assistantProfile.Normalize();
        _semanticToolCatalog = semanticToolCatalog ?? new RegistryOnlySemanticToolCatalog();
        _toolOutcomes = toolOutcomes ?? new AliFrameworkToolOutcomeSidecar();
        _toolOutcomeRegistry = new AliProductionToolOutcomeRegistry(_toolOutcomes);
        _executionAdapters = executionAdapters ?? AliExecutionEffectAdapterRegistry.Empty;
        var resolvedDurableEffects = durableEffects ?? AliProductionDurableEffectAdapters.Create();
        _executionReconcilerIds = _executionAdapters.Reconcilers
            .Concat(resolvedDurableEffects.Reconcilers)
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
            _executionAdapters,
            resolvedDurableEffects);
        try
        {
            _fileAccess = fileAccess;
            _serenaCoding = serenaCoding;
            _coreSourceFileTools = new McpSourceFileTools(_fileAccess);
            _coreFileTools = CreateCoreFileTools(_coreSourceFileTools);
            _baseTools = catalog.Tools.ToArray();
            _protocolTool = OrchestrationProtocolCapability.CreateInvariantFunction();
            _startupAssistantTools = _baseTools
                .Where(tool => tool is not AIFunctionDeclaration function
                    || !AliProductionCapabilityCatalog.IsRetiredToolName(function.Name))
                .ToArray();
            _startupPolicyTools = _startupAssistantTools
                .Append(_protocolTool)
                .ToArray();
            _orchestrationSettings = orchestrationSettings().Normalize();
            _mcpClients = mcpClients;
            _toolPermissions = toolPermissions;
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

                var initialTools = _startupPolicyTools
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
        TerminalCapabilityEnforcementProvider? capabilityEnforcementProvider = null,
        bool coreAssistantPath = false,
        string? boundReasoningEffort = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        AliOutcomeReportingAgentSkillsSource? skillsSource = null;
        if (!coreAssistantPath)
        {
            var skillsRoot = Path.Combine(AppContext.BaseDirectory, "skills");
            skillsSource = new AliOutcomeReportingAgentSkillsSource(
                new AgentFileSkillsSource(skillsRoot),
                _toolOutcomes);
        }

        var effectiveCapabilityEnforcement = coreAssistantPath
            ? null
            : capabilityEnforcementProvider ?? _capabilityEnforcementProvider;
        var contextProviders = new List<AIContextProvider>();
        if (!coreAssistantPath)
        {
            contextProviders.Add(_workMemory.CreateFrameworkProvider());
        }
        if (effectiveCapabilityEnforcement is not null)
        {
            contextProviders.Add(effectiveCapabilityEnforcement);
        }
        var chatOptions = new ChatOptions
        {
            Instructions = coreAssistantPath
                ? BuildCoreAssistantInstructions(
                    _assistantProfile.AssistantName,
                    _serenaCoding?.ServerInstructions)
                : AliToolCatalog.BuildInstructions(
                    _assistantProfile.AssistantName,
                    orchestrationSettings),
            Tools = tools.ToList(),
            ToolMode = ChatToolMode.Auto,
            AllowMultipleToolCalls = false,
            MaxOutputTokens = profile.OutputTokenLimit
        };
        if (!string.IsNullOrWhiteSpace(boundReasoningEffort))
        {
            chatOptions.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [AliInternalModelRoutingProperties.BoundReasoningEffort] = boundReasoningEffort
            };
        }

        var harnessContextWindowTokens = coreAssistantPath
            ? Math.Min(profile.ContextTokens, Math.Max(4_096, profile.ContextTokens / 4))
            : profile.ContextTokens;
        var harnessOutputTokens = coreAssistantPath
            // A single tool call's arguments (e.g. a full-file rewrite) must fit
            // entirely within this budget or the streamed JSON gets cut off mid-value
            // and the whole call fails to parse. 2,048 proved too tight for a
            // multi-method C# file in practice; 4,096 keeps a real ceiling on
            // latency while giving legitimate large single-call payloads room to
            // actually finish instead of reliably failing and needing a retry.
            ? Math.Min(profile.OutputTokenLimit, Math.Clamp(harnessContextWindowTokens / 4, 512, 4_096))
            : profile.OutputTokenLimit;
        if (harnessOutputTokens >= harnessContextWindowTokens)
        {
            harnessOutputTokens = Math.Max(1, harnessContextWindowTokens / 4);
        }

        var harnessClient = coreAssistantPath
            ? new CoreAssistantContextCompactingChatClient(planningClient)
            : planningClient;
        AIAgent agent = harnessClient.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = _assistantProfile.AssistantName,
            Description = "Local personal assistant with memory, current web, local library, reminders, identity, clock, private work memory, and approved workstation file tools.",
            // AliMinimumMessage owns one continuous model/tool/result conversation.
            // Sixty-four tool actions plus the initial model request lets substantial
            // C# work finish without the artificial four-iteration handoff that
            // previously returned an empty stream before a tool could run.
            MaximumIterationsPerRequest = coreAssistantPath
                ? MinimumMessageToolIterations
                : MaximumToolIterations,
#pragma warning disable MAAI001 // Agent Framework compaction controls are preview in Harness 1.15.
            // Local OpenAI-compatible runtimes can account Harmony/tool payloads much
            // more aggressively than the framework estimator. Compact the core loop
            // early enough that repeated source reads and edits cannot overrun the
            // runtime's real window while work is still advancing.
            MaxContextWindowTokens = harnessContextWindowTokens,
            MaxOutputTokens = harnessOutputTokens,
#pragma warning restore MAAI001
            DisableWebSearch = true,
            // The core assistant already owns its operating state and completion
            // loop. Harness mode negotiation only adds mode_get/mode_set calls
            // before useful work, so keep it entirely out of this hot path.
            DisableAgentModeProvider = coreAssistantPath,
            // Ali's store already owns stable user/conversation isolation. Supplying the
            // provider explicitly prevents Harness from adding a random working folder
            // whose setup mutation would sit outside the exact durable tool-call grant.
            DisableFileMemory = true,
            DisableAgentSkillsProvider = coreAssistantPath,
            AgentSkillsSource = skillsSource,
            DisableOpenTelemetry = coreAssistantPath,
            // Core tools are already projected without approvals and execute only
            // inside the validated Workspace scope. Avoid three redundant approval
            // decorators on every model/tool round trip.
            DisableToolAutoApproval = coreAssistantPath,
            DisableApprovalNotRequiredFunctionBypassing = coreAssistantPath,
            DisableApprovalResponseBinding = coreAssistantPath,
            // CoreAssistantContextCompactingChatClient owns exact in-RAM compaction.
            // A second Harness compaction provider duplicates work and context.
            DisableCompaction = coreAssistantPath,
            OpenTelemetrySourceName = "ProjectAli.AgentFramework",
            // Ali already exposes live progress through CoordinatorTurnContext and keeps
            // private multi-step state in scoped file memory. Harness todo lists made the
            // model narrate an internal plan on ordinary turns and repeatedly surfaced an
            // unfinished list, so keep that overlapping provider out of the conversation.
            DisableTodoProvider = true,
            // The core path binds these functions directly into ChatOptions so its
            // focused tool mode can filter and require them. The regular path retains
            // the framework provider's dynamic injection.
            FileAccessStore = coreAssistantPath ? null : _fileAccess.FrameworkStore,
            FileAccessProviderOptions = !coreAssistantPath
                ? new FileAccessProviderOptions
                {
                    Instructions = _fileAccess.Instructions,
                    DisableWriteTools = false,
                    DisableReadOnlyToolApproval = coreAssistantPath,
                    DisableWriteToolApproval = coreAssistantPath
                }
                : null,
            ToolApprovalAgentOptions = new ToolApprovalAgentOptions
            {
                AutoApprovalRules = [ShouldAutoApproveAndRecordAsync]
            },
            AIContextProviders = contextProviders,
            ChatOptions = chatOptions
        });
        if (coreAssistantPath)
        {
            // CORE PATH BYPASS: provider outcome sidecars and framework lifecycle
            // receipts remain compiled for later one-at-a-time reintegration, but
            // neither belongs between a programming request and its model/tool loop.
            // Capability and Workspace permission wrappers remain unchanged.
            // Participant-memory reads are the one exception: they require an
            // admitted permission receipt even on this path, so a narrow,
            // tool-name-scoped middleware records one for exactly those two
            // tools and is a no-op for everything else on this path.
            agent = AliCoreMemoryReadReceiptMiddleware.WithMemoryReadReceipts(agent, _turnAccessor);
            // Serena maintains its own machine-global project registry,
            // independent of Ali's configured Workspace root -- confirmed live
            // to allow activating a same-named project entirely outside the
            // sandbox. This checks every activate_project result against the
            // configured root and rejects anything outside it. A no-op when
            // Serena isn't configured or that tool isn't offered this turn.
            if (_serenaCoding is not null)
            {
                agent = AliSerenaWorkspaceGuardMiddleware.WithWorkspaceGuard(
                    agent,
                    _serenaCoding.WorkspaceRoot);
            }
            return agent;
        }

        agent = AliFrameworkProviderOutcomeMiddleware.WithOutcomeReporting(
            agent,
            _toolOutcomes,
            _turnAccessor,
            skillsSource);
        return AliAgentFrameworkMiddleware.WithVisibleLifecycle(agent, _turnAccessor, "Ali");
    }

    // Ali's own fallback coding tools, used only when Serena is unavailable so
    // a failed external MCP process degrades coding capability instead of
    // eliminating it entirely. See the Serena-unavailable branch in
    // RunCoreAssistantAsync.
    private static IReadOnlyList<AITool> CreateCoreFileTools(McpSourceFileTools provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return
        [
            BindCoreFileTool(provider, nameof(McpSourceFileTools.ReadAsync),
                AliCapabilityCatalog.FileReadName,
                "Read one UTF-8 text file under Workspace."),
            BindCoreFileTool(provider, nameof(McpSourceFileTools.WriteCoreAsync),
                AliCapabilityCatalog.FileWriteName,
                "Create one new UTF-8 Workspace text file. This tool never overwrites an existing file; use file_access_replace_lines or file_access_append to modify existing source."),
            BindCoreFileTool(provider, nameof(McpSourceFileTools.ReplaceLinesAsync),
                AliCapabilityCatalog.FileReplaceLinesName,
                "Replace an inclusive 1-based line range in one existing Workspace file with new content."),
            BindCoreFileTool(provider, nameof(McpSourceFileTools.AppendAsync),
                McpSourceFileTools.AppendToolName,
                "Append supplied UTF-8 text directly to the end of one existing Workspace file without rewriting its existing contents."),
            BindCoreFileTool(provider, nameof(McpSourceFileTools.LocateSolutionAsync),
                McpSourceFileTools.LocateSolutionToolName,
                "Find solution and C# project files and return Workspace-relative paths formatted for Ali's coding tools.")
        ];
    }

    private static AIFunction BindCoreFileTool(
        McpSourceFileTools provider,
        string methodName,
        string toolName,
        string description)
    {
        var method = typeof(McpSourceFileTools)
            .GetMethods()
            .Single(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal));
        return AIFunctionFactory.Create(
            method,
            provider,
            toolName,
            description,
            serializerOptions: null);
    }

    private static string BuildCoreAssistantInstructions(
        string assistantName,
        string? serenaInstructions) =>
        $"You are {assistantName}, a truthful, reliable, fast personal and coding assistant. "
        + "Answer ordinary conversation directly and concisely. "
        + "When the request needs current, personal, stored, local, or tool-produced facts, call the relevant available tool and answer from its result; never invent evidence or falsely claim that a capability is unavailable. "
        + "Preserve every place name and geographic qualifier exactly when forming search arguments, including state and country abbreviations. "
        + "Inspect every tool result, propagate errors truthfully, and keep using appropriate tools until the request is complete or the returned evidence proves it is impossible. "
        + "A request is not impossible merely because it contains many requirements or needs many tool calls. Decompose large work into concrete steps and keep advancing through those steps in the same request. Do not apologize for scope or give up after inspection. "
        + "Before answering, account for every explicitly requested operation and report each operation's verified success, failure, or exact unresolved obstacle. "
        + "Serena is your preferred programming toolset when it is available; use Serena's native project, memory, semantic retrieval, editing, refactoring, diagnostics, and shell tools directly for coding work. If Serena's tools are not offered to you this turn, it is unavailable right now and you have been given Ali's own built-in file tools instead -- use those normally rather than claiming coding is impossible. "
        + "The Workspace project is already activated for you at the start of every turn; never call activate_project again during a turn, even for a request that names a specific subfolder like \"foo2\" -- activate_project switches to an entirely different top-level project by exact name, it does not navigate into a subfolder of the current one, and guessing a bare name risks activating an unrelated project that happens to share it. To work inside a subfolder, use ordinary relative paths (for example \"foo2/MainWindow.xaml\") with your normal file, search, and symbol tools instead. "
        + "For creation, repair, build, test, launch, or stop requests, perform the requested Workspace operations with whichever coding tools are actually available to you this turn; pasted source or instructions are not a substitute for creating or changing the requested files. "
        + "Keep every source, XAML, project, build, and run operation inside the active Workspace project. Complete the requested multi-file implementation before attempting its first build. "
        + "Use the newest installed supported .NET SDK for new projects. Never downgrade an existing TargetFramework unless the human explicitly requested that older compatibility target. "
        + "A successful build proves compilation only. It does not satisfy an explicit request to implement or change source behavior; inspect the behavior, make the requested targeted source change, then rebuild and run when requested. "
        + "For existing code, prefer symbol-level or targeted retrieval and editing over whole-file reads or rewrites; split genuinely large implementations into focused files. "
        + "Never claim that Workspace GUI launch is blocked by a sandbox. Use your available shell or run tool when launch is requested and report its exact result. Never claim build, test, or launch success unless a tool actually returned success for the final source. "
        + "Never pause merely because a tool or protocol response is imperfect; recover, choose another available tool, ask one necessary clarification, or explain the exact obstacle. "
        + "File and command operations must remain inside the active Workspace project. "
        + "Inside a regular double-quoted C# string literal, write a line break as the two characters backslash-n, never as an actual newline; only a verbatim string (@\"...\") may contain a real line break, and only with every quote doubled. "
        + "When narrating multi-step work, put a blank line between each distinct step, plan point, or shift in topic; never run separate sentences like \"Let me do X. Now let me do Y.\" together with only a space, since that reads as one unreadable block. "
        + "If a build or diagnostics check returns many errors at once, they are very often one cascade from a single dropped or misplaced brace, parenthesis, or quote, not many independent mistakes -- a parser that loses its place after one structural break misreads everything downstream. Fix only the earliest reported error first, re-check diagnostics, and only continue to the next distinct error if it still exists after that fix. Do not attempt every listed error independently in one pass. "
        + AliToolCatalog.TypoInterpretationInstruction
        + (string.IsNullOrWhiteSpace(serenaInstructions)
            ? string.Empty
            : " Serena server instructions: " + serenaInstructions.Trim());

    // GUARDRAIL, do not remove: this method's ChatOptions below sets
    // SuppressInjectedPersona = true. That is acceptable ONLY because this
    // call's output (CoreAssistantCodingRequirements) is consumed purely as an
    // internal routing decision -- which tool drawers to open for this turn.
    // Its DirectAnswer field must never be shown to the user as Ali's answer.
    // A future change did exactly that (published this suppressed-persona text
    // directly as the chat response) and it silently erased Ali's personality
    // from ordinary conversation. Any code path that could cause this method's
    // output to reach the user directly is a bug -- Ali's real answer must
    // always come from the full persona-carrying agent, never from here.
    private static async Task<CoreAssistantCodingRequirements> ClassifyCoreCodingRequirementsAsync(
        IChatClient chatClient,
        IReadOnlyList<MeaiChatMessage> input,
        CancellationToken cancellationToken)
    {
        var classifier = AIFunctionFactory.Create(
            (
                bool useWorkspaceTools,
                bool useWebTools,
                bool useMemoryTools,
                bool requireWorkspaceMutation,
                bool requireBuild,
                bool requireRun) =>
                CreateCoreRequirements(
                    useWorkspaceTools,
                    useWebTools,
                    useMemoryTools,
                    requireWorkspaceMutation,
                    requireBuild,
                    requireRun),
            "open_core_tools",
            "Open every core tool drawer and completion requirement needed to finish the entire human request, not merely its first planned step.");
        var options = new ChatOptions
        {
            Instructions = string.Join(
                Environment.NewLine,
                "Answer ordinary conversation directly and concisely. Interpret the newest human turn semantically from the supplied recent conversation; never route by keywords.",
                "Resolve references to prior conversation accurately. The newest human turn is the request to answer; phrases such as 'what did I just ask' refer to the preceding human turn, not to the current question itself.",
                "If the request needs actual Workspace inspection, creation, editing, building, testing, running, or stopping, call open_core_tools with useWorkspaceTools true.",
                "If a truthful answer depends on present or recently changing external facts, including current weather, call open_core_tools with useWebTools true.",
                "If the request needs recall or listing of learned long-term memories, call open_core_tools with useMemoryTools true.",
                "For Workspace work, set requireWorkspaceMutation for creation, editing, repair, or formatting; requireBuild for compile, build, test, run, or verification of created or repaired code; and requireRun for launch, run, open, or show requests.",
                "Set requireWorkspaceMutation true whenever the requested outcome includes implementing, adding, removing, repairing, or changing source behavior. A project compiling successfully does not satisfy or erase an explicitly requested source change.",
                "Every selection describes the entire requested outcome, not merely the first action you plan. If a request says inspect, then implement, then build and run, Workspace, mutation, build, and run are all required.",
                "When tools are needed, call open_core_tools exactly once instead of answering or claiming inability."),
            Tools = [classifier],
            ToolMode = ChatToolMode.Auto,
            AllowMultipleToolCalls = false,
            MaxOutputTokens = 256,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [AliInternalModelRoutingProperties.SuppressInjectedPersona] = true,
                [AliInternalModelRoutingProperties.BoundReasoningEffort] = "low"
            }
        };
        var response = await chatClient.GetResponseAsync(
                input.TakeLast(6).ToArray(),
                options,
                cancellationToken)
            .ConfigureAwait(false);
        var call = FindClassifierCall(response, classifier.Name);
        if (call is null)
        {
            var directAnswer = response.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directAnswer))
            {
                return new CoreAssistantCodingRequirements(
                    RequiresWorkspaceWork: false,
                    RequiresWorkspaceMutation: false,
                    RequiresBuild: false,
                    RequiresRun: false,
                    RequiresCurrentWeb: false,
                    RequiresMemoryRecall: false,
                    Basis: "The model answered the ordinary conversation directly without opening a tool drawer.",
                    DirectAnswer: directAnswer);
            }

            options.ToolMode = ChatToolMode.RequireSpecific(classifier.Name);
            options.MaxOutputTokens = 512;
            response = await chatClient.GetResponseAsync(
                    input.TakeLast(6).ToArray(),
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            call = FindClassifierCall(response, classifier.Name);
            if (call is null)
            {
                return new CoreAssistantCodingRequirements(
                    RequiresWorkspaceWork: false,
                    RequiresWorkspaceMutation: false,
                    RequiresBuild: false,
                    RequiresRun: false,
                    RequiresCurrentWeb: false,
                    RequiresMemoryRecall: false,
                    Basis: "The model did not return the required typed tool selection after one bounded retry.",
                    DirectAnswer: "I could not start this request because the selected model did not return the required tool selection, even after one bounded retry. No tool ran and no file changed.");
            }
        }

        return CreateCoreRequirements(
            ReadBooleanArgument(call.Arguments, "useWorkspaceTools"),
            ReadBooleanArgument(call.Arguments, "useWebTools"),
            ReadBooleanArgument(call.Arguments, "useMemoryTools"),
            ReadBooleanArgument(call.Arguments, "requireWorkspaceMutation"),
            ReadBooleanArgument(call.Arguments, "requireBuild"),
            ReadBooleanArgument(call.Arguments, "requireRun"));
    }

    private static FunctionCallContent? FindClassifierCall(ChatResponse response, string classifierName) =>
        response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault(content =>
                !content.InformationalOnly
                && string.Equals(content.Name, classifierName, StringComparison.Ordinal));

    private static CoreAssistantCodingRequirements CreateCoreRequirements(
        bool workspace,
        bool web,
        bool memory,
        bool mutation,
        bool build,
        bool run)
    {
        if (mutation || build || run)
        {
            workspace = true;
        }
        return new CoreAssistantCodingRequirements(
            workspace,
            mutation,
            build,
            run,
            web,
            memory,
            "Model selected explicit core tool drawers and completion requirements.",
            DirectAnswer: string.Empty);
    }

    private static bool ReadBooleanArgument(
        IDictionary<string, object?>? arguments,
        string name) =>
        arguments is not null
        && arguments.TryGetValue(name, out var value)
        && value switch
        {
            bool boolean => boolean,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => false
        };

    private static string ReadStringArgument(
        IDictionary<string, object?>? arguments,
        string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value))
        {
            return string.Empty;
        }

        return value switch
        {
            string text => text.Trim(),
            JsonElement { ValueKind: JsonValueKind.String } element =>
                element.GetString()?.Trim() ?? string.Empty,
            _ => value?.ToString()?.Trim() ?? string.Empty
        };
    }

    private static async Task<CoreAssistantOutcomeVerification> VerifyRequestedBehaviorAsync(
        IChatClient chatClient,
        string originalRequest,
        string exactExecutionEvidence,
        CancellationToken cancellationToken)
    {
        var verifier = AIFunctionFactory.Create(
            (bool requestedBehaviorImplemented, string remainingWork) =>
                new CoreAssistantOutcomeVerification(requestedBehaviorImplemented, remainingWork),
            "confirm_requested_behavior",
            "Confirm whether the exact source mutation evidence implements the requested source behavior. Return false with the concrete remaining work when it does not.");
        var options = new ChatOptions
        {
            Instructions = string.Join(
                Environment.NewLine,
                "Judge only whether the requested source behavior is actually demonstrated by the exact mutation evidence.",
                "A successful build or launch proves mechanics only and never proves that the requested behavior was implemented.",
                "Do not infer changes absent from the evidence. An unrelated, empty, placeholder, or merely compiling file does not satisfy the request.",
                "Call confirm_requested_behavior exactly once. Set requestedBehaviorImplemented true only when the evidence itself shows the requested behavior; otherwise set it false and state the smallest concrete source work still required."),
            Tools = [verifier],
            ToolMode = ChatToolMode.Auto,
            AllowMultipleToolCalls = false,
            MaxOutputTokens = 256,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [AliInternalModelRoutingProperties.SuppressInjectedPersona] = true,
                [AliInternalModelRoutingProperties.BoundReasoningEffort] = "low"
            }
        };
        var response = await chatClient.GetResponseAsync(
                [
                    new MeaiChatMessage(
                        MeaiChatRole.User,
                        "Original request:" + Environment.NewLine + originalRequest
                        + Environment.NewLine + Environment.NewLine
                        + "Exact in-memory execution evidence:" + Environment.NewLine
                        + exactExecutionEvidence)
                ],
                options,
                cancellationToken)
            .ConfigureAwait(false);
        var call = response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault(content =>
                !content.InformationalOnly
                && string.Equals(content.Name, verifier.Name, StringComparison.Ordinal));
        if (call is null)
        {
            return new CoreAssistantOutcomeVerification(
                Implemented: false,
                RemainingWork: "The semantic completion check returned no typed decision. Re-read the affected source and perform a concrete targeted edit that visibly implements the requested behavior.");
        }

        return new CoreAssistantOutcomeVerification(
            ReadBooleanArgument(call.Arguments, "requestedBehaviorImplemented"),
            ReadStringArgument(call.Arguments, "remainingWork"));
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
            return await RunCoreAssistantAsync(
                    turn,
                    userText,
                    history,
                    attachments,
                    publishFinal,
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

    private async Task<AgentHarnessRunResult> RunCoreAssistantAsync(
        CoordinatorTurnContext turn,
        string userText,
        IReadOnlyList<RuntimeChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        Func<FinalAnswerPublication, CancellationToken,
            ValueTask<FinalAnswerPublicationAcknowledgment>> publishFinal,
        CancellationToken cancellationToken)
    {
        var input = BuildInitialInput(history, userText, attachments).ToList();
        var dispatch = CaptureBoundModelDispatch();

        // No upfront classifier call here. One used to run before every single
        // turn -- chat included -- purely to decide which tool drawers to
        // open, costing a full extra model round-trip every time for a
        // marginal prompt-size saving. Offering a tool the model doesn't need
        // costs essentially nothing: a well-behaved model simply doesn't call
        // it. So every category is offered unconditionally, and the only real
        // per-turn decision left -- Serena vs. Ali's native file tools -- is a
        // free, instant, in-memory check below, not a model call.
        var selectedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            AliCapabilityCatalog.SearchCurrentWebName,
            AliCapabilityCatalog.RecallUserMemoryName,
            AliCapabilityCatalog.ListCurrentUserMemoriesName,
            AliCapabilityCatalog.MutateParticipantMemoryName
        };
        var serenaTools = _serenaCoding?.Tools ?? Array.Empty<AITool>();
        // Serena is preferred when it is actually up -- its tools are more
        // capable and better tested than Ali's own. But coding capability must
        // never drop to zero just because one external process failed to
        // start. Ali's own native file tools are the fallback so a Serena
        // outage is a capability downgrade, not a total coding failure.
        var usingNativeFallback = serenaTools.Count == 0;
        var workspaceTools = usingNativeFallback
            ? _coreFileTools
            : serenaTools;
        var activeTools = _startupAssistantTools
            .Where(tool => tool is AIFunctionDeclaration function
                && selectedNames.Contains(function.Name))
            .Select(tool => tool is CapabilityPermissionProjectionAIFunction permissionProjection
                ? (AITool)permissionProjection.ProjectWithoutApproval()
                : tool)
            .Concat(workspaceTools)
            .ToArray();
        if (usingNativeFallback && _serenaCoding is not null)
        {
            turn.Report(
                AgentActivityKind.Warning,
                "Serena unavailable, using native file tools",
                _serenaCoding.Status.Detail);
        }
        var agent = CreateAgent(
            dispatch.ChatClient,
            dispatch.Profile,
            activeTools,
            _orchestrationSettings,
            _capabilityEnforcementProvider,
            coreAssistantPath: true,
            boundReasoningEffort: dispatch.GenerationSettingsBinding.ReasoningEffort);

        using var coreExecutionScope = AliCoreAssistantExecutionContext.Enter();
        return await new AliMinimumMessage()
            .RunAsync(turn, agent, input, publishFinal, cancellationToken)
            .ConfigureAwait(false);
    }

#if false
    // DISABLED LEGACY CORE PATH
    // Recovery decisions, receipt tracking, completion critics, shadow evidence,
    // semantic reloads, focused-repair flags, and durable pause behavior are not
    // compiled into Ali's active message route. Restore only one isolated feature
    // at a time after AliMinimumMessage passes live speed and reliability tests.
    private async Task<AgentHarnessRunResult> RunDisabledLegacyCoreAssistantAsync(
        CoordinatorTurnContext turn,
        string userText,
        IReadOnlyList<RuntimeChatMessage> history,
        IReadOnlyList<ChatAttachment> attachments,
        Func<FinalAnswerPublication, CancellationToken,
            ValueTask<FinalAnswerPublicationAcknowledgment>> publishFinal,
        CancellationToken cancellationToken)
    {
        var input = BuildInitialInput(history, userText, attachments).ToList();
        var dispatch = CaptureBoundModelDispatch();
        var requirements = await ClassifyCoreCodingRequirementsAsync(
                dispatch.ChatClient,
                input,
                cancellationToken)
            .ConfigureAwait(false);
        if (!requirements.RequiresWorkspaceWork
            && !requirements.RequiresWorkspaceMutation
            && !requirements.RequiresBuild
            && !requirements.RequiresRun
            && !requirements.RequiresCurrentWeb
            && !requirements.RequiresMemoryRecall
            && !string.IsNullOrWhiteSpace(requirements.DirectAnswer))
        {
            var directAnswer = FinalAnswerRenderer.Compose(
                requirements.DirectAnswer.Trim(),
                turn.WebSources);
            var directPublication = new FinalAnswerPublication(
                turn.ConversationId,
                turn.UserMessageId,
                turn.AssistantMessageId,
                "publication_" + turn.AssistantMessageId,
                directAnswer,
                TurnStateIntegrity.Digest(directAnswer),
                EvidenceStatus.Unverified,
                ChatFinishReason.Stop.ToString());
            var directAcknowledgment = await publishFinal(
                    directPublication,
                    cancellationToken)
                .ConfigureAwait(false);
            FinalAnswerPublicationBoundary.RequireExactInMemoryAcknowledgment(
                directPublication,
                directAcknowledgment);
            return new AgentHarnessRunResult(
                WroteAnswer: true,
                FinishReason: ChatFinishReason.Stop.ToString(),
                Paused: false,
                ResumeIdentity: null,
                CompletedSuccessfully: true);
        }
        var selectedNames = new HashSet<string>(StringComparer.Ordinal);
        if (requirements.RequiresWorkspaceWork)
        {
            selectedNames.UnionWith(MinimumCSharpToolNames);
        }
        if (requirements.RequiresCurrentWeb)
        {
            selectedNames.Add(AliCapabilityCatalog.SearchCurrentWebName);
        }
        if (requirements.RequiresMemoryRecall)
        {
            selectedNames.Add(AliCapabilityCatalog.RecallUserMemoryName);
            selectedNames.Add(AliCapabilityCatalog.ListCurrentUserMemoriesName);
        }
        var completionGate = new CoreAssistantCompletionGate();
        completionGate.Require(requirements);

        var enableWorkspaceFiles = selectedNames.Overlaps(MinimumCSharpToolNames);
        var activeTools = _startupAssistantTools
            .Where(tool => tool is AIFunctionDeclaration function
                && selectedNames.Contains(function.Name))
            .Select(tool => tool is CapabilityPermissionProjectionAIFunction permissionProjection
                ? (AITool)permissionProjection.ProjectWithoutApproval()
                : tool)
            .Concat(enableWorkspaceFiles ? _coreFileTools : [])
            .ToArray();
        turn.Report(
            AgentActivityKind.Status,
            "Core tools ready",
            requirements.Basis
            + " Loaded: "
            + (activeTools.Length == 0
                ? "none"
                : string.Join(", ", activeTools
                    .OfType<AIFunctionDeclaration>()
                    .Select(tool => tool.Name))));
        var activeAgent = CreateAgent(
            dispatch.ChatClient,
            dispatch.Profile,
            activeTools,
            _orchestrationSettings,
            capabilityEnforcementProvider: null,
            coreAssistantPath: true,
            boundReasoningEffort: dispatch.GenerationSettingsBinding.ReasoningEffort);
        var session = await activeAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        string? finishReason = null;
        var renderedAnswer = new StringBuilder();
        var pendingShadowCalls = new PendingShadowCallTracker();
        var pendingStandingPermissions = new PendingStandingPermissionTracker();
        string? lastBlockedFingerprint = null;
        long lastBlockedSourceRevision = -1;
        var finalWorkspaceReportRequested = false;
        long behaviorVerifiedRevision = -1;
        long lastRejectedBehaviorRevision = -1;
        var behaviorRepairFocused = false;
        var behaviorFullRewriteFocused = false;
        string? requiredFocusedToolName = null;
        var completedSuccessfully = true;
        using var coreExecutionScope = AliCoreAssistantExecutionContext.Enter();

        try
        {
            while (true)
            {
                ToolApprovalRequestContent? approvalRequest = null;
                using var focusedTools = CoreAssistantContextCompactingChatClient.FocusTools(
                    behaviorFullRewriteFocused
                        ? CoreBehaviorRewriteToolNames
                        : behaviorRepairFocused
                            ? CoreBehaviorRepairToolNames
                            : null,
                    requiredFocusedToolName);
                requiredFocusedToolName = null;
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
                                completionGate.Track(functionCall);
                                TrackPendingShadowCall(turn, pendingShadowCalls, functionCall);
                                var displayName = ResolveUserFacingToolName(
                                    activeTools,
                                    functionCall.Name);
                                turn.Report(
                                    AgentActivityKind.ToolCall,
                                    $"Using {displayName}",
                                    $"Ali selected {displayName}.");
                                break;
                            case FunctionResultContent functionResult:
                                completionGate.Observe(functionResult);
                                CompleteStandingPermission(
                                    turn,
                                    pendingStandingPermissions,
                                    functionResult);
                                TryObserveFrameworkResult(
                                    _shadowObserver,
                                    turn,
                                    pendingShadowCalls,
                                    functionResult);
                                if (functionResult.Exception is not null)
                                {
                                    turn.Report(
                                        AgentActivityKind.Error,
                                        "Tool failed; Ali is repairing the exact error",
                                        functionResult.Exception.GetBaseException().Message);
                                }
                                else if (ShouldReportGenericReturnedResult(functionResult))
                                {
                                    turn.Report(
                                        AgentActivityKind.ToolResult,
                                        "Tool returned",
                                        "Ali is evaluating the returned evidence.");
                                }
                                break;
                            case TextContent textContent when textContent.Text is { Length: > 0 }:
                                renderedAnswer.Append(textContent.Text);
                                break;
                        }
                    }
                }

                if (approvalRequest is null)
                {
                    if (completionGate.RequiresRequestedBehaviorVerification
                        && behaviorVerifiedRevision != completionGate.SourceRevision)
                    {
                        var behaviorCheck = await VerifyRequestedBehaviorAsync(
                                dispatch.ChatClient,
                                userText,
                                completionGate.BuildVerifiedCompletionEvidence(),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!behaviorCheck.Implemented)
                        {
                            renderedAnswer.Clear();
                            finalWorkspaceReportRequested = false;
                            var repeatedWithoutMutation =
                                lastRejectedBehaviorRevision == completionGate.SourceRevision;
                            if (repeatedWithoutMutation)
                            {
                                behaviorFullRewriteFocused = true;
                                turn.Report(
                                    AgentActivityKind.Warning,
                                    "Changing the Workspace repair approach",
                                    "The targeted edit returned without changing the source. Ali is continuing with an exact read and complete-file write instead of ending the turn.");
                            }
                            else
                            {
                                behaviorFullRewriteFocused = false;
                            }
                            lastRejectedBehaviorRevision = completionGate.SourceRevision;
                            behaviorRepairFocused = true;
                            turn.Report(
                                AgentActivityKind.Warning,
                                "Requested behavior is not implemented yet",
                                string.IsNullOrWhiteSpace(behaviorCheck.RemainingWork)
                                    ? "The exact source-edit evidence does not yet demonstrate the requested behavior."
                                    : behaviorCheck.RemainingWork);
                            input =
                            [
                                new MeaiChatMessage(
                                    MeaiChatRole.User,
                                    "The independent semantic completion check rejected the current implementation. "
                                    + (string.IsNullOrWhiteSpace(behaviorCheck.RemainingWork)
                                        ? "The exact source-edit evidence does not demonstrate the requested behavior."
                                        : behaviorCheck.RemainingWork)
                                    + " Do not build or answer yet. Re-read the exact relevant source. "
                                    + (repeatedWithoutMutation
                                        ? "The targeted edit returned without changing the source. Read the exact file once if needed, then use file_access_write to replace the complete relevant source file with the finished implementation. Do not describe or recheck the same unchanged state. "
                                        : "Make a targeted source change that implements the original request. ")
                                    + "Finish the requested multi-file implementation before rebuilding and running the final project as requested.")
                            ];
                            continue;
                        }

                        behaviorVerifiedRevision = completionGate.SourceRevision;
                        behaviorRepairFocused = false;
                        behaviorFullRewriteFocused = false;
                    }

                    if (completionGate.TryGetBlocker(out var blocker))
                    {
                        renderedAnswer.Clear();
                        var startFocusedMutationRepair =
                            requirements.RequiresWorkspaceMutation
                            && completionGate.SourceRevision == 0
                            && !behaviorRepairFocused;
                        var repeatedBlocker = string.Equals(
                                                  lastBlockedFingerprint,
                                                  blocker.Fingerprint,
                                                  StringComparison.Ordinal)
                                              && lastBlockedSourceRevision == completionGate.SourceRevision;
                        if (repeatedBlocker)
                        {
                            if (!startFocusedMutationRepair)
                            {
                                behaviorRepairFocused |= requirements.RequiresWorkspaceMutation;
                                behaviorFullRewriteFocused = requirements.RequiresWorkspaceMutation;
                                turn.Report(
                                    AgentActivityKind.Warning,
                                    "Changing the Workspace repair approach",
                                    requirements.RequiresWorkspaceMutation
                                        ? "The previous pass returned the same blocker without changing the source. Ali is continuing with an exact read and complete-file write instead of ending the turn."
                                        : "The previous pass returned the same blocker. Ali is retaining the evidence, changing the next action, and continuing instead of ending the turn.");
                            }
                            else
                            {
                                turn.Report(
                                    AgentActivityKind.Warning,
                                    "Focusing on the required source change",
                                    "Ali is retaining the exact request and current source evidence while narrowing the next pass to read, mutation, Roslyn, build, test, and run tools.");
                            }
                        }
                        else
                        {
                            behaviorFullRewriteFocused = false;
                        }

                        behaviorRepairFocused |= startFocusedMutationRepair;
                        requiredFocusedToolName = behaviorFullRewriteFocused
                            ? AliCapabilityCatalog.FileWriteName
                            : RequiredCoreToolFor(blocker);
                        lastBlockedFingerprint = blocker.Fingerprint;
                        lastBlockedSourceRevision = completionGate.SourceRevision;
                        turn.Report(
                            AgentActivityKind.Warning,
                            "Continuing unfinished Workspace work",
                            blocker.ContinuationInstruction);
                        input =
                        [
                            new MeaiChatMessage(
                                MeaiChatRole.User,
                                behaviorFullRewriteFocused
                                    ? blocker.ContinuationInstruction
                                      + " The previous targeted mutation returned without changing the source. Read the exact relevant file once if needed, then use file_access_write to replace the complete relevant source file with the corrected implementation. Keep working through a successful current build and launch; do not end the turn merely because the blocker repeated."
                                    : blocker.ContinuationInstruction)
                        ];
                        continue;
                    }

                    if (requirements.RequiresWorkspaceWork
                        && !finalWorkspaceReportRequested)
                    {
                        finalWorkspaceReportRequested = true;
                        renderedAnswer.Clear();
                        input =
                        [
                            new MeaiChatMessage(
                                MeaiChatRole.User,
                                "All mechanically required Workspace operations now have successful current tool evidence. "
                                + "Before finishing, answer the original request completely and truthfully. State the exact source behavior changed, the exact build result, and the exact run result when each was requested. "
                                + "Do not report only the last tool call and do not claim any outcome absent from returned tool evidence. Original request: "
                                + userText
                                + Environment.NewLine
                                + "Authoritative in-memory execution evidence:"
                                + Environment.NewLine
                                + completionGate.BuildVerifiedCompletionEvidence())
                        ];
                        continue;
                    }

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

            var modelAnswer = renderedAnswer.ToString();
            if (string.IsNullOrWhiteSpace(modelAnswer))
            {
                throw new InvalidOperationException(
                    "The model returned no answer and no further tool action.");
            }
            if (requirements.RequiresWorkspaceWork)
            {
                modelAnswer = modelAnswer.TrimEnd()
                    + Environment.NewLine
                    + Environment.NewLine
                    + "Verified execution evidence:"
                    + Environment.NewLine
                    + completionGate.BuildVerifiedCompletionEvidence();
            }
            var exactAnswer = FinalAnswerRenderer.Compose(
                modelAnswer,
                turn.WebSources);

            finishReason = ChatFinishReason.Stop.ToString();
            var publication = new FinalAnswerPublication(
                turn.ConversationId,
                turn.UserMessageId,
                turn.AssistantMessageId,
                "publication_" + turn.AssistantMessageId,
                exactAnswer,
                TurnStateIntegrity.Digest(exactAnswer),
                completedSuccessfully && turn.UsedEvidenceTool
                    ? EvidenceStatus.Verified
                    : EvidenceStatus.Unverified,
                finishReason);
            var acknowledgment = await publishFinal(publication, cancellationToken)
                .ConfigureAwait(false);
            FinalAnswerPublicationBoundary.RequireExactInMemoryAcknowledgment(
                publication,
                acknowledgment);

            return new AgentHarnessRunResult(
                WroteAnswer: true,
                FinishReason: finishReason,
                Paused: false,
                ResumeIdentity: null,
                CompletedSuccessfully: completedSuccessfully);
        }
        finally
        {
            pendingStandingPermissions.Clear();
        }
    }

#endif

    private static string? RequiredCoreToolFor(CoreAssistantCompletionBlocker blocker) =>
        blocker.Code switch
        {
            "workspace-mutation-not-started" =>
                AliCapabilityCatalog.FileReplaceLinesName,
            "workspace-mutation-failed" =>
                AliCapabilityCatalog.FileWriteName,
            "build-missing-or-stale" or "required-build-missing" =>
                AliCapabilityCatalog.DotNetBuildName,
            "run-missing-or-stale" or "required-run-missing" =>
                AliCapabilityCatalog.DotNetRunName,
            _ => null
        };

    internal static IReadOnlySet<string> ResolveCoreToolNames(
        SemanticToolSelection semanticSelection,
        out bool recoverySuiteLoaded)
    {
        ArgumentNullException.ThrowIfNull(semanticSelection);
        var selectedNames = semanticSelection.Tools
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        recoverySuiteLoaded = semanticSelection.RequiresAttention
            || selectedNames.Count == 0;
        if (selectedNames.Overlaps(MinimumCSharpToolNames))
        {
            selectedNames.UnionWith(MinimumCSharpToolNames);
        }
        if (recoverySuiteLoaded)
        {
            selectedNames.UnionWith(CoreRecoveryToolNames);
        }

        return selectedNames;
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
        // External MCP discovery is optional and must never delay the core assistant path.
        // It will return as an explicitly activated background feature after the core gate passes.
        await using var mcpSession = McpToolSession.Empty(
            "core-assistant-mcp-bypassed-v1",
            static () => "core-assistant-mcp-bypassed-v1");

        var orchestrationSettings = _orchestrationSettings;
        IReadOnlyList<AITool> activeTools = _startupPolicyTools;
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
            completionCritic: null);
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

internal readonly record struct CoreAssistantOutcomeVerification(
    bool Implemented,
    string RemainingWork);

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
    bool StructuredRecoveryRequired = false,
    bool CompletedSuccessfully = true);

internal sealed record AliHarnessResumeRequest(
    TurnIdentity DurableIdentity,
    string SourceMessageId,
    string SteeringText);

internal sealed record AliHarnessStructuredRecoveryRequest(
    AgentRecoveryDecision Decision);
