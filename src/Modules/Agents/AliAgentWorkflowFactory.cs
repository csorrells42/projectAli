using Ali.Modules.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Hosts official Agent Framework workflows as synchronous tools for Ali.
/// Every workflow uses the lockstep in-process environment; no concurrent or
/// background execution path is registered.
/// </summary>
internal sealed class AliAgentWorkflowFactory : IDisposable
{
    internal const int ProgrammingMaximumTurns = 4;
    internal const int MaximumWorkflowAdvisoryCharacters = 2200;
    internal const string ProgrammingReviewerAgentId = "ali-programming-reviewer";
    internal const string MagenticManagerAgentId = "ali-magentic-manager";
    private const string ResearchArtifactKind = "research-artifact";
    private const string ProgrammingReviewKind = "programming-review";
    private const string MagenticKind = "magentic";

    private readonly IChatClient _chatClient;
    private readonly ILocalModelRuntime _runtime;
    private readonly Func<CoordinatorTurnContext?> _turnAccessor;
    private readonly FileSystemJsonCheckpointStore _checkpointStore;
    private readonly CheckpointManager _checkpointManager;
    private readonly IWorkflowExecutionEnvironment _executionEnvironment;
    private readonly AliWorkflowRecoveryCatalog _recoveryCatalog;
    private readonly Dictionary<string, AliWorkflowRegistration> _workflows = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _resumeGate = new(1, 1);

    public AliAgentWorkflowFactory(
        IChatClient chatClient,
        ILocalModelRuntime runtime,
        Func<CoordinatorTurnContext?> turnAccessor,
        string checkpointPath)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(turnAccessor);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointPath);
        _chatClient = chatClient;
        _runtime = runtime;
        _turnAccessor = turnAccessor;
        var fullPath = Path.GetFullPath(checkpointPath);
        _checkpointStore = new FileSystemJsonCheckpointStore(Directory.CreateDirectory(fullPath));
        _checkpointManager = CheckpointManager.CreateJson(_checkpointStore);
        _executionEnvironment = InProcessExecution.Lockstep.WithCheckpointing(_checkpointManager);
        _recoveryCatalog = new AliWorkflowRecoveryCatalog(fullPath);
    }

    public IReadOnlyList<AITool> CreateStandardTools(AliSpecialistTeam team)
    {
        ArgumentNullException.ThrowIfNull(team);
        var researchArtifact = CreateResearchArtifactWorkflow(team);
        var programmingReview = CreateProgrammingGroupChat(team);
        Register(new AliWorkflowRegistration(
            ResearchArtifactKind,
            "Research to Artifact Workflow",
            researchArtifact,
            AliSpecialistAgentFactory.ResearcherAgentId,
            [AliSpecialistAgentFactory.ResearcherAgentId, AliSpecialistAgentFactory.OfficeArtifactAgentId]));
        Register(new AliWorkflowRegistration(
            ProgrammingReviewKind,
            "Programming Maker Checker Workflow",
            programmingReview,
            "GroupChatHost",
            ["GroupChatHost", AliSpecialistAgentFactory.SoftwareEngineerAgentId, ProgrammingReviewerAgentId]));
        return
        [
            HostAsTool(
                researchArtifact,
                AliCapabilityCatalog.RunResearchArtifactWorkflowName,
                "Research to Artifact Workflow",
                "Run a synchronous two-stage workflow where the private Researcher gathers evidence and the Office/Artifact specialist turns it into a polished deliverable draft. Use only when both stages are genuinely required; Ali performs approved file creation afterward."),
            HostAsTool(
                programmingReview,
                AliCapabilityCatalog.RunProgrammingGroupChatName,
                "Programming Maker Checker Workflow",
                "Run a bounded synchronous programming maker/checker conversation for substantial software creation, repair, architecture, or delivery work. Pass the complete objective, exact project path, constraints, and evidence already gathered. This workflow is advisory only: it cannot replace Ali's direct edit, build, test, run, or delivery tools. It uses at most four agent turns and returns reviewed guidance to Ali, who must perform approvals and every requested action afterward."),
            AIFunctionFactory.Create(
                (Func<AliRecoverableWorkflowReport>)ListRecoverableWorkflows,
                AliCapabilityCatalog.ListRecoverableWorkflowsName,
                "List interrupted Agent Framework workflows that have a compatible durable checkpoint. This is read-only. Call it when the user asks whether prior work can be recovered."),
            AIFunctionFactory.Create(
                (Func<string, CancellationToken, Task<AliWorkflowResumeResult>>)ResumeWorkflowAsync,
                AliCapabilityCatalog.ResumeWorkflowCheckpointName,
                "Resume one interrupted Agent Framework workflow from its latest compatible local checkpoint. Call only after the user explicitly asks to resume or continue that saved workflow. Pass the exact sessionId returned by list_recoverable_workflows.")
        ];
    }

    public AIFunction CreateMagenticTool(
        AliSpecialistTeam team,
        AgentOrchestrationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(team);
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        var workflow = AgentWorkflowBuilder
            .CreateMagenticBuilderWith(CreateMagenticManager())
            .AddParticipants(
            [
                team.Get(AliCapabilityCatalog.ConsultSoftwareEngineerName),
                team.Get(AliCapabilityCatalog.ConsultResearcherName),
                team.Get(AliCapabilityCatalog.ConsultOfficeSpecialistName)
            ])
            .WithMaxRounds(normalized.MagenticMaximumRounds)
            .WithMaxResets(1)
            .WithMaxStalls(2)
            .RequirePlanSignoff(false)
            .Build();
        Register(new AliWorkflowRegistration(
            MagenticKind,
            "Magentic Orchestration",
            workflow,
            null,
            [MagenticManagerAgentId]));
        return (AIFunction)HostAsTool(
            workflow,
            AliCapabilityCatalog.RunMagenticOrchestrationName,
            "Magentic Orchestration",
            $"Run bounded synchronous Magentic orchestration for an open-ended multi-domain objective that cannot be handled by one specialist or an established workflow. Maximum coordination rounds: {normalized.MagenticMaximumRounds}. Never use for greetings, factual questions, memory recall, ordinary search, one file edit, or routine build/test work.");
    }

    internal static Workflow CreateResearchArtifactWorkflow(AliSpecialistTeam team) =>
        AgentWorkflowBuilder.BuildSequential(
            "ali-research-to-artifact",
            chainOnlyAgentResponses: true,
            [
                team.Get(AliCapabilityCatalog.ConsultResearcherName),
                team.Get(AliCapabilityCatalog.ConsultOfficeSpecialistName)
            ]);

    private Workflow CreateProgrammingGroupChat(AliSpecialistTeam team)
    {
        var reviewer = CreateProgrammingReviewer();
        return AgentWorkflowBuilder
            .CreateGroupChatBuilderWith(agents =>
                new RoundRobinGroupChatManager(agents)
                {
                    MaximumIterationCount = ProgrammingMaximumTurns
                })
            .AddParticipants(
            [
                team.Get(AliCapabilityCatalog.ConsultSoftwareEngineerName),
                reviewer
            ])
            .Build();
    }

    private AIAgent CreateProgrammingReviewer()
    {
        var profile = _runtime.ActiveProfile;
        AIAgent reviewer = _chatClient.AsHarnessAgent(new HarnessAgentOptions
        {
            Id = ProgrammingReviewerAgentId,
            Name = "ProgrammingReviewer",
            Description = "Private programming checker for Ali's bounded maker/checker workflow.",
            MaximumIterationsPerRequest = 2,
            MaxContextWindowTokens = profile.ContextTokens,
            MaxOutputTokens = Math.Min(profile.OutputTokenLimit, 4096),
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableTodoProvider = true,
            DisableAgentSkillsProvider = false,
            AgentSkillsSource = new AgentFileSkillsSource(Path.Combine(AppContext.BaseDirectory, "skills")),
            DisableOpenTelemetry = false,
            OpenTelemetrySourceName = "ProjectAli.AgentFramework.Workflows",
            ChatOptions = new ChatOptions
            {
                Instructions = "You are Ali's private programming checker. Review the maker's proposed solution against the user's complete objective. Identify correctness, security, permission, build, test, runtime, and delivery gaps. On the final turn, return a compact acceptance checklist and clearly distinguish verified evidence from unperformed work. Never address the user or claim that a file, build, test, or run exists without tool evidence.",
                Tools = [],
                ToolMode = ChatToolMode.None,
                AllowMultipleToolCalls = false,
                MaxOutputTokens = Math.Min(profile.OutputTokenLimit, 4096)
            }
        });
        return AliAgentFrameworkMiddleware.WithVisibleLifecycle(reviewer, _turnAccessor, "Programming Reviewer");
    }

    private AIAgent CreateMagenticManager()
    {
        var profile = _runtime.ActiveProfile;
        AIAgent manager = _chatClient.AsHarnessAgent(new HarnessAgentOptions
        {
            Id = MagenticManagerAgentId,
            Name = "MagenticManager",
            Description = "Private bounded manager for Ali's multi-domain Magentic orchestration.",
            MaximumIterationsPerRequest = 2,
            MaxContextWindowTokens = profile.ContextTokens,
            MaxOutputTokens = Math.Min(profile.OutputTokenLimit, 4096),
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableTodoProvider = true,
            DisableAgentSkillsProvider = true,
            DisableOpenTelemetry = false,
            OpenTelemetrySourceName = "ProjectAli.AgentFramework.Magentic",
            ChatOptions = new ChatOptions
            {
                Instructions = "Coordinate Ali's private specialists for one open-ended, multi-domain objective. Create the smallest useful plan, select one specialist at a time, monitor concrete progress, replan only when evidence requires it, and stop when a sufficient result is ready for Ali. Never perform user-facing conversation, claim unverified actions, or expand the objective beyond the request.",
                Tools = [],
                ToolMode = ChatToolMode.None,
                AllowMultipleToolCalls = false,
                MaxOutputTokens = Math.Min(profile.OutputTokenLimit, 4096)
            }
        });
        return AliAgentFrameworkMiddleware.WithVisibleLifecycle(manager, _turnAccessor, "Magentic Manager");
    }

    private AITool HostAsTool(
        Workflow workflow,
        string toolName,
        string role,
        string description)
    {
        AIAgent hosted = workflow.AsAIAgent(
            id: toolName,
            name: role.Replace(" ", string.Empty, StringComparison.Ordinal),
            description: description,
            executionEnvironment: _executionEnvironment,
            includeExceptionDetails: false,
            includeWorkflowOutputsInResponse: true);
        hosted = AliAgentFrameworkMiddleware.WithVisibleLifecycle(hosted, _turnAccessor, role);
        var hostedFunction = (AIFunction)hosted.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = toolName,
            Description = description
        });
        return AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<string>>)InvokeCompactAsync,
            toolName,
            description);

        async Task<string> InvokeCompactAsync(string query, CancellationToken cancellationToken)
        {
            var result = await hostedFunction.InvokeAsync(
                new AIFunctionArguments { ["query"] = query },
                cancellationToken).ConfigureAwait(false);
            return CompactWorkflowAdvisory(result?.ToString());
        }
    }

    internal static string CompactWorkflowAdvisory(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length <= MaximumWorkflowAdvisoryCharacters)
        {
            return normalized;
        }

        const string marker = "\n\n... private workflow transcript compacted; full lifecycle remains in Ali Activity and durable checkpoints ...\n\n";
        var remaining = MaximumWorkflowAdvisoryCharacters - marker.Length;
        var headLength = remaining / 3;
        return normalized[..headLength] + marker + normalized[^(remaining - headLength)..];
    }

    public AliRecoverableWorkflowReport ListRecoverableWorkflows() =>
        _recoveryCatalog.Inspect(_workflows.Values.ToArray());

    public async Task<AliWorkflowResumeResult> ResumeWorkflowAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await _resumeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var report = ListRecoverableWorkflows();
            var recoverable = report.Workflows.FirstOrDefault(item =>
                string.Equals(item.SessionId, sessionId.Trim(), StringComparison.Ordinal));
            if (recoverable is null)
            {
                return new AliWorkflowResumeResult(
                    false,
                    "No compatible interrupted workflow has that session ID. List recoverable workflows again and use an exact returned ID.",
                    sessionId.Trim(),
                    "Unknown workflow",
                    string.Empty);
            }

            if (!_workflows.TryGetValue(recoverable.WorkflowKind, out var registration))
            {
                return new AliWorkflowResumeResult(
                    false,
                    "The checkpoint is intact, but this build does not have its workflow graph enabled.",
                    recoverable.SessionId,
                    recoverable.WorkflowName,
                    string.Empty);
            }

            _turnAccessor()?.Report(
                AgentActivityKind.Status,
                "Resuming interrupted workflow",
                $"{recoverable.WorkflowName} is continuing from durable step {recoverable.CompletedStep}.");
            var checkpoint = new CheckpointInfo(recoverable.SessionId, recoverable.CheckpointId);
            await using var run = await _executionEnvironment
                .ResumeAsync(registration.Workflow, checkpoint, cancellationToken)
                .ConfigureAwait(false);
            var output = RenderOutputs(run.NewEvents);
            var remaining = ListRecoverableWorkflows().Workflows.Any(item =>
                string.Equals(item.SessionId, recoverable.SessionId, StringComparison.Ordinal));
            var summary = remaining
                ? $"{recoverable.WorkflowName} resumed and advanced, then paused again with a newer recoverable checkpoint."
                : $"{recoverable.WorkflowName} resumed from its durable checkpoint and completed.";
            _turnAccessor()?.Report(
                remaining ? AgentActivityKind.Warning : AgentActivityKind.ToolResult,
                remaining ? "Workflow paused with recovery preserved" : "Recovered workflow completed",
                summary);
            return new AliWorkflowResumeResult(
                !remaining,
                summary,
                recoverable.SessionId,
                recoverable.WorkflowName,
                output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _turnAccessor()?.Report(
                AgentActivityKind.Error,
                "Workflow recovery failed safely",
                ex.Message);
            return new AliWorkflowResumeResult(
                false,
                "The checkpoint was left intact because recovery failed safely: " + ex.Message,
                sessionId.Trim(),
                "Interrupted workflow",
                string.Empty);
        }
        finally
        {
            _resumeGate.Release();
        }
    }

    private void Register(AliWorkflowRegistration registration) =>
        _workflows[registration.Kind] = registration;

    private static string RenderOutputs(IEnumerable<WorkflowEvent> events)
    {
        var outputs = events
            .OfType<WorkflowOutputEvent>()
            .Where(item => !item.IsIntermediate())
            .Select(item => item.Data switch
            {
                AgentResponse response => response.Text,
                Microsoft.Extensions.AI.ChatMessage message => message.Text,
                _ => item.Data?.ToString()
            })
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        return outputs.Length == 0
            ? "The workflow produced no terminal text; inspect Ali Activity for its final state."
            : string.Join(Environment.NewLine + Environment.NewLine, outputs);
    }

    public void Dispose()
    {
        _resumeGate.Dispose();
        _checkpointStore.Dispose();
    }
}
