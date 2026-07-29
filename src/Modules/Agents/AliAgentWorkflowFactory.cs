using Ali.Modules.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
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
    Func<CoordinatorTurnContext?> turnAccessor)
{
    internal const int ProgrammingMaximumTurns = 4;

    public IReadOnlyList<AITool> CreateTools(AliSpecialistTeam team)
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
            executionEnvironment: InProcessExecution.Lockstep,
            includeExceptionDetails: false,
            includeWorkflowOutputsInResponse: true);
        hosted = AliAgentFrameworkMiddleware.WithVisibleLifecycle(hosted, turnAccessor, role);
        return hosted.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = toolName,
            Description = description
        });
    }
}
