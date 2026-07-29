using System.Runtime.CompilerServices;
using Ali.Modules.Coordinator;
using Ali.Modules.Identity;
using Ali.Modules.Permissions;
using Ali.Modules.UserMemory;
using AvatarBuilder.Modules.Vision.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests;

public sealed class UserMemoryArchitectureTests
{
    [Fact]
    public void Mem0Warmup_DoesNotThrashAColdCpuEmbeddingWorker()
    {
        Assert.True(Mem0UserMemoryService.WarmupAttemptTimeout >= TimeSpan.FromSeconds(20));
        Assert.True(Mem0UserMemoryService.WarmupOverallTimeout > Mem0UserMemoryService.WarmupAttemptTimeout);
    }

    [Fact]
    public void EmptyIdentityStoreCreatesStableJohnDoeTestProfile()
    {
        var root = TemporaryRoot();
        try
        {
            var first = new ActiveUserSession(root, new FakeIdentityProfiles([]));
            Assert.Equal("John Doe", first.Current.DisplayName);
            Assert.True(first.Current.IsTestProfile);
            Assert.StartsWith("test_", first.Current.StableId);

            var restarted = new ActiveUserSession(root, new FakeIdentityProfiles([]));
            Assert.Equal(first.Current.StableId, restarted.Current.StableId);
            Assert.True(restarted.Current.IsTestProfile);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CameraIndependentIdentityProfilePersistsAndOwnsTheActiveUserSession()
    {
        var root = TemporaryRoot();
        var identityRoot = Path.Combine(root, "Vision");
        var settingsRoot = Path.Combine(root, "Settings");
        try
        {
            var writer = new StoredPersonIdentityReviewService(identityRoot);
            var created = writer.CreateUserProfile(new IdentityEnrollmentRequest(
                "Bill",
                "Engineer",
                "bill",
                "",
                "",
                "",
                "Default User"));

            Assert.True(created.Success, created.Status);
            var reloaded = new StoredPersonIdentityReviewService(identityRoot);
            var profile = Assert.Single(reloaded.GetIdentityReviewItems());
            Assert.True(profile.IsRegisteredUser);
            Assert.Equal("Bill Engineer", profile.DisplayName);
            Assert.Equal("bill", profile.Username);
            Assert.True(string.IsNullOrWhiteSpace(profile.ContextPhotoPath)
                || !File.Exists(profile.ContextPhotoPath));

            var session = new ActiveUserSession(settingsRoot, reloaded);
            Assert.False(session.RequiresSelection);
            Assert.Equal(profile.IdentityId, session.Current.StableId);
            Assert.Equal("Bill Engineer", session.Current.DisplayName);
            Assert.False(session.Current.IsTestProfile);

            var duplicate = writer.CreateUserProfile(new IdentityEnrollmentRequest(
                "Other",
                "Bill",
                "BILL",
                "",
                "",
                "",
                "Default User"));
            Assert.False(duplicate.Success);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CameraEnrollmentEnrichesExistingCameraIndependentProfileWithoutChangingItsId()
    {
        var root = TemporaryRoot();
        try
        {
            using var memory = new PersonIdentityMemory("test identity backend");
            memory.ConfigureOutputFolder(root);
            var created = memory.CreateUserProfile(new IdentityEnrollmentRequest(
                "Bill",
                "Engineer",
                "bill",
                "bill@example.test",
                "",
                "",
                "Default User"));
            Assert.True(created.Success, created.Status);
            var original = Assert.Single(memory.GetIdentityReviewItems());

            var begin = memory.BeginEnrollment(new IdentityEnrollmentRequest(
                "Bill",
                "Engineer",
                "BILL",
                "bill@example.test",
                "",
                "",
                "Default User"));
            Assert.True(begin.Success, begin.Status);

            var embedding = Enumerable.Repeat(0f, 128).ToArray();
            embedding[0] = 1f;
            for (var index = 0; index < 5; index++)
            {
                Assert.True(memory.RequestEnrollmentCapture().Success);
                memory.ObserveEmbeddingFrame(
                    [new PersonIdentityEmbeddingObservation(
                        embedding,
                        0.99d,
                        new PersonFaceBox(0.1d, 0.1d, 0.9d, 0.9d))],
                    DateTime.UtcNow.AddMilliseconds(index),
                    static () => [0xff, 0xd8, 0xff, 0xd9]);
            }

            var completed = memory.GetEnrollmentState();
            Assert.Equal(original.IdentityId, completed.CompletedIdentityId);
            Assert.Contains("added", completed.Status, StringComparison.OrdinalIgnoreCase);
            Assert.Single(memory.GetIdentityReviewItems());

            var duplicate = memory.BeginEnrollment(new IdentityEnrollmentRequest(
                "Bill",
                "Engineer",
                "bill",
                "",
                "",
                "",
                "Default User"));
            Assert.False(duplicate.Success);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RegisteredUsersRemainIsolatedByStableIdAndDisplayRenameDoesNotChangeOwnership()
    {
        var root = TemporaryRoot();
        try
        {
            var profiles = new FakeIdentityProfiles([Person("person-a", "Alice"), Person("person-b", "Bob")]);
            var session = new ActiveUserSession(root, profiles);
            Assert.False(session.Current.IsTestProfile);
            Assert.True(session.RequiresSelection);
            Assert.Equal("identity-profile-selection-required", session.Current.ResolutionMethod);
            var changed = session.Select("person-b");
            Assert.False(session.RequiresSelection);
            Assert.Equal("person-b", changed.StableId);

            profiles.Items = [Person("person-a", "Alice"), Person("person-b", "Robert")];
            session.Refresh();
            Assert.Equal("person-b", session.Current.StableId);
            Assert.Equal("Robert", session.Current.DisplayName);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PreAnswerRecallIsBoundedUsesOnlyActiveUserAndFailsSafe()
    {
        var user = new ActiveUser("person-a", "Alice", false, "explicit-selection");
        var session = new FakeActiveSession(user);
        var service = new CapturingMemoryService();
        var tools = new AliMemoryTools(service, session, () => new UserMemorySettings
        {
            RecallMaximumResults = 3,
            RecallTimeoutMilliseconds = 1000
        }, static () => null);

        var result = await tools.SearchAsync("What is my neighbor's name?", CancellationToken.None);
        Assert.Equal("person-a", service.LastUser?.StableId);
        Assert.Equal(3, service.LastMaximumResults);
        Assert.Contains(result.Memories, memory => memory.Text.Contains("Bill", StringComparison.Ordinal));

        service.ThrowOnRecall = true;
        var failed = await tools.SearchAsync("still answer", CancellationToken.None);
        Assert.Empty(failed.Memories);
        Assert.Single(failed.Warnings);
    }

    [Fact]
    public async Task ModelRecallMarksItsResultAsAuthoritativeWithoutMarkingBackgroundPreRecall()
    {
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "What is my bridge validation codeword?",
            _ => { });
        var user = new ActiveUser("person-a", "Alice", false, "explicit-selection");
        var tools = new AliMemoryTools(
            new CapturingMemoryService(),
            new FakeActiveSession(user),
            () => new UserMemorySettings(),
            () => turn);

        await tools.SearchAsync("background context", CancellationToken.None);
        Assert.False(turn.UsedEvidenceTool);

        await tools.SearchAsModelToolAsync("bridge validation codeword", CancellationToken.None);
        Assert.True(turn.UsedEvidenceTool);
    }

    [Fact]
    public void RecallFiltering_RejectsWeakMatchesAndKeepsOnlyTheConfidentSemanticCluster()
    {
        var settings = new UserMemorySettings
        {
            RecallMinimumScore = 0.65,
            RecallScoreWindow = 0.05
        };
        var values = new UserMemory[]
        {
            new("name", "The user's name is Chris.", "people", null, null, .693, true, "explicit_user_request"),
            new("touch", "Assistant created touch.txt.", "general", null, null, .636, false, "conversation"),
            new("catalog", "The tool catalog contains 108 tools.", "general", null, null, .605, false, "conversation")
        };

        var relevant = Mem0UserMemoryService.FilterRecallMatches(values, settings, 5);

        Assert.Collection(relevant, memory => Assert.Equal("name", memory.MemoryId));
        var weakOnly = Mem0UserMemoryService.FilterRecallMatches(values[1..], settings, 5);
        Assert.Empty(weakOnly);
    }

    [Fact]
    public void RecallFiltering_DefaultHybridFloorKeepsKeywordSupportedMatchAndRejectsDenseOnlyNoise()
    {
        var values = new UserMemory[]
        {
            new("supported", "token: amber compass 9462", "token", null, null, .358, true, "explicit_user_request", .46, .256),
            new("noise", "Unrelated recent task state.", "general", null, null, .274, false, "conversation")
        };

        var relevant = Mem0UserMemoryService.FilterRecallMatches(values, new UserMemorySettings(), 5);

        Assert.Collection(relevant, memory => Assert.Equal("supported", memory.MemoryId));
    }

    [Fact]
    public void RecallFiltering_RejectsTightSemanticNoiseClusterForMissingFact()
    {
        var values = new UserMemory[]
        {
            new("name", "User's name is Chris.", "general", null, null, .465, true, "explicit_user_request", .465, 0),
            new("desktop", "User requested a Desktop tree.", "general", null, null, .453, false, "conversation", .453, 0),
            new("files", "User grants explicit file creation.", "general", null, null, .451, true, "explicit_user_request", .451, 0)
        };

        var relevant = Mem0UserMemoryService.FilterRecallMatches(values, new UserMemorySettings(), 5);

        Assert.Empty(relevant);
    }

    [Fact]
    public void RecallFiltering_KeepsClearlySeparatedSemanticParaphrase()
    {
        var values = new UserMemory[]
        {
            new("name", "User's name is Chris.", "general", null, null, .573, true, "explicit_user_request", .573, 0),
            new("desktop", "User requested a Desktop tree.", "general", null, null, .444, false, "conversation", .444, 0),
            new("files", "User grants explicit file creation.", "general", null, null, .440, true, "explicit_user_request", .440, 0)
        };

        var relevant = Mem0UserMemoryService.FilterRecallMatches(values, new UserMemorySettings(), 5);

        Assert.Collection(relevant, memory => Assert.Equal("name", memory.MemoryId));
    }

    [Fact]
    public void RecallFiltering_FailsClosedWhenWorkerReturnsNoScores()
    {
        var values = new UserMemory[]
        {
            new("unscored", "An unscored item is inventory, not recall evidence.", "general", null, null, null, false, "mem0")
        };

        Assert.Empty(Mem0UserMemoryService.FilterRecallMatches(values, new UserMemorySettings(), 5));
    }

    [Fact]
    public void BackgroundLearning_IsOptInWhileExplicitMemoryRemainsEnabled()
    {
        var settings = new UserMemorySettings();

        Assert.True(settings.Enabled);
        Assert.False(settings.AutomaticBackgroundLearning);
    }

    [Fact]
    public void FailedRecallContext_DoesNotMasqueradeAsAnEmptySuccessfulLookup()
    {
        var context = new CoordinatorMemoryResult(
            "Memory recall timed out; Ali continued safely.",
            [],
            ["Per-user memory recall exceeded its short timeout."]);

        var messages = AliAgentHarnessRunner.BuildInitialInput(
            [],
            "What is my name?",
            context,
            []);

        var systemText = Assert.IsType<TextContent>(messages[0].Contents.Single()).Text;
        Assert.Contains("does NOT mean that no memories exist", systemText, StringComparison.Ordinal);
        Assert.Contains("retry recall_user_memory once", systemText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelMemoryToolsNeverAcceptCallerSelectedUserIds()
    {
        var user = new ActiveUser("person-a", "Alice", false, "explicit-selection");
        var service = new CapturingMemoryService();
        var tools = new AliMemoryTools(service, new FakeActiveSession(user), () => new UserMemorySettings(), static () => null);

        await tools.RememberAsync("My neighbor is Bill", "people_relationships", CancellationToken.None);
        await tools.CorrectAsync("m1", "My neighbor is William", CancellationToken.None);
        await tools.ForgetAsync("m1", CancellationToken.None);
        await tools.ListCurrentAsync(CancellationToken.None);

        Assert.All(service.SeenUsers, seen => Assert.Equal("person-a", seen.StableId));
        var exposed = typeof(AliMemoryTools).GetMethods()
            .Where(method => method.Name is nameof(AliMemoryTools.SearchAsync) or nameof(AliMemoryTools.RememberAsync)
                or nameof(AliMemoryTools.CorrectAsync) or nameof(AliMemoryTools.ForgetAsync) or nameof(AliMemoryTools.ListCurrentAsync))
            .SelectMany(method => method.GetParameters());
        Assert.DoesNotContain(exposed, parameter => parameter.Name?.Contains("userId", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal("m1", service.LastCorrectedMemoryId);
        Assert.Equal("m1", service.LastDeletedMemoryId);
    }

    [Fact]
    public void Mem0WorkerUsesSemanticSearchOnlyForReadOnlyRecall()
    {
        var path = FindRepositoryFile("src", "Modules", "UserMemory", "Tools", "mem0_service.py");
        var source = File.ReadAllText(path);
        var correctBlock = source[source.IndexOf("if operation == \"correct\":", StringComparison.Ordinal)..source.IndexOf("if operation == \"forget\":", StringComparison.Ordinal)];
        var forgetBlock = source[source.IndexOf("if operation == \"forget\":", StringComparison.Ordinal)..source.IndexOf("if operation == \"delete\":", StringComparison.Ordinal)];

        Assert.DoesNotContain("self.memory.search", correctBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("self.memory.search", forgetBlock, StringComparison.Ordinal);
        Assert.Contains("request.get(\"memoryId\"", correctBlock, StringComparison.Ordinal);
        Assert.Contains("request.get(\"memoryId\"", forgetBlock, StringComparison.Ordinal);
        Assert.Contains("self.owns(stable_id, memory_id)", correctBlock, StringComparison.Ordinal);
        Assert.Contains("self.owns(stable_id, memory_id)", forgetBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Mem0WorkerTreatsNullListCategoryAsAnUnfilteredInventory()
    {
        var path = FindRepositoryFile("src", "Modules", "UserMemory", "Tools", "mem0_service.py");
        var source = File.ReadAllText(path);
        var listBlock = source[source.IndexOf("if operation == \"list\":", StringComparison.Ordinal)..source.IndexOf("if operation == \"remember\":", StringComparison.Ordinal)];

        Assert.Contains("request.get(\"category\") or \"\"", listBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("str(request.get(\"category\", \"\"))", listBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Mem0WorkerReturnsExplainableHybridRecallScores()
    {
        var path = FindRepositoryFile("src", "Modules", "UserMemory", "Tools", "mem0_service.py");
        var source = File.ReadAllText(path);
        var recallBlock = source[source.IndexOf("if operation == \"recall\":", StringComparison.Ordinal)..source.IndexOf("if operation == \"list\":", StringComparison.Ordinal)];

        Assert.Contains("explain=True", recallBlock, StringComparison.Ordinal);
        Assert.Contains("\"semanticScore\"", source, StringComparison.Ordinal);
        Assert.Contains("\"keywordScore\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RememberToolRejectsBareValuesAndStoresTheCompleteRelationshipOnRetry()
    {
        var user = new ActiveUser("person-a", "Alice", false, "explicit-selection");
        var service = new PersistingMemoryService();
        var tools = new AliMemoryTools(
            service,
            new FakeActiveSession(user),
            () => new UserMemorySettings(),
            static () => null);

        var rejected = await tools.RememberAsync(
            "teal-anvil-6304",
            "general",
            TestContext.Current.CancellationToken);

        Assert.False(rejected.Saved);
        Assert.Contains("bare value", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(service.StoredFact);

        var saved = await tools.RememberAsync(
            "The user's persistence marker is teal-anvil-6304",
            "general",
            TestContext.Current.CancellationToken);

        Assert.True(saved.Saved);
        Assert.Equal(
            "The user's persistence marker is teal-anvil-6304",
            service.StoredFact);
    }

    [Fact]
    public async Task ApprovedNativeMemoryTool_ResumesSavesAndCanBeRecalled()
    {
        var user = new ActiveUser("person-a", "Alice", false, "explicit-selection");
        var service = new PersistingMemoryService();
        var memoryTools = new AliMemoryTools(
            service,
            new FakeActiveSession(user),
            () => new UserMemorySettings(),
            static () => null);
        var policy = new AliToolPermissionPolicy(static () => null);
        var rememberFunction = policy.Apply(AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<CoordinatorMemoryWriteResult>>)memoryTools.RememberAsync,
            AliCapabilityCatalog.RememberCurrentUserName,
            "Save an explicitly requested memory."));
        using var client = new ScriptedChatClient(
        [
            ToolCall(AliCapabilityCatalog.RememberCurrentUserName, new Dictionary<string, object?>
            {
                ["fact"] = "My shop foreman is Bill",
                ["category"] = "people"
            }),
            FinalAnswer("saved")
        ]);
        var agent = client.AsHarnessAgent(new HarnessAgentOptions
        {
            MaximumIterationsPerRequest = 4,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            ChatOptions = new ChatOptions
            {
                Tools = [rememberFunction],
                ToolMode = ChatToolMode.Auto
            }
        });
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var first = await agent.RunAsync(
            "Remember that my shop foreman is Bill.",
            session,
            cancellationToken: TestContext.Current.CancellationToken);
        var request = Assert.Single(first.Messages
            .SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>());
        Assert.Null(service.StoredFact);

        var second = await agent.RunAsync(
            new ChatMessage(ChatRole.User,
            [
                request.CreateResponse(true, "Approved once by the user.")
            ]),
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("saved", second.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("people: My shop foreman is Bill", service.StoredFact);
        var recalled = await memoryTools.SearchAsync("Who is my shop foreman?", TestContext.Current.CancellationToken);
        Assert.Contains(recalled.Memories, memory => memory.Text == "people: My shop foreman is Bill");
    }

    [Fact]
    public async Task DeniedNativeMemoryTool_DoesNotExecuteRetryOrMutate()
    {
        var user = new ActiveUser("person-a", "Alice", false, "explicit-selection");
        var service = new PersistingMemoryService();
        var memoryTools = new AliMemoryTools(
            service,
            new FakeActiveSession(user),
            () => new UserMemorySettings(),
            static () => null);
        var policy = new AliToolPermissionPolicy(static () => null);
        var rememberFunction = policy.Apply(AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<CoordinatorMemoryWriteResult>>)memoryTools.RememberAsync,
            AliCapabilityCatalog.RememberCurrentUserName,
            "Save an explicitly requested memory."));
        using var client = new ScriptedChatClient(
        [
            ToolCall(AliCapabilityCatalog.RememberCurrentUserName, new Dictionary<string, object?>
            {
                ["fact"] = "This must not be saved",
                ["category"] = "test"
            }),
            FinalAnswer("Understood. I did not save that memory because you denied the request.")
        ]);
        var agent = client.AsHarnessAgent(new HarnessAgentOptions
        {
            MaximumIterationsPerRequest = 4,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            ChatOptions = new ChatOptions
            {
                Tools = [rememberFunction],
                ToolMode = ChatToolMode.Auto
            }
        });
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var first = await agent.RunAsync(
            "Remember this test fact.",
            session,
            cancellationToken: TestContext.Current.CancellationToken);
        var request = Assert.Single(first.Messages
            .SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>());

        var second = await agent.RunAsync(
            new ChatMessage(ChatRole.User,
            [
                request.CreateResponse(false, "Denied by the user.")
            ]),
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(service.StoredFact);
        Assert.Contains("did not save", second.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, client.CallCount);
        Assert.Empty(second.Messages
            .SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>());
    }

    [Fact]
    public void McpMemoryPoliciesAreDisabledByDefaultAndClassifiedAsPrivate()
    {
        var policies = Ali.Modules.Mcp.McpServerToolCatalog.CreateDefaultPolicies();
        foreach (var name in new[]
        {
            AliCapabilityCatalog.RecallUserMemoryName,
            AliCapabilityCatalog.RememberCurrentUserName,
            AliCapabilityCatalog.CorrectCurrentUserMemoryName,
            AliCapabilityCatalog.ForgetCurrentUserMemoryName,
            AliCapabilityCatalog.ListCurrentUserMemoriesName
        })
        {
            var policy = Assert.Single(policies, item => item.Name == name);
            Assert.False(policy.Enabled);
            Assert.True(policy.ReadsPrivateData);
        }
        Assert.True(Assert.Single(policies, item => item.Name == AliCapabilityCatalog.ForgetCurrentUserMemoryName).WritesLocalData);
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ali-user-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(segments)}");
    }

    private static PersonIdentityReviewItem Person(string id, string name) => new(
        id, name, name, "User", name.ToLowerInvariant(), "", "", "", "", true,
        "Default User", DateTime.UtcNow, DateTime.UtcNow, 1, 1);

    private sealed class FakeIdentityProfiles(IReadOnlyList<PersonIdentityReviewItem> items) : IPersonIdentityReviewService
    {
        public IReadOnlyList<PersonIdentityReviewItem> Items { get; set; } = items;
        public string Status => "test";
        public IReadOnlyList<PersonIdentityReviewItem> GetIdentityReviewItems() => Items;
        public IdentityReviewUpdateResult UpdateIdentityReview(IdentityReviewUpdate update) => new(false, "unused");
        public IdentityReviewUpdateResult ReplaceContextPhoto(string identityId, ReadOnlyMemory<byte> jpegBytes) => new(false, "unused");
        public IdentityReviewUpdateResult DeleteIdentity(string identityId) => new(false, "unused");
        public IdentityReviewUpdateResult BeginEnrollment(IdentityEnrollmentRequest request) => new(false, "unused");
        public IdentityReviewUpdateResult CreateUserProfile(IdentityEnrollmentRequest request) => new(false, "unused");
        public IdentityReviewUpdateResult RequestEnrollmentCapture() => new(false, "unused");
        public IdentityEnrollmentState GetEnrollmentState() => IdentityEnrollmentState.Unavailable("unused");
        public void CancelEnrollment() { }
    }

    private sealed class FakeActiveSession(ActiveUser user) : IActiveUserSession
    {
        public ActiveUser Current { get; private set; } = user;
        public IReadOnlyList<ActiveUser> AvailableUsers => [Current];
        public bool RequiresSelection => false;
        public event EventHandler<ActiveUser>? Changed { add { } remove { } }
        public ActiveUser Select(string stableId) => Current;
        public void Refresh() { }
    }

    private sealed class CapturingMemoryService : IUserMemoryService
    {
        public ActiveUser? LastUser { get; private set; }
        public int LastMaximumResults { get; private set; }
        public bool ThrowOnRecall { get; set; }
        public List<ActiveUser> SeenUsers { get; } = [];
        public string? LastCorrectedMemoryId { get; private set; }
        public string? LastDeletedMemoryId { get; private set; }

        public Task<IReadOnlyList<UserMemory>> RecallAsync(ActiveUser user, string query, int maximumResults, CancellationToken cancellationToken)
        {
            LastUser = user;
            LastMaximumResults = maximumResults;
            SeenUsers.Add(user);
            if (ThrowOnRecall) throw new IOException("offline");
            IReadOnlyList<UserMemory> values = [new("m1", "The user's neighbor is Bill.", "people_relationships", DateTimeOffset.UtcNow, null, .9, true, "explicit_user_request")];
            return Task.FromResult(values);
        }

        public Task<MemoryOperationResult> RememberAsync(ActiveUser user, string conversation, string source, string? category, CancellationToken cancellationToken) => Operation(user);
        public Task<MemoryOperationResult> CorrectAsync(ActiveUser user, string memoryId, string correction, CancellationToken cancellationToken)
        {
            LastCorrectedMemoryId = memoryId;
            return Operation(user);
        }
        public Task<IReadOnlyList<UserMemory>> ListAsync(ActiveUser user, string? category, CancellationToken cancellationToken)
        {
            SeenUsers.Add(user);
            return Task.FromResult<IReadOnlyList<UserMemory>>([]);
        }
        public Task<MemoryOperationResult> DeleteAsync(ActiveUser user, string memoryId, CancellationToken cancellationToken)
        {
            LastDeletedMemoryId = memoryId;
            return Operation(user);
        }
        public Task<UserMemoryStatus> TestAsync(ActiveUser user, CancellationToken cancellationToken) => Task.FromResult(new UserMemoryStatus(true, true, true, "Ready", "ok"));
        private Task<MemoryOperationResult> Operation(ActiveUser user)
        {
            SeenUsers.Add(user);
            return Task.FromResult(new MemoryOperationResult(true, "ok", []));
        }
    }

    private sealed class PersistingMemoryService : IUserMemoryService
    {
        public string? StoredFact { get; private set; }

        public Task<IReadOnlyList<UserMemory>> RecallAsync(
            ActiveUser user,
            string query,
            int maximumResults,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<UserMemory> values = StoredFact is null
                ? []
                : [new("memory-1", StoredFact, "people", DateTimeOffset.UtcNow, null, 1, true, "explicit_user_request")];
            return Task.FromResult(values);
        }

        public Task<MemoryOperationResult> RememberAsync(
            ActiveUser user,
            string conversation,
            string source,
            string? category,
            CancellationToken cancellationToken)
        {
            StoredFact = conversation;
            return Task.FromResult(new MemoryOperationResult(
                true,
                "Memory saved locally.",
                [new UserMemory("memory-1", conversation, "people", DateTimeOffset.UtcNow, null, 1, true, source)]));
        }

        public Task<MemoryOperationResult> CorrectAsync(ActiveUser user, string memoryId, string correction, CancellationToken cancellationToken) =>
            Task.FromResult(new MemoryOperationResult(false, "unused", []));

        public Task<IReadOnlyList<UserMemory>> ListAsync(ActiveUser user, string? category, CancellationToken cancellationToken) =>
            RecallAsync(user, string.Empty, 10, cancellationToken);

        public Task<MemoryOperationResult> DeleteAsync(ActiveUser user, string memoryId, CancellationToken cancellationToken) =>
            Task.FromResult(new MemoryOperationResult(false, "unused", []));

        public Task<UserMemoryStatus> TestAsync(ActiveUser user, CancellationToken cancellationToken) =>
            Task.FromResult(new UserMemoryStatus(true, true, true, "Ready", "ok"));
    }

    private static ChatResponse ToolCall(string name, IDictionary<string, object?> arguments)
    {
        var message = new ChatMessage(ChatRole.Assistant, string.Empty);
        message.Contents.Add(new FunctionCallContent($"call-{Guid.NewGuid():N}", name, arguments));
        return new ChatResponse(message) { FinishReason = ChatFinishReason.ToolCalls };
    }

    private static ChatResponse FinalAnswer(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = ChatFinishReason.Stop };

    private sealed class ScriptedChatClient(IEnumerable<ChatResponse> responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : FinalAnswer("script exhausted"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
