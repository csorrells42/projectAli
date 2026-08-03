using System.Text.Json;
using System.Text.Json.Serialization;
using Ali.Modules.UserMemory;

namespace Ali.Framework.Tests;

public sealed class ParticipantMemoryServiceIntegrationTests
{
    private static readonly Mem0EmbeddingSpaceConfiguration Space = new(
        "space-verified",
        UserMemorySettings.FreshParticipantCollectionName,
        UserMemorySettings.FreshParticipantCollectionName + "__space-verified",
        Path.Combine(Path.GetTempPath(), "ali-participant-memory-tests"));

    [Fact]
    public async Task Recall_RejectsWorkerResultWhenLiveRosterChanges()
    {
        var receipts = new ParticipantMemoryReceiptAuthority();
        var roster = Roster();
        var rosters = new MutableRosterAuthority();
        var permission = receipts.IssuePermission(
            "alice",
            ["Read"],
            "call-read",
            "test",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1));
        var authority = new ParticipantMemoryAuthorityContext("alice", null, [])
        {
            Permission = permission
        };
        var transport = new ScriptedTransport(Space, (request, _) =>
        {
            rosters.IsCurrent = false;
            return Task.FromResult(new Mem0Response(
                "worker",
                true,
                "ok",
                null,
                0,
                null,
                EmbeddingSpaceId: Space.Id,
                RosterRevision: roster.Revision,
                ParticipantMemories: []));
        });
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings(),
            receipts,
            rosters);

        var result = await service.RecallParticipantsAsync(
            new ParticipantMemoryRecallRequest(
                "recall-request",
                roster,
                authority,
                "Alice's exact preference",
                4,
                Space.Id),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ParticipantMemoryFailureCode.StaleRoster, result.Failure?.Code);
        var request = Assert.Single(transport.Requests);
        Assert.Equal("participant_recall", request.GetProperty("operation").GetString());
        var keys = request.GetProperty("accessKeys").EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();
        Assert.Contains("scope:general:low", keys);
        Assert.Contains("participant:alice:low", keys);
    }

    [Fact]
    public async Task Recall_RejectsANullWorkerRecordWithoutDereferencingIt()
    {
        var receipts = new ParticipantMemoryReceiptAuthority();
        var roster = Roster();
        var permission = receipts.IssuePermission(
            "alice",
            ["Read"],
            "call-null-read",
            "test",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1));
        var authority = new ParticipantMemoryAuthorityContext("alice", null, [])
        {
            Permission = permission
        };
        var transport = new ScriptedTransport(Space, (_, _) => Task.FromResult(new Mem0Response(
            "worker-null",
            true,
            "malformed",
            null,
            1,
            null,
            EmbeddingSpaceId: Space.Id,
            RosterRevision: roster.Revision,
            ParticipantMemories: [null!])));
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings(),
            receipts,
            new MutableRosterAuthority());

        var result = await service.RecallParticipantsAsync(
            new ParticipantMemoryRecallRequest(
                "recall-null-record",
                roster,
                authority,
                "Alice's exact preference",
                4,
                Space.Id),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ParticipantMemoryFailureCode.ProtocolFailure, result.Failure?.Code);
    }

    [Fact]
    public async Task Mutation_ReconcilesInDoubtReceiptWithoutReapplying()
    {
        var receipts = new ParticipantMemoryReceiptAuthority();
        var roster = Roster();
        var rosters = new MutableRosterAuthority();
        const string requestId = "stable-mutation-request";
        var mutation = CreateAddRequest(receipts, roster, requestId);
        var record = Record(mutation);
        var transport = new ScriptedTransport(Space, (request, _) =>
        {
            var operation = request.GetProperty("operation").GetString();
            Assert.Equal(requestId, request.GetProperty("mutationRequestId").GetString());
            return Task.FromResult(operation switch
            {
                "participant_mutate" => new Mem0Response(
                    "worker-1",
                    false,
                    "in doubt",
                    null,
                    0,
                    "mutation_in_doubt",
                    EmbeddingSpaceId: Space.Id,
                    RosterRevision: roster.Revision,
                    MutationStatus: "in_doubt",
                    MutationRequestId: requestId),
                "participant_reconcile_mutation" => new Mem0Response(
                    "worker-2",
                    true,
                    "committed",
                    null,
                    1,
                    null,
                    EmbeddingSpaceId: Space.Id,
                    RosterRevision: roster.Revision,
                    ParticipantMemories: [record],
                    MutationStatus: "committed",
                    MutationRequestId: requestId,
                    MutationOperation: "add",
                    Reconciled: true),
                _ => throw new InvalidOperationException($"Unexpected operation '{operation}'.")
            });
        });
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings(),
            receipts,
            rosters);

        var result = await service.MutateParticipantsAsync(mutation, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.SafeMessage);
        Assert.Equal("memory-1", Assert.Single(result.Records).MemoryId);
        Assert.Equal(
            ["participant_mutate", "participant_reconcile_mutation"],
            transport.Requests.Select(request => request.GetProperty("operation").GetString()));
    }

    [Fact]
    public async Task Mutation_RejectsANullCommittedWorkerRecordWithoutDereferencingIt()
    {
        var receipts = new ParticipantMemoryReceiptAuthority();
        var roster = Roster();
        const string requestId = "null-committed-record";
        var mutation = CreateAddRequest(receipts, roster, requestId);
        var transport = new ScriptedTransport(Space, (_, _) => Task.FromResult(new Mem0Response(
            "worker-null",
            true,
            "committed",
            null,
            1,
            null,
            EmbeddingSpaceId: Space.Id,
            RosterRevision: roster.Revision,
            ParticipantMemories: [null!],
            MutationStatus: "committed",
            MutationRequestId: requestId,
            MutationOperation: "add",
            Reconciled: false)));
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings(),
            receipts,
            new MutableRosterAuthority());

        var result = await service.MutateParticipantsAsync(
            mutation,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ParticipantMemoryFailureCode.ProtocolFailure, result.Failure?.Code);
    }

    [Fact]
    public async Task Mutation_AllowsNewlinesAndTabsConsistentlyBeforeAndAfterDispatch()
    {
        var receipts = new ParticipantMemoryReceiptAuthority();
        var roster = Roster();
        const string requestId = "multiline-committed-record";
        var original = CreateAddRequest(receipts, roster, requestId);
        var proposal = original.Proposal with
        {
            Text = "Alice prefers\nblue\tworkspaces."
        };
        var now = DateTimeOffset.UtcNow;
        var consent = receipts.IssueConsent(
            original.Authority.Permission!,
            "Add",
            ParticipantMemoryProposalFingerprint.Create(proposal, roster.TenantId),
            $"multiline-consent:{Guid.NewGuid():N}",
            proposal.Visibility,
            proposal.AudienceParticipantReferences,
            roster.TurnId,
            now,
            TimeSpan.FromMinutes(1));
        var mutation = original with
        {
            Proposal = proposal,
            ConsentReceipts = [consent]
        };
        var transport = new ScriptedTransport(Space, (_, _) => Task.FromResult(new Mem0Response(
            "worker-multiline",
            true,
            "committed",
            null,
            1,
            null,
            EmbeddingSpaceId: Space.Id,
            RosterRevision: roster.Revision,
            ParticipantMemories: [Record(mutation)],
            MutationStatus: "committed",
            MutationRequestId: requestId,
            MutationOperation: "add",
            Reconciled: false)));
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings(),
            receipts,
            new MutableRosterAuthority());

        var result = await service.MutateParticipantsAsync(
            mutation,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Failure?.SafeMessage);
        Assert.Equal(proposal.Text, Assert.Single(result.Records).Text);
    }

    [Fact]
    public async Task Delete_StagesThenFinalizesToAZeroContentTombstone()
    {
        var receipts = new ParticipantMemoryReceiptAuthority();
        var roster = Roster();
        const string requestId = "stable-delete-request";
        var mutation = CreateDeleteRequest(receipts, roster, requestId);
        var prior = Record(CreateAddRequest(receipts, roster, "prior-add"));
        var transport = new ScriptedTransport(Space, (request, _) =>
        {
            var operation = request.GetProperty("operation").GetString();
            Assert.Equal(requestId, request.GetProperty("mutationRequestId").GetString());
            if (operation == "participant_mutate")
            {
                return Task.FromResult(new Mem0Response(
                    "worker-stage",
                    true,
                    "staged",
                    null,
                    1,
                    null,
                    EmbeddingSpaceId: Space.Id,
                    RosterRevision: roster.Revision,
                    ParticipantMemories: [prior],
                    MutationStatus: "delete_staged",
                    MutationRequestId: requestId,
                    MutationOperation: "delete",
                    Reconciled: false,
                    DeletionFinalized: false));
            }
            Assert.Equal("participant_reconcile_mutation", operation);
            Assert.True(request.GetProperty("finalizeDelete").GetBoolean());
            return Task.FromResult(new Mem0Response(
                "worker-final",
                true,
                "finalized",
                null,
                0,
                null,
                EmbeddingSpaceId: Space.Id,
                RosterRevision: roster.Revision,
                ParticipantMemories: [],
                MutationStatus: "committed",
                MutationRequestId: requestId,
                MutationOperation: "delete",
                Reconciled: true,
                DeletionFinalized: true));
        });
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings(),
            receipts,
            new MutableRosterAuthority());

        var result = await service.MutateParticipantsAsync(mutation, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.SafeMessage);
        Assert.Empty(result.Records);
        Assert.Equal(
            ["participant_mutate", "participant_reconcile_mutation"],
            transport.Requests.Select(request => request.GetProperty("operation").GetString()));
    }

    [Fact]
    public async Task Delete_RejectsAWorkerThatSkipsStagingAndReturnsDirectFinalization()
    {
        var receipts = new ParticipantMemoryReceiptAuthority();
        var roster = Roster();
        const string requestId = "direct-finalized-delete";
        var mutation = CreateDeleteRequest(receipts, roster, requestId);
        var transport = new ScriptedTransport(Space, (request, _) =>
        {
            Assert.Equal("participant_mutate", request.GetProperty("operation").GetString());
            return Task.FromResult(new Mem0Response(
                "worker-direct-final",
                true,
                "finalized without staging",
                null,
                0,
                null,
                EmbeddingSpaceId: "wrong-space-must-not-be-accepted",
                RosterRevision: roster.Revision,
                ParticipantMemories: [],
                MutationStatus: "committed",
                MutationRequestId: requestId,
                MutationOperation: "delete",
                Reconciled: false,
                DeletionFinalized: true));
        });
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings(),
            receipts,
            new MutableRosterAuthority());

        var result = await service.MutateParticipantsAsync(
            mutation,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ParticipantMemoryFailureCode.ProtocolFailure, result.Failure?.Code);
        Assert.Equal("participant_mutate", Assert.Single(transport.Requests)
            .GetProperty("operation").GetString());
    }

    [Fact]
    public async Task Reconcile_DoesNotRollbackAHistoricalCommitWhenInspectionRosterChanges()
    {
        var receipts = new ParticipantMemoryReceiptAuthority();
        var roster = Roster();
        var rosters = new MutableRosterAuthority();
        const string mutationRequestId = "historical-committed-add";
        var record = Record(CreateAddRequest(receipts, roster, "record-origin"));
        var transport = new ScriptedTransport(Space, (_, _) =>
        {
            rosters.IsCurrent = false;
            return Task.FromResult(new Mem0Response(
                "worker-reconcile",
                true,
                "committed",
                null,
                1,
                null,
                EmbeddingSpaceId: Space.Id,
                RosterRevision: roster.Revision,
                ParticipantMemories: [record],
                MutationStatus: "committed",
                MutationRequestId: mutationRequestId,
                MutationOperation: "add",
                Reconciled: true));
        });
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings(),
            receipts,
            rosters);

        var result = await service.ReconcileParticipantMutationAsync(
            new ParticipantMemoryReconciliationRequest(
                "inspect-historical",
                roster,
                ReconcileAuthority(receipts),
                mutationRequestId,
                Space.Id),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ParticipantMemoryFailureCode.StaleRoster, result.Failure?.Code);
        Assert.Equal("committed", result.MutationStatus);
        Assert.Equal("participant_reconcile_mutation", Assert.Single(transport.Requests)
            .GetProperty("operation").GetString());
    }

    [Fact]
    public async Task Reconcile_RejectsCommittedCorrectionWithSelfReferentialLineage()
    {
        var receipts = new ParticipantMemoryReceiptAuthority();
        var roster = Roster();
        const string mutationRequestId = "malformed-correction-receipt";
        var recordWithSelfReferentialLineage = Record(
            CreateAddRequest(receipts, roster, "record-origin")) with
        {
            CorrectsMemoryId = "memory-1",
            SupersedesMemoryId = "memory-1"
        };
        var transport = new ScriptedTransport(Space, (_, _) => Task.FromResult(new Mem0Response(
            "worker-reconcile",
            true,
            "committed",
            null,
            1,
            null,
            EmbeddingSpaceId: Space.Id,
            RosterRevision: roster.Revision,
            ParticipantMemories: [recordWithSelfReferentialLineage],
            MutationStatus: "committed",
            MutationRequestId: mutationRequestId,
            MutationOperation: "correct",
            Reconciled: true)));
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings(),
            receipts,
            new MutableRosterAuthority());

        var result = await service.ReconcileParticipantMutationAsync(
            new ParticipantMemoryReconciliationRequest(
                "inspect-malformed-correction",
                roster,
                ReconcileAuthority(receipts),
                mutationRequestId,
                Space.Id),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ParticipantMemoryFailureCode.ProtocolFailure, result.Failure?.Code);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Mutation_UsesConfiguredDeadlineAndReturnsStableConflictGuidance()
    {
        var receipts = new ParticipantMemoryReceiptAuthority();
        var roster = Roster();
        var rosters = new MutableRosterAuthority();
        var mutation = CreateAddRequest(receipts, roster, "timeout-request");
        var transport = new ScriptedTransport(Space, async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings { MutationTimeoutMilliseconds = 500 },
            receipts,
            rosters);

        var result = await service.MutateParticipantsAsync(mutation, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ParticipantMemoryFailureCode.TimedOut, result.Failure?.Code);
        Assert.Contains("same request ID", result.Failure?.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mutation_RejectsConsentThatExpiresDuringEmbeddingVerificationBeforeDispatch()
    {
        var receipts = new ParticipantMemoryReceiptAuthority();
        var roster = Roster();
        var mutation = CreateAddRequest(
            receipts,
            roster,
            "expiring-consent-request",
            TimeSpan.FromMilliseconds(500));
        var transport = new ScriptedTransport(
            Space,
            (_, _) => throw new InvalidOperationException(
                "An expired consent must not cross the state-changing worker boundary."),
            TimeSpan.FromMilliseconds(800));
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings { MutationTimeoutMilliseconds = 5_000 },
            receipts,
            new MutableRosterAuthority());

        var result = await service.MutateParticipantsAsync(
            mutation,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.NoEffectConfirmed);
        Assert.Equal("rolled_back", result.MutationStatus);
        Assert.Equal(ParticipantMemoryFailureCode.ConsentRequired, result.Failure?.Code);
        Assert.Equal(1, transport.ResolveCalls);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Mutation_RejectsOversizedProvenanceFieldBeforeTransport()
    {
        var receipts = new ParticipantMemoryReceiptAuthority();
        var roster = Roster();
        var original = CreateAddRequest(receipts, roster, "oversized-provenance-request");
        var mutation = original with
        {
            Provenance = original.Provenance with
            {
                SourceMessageId = new string('x', 129)
            }
        };
        var transport = new ScriptedTransport(
            Space,
            (_, _) => throw new InvalidOperationException(
                "Oversized provenance must not cross the worker boundary."));
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings(),
            receipts,
            new MutableRosterAuthority());

        var result = await service.MutateParticipantsAsync(
            mutation,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ParticipantMemoryFailureCode.InvalidProposal, result.Failure?.Code);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Health_DoesNotTreatMissingProviderFactsAsReady()
    {
        var receipts = new ParticipantMemoryReceiptAuthority();
        var roster = Roster();
        var permission = receipts.IssuePermission(
            "alice",
            ["Read"],
            "call-health",
            "test",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1));
        var authority = new ParticipantMemoryAuthorityContext("alice", null, [])
        {
            Permission = permission
        };
        var transport = new ScriptedTransport(Space, (_, _) => Task.FromResult(new Mem0Response(
            "worker",
            true,
            "malformed health",
            null,
            0,
            null,
            EmbeddingSpaceId: Space.Id,
            RosterRevision: roster.Revision)));
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings(),
            receipts,
            new MutableRosterAuthority());

        var result = await service.CheckParticipantHealthAsync(
            roster,
            authority,
            CancellationToken.None);

        Assert.False(result.EmbeddingAvailable);
        Assert.False(result.Mem0Available);
        Assert.False(result.QdrantAvailable);
        Assert.Equal(ParticipantMemoryFailureCode.ProtocolFailure, result.Failure?.Code);
    }

    [Fact]
    public async Task LegacyActiveUserMethods_NeverEnterParticipantTransport()
    {
        var transport = new ScriptedTransport(
            Space,
            (_, _) => throw new InvalidOperationException("Legacy methods must not enter CP11 transport."));
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings());
        var user = new ActiveUser("alice", "Alice", false, "explicit-selection");

        Assert.Empty(await service.RecallAsync(user, "query", 4, CancellationToken.None));
        Assert.Empty(await service.ListAsync(user, null, CancellationToken.None));
        Assert.False((await service.RememberAsync(user, "fact", "test", null, CancellationToken.None)).Success);
        Assert.False((await service.CorrectAsync(user, "memory", "change", CancellationToken.None)).Success);
        Assert.False((await service.DeleteAsync(user, "memory", CancellationToken.None)).Success);
        Assert.Empty(transport.Requests);
    }

    private static ParticipantMemoryMutationRequest CreateAddRequest(
        ParticipantMemoryReceiptAuthority receipts,
        ParticipantRosterSnapshot roster,
        string requestId,
        TimeSpan? consentLifetime = null)
    {
        var now = DateTimeOffset.UtcNow;
        var permission = receipts.IssuePermission(
            "alice",
            ["Add"],
            "call-add",
            "interactive-user",
            now,
            TimeSpan.FromMinutes(1));
        var authority = new ParticipantMemoryAuthorityContext("alice", null, [])
        {
            Permission = permission
        };
        var proposal = new ParticipantMemoryProposal(
            ParticipantMemoryMutationKind.Add,
            null,
            "Alice prefers blue workspaces.",
            "preferences",
            "alice",
            ["alice"],
            [],
            null,
            ParticipantMemoryClaimKind.Preference,
            ParticipantMemoryEvidenceKind.StatedDirectly,
            ParticipantMemoryVisibility.General,
            [],
            ParticipantMemorySensitivity.Low,
            .98,
            "alice");
        var consent = receipts.IssueConsent(
            permission,
            "Add",
            ParticipantMemoryProposalFingerprint.Create(proposal, roster.TenantId),
            $"test-consent-session:{Guid.NewGuid():N}",
            ParticipantMemoryVisibility.General,
            [],
            roster.TurnId,
            now,
            consentLifetime ?? TimeSpan.FromMinutes(1));
        return new(
            requestId,
            roster,
            authority,
            proposal,
            Space.Id,
            new ParticipantMemoryProvenance(
                roster.TurnId,
                "message-1",
                "test",
                now,
                "alice"),
            [consent]);
    }

    private static ParticipantMemoryMutationRequest CreateDeleteRequest(
        ParticipantMemoryReceiptAuthority receipts,
        ParticipantRosterSnapshot roster,
        string requestId)
    {
        var now = DateTimeOffset.UtcNow;
        var permission = receipts.IssuePermission(
            "alice",
            ["Delete"],
            "call-delete",
            "interactive-user",
            now,
            TimeSpan.FromMinutes(1));
        var authentication = receipts.IssueTestAuthentication(
            "alice",
            ["Delete"],
            now,
            TimeSpan.FromMinutes(1));
        var proposal = new ParticipantMemoryProposal(
            ParticipantMemoryMutationKind.Delete,
            "memory-1",
            string.Empty,
            string.Empty,
            "alice",
            ["alice"],
            [],
            null,
            ParticipantMemoryClaimKind.Preference,
            ParticipantMemoryEvidenceKind.StatedDirectly,
            ParticipantMemoryVisibility.General,
            [],
            ParticipantMemorySensitivity.Low,
            .98,
            "alice");
        return new(
            requestId,
            roster,
            new ParticipantMemoryAuthorityContext("alice", authentication, [])
            {
                Permission = permission
            },
            proposal,
            Space.Id,
            new ParticipantMemoryProvenance(
                roster.TurnId,
                "message-delete",
                "test",
                now,
                "alice"),
            []);
    }

    private static ParticipantMemoryRecord Record(ParticipantMemoryMutationRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        return new ParticipantMemoryRecord(
            MemoryId: "memory-1",
            TenantId: request.Roster.TenantId,
            Text: request.Proposal.Text,
            Category: request.Proposal.Category,
            SpeakerParticipantReference: request.Proposal.SpeakerParticipantReference,
            SubjectParticipantReferences: request.Proposal.SubjectParticipantReferences,
            WitnessParticipantReferences: request.Proposal.WitnessParticipantReferences,
            SharedEventReference: null,
            ClaimKind: request.Proposal.ClaimKind,
            EvidenceKind: request.Proposal.EvidenceKind,
            Visibility: request.Proposal.Visibility,
            AudienceParticipantReferences: request.Proposal.AudienceParticipantReferences,
            Sensitivity: request.Proposal.Sensitivity,
            AttributionConfidence: request.Proposal.AttributionConfidence,
            State: ParticipantMemoryState.Confirmed,
            Provenance: request.Provenance,
            ConsentReceipts: request.ConsentReceipts,
            CorrectsMemoryId: null,
            SupersedesMemoryId: null,
            DisputesMemoryId: null,
            CreatedUtc: now,
            ConfirmedUtc: now,
            CorrectedUtc: null,
            RevokedUtc: null,
            ArchivedUtc: null,
            EmbeddingSpaceId: Space.Id);
    }

    private static ParticipantRosterSnapshot Roster() => new ParticipantRosterSnapshot(
        "tenant",
        "turn",
        "conversation",
        DateTimeOffset.UtcNow,
        "selection-1",
        "presence-1",
        "alice",
        [new ParticipantReference(
            "alice",
            "Alice",
            ParticipantReferenceKind.Registered,
            ParticipantPresenceState.Present,
            "explicit-selection",
            1)]).Normalize();

    private static ParticipantMemoryAuthorityContext ReconcileAuthority(
        ParticipantMemoryReceiptAuthority receipts)
    {
        var now = DateTimeOffset.UtcNow;
        var permission = receipts.IssuePermission(
            "alice",
            ["Reconcile"],
            $"reconcile-call:{Guid.NewGuid():N}",
            "test",
            now,
            TimeSpan.FromMinutes(1));
        var authentication = receipts.IssueTestAuthentication(
            "alice",
            ["Reconcile"],
            now,
            TimeSpan.FromMinutes(1));
        return new ParticipantMemoryAuthorityContext("alice", authentication, [])
        {
            Permission = permission
        };
    }

    private sealed class MutableRosterAuthority : IParticipantRosterAuthority
    {
        public bool IsCurrent { get; set; } = true;

        public ParticipantRosterSnapshot CaptureAtAdmission(
            string turnId,
            string conversationId,
            DateTimeOffset capturedUtc) =>
            throw new NotSupportedException();

        public ParticipantRosterFreshness CheckCurrent(ParticipantRosterSnapshot admittedRoster) =>
            new(IsCurrent, admittedRoster.SelectionGeneration, admittedRoster.PresenceGeneration);
    }

    private sealed class ScriptedTransport(
        Mem0EmbeddingSpaceConfiguration space,
        Func<JsonElement, CancellationToken, Task<Mem0Response>> handler,
        TimeSpan? resolveDelay = null) : IParticipantMemoryTransport
    {
        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
        private readonly object _sync = new();

        public string DataRoot => space.DataRoot;

        public List<JsonElement> Requests { get; } = [];

        public int ResolveCalls { get; private set; }

        public async ValueTask<Mem0EmbeddingSpaceConfiguration> ResolveCurrentEmbeddingSpaceAsync(
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (resolveDelay is { } delay && delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
            return space;
        }

        public Task<Mem0Response> SendAsync(object request, CancellationToken cancellationToken)
        {
            var serialized = JsonSerializer.SerializeToElement(request, JsonOptions);
            lock (_sync)
            {
                Requests.Add(serialized);
            }
            return handler(serialized, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            return options;
        }
    }
}
