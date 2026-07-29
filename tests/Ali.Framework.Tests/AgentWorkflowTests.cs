using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.Runtime;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests;

public sealed class AgentWorkflowTests
{
    [Fact]
    public void SpecialistWorkflowAgentIds_AreStableAcrossRecreatedFactories()
    {
        var client = new CountingChatClient();
        var runtime = new DevelopmentLocalModelRuntime();
        var first = new AliSpecialistAgentFactory(client, runtime, () => null).CreateTeam([]);
        var second = new AliSpecialistAgentFactory(client, runtime, () => null).CreateTeam([]);

        Assert.Equal(
            AliSpecialistAgentFactory.SoftwareEngineerAgentId,
            first.Get(AliCapabilityCatalog.ConsultSoftwareEngineerName).Id);
        Assert.Equal(
            AliSpecialistAgentFactory.ResearcherAgentId,
            first.Get(AliCapabilityCatalog.ConsultResearcherName).Id);
        Assert.Equal(
            AliSpecialistAgentFactory.OfficeArtifactAgentId,
            first.Get(AliCapabilityCatalog.ConsultOfficeSpecialistName).Id);
        Assert.Equal(
            first.Get(AliCapabilityCatalog.ConsultSoftwareEngineerName).Id,
            second.Get(AliCapabilityCatalog.ConsultSoftwareEngineerName).Id);
    }

    [Fact]
    public void Catalog_RegistersOfficialSequentialAndGroupChatWorkflows()
    {
        var workflows = AliCapabilityCatalog.Tools
            .Where(item => item.Source == "Microsoft Agent Framework workflow")
            .ToArray();

        Assert.Equal(3, workflows.Length);
        Assert.Contains(workflows, item => item.Name == AliCapabilityCatalog.RunResearchArtifactWorkflowName);
        Assert.Contains(workflows, item => item.Name == AliCapabilityCatalog.RunProgrammingGroupChatName);
        Assert.Contains(workflows, item => item.Name == AliCapabilityCatalog.RunMagenticOrchestrationName);
        Assert.Equal(4, AliAgentWorkflowFactory.ProgrammingMaximumTurns);
        Assert.True(AliToolPermissionPolicy.RequiresApproval(
            AliCapabilityCatalog.RunMagenticOrchestrationName));
    }

    [Fact]
    public async Task SequentialWorkflow_RunsResearchThenArtifactSynthesis()
    {
        var client = new CountingChatClient();
        var tools = CreateWorkflowTools(client);
        var sequential = Assert.Single(
            tools.OfType<AIFunction>(),
            item => item.Name == AliCapabilityCatalog.RunResearchArtifactWorkflowName);

        var result = await sequential.InvokeAsync(
            new AIFunctionArguments { ["query"] = "Research a topic and draft a one-page brief." },
            TestContext.Current.CancellationToken);

        Assert.Contains("workflow response", result?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task ProgrammingGroupChat_IsSynchronousAndBoundedToFourTurns()
    {
        var client = new CountingChatClient();
        var tools = CreateWorkflowTools(client);
        var groupChat = Assert.Single(
            tools.OfType<AIFunction>(),
            item => item.Name == AliCapabilityCatalog.RunProgrammingGroupChatName);

        var result = await groupChat.InvokeAsync(
            new AIFunctionArguments { ["query"] = "Review a substantial C# implementation plan." },
            TestContext.Current.CancellationToken);

        Assert.Contains("workflow response", result?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AliAgentWorkflowFactory.ProgrammingMaximumTurns, client.CallCount);
        Assert.Contains("complete objective", groupChat.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("advisory only", groupChat.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("direct edit, build, test, run", groupChat.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MagenticTool_IsConstructedWithConfiguredBound()
    {
        var client = new CountingChatClient();
        var runtime = new DevelopmentLocalModelRuntime();
        var team = new AliSpecialistAgentFactory(client, runtime, () => null).CreateTeam([]);
        var checkpointPath = CreateCheckpointPath();
        var tool = new AliAgentWorkflowFactory(client, runtime, () => null, checkpointPath)
            .CreateMagenticTool(team, new AgentOrchestrationSettings
            {
                MagenticPolicy = MagenticPolicies.Automatic,
                MagenticMaximumRounds = 7
            });

        Assert.Equal(AliCapabilityCatalog.RunMagenticOrchestrationName, tool.Name);
        Assert.Contains("Maximum coordination rounds: 7", tool.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkflowExecution_PersistsDurableCheckpointFiles()
    {
        var client = new CountingChatClient();
        var checkpointPath = CreateCheckpointPath();
        var tools = CreateWorkflowTools(client, checkpointPath);
        var sequential = Assert.Single(
            tools.OfType<AIFunction>(),
            item => item.Name == AliCapabilityCatalog.RunResearchArtifactWorkflowName);

        await sequential.InvokeAsync(
            new AIFunctionArguments { ["query"] = "Research and draft a short checkpoint test." },
            TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(checkpointPath));
        Assert.NotEmpty(Directory.EnumerateFiles(checkpointPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void RecoveryCatalog_OffersOnlyPendingCompatibleWorkflowGraphs()
    {
        var checkpointPath = CreateCheckpointPath();
        Directory.CreateDirectory(checkpointPath);
        WriteCheckpoint(
            checkpointPath,
            "pending-session",
            "pending-checkpoint",
            step: 3,
            hasQueuedWork: true,
            objective: "Finish the recoverable programming review.");
        WriteCheckpoint(
            checkpointPath,
            "complete-session",
            "complete-checkpoint",
            step: 8,
            hasQueuedWork: false,
            objective: "This completed run must not be offered.");

        var client = new CountingChatClient();
        var runtime = new DevelopmentLocalModelRuntime();
        var team = new AliSpecialistAgentFactory(client, runtime, () => null).CreateTeam([]);
        using var factory = new AliAgentWorkflowFactory(client, runtime, () => null, checkpointPath);
        _ = factory.CreateStandardTools(team);

        var report = factory.ListRecoverableWorkflows();

        var recovered = Assert.Single(report.Workflows);
        Assert.Equal("pending-session", recovered.SessionId);
        Assert.Equal("Programming Maker Checker Workflow", recovered.WorkflowName);
        Assert.Equal(3, recovered.CompletedStep);
        Assert.Contains("Finish the recoverable", recovered.Objective, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkflowRecovery_RecreatedFactoryResumesSavedCheckpointAndCompletes()
    {
        var sourcePath = CreateCheckpointPath();
        var sourceClient = new CountingChatClient();
        var runtime = new DevelopmentLocalModelRuntime();
        var sourceTeam = new AliSpecialistAgentFactory(sourceClient, runtime, () => null).CreateTeam([]);
        using (var sourceFactory = new AliAgentWorkflowFactory(sourceClient, runtime, () => null, sourcePath))
        {
            var groupChat = Assert.Single(
                sourceFactory.CreateStandardTools(sourceTeam).OfType<AIFunction>(),
                item => item.Name == AliCapabilityCatalog.RunProgrammingGroupChatName);
            _ = await groupChat.InvokeAsync(
                new AIFunctionArguments { ["query"] = "Recover this maker checker workflow after a restart." },
                TestContext.Current.CancellationToken);
        }

        var indexLines = File.ReadAllLines(Path.Combine(sourcePath, "index.jsonl"));
        Assert.NotEmpty(indexLines);
        using var firstIndex = System.Text.Json.JsonDocument.Parse(indexLines[0]);
        var info = firstIndex.RootElement.GetProperty("checkpointInfo");
        var sessionId = info.GetProperty("sessionId").GetString()!;
        var fileName = firstIndex.RootElement.GetProperty("fileName").GetString()!;
        var recoveryPath = CreateCheckpointPath();
        Directory.CreateDirectory(recoveryPath);
        File.Copy(Path.Combine(sourcePath, fileName), Path.Combine(recoveryPath, fileName));
        File.WriteAllText(Path.Combine(recoveryPath, "index.jsonl"), indexLines[0] + Environment.NewLine);

        var recoveryClient = new CountingChatClient();
        var recoveryTeam = new AliSpecialistAgentFactory(recoveryClient, runtime, () => null).CreateTeam([]);
        using var recoveryFactory = new AliAgentWorkflowFactory(recoveryClient, runtime, () => null, recoveryPath);
        _ = recoveryFactory.CreateStandardTools(recoveryTeam);
        var waiting = Assert.Single(recoveryFactory.ListRecoverableWorkflows().Workflows);
        Assert.Equal(sessionId, waiting.SessionId);

        var result = await recoveryFactory.ResumeWorkflowAsync(
            sessionId,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Contains("completed", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workflow response", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(recoveryClient.CallCount > 0);
        Assert.Empty(recoveryFactory.ListRecoverableWorkflows().Workflows);
    }

    private static IReadOnlyList<AITool> CreateWorkflowTools(
        CountingChatClient client,
        string? checkpointPath = null)
    {
        var runtime = new DevelopmentLocalModelRuntime();
        var team = new AliSpecialistAgentFactory(client, runtime, () => null).CreateTeam([]);
        return new AliAgentWorkflowFactory(client, runtime, () => null, checkpointPath ?? CreateCheckpointPath())
            .CreateStandardTools(team);
    }

    private static string CreateCheckpointPath() => Path.Combine(
        Path.GetTempPath(),
        "AliAgentWorkflowTests",
        Guid.NewGuid().ToString("N"));

    private static void WriteCheckpoint(
        string directory,
        string sessionId,
        string checkpointId,
        int step,
        bool hasQueuedWork,
        string objective)
    {
        var queued = hasQueuedWork
            ? $"\"{AliAgentWorkflowFactory.ProgrammingReviewerAgentId}\":[{{}}]"
            : string.Empty;
        var json = $$"""
        {
          "stepNumber": {{step}},
          "workflow": {
            "startExecutorId": "GroupChatHost",
            "executors": {
              "GroupChatHost": {},
              "{{AliSpecialistAgentFactory.SoftwareEngineerAgentId}}": {},
              "{{AliAgentWorkflowFactory.ProgrammingReviewerAgentId}}": {}
            }
          },
          "runnerData": {
            "queuedMessages": { {{queued}} },
            "outstandingRequests": []
          },
          "stateData": {
            "history": {
              "value": [
                { "role": "user", "contents": [ { "text": {{System.Text.Json.JsonSerializer.Serialize(objective)}} } ] }
              ]
            }
          }
        }
        """;
        File.WriteAllText(
            Path.Combine(directory, $"{sessionId}_{checkpointId}%2Ejson"),
            json);
    }

    private sealed class CountingChatClient : IChatClient
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            return Task.FromResult(new ChatResponse(
                new MeaiChatMessage(MeaiChatRole.Assistant, $"workflow response {call}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(MeaiChatRole.Assistant, $"workflow response {call}");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
