using Ali.Modules.RAG;
using Ali.Modules.Runtime;
using Ali.Modules.UserMemory;

namespace Ali.Framework.Tests;

public sealed class ParticipantAwareMemoryTests
{
    private static readonly ParticipantMemoryReceiptAuthority TestReceiptAuthority = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        ParticipantMemoryProposal,
        TestProposalReceipts> ProposalReceipts = new();

    [Fact]
    public void CompletedTurn_DoesNotInvokeTheLegacyAutomaticMemoryReviewQueue()
    {
        var coordinator = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "Coordinator", "AliToolCoordinator.cs"));

        Assert.DoesNotContain(
            "QueueIncomingUserMemoryReview(turn, userText);",
            coordinator,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ModelVisibleParticipantRecallNeverFallsBackToTheLegacyMemoryStore()
    {
        var catalog = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "Coordinator", "AliToolCatalog.cs"));

        Assert.DoesNotContain("MemoryTools.SearchAsModelToolAsync", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("MemoryTools.ListCurrentAsync", catalog, StringComparison.Ordinal);
        Assert.Contains("No legacy memory store was read.", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void Roster_KeepsRecognitionAdvisoryAndUnknownParticipantsSessionScoped()
    {
        var selection = ActiveUserSelectionSnapshot.Resolved(User("alice", "Alice"));
        var roster = ParticipantRosterFactory.Capture(
            "tenant",
            "turn",
            "conversation",
            selection,
            "selection-7",
            "presence-12",
            new HashSet<string>(["alice", "bob"], StringComparer.Ordinal),
            [
                new("voice-bob", "bob", "Bob", ParticipantPresenceState.Present, "speaker-recognition", .91),
                new("camera-unknown", null, "Unknown visitor", ParticipantPresenceState.Present, "face-presence", .72),
                new("voice-guest", "unregistered-biometric-id", "Guest", ParticipantPresenceState.Present, "speaker-recognition", .63)
            ],
            DateTimeOffset.UtcNow);

        Assert.Equal("alice", roster.SelectedParticipantReference);
        Assert.Equal(ParticipantReferenceKind.Registered, roster.Find("bob")?.Kind);
        var unknown = Assert.Single(roster.Participants, participant =>
            participant.Kind == ParticipantReferenceKind.Unknown);
        var guest = Assert.Single(roster.Participants, participant =>
            participant.Kind == ParticipantReferenceKind.Guest);
        Assert.StartsWith("unknown:", unknown.ReferenceId, StringComparison.Ordinal);
        Assert.StartsWith("guest:", guest.ReferenceId, StringComparison.Ordinal);
        Assert.DoesNotContain("camera-unknown", unknown.ReferenceId, StringComparison.Ordinal);
        Assert.DoesNotContain("voice-guest", guest.ReferenceId, StringComparison.Ordinal);
        Assert.DoesNotContain(roster.Participants, participant =>
            participant.ReferenceId == "unregistered-biometric-id");
        Assert.Null(typeof(ParticipantRosterSnapshot).GetProperty("SpeakerParticipantReference"));
        Assert.Null(typeof(ParticipantRosterSnapshot).GetProperty("IsAuthenticated"));
    }

    [Fact]
    public void DirectObservation_PreservesSubjectWitnessAndMissingSpeaker()
    {
        var roster = Roster("alice", "bob");
        var proposal = Proposal(
            ParticipantMemoryMutationKind.Add,
            null,
            "Bob repaired the red bicycle while Alice watched.",
            null,
            ["bob"],
            ["alice"],
            ParticipantMemoryClaimKind.DirectObservation,
            ParticipantMemoryEvidenceKind.ObservedDirectly,
            ParticipantMemoryVisibility.Shared,
            ["alice", "bob"],
            consents: [Consent("alice", "Add", ["alice", "bob"]), Consent("bob", "Add", ["alice", "bob"])],
            reportedBy: "alice");

        var result = ParticipantMemoryPolicy.ValidateMutation(
            Request(roster, proposal, Authority("alice")),
            DateTimeOffset.UtcNow,
            TestReceiptAuthority);

        Assert.True(result.Valid, result.Failure?.SafeMessage);
        Assert.Null(result.Proposal?.SpeakerParticipantReference);
        Assert.Equal(["bob"], result.Proposal?.SubjectParticipantReferences);
        Assert.Equal(["alice"], result.Proposal?.WitnessParticipantReferences);
        Assert.Equal(ParticipantMemoryEvidenceKind.ObservedDirectly, result.Proposal?.EvidenceKind);
    }

    [Fact]
    public void ContentValidation_AllowsLineBreaksAndTabsButRejectsOtherControls()
    {
        var roster = Roster("alice");
        var allowed = Proposal(
            ParticipantMemoryMutationKind.Add,
            null,
            "Alice prefers\nblue\tworkspaces.",
            "alice",
            ["alice"],
            [],
            ParticipantMemoryClaimKind.Preference,
            ParticipantMemoryEvidenceKind.StatedDirectly,
            ParticipantMemoryVisibility.Private,
            ["alice"],
            consents: [Consent("alice", "Add", ["alice"])]);
        var rejected = Proposal(
            ParticipantMemoryMutationKind.Add,
            null,
            "Alice prefers\u0001blue workspaces.",
            "alice",
            ["alice"],
            [],
            ParticipantMemoryClaimKind.Preference,
            ParticipantMemoryEvidenceKind.StatedDirectly,
            ParticipantMemoryVisibility.Private,
            ["alice"],
            consents: [Consent("alice", "Add", ["alice"])]);

        var allowedResult = ParticipantMemoryPolicy.ValidateMutation(
            Request(roster, allowed, Authority("alice")),
            DateTimeOffset.UtcNow,
            TestReceiptAuthority);
        var rejectedResult = ParticipantMemoryPolicy.ValidateMutation(
            Request(roster, rejected, Authority("alice")),
            DateTimeOffset.UtcNow,
            TestReceiptAuthority);

        Assert.True(allowedResult.Valid, allowedResult.Failure?.SafeMessage);
        Assert.False(rejectedResult.Valid);
        Assert.Equal(ParticipantMemoryFailureCode.InvalidProposal, rejectedResult.Failure?.Code);
    }

    [Fact]
    public void Hearsay_RemainsTheSpeakersClaimAboutTheSubject()
    {
        var roster = Roster("alice", "bob");
        var proposal = Proposal(
            ParticipantMemoryMutationKind.Add,
            null,
            "Alice reports that Bob prefers jasmine tea.",
            "alice",
            ["bob"],
            [],
            ParticipantMemoryClaimKind.Hearsay,
            ParticipantMemoryEvidenceKind.ReportedByParticipant,
            ParticipantMemoryVisibility.Shared,
            ["alice", "bob"],
            consents: [Consent("alice", "Add", ["alice", "bob"]), Consent("bob", "Add", ["alice", "bob"])]);

        var result = ParticipantMemoryPolicy.ValidateMutation(
            Request(roster, proposal, Authority("alice")),
            DateTimeOffset.UtcNow,
            TestReceiptAuthority);

        Assert.True(result.Valid, result.Failure?.SafeMessage);
        Assert.Equal("alice", result.Proposal?.SpeakerParticipantReference);
        Assert.Equal(["bob"], result.Proposal?.SubjectParticipantReferences);
        Assert.Equal(ParticipantMemoryClaimKind.Hearsay, result.Proposal?.ClaimKind);
        Assert.Equal("alice", result.Provenance?.ReportedByParticipantReference);
    }

    [Fact]
    public void ConsentSession_BindsAtomicallyToTheFirstStableMutationRequestId()
    {
        var roster = Roster("alice");
        var proposal = Proposal(
            ParticipantMemoryMutationKind.Add,
            null,
            "Alice prefers cobalt workspaces.",
            "alice",
            ["alice"],
            [],
            ParticipantMemoryClaimKind.Preference,
            ParticipantMemoryEvidenceKind.StatedDirectly,
            ParticipantMemoryVisibility.Private,
            ["alice"],
            consents: [Consent("alice", "Add", ["alice"])]);
        var original = Request(roster, proposal, Authority("alice")) with
        {
            RequestId = "stable-mutation-a"
        };

        var first = ParticipantMemoryPolicy.ValidateMutation(
            original,
            DateTimeOffset.UtcNow,
            TestReceiptAuthority);
        var sameRequestRetry = ParticipantMemoryPolicy.ValidateMutation(
            original,
            DateTimeOffset.UtcNow,
            TestReceiptAuthority);
        var differentRequest = ParticipantMemoryPolicy.ValidateMutation(
            original with { RequestId = "stable-mutation-b" },
            DateTimeOffset.UtcNow,
            TestReceiptAuthority);

        Assert.True(first.Valid, first.Failure?.SafeMessage);
        Assert.True(sameRequestRetry.Valid, sameRequestRetry.Failure?.SafeMessage);
        Assert.False(differentRequest.Valid);
        Assert.Equal(ParticipantMemoryFailureCode.ConsentRequired, differentRequest.Failure?.Code);
    }

    [Fact]
    public void ConsentSessionLedger_ReclaimsBindingsWhoseReceiptsWereEvicted()
    {
        var authority = new ParticipantMemoryReceiptAuthority();
        var now = DateTimeOffset.UtcNow;
        var permission = authority.IssuePermission(
            "alice",
            ["Add"],
            "ledger-permission",
            "test",
            now,
            TimeSpan.FromMinutes(10));
        for (var index = 0; index < 4_096; index++)
        {
            var consent = authority.IssueConsent(
                permission,
                "Add",
                "proposal",
                $"ledger-session-{index}",
                ParticipantMemoryVisibility.Private,
                ["alice"],
                "turn",
                now,
                TimeSpan.FromMinutes(10));
            Assert.True(authority.TryBindConsentSession([consent], $"mutation-{index}"));
        }

        var replacementPermission = authority.IssuePermission(
            "alice",
            ["Add"],
            "replacement-ledger-permission",
            "test",
            now,
            TimeSpan.FromMinutes(10));
        var replacementConsent = authority.IssueConsent(
            replacementPermission,
            "Add",
            "proposal",
            "replacement-ledger-session",
            ParticipantMemoryVisibility.Private,
            ["alice"],
            "turn",
            now,
            TimeSpan.FromMinutes(10));

        Assert.True(authority.TryBindConsentSession(
            [replacementConsent],
            "replacement-mutation"));
    }

    [Fact]
    public void Correction_RequiresExactTargetAuthenticationAndAppendOnlyLineage()
    {
        var roster = Roster("alice");
        var proposal = Proposal(
            ParticipantMemoryMutationKind.Correct,
            "memory-original",
            "Alice's preferred spelling is Alyce.",
            "alice",
            ["alice"],
            [],
            ParticipantMemoryClaimKind.DirectStatement,
            ParticipantMemoryEvidenceKind.StatedDirectly,
            ParticipantMemoryVisibility.Private,
            ["alice"],
            consents: [Consent("alice", "Correct", ["alice"])]);
        var authority = Authority(
            "alice",
            ParticipantMemoryAuthenticationKind.Passkey,
            ["Correct"]);

        var result = ParticipantMemoryPolicy.ValidateMutation(
            Request(roster, proposal, authority),
            DateTimeOffset.UtcNow,
            TestReceiptAuthority);
        var worker = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "UserMemory", "Tools", "mem0_service.py"));

        Assert.True(result.Valid, result.Failure?.SafeMessage);
        Assert.Equal("memory-original", result.Proposal?.TargetMemoryId);
        Assert.Contains("metadata[\"corrects_memory_id\"] = target_id", worker, StringComparison.Ordinal);
        Assert.Contains("\"superseded\" if mutation == \"correct\" else \"disputed\"", worker, StringComparison.Ordinal);
        Assert.Contains("metadata[\"state\"] = \"candidate\"", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("self.memory.update(target_id, text=", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedExperience_ProducesExplicitAudienceKeysForEveryParticipant()
    {
        var roster = Roster("alice", "bob", "charlie");
        var audience = new[] { "alice", "bob", "charlie" };
        var proposal = Proposal(
            ParticipantMemoryMutationKind.Add,
            null,
            "Alice, Bob, and Charlie completed the garden project together.",
            "alice",
            audience,
            audience,
            ParticipantMemoryClaimKind.SharedExperience,
            ParticipantMemoryEvidenceKind.StatedDirectly,
            ParticipantMemoryVisibility.Shared,
            audience,
            sharedEvent: "event:garden-project",
            consents: audience.Select(value => Consent(value, "Add", audience)).ToArray());

        var result = ParticipantMemoryPolicy.ValidateMutation(
            Request(roster, proposal, Authority("alice")),
            DateTimeOffset.UtcNow,
            TestReceiptAuthority);
        var keys = ParticipantMemoryPolicy.BuildAccessKeys(
            ParticipantMemoryVisibility.Shared,
            ParticipantMemorySensitivity.Low,
            audience);

        Assert.True(result.Valid, result.Failure?.SafeMessage);
        Assert.Equal("event:garden-project", result.Proposal?.SharedEventReference);
        Assert.Equal(
            ["participant:alice:low", "participant:bob:low", "participant:charlie:low"],
            keys);
    }

    [Fact]
    public void AmbiguousSpeaker_IsExplicitAndNeverForcedIntoDurableAttribution()
    {
        var selection = ActiveUserSelectionSnapshot.Resolved(User("alice", "Alice"));
        var roster = ParticipantRosterFactory.Capture(
            "tenant",
            "turn",
            "conversation",
            selection,
            "selection",
            "presence",
            new HashSet<string>(["alice"], StringComparer.Ordinal),
            [new("voice-unknown", null, "Unknown speaker", ParticipantPresenceState.Present, "speaker-recognition", .51)],
            DateTimeOffset.UtcNow);
        var unknownReference = Assert.Single(roster.Participants, participant =>
            participant.Kind == ParticipantReferenceKind.Unknown).ReferenceId;
        var proposal = Proposal(
            ParticipantMemoryMutationKind.Add,
            null,
            "The unknown speaker likes quiet rooms.",
            unknownReference,
            [],
            [],
            ParticipantMemoryClaimKind.Preference,
            ParticipantMemoryEvidenceKind.StatedDirectly,
            ParticipantMemoryVisibility.General,
            [],
            consents: [Consent(unknownReference, "Add", [])]);

        var result = ParticipantMemoryPolicy.ValidateMutation(
            Request(roster, proposal, Authority("alice")),
            DateTimeOffset.UtcNow,
            TestReceiptAuthority);

        Assert.False(result.Valid);
        Assert.Equal(ParticipantMemoryFailureCode.AmbiguousIdentity, result.Failure?.Code);
        Assert.Contains("cannot be forced", result.Failure?.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrivacyFilters_ArePrincipalScopedBeforeSemanticScoring()
    {
        var alicePrivate = ParticipantMemoryPolicy.BuildAccessKeys(
            ParticipantMemoryVisibility.Private,
            ParticipantMemorySensitivity.Low,
            ["alice"]);
        var aliceKeys = ParticipantMemoryPolicy.BuildAuthorizedRecallKeys(
            Authority("alice"),
            DateTimeOffset.UtcNow,
            TestReceiptAuthority);
        var bobKeys = ParticipantMemoryPolicy.BuildAuthorizedRecallKeys(
            Authority("bob"),
            DateTimeOffset.UtcNow,
            TestReceiptAuthority);
        var worker = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "UserMemory", "Tools", "mem0_service.py"));

        Assert.Contains("participant:alice:low", aliceKeys);
        Assert.DoesNotContain("participant:alice:low", bobKeys);
        Assert.Contains(alicePrivate.Single(), aliceKeys);
        Assert.Contains("\"metadata.access_keys\": str(access_key)", worker, StringComparison.Ordinal);
        Assert.Contains("search_exact_hybrid(", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("self.memory.search(", worker, StringComparison.Ordinal);
        Assert.True(
            worker.IndexOf("\"metadata.access_keys\": str(access_key)", StringComparison.Ordinal)
            < worker.IndexOf("maximum,", worker.IndexOf("participant_recall", StringComparison.Ordinal), StringComparison.Ordinal));
    }

    [Fact]
    public void RecognitionCannotAuthorizeDestructiveMemoryOperations()
    {
        var roster = Roster("alice");
        var proposal = Proposal(
            ParticipantMemoryMutationKind.Delete,
            "memory-1",
            string.Empty,
            null,
            [],
            [],
            ParticipantMemoryClaimKind.Other,
            ParticipantMemoryEvidenceKind.Unknown,
            ParticipantMemoryVisibility.Private,
            ["alice"]);
        var recognition = ParticipantMemoryPolicy.ValidateMutation(
            Request(
                roster,
                proposal,
                Authority("alice", ParticipantMemoryAuthenticationKind.FaceRecognition, ["Delete"])),
            DateTimeOffset.UtcNow,
            TestReceiptAuthority);
        var passkey = ParticipantMemoryPolicy.ValidateMutation(
            Request(
                roster,
                proposal,
                Authority("alice", ParticipantMemoryAuthenticationKind.Passkey, ["Delete"])),
            DateTimeOffset.UtcNow,
            TestReceiptAuthority);

        Assert.False(recognition.Valid);
        Assert.Equal(ParticipantMemoryFailureCode.AuthenticationRequired, recognition.Failure?.Code);
        Assert.True(passkey.Valid, passkey.Failure?.SafeMessage);
    }

    [Fact]
    public async Task DisabledParticipantMemory_DoesNotStartMem0OrQdrant()
    {
        var root = Path.Combine(Path.GetTempPath(), "ali-cp11-disabled-memory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var qdrant = new QdrantServiceManager(root);
            await using var client = new Mem0ProcessClient(
                root,
                qdrant,
                SharedVectorSettings,
                static () => new UserMemorySettings { Enabled = false },
                RuntimeSettings);
            await using var service = new Mem0UserMemoryService(
                client,
                static () => new UserMemorySettings { Enabled = false });

            var health = await service.CheckParticipantHealthAsync(
                Roster("alice"),
                ParticipantMemoryAuthorityContext.Anonymous,
                TestContext.Current.CancellationToken);

            Assert.False(health.Enabled);
            Assert.False(health.Mem0Available);
            Assert.False(health.QdrantAvailable);
            Assert.Equal("Stopped", qdrant.Status.State);
            Assert.False(Directory.Exists(client.DataRoot));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void FreshStoreAndEmbeddingIdentity_RejectEveryDifferentVectorMeaning()
    {
        var settings = SharedVectorSettings();
        Assert.Throws<InvalidOperationException>(() =>
            Mem0ProcessClient.ResolveEmbeddingConfiguration(settings));
        var baselineEmbedding = Mem0ProcessClient.ResolveEmbeddingConfiguration(
            settings,
            new FixedIdentitySource("none-v1"));
        var baseline = Mem0ProcessClient.ResolveEmbeddingSpace(
            Path.Combine("data", Mem0ProcessClient.FreshRelativeDataRoot),
            UserMemorySettings.FreshParticipantCollectionName,
            baselineEmbedding,
            settings);
        var alternativeEmbedding = Mem0ProcessClient.ResolveEmbeddingConfiguration(
            settings,
            new FixedIdentitySource("prefix-v1"));
        var alternative = Mem0ProcessClient.ResolveEmbeddingSpace(
            Path.Combine("data", Mem0ProcessClient.FreshRelativeDataRoot),
            UserMemorySettings.FreshParticipantCollectionName,
            alternativeEmbedding,
            settings);

        Assert.Equal("ali_participant_memories_cp11", UserMemorySettings.FreshParticipantCollectionName);
        Assert.Contains(Path.Combine("Memory", "ParticipantAware", "Mem0"), baseline.DataRoot, StringComparison.Ordinal);
        Assert.NotEqual(baseline.Id, alternative.Id);
        Assert.NotEqual(baseline.CollectionName, alternative.CollectionName);
        Assert.NotEqual(baselineEmbedding.Identity.Fingerprint, alternativeEmbedding.Identity.Fingerprint);
    }

    [Fact]
    public void WorkerMutationBoundary_PersistsStableDedupAndExplicitRecoveryReceipts()
    {
        var worker = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "UserMemory", "Tools", "mem0_service.py"));
        var reconcileStart = worker.IndexOf("def handle_participant_reconcile", StringComparison.Ordinal);
        var rollbackStart = worker.IndexOf("def handle_participant_rollback", StringComparison.Ordinal);
        var mutationStart = worker.IndexOf("def handle_participant_mutation", StringComparison.Ordinal);

        Assert.Contains("class ParticipantMutationJournal", worker, StringComparison.Ordinal);
        Assert.Contains("class ParticipantMutationLease", worker, StringComparison.Ordinal);
        Assert.Contains("msvcrt.LK_NBLCK", worker, StringComparison.Ordinal);
        Assert.Contains("fcntl.LOCK_EX | fcntl.LOCK_NB", worker, StringComparison.Ordinal);
        Assert.Equal(4, worker.Split("@participant_single_writer", StringSplitOptions.None).Length - 1);
        Assert.Contains("mutationRequestId", worker, StringComparison.Ordinal);
        Assert.Contains("mutation_request_id", worker, StringComparison.Ordinal);
        Assert.Contains("os.fsync(stream.fileno())", worker, StringComparison.Ordinal);
        Assert.Contains("os.replace(temporary, path)", worker, StringComparison.Ordinal);
        Assert.Contains("stable mutation request ID was reused with different content", worker, StringComparison.Ordinal);
        Assert.Contains("mutationOperation=receipt[\"operation\"]", worker, StringComparison.Ordinal);
        Assert.Contains("error.details[\"mutationOperation\"] = receipt.get(\"operation\")", worker, StringComparison.Ordinal);
        Assert.True(reconcileStart >= 0 && rollbackStart > reconcileStart && mutationStart > rollbackStart);
        var reconcile = worker[reconcileStart..rollbackStart];
        var rollbackIntent = reconcile.LastIndexOf(
            "receipt[\"status\"] = \"rollback_started\"",
            StringComparison.Ordinal);
        var durableIntent = reconcile.LastIndexOf(
            "self.mutation_journal.save(receipt)",
            StringComparison.Ordinal);
        var resumeRollback = reconcile.LastIndexOf(
            "return self.resume_started_rollback(",
            StringComparison.Ordinal);
        Assert.InRange(rollbackIntent, 0, int.MaxValue);
        Assert.True(rollbackIntent < durableIntent && durableIntent < resumeRollback);
        Assert.DoesNotContain("if self.rollback_exact_partial_mutation(receipt):", reconcile, StringComparison.Ordinal);
        Assert.Contains("restore_exact_point(snapshot)", worker, StringComparison.Ordinal);
        Assert.Contains("self.memory.vector_store.replace_exact_payload(", worker, StringComparison.Ordinal);
        Assert.Contains("snapshot[\"payload\"],", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerMutationBoundary_EnforcesExactAudienceCurrentStateAndOneSuccessor()
    {
        var worker = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "UserMemory", "Tools", "mem0_service.py"));
        var mutationStart = worker.IndexOf("def handle_participant_mutation", StringComparison.Ordinal);
        var repairStart = worker.IndexOf("def handle_participant_repair", mutationStart, StringComparison.Ordinal);
        var mutation = worker[mutationStart..repairStart];
        var targetActorStart = worker.IndexOf("def require_authenticated_target_actor", StringComparison.Ordinal);
        var metadataStart = worker.IndexOf("def participant_metadata", targetActorStart, StringComparison.Ordinal);
        var targetActor = worker[targetActorStart..metadataStart];

        Assert.Contains("sorted(stored_keys) != expected_keys", worker, StringComparison.Ordinal);
        Assert.Contains("if current and state != \"confirmed\"", worker, StringComparison.Ordinal);
        Assert.Contains("record_access_keys.intersection(authorized)", worker, StringComparison.Ordinal);
        Assert.Contains("A correction or dispute cannot silently change the exact target audience", mutation, StringComparison.Ordinal);
        Assert.Contains("if len(successors) != 1", mutation, StringComparison.Ordinal);
        Assert.Contains("if len(self.mutation_points(tenant_id, request_id)) != 1", mutation, StringComparison.Ordinal);
        Assert.True(
            mutation.IndexOf("if len(successors) != 1", StringComparison.Ordinal)
            < mutation.IndexOf("self.update_participant_state(", StringComparison.Ordinal));
        Assert.Contains("metadata[\"state\"] = \"candidate\"", mutation, StringComparison.Ordinal);
        Assert.Contains("requestingParticipantReference", targetActor, StringComparison.Ordinal);
        Assert.Contains("requestingParticipantAuthenticated", targetActor, StringComparison.Ordinal);
        Assert.Contains("authenticated is not True", targetActor, StringComparison.Ordinal);
        Assert.Contains("speaker, subject, or reporter", targetActor, StringComparison.Ordinal);
        Assert.DoesNotContain("audience_participant_references", targetActor, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerHealth_EmitsEveryExplicitReadinessComponent()
    {
        var worker = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "UserMemory", "Tools", "mem0_service.py"));
        var healthStart = worker.IndexOf("if operation == \"participant_health\"", StringComparison.Ordinal);
        var reconcileStart = worker.IndexOf("if operation == \"participant_reconcile_mutation\"", healthStart, StringComparison.Ordinal);
        var health = worker[healthStart..reconcileStart];

        Assert.Contains("embeddingAvailable=True", health, StringComparison.Ordinal);
        Assert.Contains("mem0Available=True", health, StringComparison.Ordinal);
        Assert.Contains("qdrantAvailable=True", health, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerCollection_RejectsLegacyAndAppliesVerifiedExactPromptPrefixes()
    {
        var worker = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "UserMemory", "Tools", "mem0_service.py"));
        var listStart = worker.IndexOf("if operation == \"participant_list\"", StringComparison.Ordinal);
        var recallStart = worker.IndexOf("if operation == \"participant_recall\"", listStart, StringComparison.Ordinal);
        var list = worker[listStart..recallStart];

        Assert.Contains("mode == \"none-v1\" and prefix == \"\"", worker, StringComparison.Ordinal);
        Assert.Contains("mode == \"prefix-v1\" and prefix.strip()", worker, StringComparison.Ordinal);
        Assert.Contains("utf16_length(prefix) > 128", worker, StringComparison.Ordinal);
        Assert.Contains("embedded_query = self.embedding_query_prefix + query", worker, StringComparison.Ordinal);
        Assert.Contains("self.embedding_document_prefix + display_text", worker, StringComparison.Ordinal);
        Assert.Contains("\"display_text\": text", worker, StringComparison.Ordinal);
        Assert.Contains("Legacy user-memory operations cannot enter", worker, StringComparison.Ordinal);
        Assert.Contains("\"metadata.state\": \"confirmed\"", list, StringComparison.Ordinal);
        Assert.Contains("scroll_exact_filters", list, StringComparison.Ordinal);
        Assert.DoesNotContain("self.memory.search(", list, StringComparison.Ordinal);
        Assert.Contains("class BoundedRedactingStderr", worker, StringComparison.Ordinal);
        Assert.Contains("Participant memory failed safely at the worker boundary", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("type(error).__name__", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("\"message\": str(error)", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void HybridRepair_IsExplicitBoundedAndReportsDegradedCoverage()
    {
        var worker = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "UserMemory", "Tools", "mem0_service.py"));
        var qdrant = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "UserMemory", "Tools", "local_qdrant.py"));
        var repairStart = worker.IndexOf("def handle_participant_repair", StringComparison.Ordinal);
        var participantStart = worker.IndexOf("def handle_participant(self", repairStart, StringComparison.Ordinal);
        var repair = worker[repairStart..participantStart];

        Assert.Contains("participant_repair_hybrid", worker, StringComparison.Ordinal);
        Assert.Contains("repairPointIds", worker, StringComparison.Ordinal);
        Assert.Contains("len(requested_ids) > 32", repair, StringComparison.Ordinal);
        Assert.Contains("authorization_failed_ids", repair, StringComparison.Ordinal);
        Assert.Contains("repairable_ids", repair, StringComparison.Ordinal);
        Assert.Contains("repair[\"failed_ids\"] = failed_ids", repair, StringComparison.Ordinal);
        Assert.DoesNotContain("point_ids = list(self.hybrid_status", repair, StringComparison.Ordinal);
        Assert.Contains("inspect_hybrid_indexed", qdrant, StringComparison.Ordinal);
        Assert.Contains("repair_hybrid_indexed", qdrant, StringComparison.Ordinal);
        Assert.Contains("len(point_ids) > 32", qdrant, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"ready\" if not pending_ids else \"degraded\"", qdrant, StringComparison.Ordinal);
        Assert.Contains("Never install an empty sparse vector", qdrant, StringComparison.Ordinal);
        Assert.DoesNotContain("ensure_hybrid_indexed()", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpTransport_RequiresExactListMutationRecoveryAndRepairReceipts()
    {
        var service = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "UserMemory", "Mem0UserMemoryService.cs"));
        var client = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "UserMemory", "Mem0ProcessClient.cs"));

        Assert.Contains("operation = \"participant_list\"", service, StringComparison.Ordinal);
        Assert.Contains("authorizedAccessKeys = accessKeys", service, StringComparison.Ordinal);
        Assert.Contains("operation = \"participant_reconcile_mutation\"", service, StringComparison.Ordinal);
        Assert.Contains("operation = \"participant_rollback_mutation\"", service, StringComparison.Ordinal);
        Assert.Contains("operation = \"participant_repair_hybrid\"", service, StringComparison.Ordinal);
        Assert.Contains("mutationRequestId = request.RequestId", service, StringComparison.Ordinal);
        Assert.Contains("response.MutationStatus, \"committed\"", service, StringComparison.Ordinal);
        Assert.Contains("response.Reconciled != true", service, StringComparison.Ordinal);
        Assert.Contains("rollback.MutationStatus, \"rolled_back\"", service, StringComparison.Ordinal);
        Assert.Contains("string? MutationStatus = null", client, StringComparison.Ordinal);
        Assert.Contains("string? MutationRequestId = null", client, StringComparison.Ordinal);
        Assert.Contains("bool? Reconciled = null", client, StringComparison.Ordinal);
    }

    private static ParticipantMemoryMutationRequest Request(
        ParticipantRosterSnapshot roster,
        ParticipantMemoryProposal proposal,
        ParticipantMemoryAuthorityContext authority)
    {
        ProposalReceipts.TryGetValue(proposal, out var receipts);
        return new(
            "request",
            roster,
            authority,
            proposal,
            "space",
            new ParticipantMemoryProvenance(
                roster.TurnId,
                "message",
                "test",
                DateTimeOffset.UtcNow,
                proposal.ReportedByParticipantReference),
            receipts?.Consents ?? []);
    }

    private static ParticipantMemoryProposal Proposal(
        ParticipantMemoryMutationKind operation,
        string? target,
        string text,
        string? speaker,
        IReadOnlyList<string> subjects,
        IReadOnlyList<string> witnesses,
        ParticipantMemoryClaimKind claimKind,
        ParticipantMemoryEvidenceKind evidenceKind,
        ParticipantMemoryVisibility visibility,
        IReadOnlyList<string> audience,
        string? sharedEvent = null,
        IReadOnlyList<TestConsentDraft>? consents = null,
        string? reportedBy = null)
    {
        var proposal = new ParticipantMemoryProposal(
            operation,
            target,
            text,
            operation is ParticipantMemoryMutationKind.Delete
                or ParticipantMemoryMutationKind.Revoke
                or ParticipantMemoryMutationKind.Archive
                ? string.Empty
                : "people",
            speaker,
            subjects,
            witnesses,
            sharedEvent,
            claimKind,
            evidenceKind,
            visibility,
            audience,
            ParticipantMemorySensitivity.Low,
            .91,
            reportedBy ?? (claimKind == ParticipantMemoryClaimKind.Hearsay ? speaker : null));
        var now = DateTimeOffset.UtcNow;
        var proposalFingerprint = ParticipantMemoryProposalFingerprint.Create(proposal, "tenant");
        var consentSessionId = $"test-consent-session:{Guid.NewGuid():N}";
        var receipts = (consents ?? []).Select(consent =>
        {
            var permission = TestReceiptAuthority.IssuePermission(
                consent.Participant,
                [consent.Operation],
                $"test-consent:{Guid.NewGuid():N}",
                "test",
                now,
                TimeSpan.FromMinutes(5));
            return TestReceiptAuthority.IssueConsent(
                permission,
                consent.Operation,
                proposalFingerprint,
                consentSessionId,
                visibility,
                consent.Audience,
                "turn",
                now,
                TimeSpan.FromMinutes(5));
        }).ToArray();
        ProposalReceipts.Add(proposal, new TestProposalReceipts(receipts));
        return proposal;
    }

    private static TestConsentDraft Consent(
        string participant,
        string operation,
        IReadOnlyList<string> audience) =>
        new(participant, operation, audience);

    private static ParticipantMemoryAuthorityContext Authority(
        string participant,
        ParticipantMemoryAuthenticationKind? kind = null,
        IReadOnlyList<string>? operations = null)
    {
        var now = DateTimeOffset.UtcNow;
        var grants = operations ??
            ["Add", "Correct", "Dispute", "Revoke", "Archive", "Delete", "Read", "Repair"];
        var permission = TestReceiptAuthority.IssuePermission(
            participant,
            grants,
            $"test-authority:{Guid.NewGuid():N}",
            "test",
            now,
            TimeSpan.FromMinutes(5));
        ParticipantMemoryAuthenticationReceipt? authentication = kind switch
        {
            null => null,
            ParticipantMemoryAuthenticationKind.FaceRecognition
                or ParticipantMemoryAuthenticationKind.SpeakerRecognition
                or ParticipantMemoryAuthenticationKind.PassivePresence =>
                new ParticipantMemoryAuthenticationReceipt(
                    $"untrusted-recognition:{Guid.NewGuid():N}",
                    participant,
                    kind.Value,
                    now,
                    now.AddMinutes(5),
                    grants),
            _ => TestReceiptAuthority.IssueTestAuthentication(
                participant,
                grants,
                now,
                TimeSpan.FromMinutes(5))
        };
        return new ParticipantMemoryAuthorityContext(participant, authentication, [])
        {
            Permission = permission
        };
    }

    private static ParticipantRosterSnapshot Roster(params string[] participants) =>
        new ParticipantRosterSnapshot(
            "tenant",
            "turn",
            "conversation",
            DateTimeOffset.UtcNow,
            "selection",
            "presence",
            participants.FirstOrDefault(),
            participants.Select(value => new ParticipantReference(
                value,
                value,
                ParticipantReferenceKind.Registered,
                ParticipantPresenceState.Present,
                "test",
                1)).ToArray()).Normalize();

    private static ActiveUser User(string id, string name) =>
        new(id, name, false, "explicit-selection");

    private static LocalVectorLibrarySettings SharedVectorSettings() => new()
    {
        EmbeddingProvider = "custom",
        EmbeddingEndpoint = "http://127.0.0.1:9123/v1/embeddings",
        EmbeddingModel = "configured-model",
        EmbeddingDimensions = 768,
        UseManagedLocalQdrant = false,
        AutoStartQdrant = false
    };

    private static OpenAiCompatibleRuntimeOptions RuntimeSettings() => new(
        Enabled: true,
        Endpoint: new Uri("http://127.0.0.1:1234/v1/"),
        Model: "local-chat-model",
        DisplayName: "Local chat model",
        Family: "Generic",
        Size: "test",
        Quantization: "test",
        ContextTokens: 8192,
        OutputTokenLimit: 1024,
        Temperature: .1,
        TopP: null,
        StreamingEnabled: false,
        SupportsVision: false,
        SupportsToolCalls: true,
        AllowPrivateLanEndpoint: false);

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(Path.Combine(segments));
    }

    private sealed class FixedIdentitySource(string queryPromptMode) :
        IParticipantMemoryEmbeddingIdentitySource
    {
        public ParticipantMemoryEmbeddingIdentity Resolve(LocalVectorLibrarySettings settings) =>
            new(
                settings.EmbeddingProvider,
                "openai-compatible-embeddings-v1",
                new Uri(settings.EmbeddingEndpoint),
                settings.EmbeddingModel,
                settings.EmbeddingModel,
                "verified-test-quantization",
                settings.EmbeddingDimensions,
                8192,
                queryPromptMode,
                "none-v1",
                queryPromptMode == "prefix-v1" ? "search_query: " : string.Empty,
                string.Empty,
                "test-resolved-identity",
                true,
                DateTimeOffset.UtcNow);
    }

    private sealed record TestProposalReceipts(
        IReadOnlyList<ParticipantMemoryConsentReceipt> Consents);

    private sealed record TestConsentDraft(
        string Participant,
        string Operation,
        IReadOnlyList<string> Audience);
}
