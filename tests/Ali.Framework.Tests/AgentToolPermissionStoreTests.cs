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
        const string internalName = "mcp_server_0123456789abcdef_tool_fedcba9876543210";
        const string displayName = "Build Server: update project";
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
            internalName,
            "Mutate test state.");
        var guarded = new AliToolPermissionPolicy(() => turn).Apply(
            inner,
            requiresApproval: true,
            userFacingDisplayName: displayName);

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
            && item.Text.Contains($"Blocked follow-up protected action {displayName}", StringComparison.Ordinal));
        Assert.DoesNotContain(activity, item =>
            item.Text.Contains("0123456789abcdef", StringComparison.Ordinal)
            || (item.ActivityDetail?.Contains("0123456789abcdef", StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public void NativeRiskClassification_KeepsWritesAndPaidResearchApprovalGated()
    {
        Assert.False(AliToolPermissionPolicy.RequiresApproval("remember_for_current_user"));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(
            "remember_for_current_user",
            AgentPermissionProfile.LockedDown));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(
            AliCapabilityCatalog.CorrectCurrentUserMemoryName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(
            AliCapabilityCatalog.CorrectCurrentUserMemoryName,
            AgentPermissionProfile.LockedDown));
        Assert.False(AliToolPermissionPolicy.RequiresApproval("forget_current_user_memory"));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(
            AliCapabilityCatalog.MutateParticipantMemoryName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval("create_calendar_event"));
        Assert.True(AliToolPermissionPolicy.RequiresApproval("research_web"));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.RunAgentSkillScriptName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.LoadAgentSkillName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.ReadAgentSkillResourceName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(
            AliCapabilityCatalog.LoadAgentSkillName,
            AgentPermissionProfile.LockedDown));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(
            AliCapabilityCatalog.ReadAgentSkillResourceName,
            AgentPermissionProfile.LockedDown));
        Assert.False(AliToolPermissionPolicy.RequiresApproval("search_current_web"));
        Assert.False(AliToolPermissionPolicy.RequiresApproval("search_local_library"));
        Assert.NotEmpty(AliToolPermissionPolicy.ProtectedTools);
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.FileWriteName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.FileDeleteName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.FileReplaceName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.FileReplaceLinesName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.FileMoveName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.FileCopyName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.FileCreateDirectoryName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.FileMetadataName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.ArchiveCreateName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.ArchiveListName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.ArchiveExtractName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(
            AliCapabilityCatalog.ArchiveExtractName,
            AgentPermissionProfile.LockedDown));
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
        Assert.True(AliToolPermissionPolicy.RequiresApproval(
            AliCapabilityCatalog.CodingListCapabilitiesName,
            AgentPermissionProfile.LockedDown));
    }

    [Fact]
    public void PermissionInventory_OmitsTheExactRetiredSingleLoopToolSet()
    {
        var retiredProductionNames = AliCapabilityCatalog.Tools
            .Select(tool => tool.Name)
            .Where(AliProductionCapabilityCatalog.IsRetiredToolName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedRetiredNames = RetiredSingleLoopSurfaceCanary.ToolNames
            .Order(StringComparer.Ordinal)
            .ToArray();
        var trustedNames = AliToolPermissionPolicy
            .ProtectedToolsFor(AgentPermissionProfile.TrustedWorkstation)
            .Select(policy => policy.ToolName)
            .ToArray();
        var lockedDownNames = AliToolPermissionPolicy
            .ProtectedToolsFor(AgentPermissionProfile.LockedDown)
            .Select(policy => policy.ToolName)
            .ToArray();

        Assert.Equal(expectedRetiredNames, retiredProductionNames);
        Assert.All(
            expectedRetiredNames,
            toolName => Assert.DoesNotContain(toolName, trustedNames));
        Assert.All(
            expectedRetiredNames,
            toolName => Assert.DoesNotContain(toolName, lockedDownNames));
        Assert.Contains(AliCapabilityCatalog.FileWriteName, trustedNames);
        Assert.Contains(AliCapabilityCatalog.FileReadName, lockedDownNames);
    }

    [Fact]
    public void PermissionProfile_DefaultsToTrustedWorkstationAndPersistsLockedDown()
    {
        WithStore((root, store) =>
        {
            Assert.Equal(AgentPermissionProfile.TrustedWorkstation, store.CurrentProfile);
            Assert.True(File.Exists(store.SettingsPath));
            Assert.True(File.Exists(store.InitializationMarkerPath));

            store.SetProfile(AgentPermissionProfile.LockedDown);

            var restored = new AgentToolPermissionStore(root);
            Assert.Equal(AgentPermissionProfile.LockedDown, restored.CurrentProfile);
        });
    }

    [Fact]
    public void DeletedPermissionFile_RemainsLockedDownAfterStoreRestart()
    {
        var root = NewRoot();
        try
        {
            var initial = new AgentToolPermissionStore(root);
            var user = User("alice", "Alice");
            initial.Save(user, "research_web", AgentToolPermissionScope.Tool, null);
            Assert.True(File.Exists(initial.InitializationMarkerPath));

            File.Delete(initial.SettingsPath);
            var restarted = new AgentToolPermissionStore(root);
            var snapshot = restarted.CaptureSnapshot();

            Assert.Equal(AgentPermissionProfile.LockedDown, snapshot.Profile);
            Assert.Empty(snapshot.Grants);
            Assert.False(restarted.TryMatch(user, "research_web", null, out _));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void UnreadableInitializationMarker_FailsClosedOnRestart()
    {
        var root = NewRoot();
        try
        {
            var initial = new AgentToolPermissionStore(root);
            initial.Save(
                User("alice", "Alice"),
                "research_web",
                AgentToolPermissionScope.Tool,
                null);

            using (new FileStream(
                       initial.InitializationMarkerPath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                var restarted = new AgentToolPermissionStore(root);
                var snapshot = restarted.CaptureSnapshot();

                Assert.Equal(AgentPermissionProfile.LockedDown, snapshot.Profile);
                Assert.Empty(snapshot.Grants);
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void PermissionRevision_IsMonotonicAcrossProfileAba()
    {
        WithStore((root, store) =>
        {
            var trusted = store.CaptureSnapshot();
            store.SetProfile(AgentPermissionProfile.LockedDown);
            var firstLocked = store.CaptureSnapshot();
            store.SetProfile(AgentPermissionProfile.TrustedWorkstation);
            var trustedAgain = store.CaptureSnapshot();
            store.SetProfile(AgentPermissionProfile.LockedDown);
            var secondLocked = store.CaptureSnapshot();

            Assert.Equal(AgentPermissionProfile.LockedDown, firstLocked.Profile);
            Assert.Equal(AgentPermissionProfile.LockedDown, secondLocked.Profile);
            Assert.Equal(AgentPermissionProfile.TrustedWorkstation, trustedAgain.Profile);
            Assert.Equal(4, new[]
            {
                trusted.Revision,
                firstLocked.Revision,
                trustedAgain.Revision,
                secondLocked.Revision
            }.Distinct(StringComparer.Ordinal).Count());
        });
    }

    [Fact]
    public void ExternalAba_IsDetectedByFileStateEvenWhenFinalBytesMatch()
    {
        WithStore((root, store) =>
        {
            store.SetProfile(AgentPermissionProfile.LockedDown);
            store.SetProfile(AgentPermissionProfile.TrustedWorkstation);
            var before = store.CaptureSnapshot();
            var original = File.ReadAllText(store.SettingsPath);
            var originalWriteTime = File.GetLastWriteTimeUtc(store.SettingsPath);

            File.WriteAllText(store.SettingsPath, original + " ");
            File.WriteAllText(store.SettingsPath, original);
            File.SetLastWriteTimeUtc(store.SettingsPath, originalWriteTime.AddSeconds(2));

            var after = store.CaptureSnapshot();

            Assert.Equal(AgentPermissionProfile.TrustedWorkstation, after.Profile);
            Assert.Empty(after.Grants);
            Assert.NotEqual(before.Revision, after.Revision);
        });
    }

    [Fact]
    public void ProfileAwareProjection_FollowsEveryLiveProfileChange()
    {
        var profile = AgentPermissionProfile.TrustedWorkstation;
        var policy = new AliToolPermissionPolicy(() => null, () => profile);
        var function = AIFunctionFactory.Create(
            () => "searched",
            AliCapabilityCatalog.SearchCurrentWebName,
            "Search current web sources.");
        var profileAware = Assert.IsType<CapabilityPermissionProjectionAIFunction>(
            policy.ApplyProfileAware(function));

        Assert.Null(profileAware.GetService<ApprovalRequiredAIFunction>());

        profile = AgentPermissionProfile.LockedDown;
        var lockedDown = profileAware.Project(profile);

        Assert.NotNull(lockedDown.GetService<ApprovalRequiredAIFunction>());

        profile = AgentPermissionProfile.TrustedWorkstation;
        var trustedAgain = profileAware.Project(profile);

        Assert.Null(trustedAgain.GetService<ApprovalRequiredAIFunction>());
    }

    [Fact]
    public void McpDiscovery_AlwaysDefaultsNewToolsToApprovalRequired()
    {
        WithStore((root, store) =>
        {
            var manager = new McpClientManager(root);
            var readOnly = new McpDiscoveredTool("read_file", "Read a file", true, false);
            var write = new McpDiscoveredTool("write_file", "Write a file", false, false);
            var destructive = new McpDiscoveredTool("delete_file", "Delete a file", false, true);

            Assert.True(manager.RequiresApprovalByDefault(readOnly));
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
    public void GrantRevocation_ChangesTheExactPermissionRevision()
    {
        WithStore((root, store) =>
        {
            var user = User("alice", "Alice");
            var saved = store.Save(
                user,
                "remember_for_current_user",
                AgentToolPermissionScope.Tool,
                null);
            var withGrant = store.CaptureSnapshot();

            Assert.True(store.Revoke(user.StableId, saved.Id));
            var revoked = store.CaptureSnapshot();

            Assert.NotEqual(withGrant.Revision, revoked.Revision);
            Assert.Empty(revoked.Grants);
            Assert.False(store.TryMatch(user, saved.ToolName, null, out _));
        });
    }

    [Fact]
    public void RevokeAll_ChangesTheExactPermissionRevision()
    {
        WithStore((root, store) =>
        {
            var user = User("alice", "Alice");
            store.Save(user, "research_web", AgentToolPermissionScope.Tool, null);
            store.Save(user, "create_calendar_event", AgentToolPermissionScope.Tool, null);
            var withGrants = store.CaptureSnapshot();

            Assert.Equal(2, store.RevokeAll(user.StableId));
            var revoked = store.CaptureSnapshot();

            Assert.NotEqual(withGrants.Revision, revoked.Revision);
            Assert.Empty(revoked.Grants);
        });
    }

    [Fact]
    public void RevokePersistenceFailure_LeavesGrantsAndRevisionUnchanged()
    {
        WithStore((root, store) =>
        {
            var user = User("alice", "Alice");
            var first = store.Save(user, "research_web", AgentToolPermissionScope.Tool, null);
            store.Save(user, "create_calendar_event", AgentToolPermissionScope.Tool, null);
            var before = store.CaptureSnapshot();

            using (new FileStream(
                       store.SettingsPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                var revokeFailure = Record.Exception(() => store.Revoke(user.StableId, first.Id));
                var revokeAllFailure = Record.Exception(() => store.RevokeAll(user.StableId));
                Assert.True(revokeFailure is IOException or UnauthorizedAccessException);
                Assert.True(revokeAllFailure is IOException or UnauthorizedAccessException);
                var after = store.CaptureSnapshot();

                Assert.Equal(before.Revision, after.Revision);
                Assert.Equal(before.Grants, after.Grants);
            }
        });
    }

    [Fact]
    public void UnreadableFileAfterStartup_FailsClosedAndReloadsWhenReadable()
    {
        WithStore((root, store) =>
        {
            var user = User("alice", "Alice");
            store.Save(user, "research_web", AgentToolPermissionScope.Tool, null);
            var readable = store.CaptureSnapshot();

            AgentToolPermissionSnapshot unreadable;
            using (new FileStream(
                       store.SettingsPath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                unreadable = store.CaptureSnapshot();
            }

            Assert.Equal(AgentPermissionProfile.LockedDown, unreadable.Profile);
            Assert.Empty(unreadable.Grants);
            Assert.NotEqual(readable.Revision, unreadable.Revision);

            var restored = store.CaptureSnapshot();
            Assert.Equal(AgentPermissionProfile.TrustedWorkstation, restored.Profile);
            Assert.Single(restored.Grants);
            Assert.NotEqual(unreadable.Revision, restored.Revision);
        });
    }

    [Fact]
    public void MissingFileAfterStartup_FailsClosedAndRevokesLoadedGrants()
    {
        WithStore((root, store) =>
        {
            var user = User("alice", "Alice");
            store.Save(user, "research_web", AgentToolPermissionScope.Tool, null);
            var before = store.CaptureSnapshot();

            File.Delete(store.SettingsPath);
            var after = store.CaptureSnapshot();

            Assert.Equal(AgentPermissionProfile.LockedDown, after.Profile);
            Assert.Empty(after.Grants);
            Assert.NotEqual(before.Revision, after.Revision);
            Assert.False(store.TryMatch(user, "research_web", null, out _));
        });
    }

    [Fact]
    public void PersistenceFailure_LeavesProfileGrantsAndRevisionUnchanged()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Permissions"), "directory blocker");
            var store = new AgentToolPermissionStore(root);
            var before = store.CaptureSnapshot();

            Assert.ThrowsAny<IOException>(() =>
                store.SetProfile(AgentPermissionProfile.LockedDown));
            Assert.ThrowsAny<IOException>(() =>
                store.Save(
                    User("alice", "Alice"),
                    "research_web",
                    AgentToolPermissionScope.Tool,
                    null));
            var after = store.CaptureSnapshot();

            Assert.Equal(before.Profile, after.Profile);
            Assert.Equal(before.Grants, after.Grants);
            Assert.Equal(before.Revision, after.Revision);
        }
        finally
        {
            DeleteRoot(root);
        }
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

            Assert.Equal(AgentPermissionProfile.LockedDown, store.CurrentProfile);
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
