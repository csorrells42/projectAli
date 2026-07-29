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
internal sealed class AliAgentWorkflowFactory(
    IChatClient chatClient,
    ILocalModelRuntime runtime,
    Func<CoordinatorTurnContext?> turnAccessor,
    string checkpointPath)
{
    internal const int ProgrammingMaximumTurns = 4;
    private readonly IWorkflowExecutionEnvironment _executionEnvironment =
        CreateCheckpointEnvironment(checkpointPath);

    public IReadOnlyList<AITool> CreateStandardTools(AliSpecialistTeam team)
    {
        ArgumentNullException.ThrowIfNull(team);
        var researchArtifact = CreateResearchArtifactWorkflow(team);
        var programmingReview = CreateProgrammingGroupChat(team);
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
                "Run a bounded synchronous programming maker/checker conversation for substantial software creation, repair, architecture, or delivery work. It uses at most four agent turns and returns reviewed guidance to Ali, who performs approvals and final actions.")
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
        var profile = runtime.ActiveProfile;
        AIAgent reviewer = chatClient.AsHarnessAgent(new HarnessAgentOptions
        {
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
        return AliAgentFrameworkMiddleware.WithVisibleLifecycle(reviewer, turnAccessor, "Programming Reviewer");
    }

    private AIAgent CreateMagenticManager()
    {
        var profile = runtime.ActiveProfile;
        AIAgent manager = chatClient.AsHarnessAgent(new HarnessAgentOptions
        {
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
        return AliAgentFrameworkMiddleware.WithVisibleLifecycle(manager, turnAccessor, "Magentic Manager");
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
        hosted = AliAgentFrameworkMiddleware.WithVisibleLifecycle(hosted, turnAccessor, role);
        return hosted.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = toolName,
            Description = description
        });
    }

    private static IWorkflowExecutionEnvironment CreateCheckpointEnvironment(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Directory.CreateDirectory(Path.GetFullPath(path));
        var manager = CheckpointManager.CreateJson(new FileSystemJsonCheckpointStore(directory));
        return InProcessExecution.Lockstep.WithCheckpointing(manager);
    }
}
