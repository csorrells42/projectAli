using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.Runtime;
using Ali.Modules.UserMemory;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests;

public sealed class AgentWorkflowTests
{
    private const string UserAId = "workflow-user-a";

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
    public async Task ProgrammingGroupChat_CompactsAdvisoryBeforeReturningToOuterAgent()
    {
        var client = new LargeWorkflowResponseChatClient();
        var tools = CreateWorkflowTools(client);
        var groupChat = Assert.Single(
            tools.OfType<AIFunction>(),
            item => item.Name == AliCapabilityCatalog.RunProgrammingGroupChatName);

        var result = await groupChat.InvokeAsync(
            new AIFunctionArguments { ["query"] = "Design a substantial WPF chess application." },
            TestContext.Current.CancellationToken);
        var json = Assert.IsType<System.Text.Json.JsonElement>(result);
        Assert.Equal(System.Text.Json.JsonValueKind.String, json.ValueKind);
        var text = json.GetString()!;

        Assert.True(text.Length <= AliAgentWorkflowFactory.MaximumWorkflowAdvisoryCharacters);
        Assert.Contains("private workflow transcript compacted", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workflow response 1", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MagenticTool_KeepsStableSchemaDescriptionAcrossConfiguredBounds()
    {
        var client = new CountingChatClient();
        var runtime = new DevelopmentLocalModelRuntime();
        var team = new AliSpecialistAgentFactory(client, runtime, () => null).CreateTeam([]);
        var checkpointPath = CreateCheckpointPath();
        var factory = new AliAgentWorkflowFactory(
            client,
            runtime,
            () => null,
            checkpointPath,
            () => UserSelection(UserAId));
        var first = factory.CreateMagenticTool(team, new AgentOrchestrationSettings
            {
                MagenticPolicy = MagenticPolicies.Automatic,
                MagenticMaximumRounds = 7
            });
        var second = factory.CreateMagenticTool(team, new AgentOrchestrationSettings
        {
            MagenticPolicy = MagenticPolicies.Automatic,
            MagenticMaximumRounds = 9
        });

        Assert.Equal(AliCapabilityCatalog.RunMagenticOrchestrationName, first.Name);
        Assert.Equal(first.Description, second.Description);
        Assert.Contains("configured orchestration settings", first.Description, StringComparison.Ordinal);
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
        var ownerDirectory = UserCheckpointDirectory(checkpointPath, UserAId);
        Assert.DoesNotContain(UserAId, ownerDirectory, StringComparison.OrdinalIgnoreCase);
        var checkpoint = Assert.Single(
            Directory.EnumerateFiles(ownerDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !path.EndsWith("index.jsonl", StringComparison.OrdinalIgnoreCase))
                .Take(1));
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(checkpoint));
        Assert.True(document.RootElement.TryGetProperty(
            AliWorkflowCheckpointOwnership.OwnerPropertyName,
            out _));
    }

    [Fact]
    public void RecoveryCatalog_FreshReadDoesNotCreatePerUserDirectory()
    {
        var checkpointPath = CreateCheckpointPath();
        var client = new CountingChatClient();
        var runtime = new DevelopmentLocalModelRuntime();
        var team = new AliSpecialistAgentFactory(client, runtime, () => null).CreateTeam([]);
        using var factory = new AliAgentWorkflowFactory(
            client,
            runtime,
            () => null,
            checkpointPath,
            () => UserSelection(UserAId));
        _ = factory.CreateStandardTools(team);
        var ownerDirectory = UserCheckpointDirectory(checkpointPath, UserAId);
        Assert.False(Directory.Exists(ownerDirectory));

        var report = factory.ListRecoverableWorkflows();

        Assert.Empty(report.Workflows);
        Assert.False(Directory.Exists(ownerDirectory));
    }

    [Fact]
    public void RecoveryCatalog_OversizedCheckpointIsLeftUntouchedAndReportedAsBounded()
    {
        var checkpointPath = CreateCheckpointPath();
        string oversizedPath;
        using (var ownership = new AliWorkflowCheckpointOwnership(checkpointPath))
        {
            var owner = ownership.CreateOwner(UserAId);
            var ownerDirectory = ownership.GetCheckpointDirectory(owner);
            Directory.CreateDirectory(ownerDirectory);
            oversizedPath = Path.Combine(ownerDirectory, "oversized_checkpoint%2Ejson");
            using var stream = File.Create(oversizedPath);
            stream.SetLength(AliWorkflowRecoveryCatalog.MaximumCheckpointFileBytes + 1L);
        }

        var originalLength = new FileInfo(oversizedPath).Length;
        var client = new CountingChatClient();
        var runtime = new DevelopmentLocalModelRuntime();
        var team = new AliSpecialistAgentFactory(client, runtime, () => null).CreateTeam([]);
        using var factory = new AliAgentWorkflowFactory(
            client,
            runtime,
            () => null,
            checkpointPath,
            () => UserSelection(UserAId));
        _ = factory.CreateStandardTools(team);

        var report = factory.ListRecoverableWorkflows();

        Assert.Empty(report.Workflows);
        Assert.True(report.IsTruncated);
        Assert.Equal(1, report.SkippedCheckpointFiles);
        Assert.Equal(originalLength, new FileInfo(oversizedPath).Length);
    }

    [Theory]
    [InlineData(AliWorkflowRecoveryCatalog.MaximumRecoverableWorkflows, false)]
    [InlineData(AliWorkflowRecoveryCatalog.MaximumRecoverableWorkflows + 1, true)]
    public void RecoveryCatalog_BoundsRecoverableEntriesNewestFirst(
        int checkpointCount,
        bool expectedTruncation)
    {
        var checkpointPath = CreateCheckpointPath();
        var baseline = DateTime.UtcNow.AddMinutes(-checkpointCount - 1);
        using (var ownership = new AliWorkflowCheckpointOwnership(checkpointPath))
        {
            var owner = ownership.CreateOwner(UserAId);
            var ownerDirectory = ownership.GetCheckpointDirectory(owner);
            Directory.CreateDirectory(ownerDirectory);
            for (var index = 0; index < checkpointCount; index++)
            {
                var sessionId = $"bounded-session-{index:D3}";
                var checkpointId = $"checkpoint-{index:D3}";
                WriteOwnedCheckpoint(
                    ownership,
                    owner,
                    ownerDirectory,
                    sessionId,
                    checkpointId,
                    step: index,
                    hasQueuedWork: true,
                    objective: $"Recover objective {index}.");
                File.SetLastWriteTimeUtc(
                    Path.Combine(ownerDirectory, $"{sessionId}_{checkpointId}%2Ejson"),
                    baseline.AddMinutes(index));
            }
        }

        var client = new CountingChatClient();
        var runtime = new DevelopmentLocalModelRuntime();
        var team = new AliSpecialistAgentFactory(client, runtime, () => null).CreateTeam([]);
        using var factory = new AliAgentWorkflowFactory(
            client,
            runtime,
            () => null,
            checkpointPath,
            () => UserSelection(UserAId));
        _ = factory.CreateStandardTools(team);

        var report = factory.ListRecoverableWorkflows();

        Assert.Equal(
            Math.Min(checkpointCount, AliWorkflowRecoveryCatalog.MaximumRecoverableWorkflows),
            report.Workflows.Count);
        Assert.Equal(expectedTruncation, report.IsTruncated);
        Assert.Equal($"bounded-session-{checkpointCount - 1:D3}", report.Workflows[0].SessionId);
    }

    [Theory]
    [InlineData(AliWorkflowRecoveryCatalog.MaximumCheckpointFilesToInspect, false, 0)]
    [InlineData(AliWorkflowRecoveryCatalog.MaximumCheckpointFilesToInspect + 1, true, 1)]
    [InlineData(AliWorkflowRecoveryCatalog.MaximumCheckpointDirectoryEntriesToScan + 1, true, 769)]
    public void RecoveryCatalog_BoundsCheckpointFilesInspected(
        int checkpointCount,
        bool expectedTruncation,
        int expectedSkippedCheckpointFiles)
    {
        var checkpointPath = CreateCheckpointPath();
        string ownerDirectory;
        using (var ownership = new AliWorkflowCheckpointOwnership(checkpointPath))
        {
            ownerDirectory = ownership.GetCheckpointDirectory(ownership.CreateOwner(UserAId));
            Directory.CreateDirectory(ownerDirectory);
        }
        for (var index = 0; index < checkpointCount; index++)
        {
            File.WriteAllText(
                Path.Combine(ownerDirectory, $"invalid-session-{index:D3}_checkpoint%2Ejson"),
                "{}");
        }

        var client = new CountingChatClient();
        var runtime = new DevelopmentLocalModelRuntime();
        var team = new AliSpecialistAgentFactory(client, runtime, () => null).CreateTeam([]);
        using var factory = new AliAgentWorkflowFactory(
            client,
            runtime,
            () => null,
            checkpointPath,
            () => UserSelection(UserAId));
        _ = factory.CreateStandardTools(team);

        var report = factory.ListRecoverableWorkflows();

        Assert.Empty(report.Workflows);
        Assert.Equal(expectedTruncation, report.IsTruncated);
        Assert.Equal(expectedSkippedCheckpointFiles, report.SkippedCheckpointFiles);
    }

    [Fact]
    public void RecoveryCatalog_OffersOnlyPendingCompatibleWorkflowGraphs()
    {
        var checkpointPath = CreateCheckpointPath();
        using var ownership = new AliWorkflowCheckpointOwnership(checkpointPath);
        var owner = ownership.CreateOwner(UserAId);
        var ownerDirectory = ownership.GetCheckpointDirectory(owner);
        Directory.CreateDirectory(ownerDirectory);
        WriteOwnedCheckpoint(
            ownership,
            owner,
            ownerDirectory,
            "pending-session",
            "pending-checkpoint",
            step: 3,
            hasQueuedWork: true,
            objective: "Finish the recoverable programming review.");
        WriteOwnedCheckpoint(
            ownership,
            owner,
            ownerDirectory,
            "complete-session",
            "complete-checkpoint",
            step: 8,
            hasQueuedWork: false,
            objective: "This completed run must not be offered.");

        var client = new CountingChatClient();
        var runtime = new DevelopmentLocalModelRuntime();
        var team = new AliSpecialistAgentFactory(client, runtime, () => null).CreateTeam([]);
        using var factory = new AliAgentWorkflowFactory(
            client,
            runtime,
            () => null,
            checkpointPath,
            () => UserSelection(UserAId));
        _ = factory.CreateStandardTools(team);

        var report = factory.ListRecoverableWorkflows();

        var recovered = Assert.Single(report.Workflows);
        Assert.Equal("pending-session", recovered.SessionId);
        Assert.Equal("Programming Maker Checker Workflow", recovered.WorkflowName);
        Assert.Equal(3, recovered.CompletedStep);
        Assert.Contains("Finish the recoverable", recovered.Objective, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryCatalog_TurnCapturedUserCannotBeReplacedByLaterLiveSelection()
    {
        const string userBId = "workflow-user-b";
        var checkpointPath = CreateCheckpointPath();
        using (var ownership = new AliWorkflowCheckpointOwnership(checkpointPath))
        {
            var ownerA = ownership.CreateOwner(UserAId);
            var ownerB = ownership.CreateOwner(userBId);
            var userAPath = ownership.GetCheckpointDirectory(ownerA);
            var userBPath = ownership.GetCheckpointDirectory(ownerB);
            Directory.CreateDirectory(userAPath);
            Directory.CreateDirectory(userBPath);
            WriteOwnedCheckpoint(
                ownership,
                ownerA,
                userAPath,
                "user-a-session",
                "checkpoint-a",
                step: 3,
                hasQueuedWork: true,
                objective: "Only user A may see this objective.");
            WriteOwnedCheckpoint(
                ownership,
                ownerB,
                userBPath,
                "user-b-session",
                "checkpoint-b",
                step: 4,
                hasQueuedWork: true,
                objective: "Only user B may see this objective.");
        }

        var capturedUserA = UserSelection(UserAId, "User A");
        var laterLiveUserB = UserSelection(userBId, "User B");
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "continue",
            _ => { },
            capturedUserA);
        var client = new CountingChatClient();
        var runtime = new DevelopmentLocalModelRuntime();
        var team = new AliSpecialistAgentFactory(client, runtime, () => turn).CreateTeam([]);
        using var factory = new AliAgentWorkflowFactory(
            client,
            runtime,
            () => turn,
            checkpointPath,
            () => laterLiveUserB);
        _ = factory.CreateStandardTools(team);

        var implicitTurnReport = factory.ListRecoverableWorkflows();
        var explicitRunnerReport = factory.ListRecoverableWorkflows(capturedUserA);

        Assert.Equal("user-a-session", Assert.Single(implicitTurnReport.Workflows).SessionId);
        Assert.Equal("user-a-session", Assert.Single(explicitRunnerReport.Workflows).SessionId);
        Assert.DoesNotContain(
            implicitTurnReport.Workflows,
            item => item.Objective.Contains("user B", StringComparison.Ordinal));
        Assert.Empty(factory.ListRecoverableWorkflows(
            ActiveUserSelectionSnapshot.SelectionRequired).Workflows);
    }

    [Fact]
    public async Task WorkflowRecovery_RecreatedFactoryResumesSavedCheckpointAndCompletes()
    {
        var sourcePath = CreateCheckpointPath();
        var sourceClient = new CountingChatClient();
        var runtime = new DevelopmentLocalModelRuntime();
        var sourceTeam = new AliSpecialistAgentFactory(sourceClient, runtime, () => null).CreateTeam([]);
        using (var sourceFactory = new AliAgentWorkflowFactory(
                   sourceClient,
                   runtime,
                   () => null,
                   sourcePath,
                   () => UserSelection(UserAId)))
        {
            var groupChat = Assert.Single(
                sourceFactory.CreateStandardTools(sourceTeam).OfType<AIFunction>(),
                item => item.Name == AliCapabilityCatalog.RunProgrammingGroupChatName);
            _ = await groupChat.InvokeAsync(
                new AIFunctionArguments { ["query"] = "Recover this maker checker workflow after a restart." },
                TestContext.Current.CancellationToken);
        }

        var sourceOwnerPath = UserCheckpointDirectory(sourcePath, UserAId);
        var indexLines = File.ReadAllLines(Path.Combine(sourceOwnerPath, "index.jsonl"));
        Assert.NotEmpty(indexLines);
        using var firstIndex = System.Text.Json.JsonDocument.Parse(indexLines[0]);
        var info = firstIndex.RootElement.GetProperty("checkpointInfo");
        var sessionId = info.GetProperty("sessionId").GetString()!;
        var fileName = firstIndex.RootElement.GetProperty("fileName").GetString()!;
        var recoveryPath = CreateCheckpointPath();
        Directory.CreateDirectory(recoveryPath);
        File.Copy(
            Path.Combine(sourcePath, AliWorkflowCheckpointOwnership.KeyFileName),
            Path.Combine(recoveryPath, AliWorkflowCheckpointOwnership.KeyFileName));
        var recoveryOwnerPath = UserCheckpointDirectory(recoveryPath, UserAId);
        Directory.CreateDirectory(recoveryOwnerPath);
        File.Copy(
            Path.Combine(sourceOwnerPath, fileName),
            Path.Combine(recoveryOwnerPath, fileName));
        File.WriteAllText(
            Path.Combine(recoveryOwnerPath, "index.jsonl"),
            indexLines[0] + Environment.NewLine);

        const string userBId = "workflow-user-b";
        var userBPath = UserCheckpointDirectory(recoveryPath, userBId);
        Directory.CreateDirectory(userBPath);
        var copiedCheckpointPath = Path.Combine(userBPath, fileName);
        File.Copy(Path.Combine(recoveryOwnerPath, fileName), copiedCheckpointPath);
        File.WriteAllText(
            Path.Combine(userBPath, "index.jsonl"),
            indexLines[0] + Environment.NewLine);

        var userBClient = new CountingChatClient();
        var userBTeam = new AliSpecialistAgentFactory(userBClient, runtime, () => null).CreateTeam([]);
        using (var userBFactory = new AliAgentWorkflowFactory(
                   userBClient,
                   runtime,
                   () => null,
                   recoveryPath,
                   () => UserSelection(userBId)))
        {
            _ = userBFactory.CreateStandardTools(userBTeam);
            Assert.Empty(userBFactory.ListRecoverableWorkflows().Workflows);
            var blocked = await userBFactory.ResumeWorkflowAsync(
                sessionId,
                TestContext.Current.CancellationToken);
            Assert.False(blocked.Success);

            var userAOwnerKey = AliWorkflowCheckpointOwnership.CreateOwnerKey(UserAId);
            var userBOwnerKey = AliWorkflowCheckpointOwnership.CreateOwnerKey(userBId);
            var forged = File.ReadAllText(copiedCheckpointPath)
                .Replace(userAOwnerKey, userBOwnerKey, StringComparison.Ordinal);
            File.WriteAllText(copiedCheckpointPath, forged);
            Assert.Empty(userBFactory.ListRecoverableWorkflows().Workflows);
        }

        var recoveryClient = new CountingChatClient();
        var recoveryTeam = new AliSpecialistAgentFactory(recoveryClient, runtime, () => null).CreateTeam([]);
        using var recoveryFactory = new AliAgentWorkflowFactory(
            recoveryClient,
            runtime,
            () => null,
            recoveryPath,
            () => UserSelection(UserAId));
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

    [Fact]
    public async Task WorkflowRecovery_SelectionRequiredExposesNothingAndCannotResume()
    {
        var checkpointPath = CreateCheckpointPath();
        using (var ownership = new AliWorkflowCheckpointOwnership(checkpointPath))
        {
            var owner = ownership.CreateOwner(UserAId);
            var ownerDirectory = ownership.GetCheckpointDirectory(owner);
            Directory.CreateDirectory(ownerDirectory);
            WriteOwnedCheckpoint(
                ownership,
                owner,
                ownerDirectory,
                "private-session",
                "private-checkpoint",
                step: 2,
                hasQueuedWork: true,
                objective: "Private interrupted objective.");
        }

        var client = new CountingChatClient();
        var runtime = new DevelopmentLocalModelRuntime();
        var team = new AliSpecialistAgentFactory(client, runtime, () => null).CreateTeam([]);
        using var factory = new AliAgentWorkflowFactory(
            client,
            runtime,
            () => null,
            checkpointPath,
            () => ActiveUserSelectionSnapshot.SelectionRequired);
        _ = factory.CreateStandardTools(team);

        Assert.Empty(factory.ListRecoverableWorkflows().Workflows);
        var result = await factory.ResumeWorkflowAsync(
            "private-session",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("Select an active user", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkflowFactory_MissingOwnerKeyWithExistingCheckpointFailsClosedWithoutBlockingComposition()
    {
        var checkpointPath = CreateCheckpointPath();
        var orphanDirectory = Path.Combine(checkpointPath, "users", new string('a', 64));
        Directory.CreateDirectory(orphanDirectory);
        File.WriteAllText(Path.Combine(orphanDirectory, "orphan.json"), "{}");

        var client = new CountingChatClient();
        var runtime = new DevelopmentLocalModelRuntime();
        var team = new AliSpecialistAgentFactory(client, runtime, () => null).CreateTeam([]);
        var factory = new AliAgentWorkflowFactory(
            client,
            runtime,
            () => null,
            checkpointPath,
            () => UserSelection(UserAId));

        var tools = factory.CreateStandardTools(team);
        var workflow = Assert.Single(
            tools.OfType<AIFunction>(),
            item => item.Name == AliCapabilityCatalog.RunResearchArtifactWorkflowName);
        var startResult = await workflow.InvokeAsync(
            new AIFunctionArguments { ["query"] = "Do not start this workflow." },
            TestContext.Current.CancellationToken);
        var resumeResult = await factory.ResumeWorkflowAsync(
            "orphan-session",
            TestContext.Current.CancellationToken);

        Assert.Contains("unavailable", startResult?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(factory.ListRecoverableWorkflows().Workflows);
        Assert.Contains("unavailable", factory.ListRecoverableWorkflows().Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(resumeResult.Success);
        Assert.Contains("unavailable", resumeResult.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(
            checkpointPath,
            AliWorkflowCheckpointOwnership.KeyFileName)));
        Assert.Equal(0, client.CallCount);

        factory.Dispose();
        factory.Dispose();
    }

    [Fact]
    public async Task WorkflowFactory_CorruptOwnerKeyFailsClosedWithoutReplacingKeyOrBlockingComposition()
    {
        var checkpointPath = CreateCheckpointPath();
        Directory.CreateDirectory(checkpointPath);
        var keyPath = Path.Combine(checkpointPath, AliWorkflowCheckpointOwnership.KeyFileName);
        var corruptKey = Enumerable.Range(1, 48).Select(value => (byte)value).ToArray();
        File.WriteAllBytes(keyPath, corruptKey);

        var client = new CountingChatClient();
        var runtime = new DevelopmentLocalModelRuntime();
        var team = new AliSpecialistAgentFactory(client, runtime, () => null).CreateTeam([]);
        using var factory = new AliAgentWorkflowFactory(
            client,
            runtime,
            () => null,
            checkpointPath,
            () => UserSelection(UserAId));

        var tools = factory.CreateStandardTools(team);
        var workflow = Assert.Single(
            tools.OfType<AIFunction>(),
            item => item.Name == AliCapabilityCatalog.RunProgrammingGroupChatName);
        var startResult = await workflow.InvokeAsync(
            new AIFunctionArguments { ["query"] = "Do not start this workflow." },
            TestContext.Current.CancellationToken);

        Assert.Contains("unavailable", startResult?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(factory.ListRecoverableWorkflows().Workflows);
        Assert.Equal(corruptKey, File.ReadAllBytes(keyPath));
        Assert.Equal(0, client.CallCount);
    }

    private static IReadOnlyList<AITool> CreateWorkflowTools(
        IChatClient client,
        string? checkpointPath = null)
    {
        var runtime = new DevelopmentLocalModelRuntime();
        var team = new AliSpecialistAgentFactory(client, runtime, () => null).CreateTeam([]);
        return new AliAgentWorkflowFactory(
                client,
                runtime,
                () => null,
                checkpointPath ?? CreateCheckpointPath(),
                () => UserSelection(UserAId))
            .CreateStandardTools(team);
    }

    private static ActiveUserSelectionSnapshot UserSelection(
        string stableId,
        string displayName = "Workflow user") =>
        ActiveUserSelectionSnapshot.Resolved(
            new ActiveUser(stableId, displayName, false, "test"));

    private static string CreateCheckpointPath() => Path.Combine(
        Path.GetTempPath(),
        "AliAgentWorkflowTests",
        Guid.NewGuid().ToString("N"));

    private static string UserCheckpointDirectory(string root, string stableUserId) =>
        Path.Combine(
            root,
            "users",
            AliWorkflowCheckpointOwnership.CreateOwnerKey(stableUserId));

    private static void WriteOwnedCheckpoint(
        AliWorkflowCheckpointOwnership ownership,
        AliWorkflowCheckpointOwner owner,
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
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var bound = ownership.Bind(document.RootElement, owner);
        File.WriteAllText(
            Path.Combine(directory, $"{sessionId}_{checkpointId}%2Ejson"),
            System.Text.Json.JsonSerializer.Serialize(bound));
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

    private sealed class LargeWorkflowResponseChatClient : IChatClient
    {
        private int _callCount;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            return Task.FromResult(new ChatResponse(
                new MeaiChatMessage(
                    MeaiChatRole.Assistant,
                    $"workflow response {call} " + new string((char)('a' + call - 1), 5000))));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(
                MeaiChatRole.Assistant,
                $"workflow response {call} " + new string((char)('a' + call - 1), 5000));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
