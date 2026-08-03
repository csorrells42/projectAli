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
    public void Mem0Warmup_HasNoArtificialDeadlineThatCanKillAColdCpuEmbeddingWorker()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static;

        Assert.Null(typeof(Mem0UserMemoryService).GetField("WarmupAttemptTimeout", flags));
        Assert.Null(typeof(Mem0UserMemoryService).GetField("WarmupOverallTimeout", flags));
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
                "bill@example.test",
                "931-555-0100",
                "3075 SE St Lucie Blvd, Stuart, FL",
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
            Assert.Equal("3075 SE St Lucie Blvd, Stuart, FL", session.Current.Address);
            Assert.Equal("bill@example.test", session.Current.Email);
            Assert.Equal("931-555-0100", session.Current.PhoneNumber);
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
    public void ActiveUserSelectionGenerationChangesWhenRegistryMembershipChanges()
    {
        var root = TemporaryRoot();
        try
        {
            var profiles = new FakeIdentityProfiles([Person("person-a", "Alice")]);
            var session = new ActiveUserSession(root, profiles);
            var participantSession = new ParticipantIdentitySessionBoundary(session);
            var rosters = new SelectedParticipantRosterAuthority(participantSession, "tenant");
            var admitted = rosters.CaptureAtAdmission(
                "turn",
                "conversation",
                DateTimeOffset.UtcNow);
            var before = participantSession.CaptureSelectionRevision();

            profiles.Items = [Person("person-a", "Alice"), Person("person-b", "Bob")];
            session.Refresh();

            Assert.Equal("person-a", session.Current.StableId);
            Assert.NotEqual(before, participantSession.CaptureSelectionRevision());
            var stale = rosters.CheckCurrent(admitted);
            Assert.False(stale.IsCurrent);
            Assert.Equal(
                participantSession.CaptureSelectionRevision(),
                stale.CurrentSelectionGeneration);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void GeneratedTestProfileCannotBecomeRegisteredUnderTheSameIdWithoutGenerationChange()
    {
        var root = TemporaryRoot();
        try
        {
            var profiles = new FakeIdentityProfiles([]);
            var session = new ActiveUserSession(root, profiles);
            var participantSession = new ParticipantIdentitySessionBoundary(session);
            var testId = session.Current.StableId;
            var before = participantSession.CaptureSelectionRevision();

            profiles.Items = [Person(testId, "Registered Alice")];
            session.Refresh();

            Assert.Equal(testId, session.Current.StableId);
            Assert.False(session.Current.IsTestProfile);
            Assert.NotEqual(before, participantSession.CaptureSelectionRevision());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ActiveUserSelectionSnapshot_IsAtomicAndNeverExposesAProvisionalUser()
    {
        var root = TemporaryRoot();
        try
        {
            var profiles = new FakeIdentityProfiles([Person("person-a", "Alice"), Person("person-b", "Bob")]);
            var session = new ActiveUserSession(root, profiles);

            var unresolved = session.CaptureSelectionSnapshot();
            Assert.True(unresolved.RequiresSelection);
            Assert.False(unresolved.IsResolved);
            Assert.Null(unresolved.SelectedUser);

            session.Select("person-b");
            var resolved = session.CaptureSelectionSnapshot();
            Assert.False(resolved.RequiresSelection);
            Assert.True(resolved.IsResolved);
            Assert.Equal("person-b", resolved.SelectedUser?.StableId);
            Assert.NotSame(session.Current, resolved.SelectedUser);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ActiveUserSelectionRevision_DetectsAbaSelectionChanges()
    {
        var root = TemporaryRoot();
        try
        {
            var profiles = new FakeIdentityProfiles([Person("person-a", "Alice"), Person("person-b", "Bob")]);
            var session = new ActiveUserSession(root, profiles);
            session.Select("person-a");
            var firstA = session.CaptureSelectionRevision();
            session.Select("person-b");
            var userB = session.CaptureSelectionRevision();
            session.Select("person-a");
            var secondA = session.CaptureSelectionRevision();

            Assert.Equal("person-a", session.Current.StableId);
            Assert.NotEqual(firstA, userB);
            Assert.NotEqual(firstA, secondA);
            Assert.NotEqual(userB, secondA);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ActiveUserSelectionRevision_DetectsSameUserSelectionRequiredTransitions()
    {
        var root = TemporaryRoot();
        try
        {
            var profiles = new FakeIdentityProfiles([Person("person-a", "Alice")]);
            var session = new ActiveUserSession(root, profiles);
            var resolved = session.CaptureSelectionRevision();
            profiles.Items = [Person("person-a", "Alice"), Person("person-b", "Bob")];
            File.Delete(Path.Combine(root, "active-user-session.json"));

            session.Refresh();
            var selectionRequired = session.CaptureSelectionRevision();
            Assert.True(session.RequiresSelection);
            Assert.Equal("person-a", session.Current.StableId);
            Assert.NotEqual(resolved, selectionRequired);

            session.Select("person-a");
            Assert.False(session.RequiresSelection);
            Assert.NotEqual(selectionRequired, session.CaptureSelectionRevision());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UserBoundCoordinatorTools_KeepTheUserCapturedAtTurnAdmission()
    {
        var admittedUser = new ActiveUser("person-a", "Alice", false, "explicit-selection");
        var laterLiveUser = new ActiveUser("person-b", "Bob", false, "explicit-selection");
        var snapshot = ActiveUserSelectionSnapshot.Resolved(admittedUser);
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "request",
            static _ => { },
            snapshot,
            new Ali.Modules.Orchestration.Contracts.TurnIdentity(
                admittedUser.StableId,
                "conversation",
                "assistant-message"));
        var liveSession = new FakeActiveSession(laterLiveUser);
        var service = new CapturingMemoryService();
        var memoryTools = new AliMemoryTools(
            service,
            liveSession,
            () => new UserMemorySettings(),
            () => turn);

        await memoryTools.SearchAsync("calibration", TestContext.Current.CancellationToken);
        var profile = new AliActiveUserTools(liveSession, () => turn).GetActiveProfile();

        Assert.Equal(admittedUser.StableId, service.LastUser?.StableId);
        Assert.Equal(admittedUser.StableId, profile.StableId);
        Assert.NotEqual(laterLiveUser.StableId, profile.StableId);
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
    public void PersonalMemoryIsEnabledWithoutAnAutomaticPostReplyStorageSwitch()
    {
        var settings = new UserMemorySettings();

        Assert.True(settings.Enabled);
        Assert.Null(typeof(UserMemorySettings).GetProperty("AutomaticBackgroundLearning"));
    }

    [Fact]
    public void InitialInput_DoesNotPreQueryOrInjectFailedMemoryState()
    {
        var messages = AliAgentHarnessRunner.BuildInitialInput(
            [],
            "What is my name?",
            []);

        var userText = Assert.IsType<TextContent>(messages.Single().Contents.Single()).Text;
        Assert.Equal("What is my name?", userText);
    }

    [Fact]
    public void ActiveIdentityProfile_IsReturnedOnlyWhenTheModelCallsItsTool()
    {
        var user = new ActiveUser(
            "person-bill",
            "Bill Engineer",
            false,
            "identity-profile-selection",
            "3075 SE St Lucie Blvd, Stuart, FL",
            "bill@example.com",
            "555-0100");
        var turn = new CoordinatorTurnContext("conversation", "user", "assistant", "Where is home?", _ => { });
        var tool = new AliActiveUserTools(new FakeActiveSession(user), () => turn);

        var result = tool.GetActiveProfile();

        Assert.True(result.Selected);
        Assert.Equal("person-bill", result.StableId);
        Assert.Equal("Bill Engineer", result.DisplayName);
        Assert.Equal("3075 SE St Lucie Blvd, Stuart, FL", result.Address);
        Assert.Equal("bill@example.com", result.Email);
        Assert.Equal("555-0100", result.PhoneNumber);
        Assert.True(turn.UsedEvidenceTool);
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
        var mutationBlock = source[
            source.IndexOf("def handle_participant_mutation", StringComparison.Ordinal)..
            source.IndexOf("def handle_participant_repair", StringComparison.Ordinal)];

        Assert.DoesNotContain("self.memory.search", mutationBlock, StringComparison.Ordinal);
        Assert.Contains("proposal.get(\"targetMemoryId\")", mutationBlock, StringComparison.Ordinal);
        Assert.Contains("self.participant_owned(tenant_id, target_id)", mutationBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ParticipantWorkerInventoryIsUnfilteredByModelSelectedCategory()
    {
        var path = FindRepositoryFile("src", "Modules", "UserMemory", "Tools", "mem0_service.py");
        var source = File.ReadAllText(path);
        var listBlock = source[
            source.IndexOf("if operation == \"participant_list\":", StringComparison.Ordinal)..
            source.IndexOf("if operation == \"participant_recall\":", StringComparison.Ordinal)];

        Assert.DoesNotContain("category", listBlock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("metadata.state\": \"confirmed\"", listBlock, StringComparison.Ordinal);
        Assert.Contains("scroll_exact_filters(", listBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("maximum=maximum", listBlock, StringComparison.Ordinal);
        Assert.Contains("if len(eligible) > maximum:", listBlock, StringComparison.Ordinal);
        Assert.Contains("ordered = sorted(", listBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Mem0WorkerReturnsExplainableHybridRecallScores()
    {
        var path = FindRepositoryFile("src", "Modules", "UserMemory", "Tools", "mem0_service.py");
        var source = File.ReadAllText(path);
        var recallBlock = source[
            source.IndexOf("if operation == \"participant_recall\":", StringComparison.Ordinal)..
            source.IndexOf("if operation == \"participant_mutate\":", StringComparison.Ordinal)];

        Assert.Contains("search_exact_hybrid", recallBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("self.memory.search", recallBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("extract_entities", recallBlock, StringComparison.Ordinal);
        Assert.Contains("\"semanticScore\"", source, StringComparison.Ordinal);
        Assert.Contains("\"keywordScore\"", source, StringComparison.Ordinal);

        var adapter = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "UserMemory", "Tools", "local_qdrant.py"));
        var hybrid = adapter[
            adapter.IndexOf("def search_exact_hybrid", StringComparison.Ordinal)..
            adapter.IndexOf("def set_exact_metadata", StringComparison.Ordinal)];
        Assert.Contains("internal_limit = max(maximum * 4, 60)", hybrid, StringComparison.Ordinal);
        Assert.Contains("semantic_score < 0.1", hybrid, StringComparison.Ordinal);
        Assert.Contains("not math.isfinite(semantic_score)", hybrid, StringComparison.Ordinal);
        Assert.Contains("max(0.0, min(semantic_score, 1.0))", hybrid, StringComparison.Ordinal);
        Assert.Contains("max(0.0, min(bm25_score, 1.0))", hybrid, StringComparison.Ordinal);
        Assert.Contains("divisor = 2.0 if bm25_scores else 1.0", hybrid, StringComparison.Ordinal);
        Assert.Contains("(-float(value[\"score\"]), value[\"id\"])", hybrid, StringComparison.Ordinal);
        Assert.Contains("unicodedata.normalize(\"NFKC\"", adapter, StringComparison.Ordinal);
        Assert.Contains("k1 = 1.5", adapter, StringComparison.Ordinal);
        Assert.Contains("b = 0.75", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("lemmatize_for_bm25", adapter, StringComparison.Ordinal);
    }

    [Fact]
    public void ParticipantMutationJournal_StagesThenRedactsDeleteContentFailClosed()
    {
        var path = FindRepositoryFile("src", "Modules", "UserMemory", "Tools", "mem0_service.py");
        var source = File.ReadAllText(path);
        var journal = source[
            source.IndexOf("class ParticipantMutationJournal:", StringComparison.Ordinal)..
            source.IndexOf("class ParticipantMutationLease:", StringComparison.Ordinal)];
        var finalize = journal[
            journal.IndexOf("def finalize_delete", StringComparison.Ordinal)..];
        var mutation = source[
            source.IndexOf("def handle_participant_mutation", StringComparison.Ordinal)..
            source.IndexOf("def handle_participant_repair", StringComparison.Ordinal)];

        Assert.Contains("_maximum_receipts = 4096", journal, StringComparison.Ordinal);
        Assert.Contains("_maximum_receipt_bytes = 2 * 1024 * 1024", journal, StringComparison.Ordinal);
        Assert.Contains("status in {\"committed\", \"rolled_back\"}", journal, StringComparison.Ordinal);
        Assert.DoesNotContain("tombstone_version", journal, StringComparison.Ordinal);
        Assert.Contains("receipt.get(\"redacted\") is True", journal, StringComparison.Ordinal);
        Assert.Contains("now - started > self._rollback_window", journal, StringComparison.Ordinal);
        Assert.Contains("self._maintain(required_free=1)", journal, StringComparison.Ordinal);
        Assert.Contains("require_fresh_mutation_request_id", journal, StringComparison.Ordinal);
        Assert.Contains("outside its 24-hour admission window", journal, StringComparison.Ordinal);
        Assert.Contains("_quarantine_corrupt_receipt", journal, StringComparison.Ordinal);
        Assert.Contains("def require_classified_global_state", journal, StringComparison.Ordinal);
        Assert.Contains("self._temporary_name.fullmatch(name)", journal, StringComparison.Ordinal);
        Assert.Contains("self._quarantine_name.fullmatch(name)", journal, StringComparison.Ordinal);
        Assert.Contains("self.require_classified_global_state()", finalize, StringComparison.Ordinal);
        Assert.Contains("secrets.token_hex(16)", journal, StringComparison.Ordinal);
        Assert.Contains("os.O_EXCL", journal, StringComparison.Ordinal);
        Assert.Contains("getattr(os, \"O_NOFOLLOW\", 0)", journal, StringComparison.Ordinal);
        Assert.Contains("os.path.samestat", journal, StringComparison.Ordinal);
        Assert.Contains("st_nlink", journal, StringComparison.Ordinal);
        Assert.Contains("self._content_fields.intersection(persisted)", finalize, StringComparison.Ordinal);
        Assert.True(
            finalize.IndexOf("for path, receipt in matching:", StringComparison.Ordinal)
            < finalize.IndexOf("self._atomic_write(delete_path, tombstone)", StringComparison.Ordinal));
        Assert.True(
            mutation.IndexOf("self.bind_expected_record_contract(receipt, metadata)", StringComparison.Ordinal)
            < mutation.IndexOf("self.memory.add(", StringComparison.Ordinal));
        var prepared = mutation.IndexOf("\"status\": \"prepared\"", StringComparison.Ordinal);
        var metadataStart = mutation.IndexOf("metadata = None", prepared, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "self.mutation_journal.save(receipt)",
            mutation[prepared..metadataStart],
            StringComparison.Ordinal);
        Assert.Contains(
            "Persist only after every no-side-effect Add validation succeeds",
            mutation,
            StringComparison.Ordinal);
        Assert.True(
            mutation.IndexOf("self.require_authenticated_target_actor", metadataStart, StringComparison.Ordinal)
            < mutation.IndexOf("Persist only after target, access, actor", metadataStart, StringComparison.Ordinal));
        Assert.True(
            mutation.IndexOf("Persist only after target, access, actor", metadataStart, StringComparison.Ordinal)
            < mutation.IndexOf("self.acquire_mutation_lock", metadataStart, StringComparison.Ordinal));
        Assert.True(
            mutation.LastIndexOf("receipt[\"status\"] = \"delete_staged\"", StringComparison.Ordinal)
            < mutation.LastIndexOf("mutationStatus=\"delete_staged\"", StringComparison.Ordinal));
        Assert.Contains("request.get(\"finalizeDelete\") is True", source, StringComparison.Ordinal);
        Assert.Contains("deletionFinalized=", source, StringComparison.Ordinal);
        Assert.Contains("def validate_provenance", source, StringComparison.Ordinal);
        Assert.Contains("def validate_consent_receipts", source, StringComparison.Ordinal);
        Assert.Contains("dotnet_json_length", source, StringComparison.Ordinal);
        Assert.Contains("self._validate_receipt_structure(path, value)", journal, StringComparison.Ordinal);
        var finalizationMarker = finalize.IndexOf(
            "delete_receipt[\"status\"] = \"finalization_started\"",
            StringComparison.Ordinal);
        var finalizationMarkerSave = finalize.IndexOf(
            "self.save(delete_receipt)",
            finalizationMarker,
            StringComparison.Ordinal);
        var firstScrubWrite = finalize.IndexOf(
            "for path, receipt in matching:",
            finalizationMarkerSave,
            StringComparison.Ordinal);
        Assert.InRange(finalizationMarker, 0, int.MaxValue);
        Assert.True(finalizationMarker < finalizationMarkerSave);
        Assert.True(finalizationMarkerSave < firstScrubWrite);
        Assert.Contains("finalization_receipt_ids", finalize, StringComparison.Ordinal);
        Assert.Contains("if status == \"finalization_started\":", source, StringComparison.Ordinal);
        Assert.Contains("pinned_request_ids", journal, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "and len(actual_successor_ids) > 1",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("self.has_control(key)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("self.has_control(principal)", source, StringComparison.Ordinal);
        Assert.Contains("A crash may occur after the active point is deleted", source, StringComparison.Ordinal);
        Assert.Contains("and receipt.get(\"redacted\") is not True", source, StringComparison.Ordinal);
        Assert.Contains("if status == \"rollback_started\":", source, StringComparison.Ordinal);
        Assert.Contains("def resume_started_rollback", source, StringComparison.Ordinal);
        Assert.Contains("has_other_active_reference", source, StringComparison.Ordinal);
        Assert.Contains(
            "if str(receipt.get(\"status\") or \"\") != \"rollback_started\"",
            source,
            StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("A crash may occur after the active point is deleted", StringComparison.Ordinal)
            < source.IndexOf("if status == \"committed\":", StringComparison.Ordinal));
    }

    [Fact]
    public void Mem0Transport_DirtiesWorkerBeforeFirstCancellablePipeWrite()
    {
        var path = FindRepositoryFile("src", "Modules", "UserMemory", "Mem0ProcessClient.cs");
        var source = File.ReadAllText(path);
        var dirty = source.IndexOf("workerPipeMayBeDirty = true;", StringComparison.Ordinal);
        var write = source.IndexOf("StandardInput.WriteLineAsync", StringComparison.Ordinal);
        var flush = source.IndexOf("StandardInput.FlushAsync", StringComparison.Ordinal);

        Assert.InRange(dirty, 0, int.MaxValue);
        Assert.True(dirty < write);
        Assert.True(dirty < flush);
        Assert.Contains("if (workerPipeMayBeDirty && process is not null)", source, StringComparison.Ordinal);
        Assert.Contains("ResetProcess(process);", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RememberToolPreservesTheExactModelSelectedFact()
    {
        var user = new ActiveUser("person-a", "Alice", false, "explicit-selection");
        var service = new PersistingMemoryService();
        var tools = new AliMemoryTools(
            service,
            new FakeActiveSession(user),
            () => new UserMemorySettings(),
            static () => null);

        var saved = await tools.RememberAsync(
            "teal-anvil-6304",
            "general",
            TestContext.Current.CancellationToken);

        Assert.True(saved.Saved);
        Assert.Equal("teal-anvil-6304", service.StoredFact);
        Assert.Equal("model_selected_user_fact", service.StoredSource);
    }

    [Fact]
    public async Task BackgroundReview_DoesNotBlockEnqueueAndRecallWaitsForEarlierReview()
    {
        var user = new ActiveUser("person-a", "Alice", false, "explicit-selection");
        var service = new ReviewOrderingMemoryService();
        var queue = new AliUserMemoryReviewQueue(service, TimeSpan.Zero);
        var review = queue.Enqueue(user, "My calibration color is cobalt.");
        await service.ReviewStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(review.IsCompleted);

        var tools = new AliMemoryTools(
            service,
            new FakeActiveSession(user),
            () => new UserMemorySettings(),
            static () => null,
            queue.DrainAsync);
        var recall = tools.SearchAsync("What is my calibration color?", TestContext.Current.CancellationToken);

        Assert.False(recall.IsCompleted);
        Assert.Equal(0, service.RecallCount);

        service.ReleaseReview();
        await review.WaitAsync(TestContext.Current.CancellationToken);
        await recall.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, service.RecallCount);
        Assert.Equal("conversation", service.StoredSource);
    }

    [Fact]
    public async Task BackgroundReview_WaitsUntilForegroundTurnEnds()
    {
        var user = new ActiveUser("person-a", "Alice", false, "explicit-selection");
        var service = new ReviewOrderingMemoryService();
        var queue = new AliUserMemoryReviewQueue(service, TimeSpan.Zero);

        queue.BeginForegroundTurn();
        var review = queue.Enqueue(user, "My calibration color is cobalt.");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(service.ReviewStarted.Task.IsCompleted);
        Assert.False(review.IsCompleted);

        queue.EndForegroundTurn();
        await service.ReviewStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        service.ReleaseReview();
        await review.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TrustedWorkstationMemoryTool_SavesWithoutApprovalAndCanBeRecalled()
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

        var result = await agent.RunAsync(
            "Remember that my shop foreman is Bill.",
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("saved", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Messages
            .SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>());
        Assert.Equal("My shop foreman is Bill", service.StoredFact);
        Assert.Equal("model_selected_user_fact", service.StoredSource);
        var recalled = await memoryTools.SearchAsync("Who is my shop foreman?", TestContext.Current.CancellationToken);
        Assert.Contains(recalled.Memories, memory => memory.Text == "My shop foreman is Bill");
    }

    [Fact]
    public async Task DeniedNativeMemoryTool_DoesNotExecuteRetryOrMutate()
    {
        var invoked = false;
        var policy = new AliToolPermissionPolicy(
            static () => null,
            static () => AgentPermissionProfile.LockedDown);
        var mutationFunction = policy.Apply(AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<CoordinatorMemoryWriteResult>>)((text, _) =>
            {
                invoked = true;
                return Task.FromResult(new CoordinatorMemoryWriteResult(true, text));
            }),
            AliCapabilityCatalog.MutateParticipantMemoryName,
            "Mutate attributable participant memory through the exact approval boundary."));
        using var client = new ScriptedChatClient(
        [
            ToolCall(AliCapabilityCatalog.MutateParticipantMemoryName, new Dictionary<string, object?>
            {
                ["text"] = "This must not be saved"
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
                Tools = [mutationFunction],
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

        Assert.False(invoked);
        Assert.Contains("did not save", second.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, client.CallCount);
        Assert.Empty(second.Messages
            .SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>());
    }

    [Fact]
    public void McpPoliciesDoNotExposeLegacyFileMemoryUnderParticipantToolNames()
    {
        var policies = Ali.Modules.Mcp.McpServerToolCatalog.CreateDefaultPolicies();
        foreach (var name in new[]
        {
            AliCapabilityCatalog.RecallUserMemoryName,
            AliCapabilityCatalog.ForgetCurrentUserMemoryName,
            AliCapabilityCatalog.ListCurrentUserMemoriesName
        })
        {
            Assert.DoesNotContain(policies, item => item.Name == name);
        }
        Assert.DoesNotContain(policies, item => item.Name == AliCapabilityCatalog.RememberCurrentUserName);
        Assert.DoesNotContain(policies, item => item.Name == AliCapabilityCatalog.CorrectCurrentUserMemoryName);
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
        public ActiveUserSelectionSnapshot CaptureSelectionSnapshot() =>
            ActiveUserSelectionSnapshot.Resolved(Current);
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
        public string? StoredSource { get; private set; }

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
            StoredSource = source;
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

    private sealed class ReviewOrderingMemoryService : IUserMemoryService
    {
        private readonly TaskCompletionSource _releaseReview = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReviewStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RecallCount { get; private set; }
        public string? StoredSource { get; private set; }

        public void ReleaseReview() => _releaseReview.TrySetResult();

        public async Task<IReadOnlyList<UserMemory>> RecallAsync(
            ActiveUser user,
            string query,
            int maximumResults,
            CancellationToken cancellationToken)
        {
            RecallCount++;
            await Task.Yield();
            return [];
        }

        public async Task<MemoryOperationResult> RememberAsync(
            ActiveUser user,
            string conversation,
            string source,
            string? category,
            CancellationToken cancellationToken)
        {
            StoredSource = source;
            ReviewStarted.TrySetResult();
            await _releaseReview.Task.ConfigureAwait(false);
            return new MemoryOperationResult(true, "Reviewed.", []);
        }

        public Task<MemoryOperationResult> CorrectAsync(ActiveUser user, string memoryId, string correction, CancellationToken cancellationToken) =>
            Task.FromResult(new MemoryOperationResult(true, "Corrected.", []));

        public Task<IReadOnlyList<UserMemory>> ListAsync(ActiveUser user, string? category, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UserMemory>>([]);

        public Task<MemoryOperationResult> DeleteAsync(ActiveUser user, string memoryId, CancellationToken cancellationToken) =>
            Task.FromResult(new MemoryOperationResult(true, "Deleted.", []));

        public Task<UserMemoryStatus> TestAsync(ActiveUser user, CancellationToken cancellationToken) =>
            Task.FromResult(new UserMemoryStatus(true, true, true, "ready", "Ready."));
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
