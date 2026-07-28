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
        finally { Directory.Delete(root, recursive: true); }
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
    public async Task ModelMemoryToolsNeverAcceptCallerSelectedUserIds()
    {
        var user = new ActiveUser("person-a", "Alice", false, "explicit-selection");
        var service = new CapturingMemoryService();
        var tools = new AliMemoryTools(service, new FakeActiveSession(user), () => new UserMemorySettings(), static () => null);

        await tools.RememberAsync("My neighbor is Bill", "people_relationships", CancellationToken.None);
        await tools.CorrectAsync("My neighbor is William", CancellationToken.None);
        await tools.ForgetAsync("neighbor", CancellationToken.None);
        await tools.ListCurrentAsync(CancellationToken.None);

        Assert.All(service.SeenUsers, seen => Assert.Equal("person-a", seen.StableId));
        var exposed = typeof(AliMemoryTools).GetMethods()
            .Where(method => method.Name is nameof(AliMemoryTools.SearchAsync) or nameof(AliMemoryTools.RememberAsync)
                or nameof(AliMemoryTools.CorrectAsync) or nameof(AliMemoryTools.ForgetAsync) or nameof(AliMemoryTools.ListCurrentAsync))
            .SelectMany(method => method.GetParameters());
        Assert.DoesNotContain(exposed, parameter => parameter.Name?.Contains("userId", StringComparison.OrdinalIgnoreCase) == true);
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
        Assert.Equal("My shop foreman is Bill", service.StoredFact);
        var recalled = await memoryTools.SearchAsync("Who is my shop foreman?", TestContext.Current.CancellationToken);
        Assert.Contains(recalled.Memories, memory => memory.Text == "My shop foreman is Bill");
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

        public Task<IReadOnlyList<UserMemory>> RecallAsync(ActiveUser user, string query, int maximumResults, CancellationToken cancellationToken)
        {
            LastUser = user;
            LastMaximumResults = maximumResults;
            SeenUsers.Add(user);
            if (ThrowOnRecall) throw new IOException("offline");
            IReadOnlyList<UserMemory> values = [new("m1", "The user's neighbor is Bill.", "people_relationships", DateTimeOffset.UtcNow, null, .9, true, "explicit_user_request")];
            return Task.FromResult(values);
        }

        public Task<MemoryOperationResult> RememberAsync(ActiveUser user, string conversation, string source, CancellationToken cancellationToken) => Operation(user);
        public Task<MemoryOperationResult> CorrectAsync(ActiveUser user, string correction, CancellationToken cancellationToken) => Operation(user);
        public Task<MemoryOperationResult> ForgetAsync(ActiveUser user, string request, CancellationToken cancellationToken) => Operation(user);
        public Task<IReadOnlyList<UserMemory>> ListAsync(ActiveUser user, string? category, CancellationToken cancellationToken)
        {
            SeenUsers.Add(user);
            return Task.FromResult<IReadOnlyList<UserMemory>>([]);
        }
        public Task<MemoryOperationResult> DeleteAsync(ActiveUser user, string memoryId, CancellationToken cancellationToken) => Operation(user);
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
            CancellationToken cancellationToken)
        {
            StoredFact = conversation;
            return Task.FromResult(new MemoryOperationResult(
                true,
                "Memory saved locally.",
                [new UserMemory("memory-1", conversation, "people", DateTimeOffset.UtcNow, null, 1, true, source)]));
        }

        public Task<MemoryOperationResult> CorrectAsync(ActiveUser user, string correction, CancellationToken cancellationToken) =>
            Task.FromResult(new MemoryOperationResult(false, "unused", []));

        public Task<MemoryOperationResult> ForgetAsync(ActiveUser user, string request, CancellationToken cancellationToken) =>
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

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : FinalAnswer("script exhausted"));

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
