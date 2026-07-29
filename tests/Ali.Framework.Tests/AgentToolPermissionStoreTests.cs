using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.Mcp;
using Ali.Modules.UserMemory;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests;

public sealed class AgentToolPermissionStoreTests
{
    [Fact]
    public async Task DeniedTurn_BlocksEveryLaterProtectedToolBeforeSavedApprovalOrExecution()
    {
        var activity = new List<AssistantStreamChunk>();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "Do not continue after I deny this.",
            activity.Add);
        var invocationCount = 0;
        var inner = AIFunctionFactory.Create(
            () =>
            {
                invocationCount++;
                return "mutated";
            },
            "protected_test_write",
            "Mutate test state.");
        var guarded = new AliToolPermissionPolicy(() => turn).Apply(inner, requiresApproval: true);

        turn.RecordPermissionDecision(AgentToolApprovalChoice.AllowOnce);
        Assert.False(turn.PermissionDenied);
        Assert.False(turn.UsedEvidenceTool);
        turn.RecordPermissionDecision(AgentToolApprovalChoice.Deny);
        turn.RecordPermissionDecision(AgentToolApprovalChoice.AllowOnce);
        Assert.True(turn.PermissionDenied);
        Assert.True(turn.UsedEvidenceTool);

        var result = await guarded.InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, invocationCount);
        Assert.Contains("denied earlier", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(activity, item =>
            item.ActivityKind == AgentActivityKind.Warning
            && item.Text.Contains("Blocked follow-up protected action", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeRiskClassification_KeepsWritesAndPaidResearchApprovalGated()
    {
        Assert.True(AliToolPermissionPolicy.RequiresApproval("remember_for_current_user"));
        Assert.True(AliToolPermissionPolicy.RequiresApproval("forget_current_user_memory"));
        Assert.True(AliToolPermissionPolicy.RequiresApproval("create_calendar_event"));
        Assert.True(AliToolPermissionPolicy.RequiresApproval("research_web"));
        Assert.False(AliToolPermissionPolicy.RequiresApproval("search_current_web"));
        Assert.False(AliToolPermissionPolicy.RequiresApproval("search_local_library"));
        Assert.NotEmpty(AliToolPermissionPolicy.ProtectedTools);
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.FileWriteName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.FileDeleteName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.FileReplaceName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.FileReplaceLinesName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.FileMoveName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetCreateProjectName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.RoslynFormatProjectName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.RoslynApplyRenameName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetBuildName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetRunName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.FileReadName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.RoslynAnalyzeProjectName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.RoslynFindSymbolName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.RoslynGetCompletionsName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.RoslynInspectSolutionName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.RoslynInspectDocumentName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.RoslynInspectPositionName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.RoslynFindReferencesName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.RoslynPreviewRenameName));
        Assert.All(
            AliToolPermissionPolicy.ProtectedTools,
            policy =>
            {
                Assert.True(AliToolPermissionPolicy.RequiresApproval(policy.ToolName));
                Assert.False(string.IsNullOrWhiteSpace(policy.Reason));
            });
        Assert.False(AliToolPermissionPolicy.RequiresApproval(
            "search_current_web",
            AgentPermissionProfile.TrustedWorkstation));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(
            "search_current_web",
            AgentPermissionProfile.LockedDown));
    }

    [Fact]
    public void PermissionProfile_DefaultsToTrustedWorkstationAndPersistsLockedDown()
    {
        WithStore((root, store) =>
        {
            Assert.Equal(AgentPermissionProfile.TrustedWorkstation, store.CurrentProfile);

            store.SetProfile(AgentPermissionProfile.LockedDown);

            var restored = new AgentToolPermissionStore(root);
            Assert.Equal(AgentPermissionProfile.LockedDown, restored.CurrentProfile);
        });
    }

    [Fact]
    public void McpDiscovery_UsesProfileAndProtocolSafetyHintsForNewTools()
    {
        WithStore((root, store) =>
        {
            var manager = new McpClientManager(root);
            var readOnly = new McpDiscoveredTool("read_file", "Read a file", true, false);
            var write = new McpDiscoveredTool("write_file", "Write a file", false, false);
            var destructive = new McpDiscoveredTool("delete_file", "Delete a file", false, true);

            Assert.False(manager.RequiresApprovalByDefault(readOnly));
            Assert.True(manager.RequiresApprovalByDefault(write));
            Assert.True(manager.RequiresApprovalByDefault(destructive));

            store.SetProfile(AgentPermissionProfile.LockedDown);
            Assert.True(manager.RequiresApprovalByDefault(readOnly));
        });
    }

    [Fact]
    public void ExactArguments_AreOrderIndependentButValueSpecificAndSecretSafe()
    {
        WithStore((root, store) =>
        {
            var user = User("alice", "Alice");
            var savedArguments = new Dictionary<string, object?>
            {
                ["query"] = "private acquisition target",
                ["limit"] = 5
            };
            store.Save(user, "research_web", AgentToolPermissionScope.ExactArguments, savedArguments);

            Assert.True(store.TryMatch(user, "research_web", new Dictionary<string, object?>
            {
                ["limit"] = 5,
                ["query"] = "private acquisition target"
            }, out var matching));
            Assert.Equal(AgentToolPermissionScope.ExactArguments, matching!.Scope);

            Assert.False(store.TryMatch(user, "research_web", new Dictionary<string, object?>
            {
                ["query"] = "different query",
                ["limit"] = 5
            }, out _));

            var persisted = File.ReadAllText(store.SettingsPath);
            Assert.DoesNotContain("private acquisition target", persisted, StringComparison.Ordinal);
            Assert.Contains("query", persisted, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ToolRule_MatchesAnyArgumentsForOnlyTheOwningUser()
    {
        WithStore((root, store) =>
        {
            var alice = User("alice", "Alice");
            var bob = User("bob", "Bob");
            store.Save(alice, "create_calendar_event", AgentToolPermissionScope.Tool, null);

            Assert.True(store.TryMatch(alice, "create_calendar_event", new Dictionary<string, object?>
            {
                ["title"] = "Anything"
            }, out var aliceGrant));
            Assert.NotNull(aliceGrant);
            Assert.False(store.TryMatch(bob, "create_calendar_event", new Dictionary<string, object?>
            {
                ["title"] = "Anything"
            }, out var bobGrant));
            Assert.Null(bobGrant);
            Assert.False(store.TryMatch(alice, "forget_memory", null, out var unrelatedGrant));
            Assert.Null(unrelatedGrant);
        });
    }

    [Fact]
    public void Store_PersistsAndRevokesRules()
    {
        WithStore((root, store) =>
        {
            var user = User("alice", "Alice");
            var saved = store.Save(user, "remember_for_current_user", AgentToolPermissionScope.Tool, null);

            var restored = new AgentToolPermissionStore(root);
            Assert.True(restored.TryMatch(user, "remember_for_current_user", null, out _));
            Assert.True(restored.Revoke(user.StableId, saved.Id));

            var afterRevoke = new AgentToolPermissionStore(root);
            Assert.False(afterRevoke.TryMatch(user, "remember_for_current_user", null, out _));
            Assert.Empty(afterRevoke.ListForUser(user.StableId));
        });
    }

    [Fact]
    public void CorruptPermissionFile_FailsClosed()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "Permissions", "agent-tool-permissions.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{not valid json");

            var store = new AgentToolPermissionStore(root);

            Assert.Empty(store.ListForUser("alice"));
            Assert.False(store.TryMatch(User("alice", "Alice"), "anything", null, out _));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static ActiveUser User(string id, string name) =>
        new(id, name, false, "test");

    private static void WithStore(Action<string, AgentToolPermissionStore> action)
    {
        var root = NewRoot();
        try
        {
            action(root, new AgentToolPermissionStore(root));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "AliPermissionTests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
