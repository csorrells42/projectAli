using System.Text.Json;
using System.Text.Json.Serialization;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.State;
using Ali.Modules.RAG;
using Ali.Modules.UserMemory;

namespace Ali.Framework.Tests;

public sealed class ParticipantMemoryProductionBoundaryTests
{
    private static readonly Mem0EmbeddingSpaceConfiguration Space = new(
        "space-production-boundary",
        UserMemorySettings.FreshParticipantCollectionName,
        UserMemorySettings.FreshParticipantCollectionName + "__space-production-boundary",
        Path.Combine(Path.GetTempPath(), "ali-participant-memory-production-boundary-tests"));

    [Fact]
    public void SensitiveRecallKeys_RequireIssuedCurrentExactOperationAuthentication()
    {
        var now = DateTimeOffset.UtcNow;
        var receipts = new ParticipantMemoryReceiptAuthority();
        var permission = receipts.IssuePermission(
            "alice",
            ["Read"],
            "call-read",
            "test",
            now,
            TimeSpan.FromMinutes(2));
        var exactAuthentication = receipts.IssueTestAuthentication(
            "alice",
            ["Read"],
            now,
            TimeSpan.FromMinutes(2));
        var wrongOperationAuthentication = receipts.IssueTestAuthentication(
            "alice",
            ["Correct"],
            now,
            TimeSpan.FromMinutes(2));
        var forgedAuthentication = exactAuthentication with
        {
            ReceiptId = "authentication:forged"
        };

        var exactKeys = ParticipantMemoryPolicy.BuildAuthorizedRecallKeys(
            Authority(permission, exactAuthentication),
            now,
            receipts,
            "Read");
        var wrongOperationKeys = ParticipantMemoryPolicy.BuildAuthorizedRecallKeys(
            Authority(permission, wrongOperationAuthentication),
            now,
            receipts,
            "Read");
        var forgedAuthority = Authority(permission, forgedAuthentication);
        var forgedKeys = ParticipantMemoryPolicy.BuildAuthorizedRecallKeys(
            forgedAuthority,
            now,
            receipts,
            "Read");
        var forgedFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
            Roster("alice", "alice"),
            forgedAuthority,
            "Read",
            "request-read",
            now,
            receipts);

        Assert.Contains("participant:alice:sensitive", exactKeys);
        Assert.DoesNotContain("participant:alice:sensitive", wrongOperationKeys);
        Assert.DoesNotContain("participant:alice:sensitive", forgedKeys);
        Assert.Equal(ParticipantMemoryFailureCode.AuthenticationRequired, forgedFailure?.Code);
    }

    [Fact]
    public void UntrustedTeamAudienceKeys_FailClosedForAnExplicitlySelectedParticipant()
    {
        var now = DateTimeOffset.UtcNow;
        var receipts = new ParticipantMemoryReceiptAuthority();
        var permission = receipts.IssuePermission(
            "alice",
            ["Read"],
            "call-read",
            "test",
            now,
            TimeSpan.FromMinutes(2));
        var authority = new ParticipantMemoryAuthorityContext(
            "alice",
            null,
            ["project:caller-controlled"])
        {
            Permission = permission
        };

        var failure = ParticipantMemoryPolicy.ValidateAuthorityContext(
            Roster("alice", "alice"),
            authority,
            "Read",
            "request-read",
            now,
            receipts);
        var keys = ParticipantMemoryPolicy.BuildAuthorizedRecallKeys(
            authority,
            now,
            receipts,
            "Read");

        Assert.Equal(ParticipantMemoryFailureCode.PermissionDenied, failure?.Code);
        Assert.DoesNotContain(keys, key => key.StartsWith("team:", StringComparison.Ordinal));
    }

    [Fact]
    public void ProbeVerificationTimestamp_AndUnknownProviderTokenLimit_ProduceStableEmbeddingSpace()
    {
        var settings = VectorSettings();
        var firstIdentity = VerifiedIdentity(new DateTimeOffset(2026, 8, 3, 8, 0, 0, TimeSpan.Zero));
        var laterIdentity = firstIdentity with
        {
            ProbeVerifiedUtc = new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.Zero)
        };
        var firstConfiguration = Mem0ProcessClient.ResolveEmbeddingConfiguration(
            settings,
            new FixedIdentitySource(firstIdentity));
        var laterConfiguration = Mem0ProcessClient.ResolveEmbeddingConfiguration(
            settings,
            new FixedIdentitySource(laterIdentity));
        var firstSpace = Mem0ProcessClient.ResolveEmbeddingSpace(
            "data-root",
            UserMemorySettings.FreshParticipantCollectionName,
            firstConfiguration,
            settings);
        var laterSpace = Mem0ProcessClient.ResolveEmbeddingSpace(
            "data-root",
            UserMemorySettings.FreshParticipantCollectionName,
            laterConfiguration,
            settings);

        Assert.Equal(firstIdentity.Fingerprint, laterIdentity.Fingerprint);
        Assert.Equal(firstSpace.Id, laterSpace.Id);
        Assert.Equal(firstSpace.CollectionName, laterSpace.CollectionName);
    }

    [Fact]
    public void ConsentCoordinator_CollectsSeparateSelectedApprovalsForOneExactFingerprint()
    {
        var now = DateTimeOffset.UtcNow;
        var receipts = new ParticipantMemoryReceiptAuthority();
        var coordinator = new ParticipantMemoryConsentCoordinator();
        var proposal = SharedProposal("Alice and Bob repaired the garden gate.");
        var aliceRoster = Roster("alice", "alice", "bob");
        var bobRoster = Roster("bob", "alice", "bob");
        var alicePermission = receipts.IssuePermission(
            "alice",
            ["Add"],
            "call-alice",
            "test",
            now,
            TimeSpan.FromMinutes(2));
        var bobPermission = receipts.IssuePermission(
            "bob",
            ["Add"],
            "call-bob",
            "test",
            now,
            TimeSpan.FromMinutes(2));

        var aliceCapture = coordinator.Capture(
            proposal,
            aliceRoster,
            alicePermission,
            receipts,
            now);
        var bobCapture = coordinator.Capture(
            proposal,
            bobRoster,
            bobPermission,
            receipts,
            now);
        var issued = coordinator.TryIssue(
            proposal,
            aliceRoster,
            receipts,
            now,
            out var consents,
            out var pending,
            out var fingerprint);

        Assert.Equal(ParticipantMemoryConsentCoordinator.Fingerprint(proposal, "tenant"), fingerprint);
        Assert.Equal(fingerprint, aliceCapture.ProposalFingerprint);
        Assert.Equal(fingerprint, bobCapture.ProposalFingerprint);
        Assert.Equal(["bob"], aliceCapture.PendingParticipantReferences);
        Assert.Empty(bobCapture.PendingParticipantReferences);
        Assert.True(issued);
        Assert.Empty(pending);
        Assert.Equal(["alice", "bob"], consents
            .Select(consent => consent.GrantedByParticipantReference)
            .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ConsentCoordinator_RejectsChangedProposalAndExpiresPriorApprovals()
    {
        var now = DateTimeOffset.UtcNow;
        var receipts = new ParticipantMemoryReceiptAuthority();
        var coordinator = new ParticipantMemoryConsentCoordinator();
        var original = SharedProposal("Alice and Bob repaired the garden gate.");
        var changed = SharedProposal("Alice and Bob painted the garden gate.");
        var aliceRoster = Roster("alice", "alice", "bob");
        var bobRoster = Roster("bob", "alice", "bob");
        var alicePermission = receipts.IssuePermission(
            "alice",
            ["Add"],
            "call-alice",
            "test",
            now,
            TimeSpan.FromSeconds(1));
        var bobPermission = receipts.IssuePermission(
            "bob",
            ["Add"],
            "call-bob",
            "test",
            now,
            TimeSpan.FromSeconds(1));
        coordinator.Capture(original, aliceRoster, alicePermission, receipts, now);
        coordinator.Capture(original, bobRoster, bobPermission, receipts, now);

        var changedIssued = coordinator.TryIssue(
            changed,
            aliceRoster,
            receipts,
            now,
            out var changedConsents,
            out var changedPending,
            out var changedFingerprint);
        var expiredIssued = coordinator.TryIssue(
            original,
            aliceRoster,
            receipts,
            now.AddSeconds(2),
            out var expiredConsents,
            out var expiredPending,
            out _);

        Assert.NotEqual(
            ParticipantMemoryConsentCoordinator.Fingerprint(original, "tenant"),
            changedFingerprint);
        Assert.False(changedIssued);
        Assert.Empty(changedConsents);
        Assert.Equal(["alice", "bob"], changedPending);
        Assert.False(expiredIssued);
        Assert.Empty(expiredConsents);
        Assert.Equal(["alice", "bob"], expiredPending);
    }

    [Fact]
    public void ProductionDurableAdapter_PreparesMutationAndStateChangingReconciliation()
    {
        var registry = AliProductionDurableEffectAdapters.Create();

        Assert.True(registry.TryGet(
            AliCapabilityCatalog.ReconcileParticipantMemoryMutationName,
            out var reconciliation));
        Assert.True(registry.TryGet(
            AliCapabilityCatalog.MutateParticipantMemoryName,
            out var mutation));
        Assert.True(registry.TryGet(
            AliCapabilityCatalog.ConsentParticipantMemoryProposalName,
            out var consent));
        Assert.False(registry.TryGet(
            AliCapabilityCatalog.ReconcileParticipantMemoryMutationName + "_approximate",
            out _));

        var turn = Turn(Roster("alice", "alice"));
        var identity = new TurnIdentity("alice", "conversation", "assistant-message");
        var reconciliationPreview = reconciliation!.Preview(new(
            identity,
            turn,
            "call-reconcile",
            AliCapabilityCatalog.ReconcileParticipantMemoryMutationName,
            "arguments-digest",
            "target-digest"));
        var mutationPreview = mutation!.Preview(new(
            identity,
            turn,
            "call-mutate",
            AliCapabilityCatalog.MutateParticipantMemoryName,
            "arguments-digest",
            "target-digest"));
        var consentPreview = consent!.Preview(new(
            identity,
            turn,
            "call-consent",
            AliCapabilityCatalog.ConsentParticipantMemoryProposalName,
            "arguments-digest",
            "target-digest"));

        Assert.True(reconciliationPreview.RequiresPreparedIntent);
        Assert.StartsWith("participant-reconcile:", reconciliationPreview.OperationId, StringComparison.Ordinal);
        Assert.True(consentPreview.RequiresPreparedIntent);
        Assert.StartsWith("participant-consent:", consentPreview.OperationId, StringComparison.Ordinal);
        var consentIntent = new PreparedActionIntent(
            consentPreview.OperationId!,
            "work-consent",
            AliCapabilityCatalog.ConsentParticipantMemoryProposalName,
            "capability-consent",
            "arguments-digest",
            "target-digest",
            "permission-digest",
            "registry-digest",
            AliParticipantMemoryDurableEffectAdapter.ParticipantMemoryReconcilerId,
            RequiresApproval: true,
            AcceptedCallId: "call-consent");
        var noEffectConsent = JsonSerializer.SerializeToElement(
            new CoordinatorParticipantMemoryConsentResult(
                false,
                false,
                "No consent was recorded.",
                string.Empty,
                []));
        Assert.True(consent.ConfirmsAuthoritativeNoEffect(
            consentIntent,
            noEffectConsent));
        Assert.True(mutationPreview.RequiresPreparedIntent);
        Assert.StartsWith("participant-mutation:", mutationPreview.OperationId, StringComparison.Ordinal);
        Assert.Equal(
            AliParticipantMemoryDurableEffectAdapter.ParticipantMemoryReconcilerId,
            mutationPreview.ReconcilerId);
        Assert.Equal(
            AliParticipantMemoryDurableEffectAdapter.ParticipantMemoryReconcilerId,
            reconciliationPreview.ReconcilerId);
    }

    [Fact]
    public async Task Repair_SendsOnlyTheExactRequestedPointIds()
    {
        var now = DateTimeOffset.UtcNow;
        var receipts = new ParticipantMemoryReceiptAuthority();
        var roster = Roster("alice", "alice");
        var permission = receipts.IssuePermission(
            "alice",
            ["Repair"],
            "call-repair",
            "test",
            now,
            TimeSpan.FromMinutes(2));
        var authentication = receipts.IssueTestAuthentication(
            "alice",
            ["Repair"],
            now,
            TimeSpan.FromMinutes(2));
        var transport = new RecordingTransport(Space, (request, _) => Task.FromResult(
            new Mem0Response(
                "worker-repair",
                true,
                "repaired",
                null,
                2,
                null,
                EmbeddingSpaceId: Space.Id,
                RosterRevision: roster.Revision,
                UpdatedPointCount: 2,
                RepairRequestId: "repair-request",
                RequestedPointCount: 2)));
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings(),
            receipts,
            new AlwaysCurrentRosterAuthority());

        var result = await service.RepairParticipantEmbeddingSpaceAsync(
            new ParticipantMemoryRepairRequest(
                "repair-request",
                roster,
                Authority(permission, authentication),
                Space.Id,
                ["point-b", "point-a"]),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Failure?.SafeMessage);
        var request = Assert.Single(transport.Requests);
        Assert.Equal("participant_repair_hybrid", request.GetProperty("operation").GetString());
        Assert.Equal(
            ["point-b", "point-a"],
            request.GetProperty("repairPointIds").EnumerateArray()
                .Select(value => value.GetString()));
    }

    [Fact]
    public async Task MutationTimeout_ReconcilesTheSameRequestWithoutReapplying()
    {
        var receipts = new ParticipantMemoryReceiptAuthority();
        var roster = Roster("alice", "alice");
        const string requestId = "stable-timeout-mutation";
        var mutation = AddRequest(receipts, roster, requestId);
        var record = Record(mutation);
        var mutationCalls = 0;
        var transport = new RecordingTransport(Space, async (request, cancellationToken) =>
        {
            var operation = request.GetProperty("operation").GetString();
            Assert.Equal(requestId, request.GetProperty("mutationRequestId").GetString());
            if (operation == "participant_mutate")
            {
                Interlocked.Increment(ref mutationCalls);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
            Assert.Equal("participant_reconcile_mutation", operation);
            return new Mem0Response(
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
                MutationRequestId: requestId,
                MutationOperation: "add",
                Reconciled: true);
        });
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings { MutationTimeoutMilliseconds = 500 },
            receipts,
            new AlwaysCurrentRosterAuthority());

        var result = await service.MutateParticipantsAsync(
            mutation,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Failure?.SafeMessage);
        Assert.Equal(1, mutationCalls);
        Assert.Equal(
            ["participant_mutate", "participant_reconcile_mutation"],
            transport.Requests.Select(request => request.GetProperty("operation").GetString()));
    }

    [Fact]
    public async Task DesktopCompatibility_RequiresExactSelectedProfileAndUsesHealthThenParticipantList()
    {
        var alice = User("alice", "Alice");
        var bob = User("bob", "Bob");
        var session = new MutableActiveUserSession(alice, [alice, bob]);
        var rosters = new SelectedParticipantRosterAuthority(session, "tenant");
        var transport = new RecordingTransport(Space, (request, _) =>
        {
            var operation = request.GetProperty("operation").GetString();
            var revision = request.GetProperty("rosterRevision").GetString();
            return Task.FromResult(operation switch
            {
                "participant_health" => new Mem0Response(
                    "worker-health",
                    true,
                    "ready",
                    null,
                    0,
                    null,
                    EmbeddingSpaceId: Space.Id,
                    RosterRevision: revision,
                    EmbeddingAvailable: true,
                    Mem0Available: true,
                    QdrantAvailable: true),
                "participant_list" => new Mem0Response(
                    "worker-list",
                    true,
                    "listed",
                    null,
                    1,
                    null,
                    EmbeddingSpaceId: Space.Id,
                    RosterRevision: revision,
                    ParticipantMemories: [GeneralRecord("memory-desktop")]),
                _ => throw new InvalidOperationException($"Unexpected operation '{operation}'.")
            });
        });
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings(),
            new ParticipantMemoryReceiptAuthority(),
            rosters,
            session);

        var mismatched = await service.ListAsync(
            bob,
            null,
            TestContext.Current.CancellationToken);
        Assert.Empty(mismatched);
        Assert.Empty(transport.Requests);

        var selected = await service.ListAsync(
            alice,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("memory-desktop", Assert.Single(selected).MemoryId);
        Assert.Equal(
            ["participant_health", "participant_list"],
            transport.Requests.Select(request => request.GetProperty("operation").GetString()));
    }

    [Fact]
    public async Task DesktopMaintenance_RejectsForgedTestFlagForARegisteredSelectedProfile()
    {
        var alice = User("alice", "Alice");
        var session = new MutableActiveUserSession(alice, [alice]);
        var rosters = new SelectedParticipantRosterAuthority(session, "tenant");
        var transport = new RecordingTransport(Space, (request, _) =>
        {
            var operation = request.GetProperty("operation").GetString();
            var revision = request.GetProperty("rosterRevision").GetString();
            return Task.FromResult(operation switch
            {
                "participant_health" => new Mem0Response(
                    "worker-health",
                    true,
                    "ready",
                    null,
                    0,
                    null,
                    EmbeddingSpaceId: Space.Id,
                    RosterRevision: revision,
                    EmbeddingAvailable: true,
                    Mem0Available: true,
                    QdrantAvailable: true),
                "participant_list" => new Mem0Response(
                    "worker-list",
                    true,
                    "listed",
                    null,
                    1,
                    null,
                    EmbeddingSpaceId: Space.Id,
                    RosterRevision: revision,
                    ParticipantMemories: [GeneralRecord("memory-desktop")]),
                _ => throw new InvalidOperationException(
                    $"Forged test metadata reached unexpected operation '{operation}'.")
            });
        });
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings(),
            new ParticipantMemoryReceiptAuthority(),
            rosters,
            session);

        Assert.Single(await service.ListAsync(
            alice,
            null,
            TestContext.Current.CancellationToken));
        var forgedCaller = alice with
        {
            IsTestProfile = true,
            ResolutionMethod = "identity-test-profile"
        };

        var deletion = await service.DeleteAsync(
            forgedCaller,
            "memory-desktop",
            TestContext.Current.CancellationToken);

        Assert.False(deletion.Success);
        Assert.DoesNotContain(
            transport.Requests,
            request => string.Equals(
                request.GetProperty("operation").GetString(),
                "participant_mutate",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthenticationProvider_IssuesOnlyAfterSuccessfulVerifierAndStableSelection()
    {
        var alice = User("alice", "Alice");
        var bob = User("bob", "Bob");
        var receipts = new ParticipantMemoryReceiptAuthority();
        var session = new MutableActiveUserSession(alice, [alice]);
        var successfulVerifier = new FakeCredentialVerifier(true);
        var provider = new WindowsCredentialParticipantAuthenticationProvider(
            session,
            receipts,
            successfulVerifier);

        var issued = await provider.AuthenticateAsync(
            "alice",
            ["Delete"],
            "confirm delete",
            TestContext.Current.CancellationToken);

        Assert.NotNull(issued);
        Assert.True(receipts.IsIssued(issued!));
        Assert.Equal(ParticipantMemoryAuthenticationKind.LocalCredential, issued.Kind);
        Assert.Equal(["Delete"], issued.GrantedOperations);
        Assert.Equal(1, successfulVerifier.CallCount);

        var ambiguousVerifier = new FakeCredentialVerifier(true);
        provider = new WindowsCredentialParticipantAuthenticationProvider(
            new MutableActiveUserSession(alice, [alice, bob]),
            receipts,
            ambiguousVerifier);
        Assert.Null(await provider.AuthenticateAsync(
            "alice",
            ["Delete"],
            "confirm delete",
            TestContext.Current.CancellationToken));
        Assert.Equal(0, ambiguousVerifier.CallCount);

        var rejectedVerifier = new FakeCredentialVerifier(false);
        provider = new WindowsCredentialParticipantAuthenticationProvider(
            session,
            receipts,
            rejectedVerifier);
        Assert.Null(await provider.AuthenticateAsync(
            "alice",
            ["Delete"],
            "confirm delete",
            TestContext.Current.CancellationToken));

        var switchingVerifier = new FakeCredentialVerifier(true, () => session.Current = bob);
        provider = new WindowsCredentialParticipantAuthenticationProvider(
            session,
            receipts,
            switchingVerifier);
        Assert.Null(await provider.AuthenticateAsync(
            "alice",
            ["Delete"],
            "confirm delete",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Mutation_RejectsAuthenticationWhenSoleOwnerRegistryBindingChangesBeforeDispatch()
    {
        var alice = User("alice", "Alice");
        var bob = User("bob", "Bob");
        var receipts = new ParticipantMemoryReceiptAuthority();
        var session = new MutableActiveUserSession(alice, [alice]);
        var provider = new WindowsCredentialParticipantAuthenticationProvider(
            session,
            receipts,
            new FakeCredentialVerifier(true));
        var authentication = await provider.AuthenticateAsync(
            "alice",
            ["Add"],
            "confirm sensitive add",
            TestContext.Current.CancellationToken);
        Assert.NotNull(authentication);

        var roster = Roster("alice", "alice");
        var original = AddRequest(receipts, roster, "binding-change-request");
        var proposal = original.Proposal with
        {
            Sensitivity = ParticipantMemorySensitivity.Sensitive,
            Visibility = ParticipantMemoryVisibility.Private,
            AudienceParticipantReferences = ["alice"]
        };
        var consent = receipts.IssueConsent(
            original.Authority.Permission!,
            "Add",
            ParticipantMemoryProposalFingerprint.Create(proposal, roster.TenantId),
            "binding-change-consent",
            proposal.Visibility,
            proposal.AudienceParticipantReferences,
            roster.TurnId,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1));
        var mutation = original with
        {
            Authority = original.Authority with { Authentication = authentication },
            Proposal = proposal,
            ConsentReceipts = [consent]
        };
        var transport = new RecordingTransport(
            Space,
            (_, _) => throw new InvalidOperationException(
                "A stale sole-owner authentication binding must not reach the worker."));
        await using var service = new Mem0UserMemoryService(
            transport,
            static () => new UserMemorySettings(),
            receipts,
            new AlwaysCurrentRosterAuthority(),
            session,
            provider);

        session.AvailableUsers = [alice, bob];
        var result = await service.MutateParticipantsAsync(
            mutation,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.NoEffectConfirmed);
        Assert.Equal(ParticipantMemoryFailureCode.AuthenticationRequired, result.Failure?.Code);
        Assert.Empty(transport.Requests);
    }

    private static ParticipantMemoryAuthorityContext Authority(
        ParticipantMemoryPermissionReceipt permission,
        ParticipantMemoryAuthenticationReceipt? authentication = null) =>
        new(permission.PrincipalParticipantReference, authentication, [])
        {
            Permission = permission
        };

    private static ParticipantMemoryProposal SharedProposal(string text) => new(
        ParticipantMemoryMutationKind.Add,
        null,
        text,
        "shared-events",
        "alice",
        ["alice", "bob"],
        [],
        "event:garden-gate",
        ParticipantMemoryClaimKind.SharedExperience,
        ParticipantMemoryEvidenceKind.StatedDirectly,
        ParticipantMemoryVisibility.Shared,
        ["alice", "bob"],
        ParticipantMemorySensitivity.Low,
        .99,
        "alice");

    private static ParticipantMemoryMutationRequest AddRequest(
        ParticipantMemoryReceiptAuthority receipts,
        ParticipantRosterSnapshot roster,
        string requestId)
    {
        var now = DateTimeOffset.UtcNow;
        var permission = receipts.IssuePermission(
            "alice",
            ["Add"],
            "call-add",
            "test",
            now,
            TimeSpan.FromMinutes(2));
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
            .99,
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
            TimeSpan.FromMinutes(2));
        return new(
            requestId,
            roster,
            Authority(permission),
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

    private static ParticipantMemoryRecord Record(ParticipantMemoryMutationRequest request) =>
        new(
            "memory-1",
            request.Roster.TenantId,
            request.Proposal.Text,
            request.Proposal.Category,
            request.Proposal.SpeakerParticipantReference,
            request.Proposal.SubjectParticipantReferences,
            request.Proposal.WitnessParticipantReferences,
            request.Proposal.SharedEventReference,
            request.Proposal.ClaimKind,
            request.Proposal.EvidenceKind,
            request.Proposal.Visibility,
            request.Proposal.AudienceParticipantReferences,
            request.Proposal.Sensitivity,
            request.Proposal.AttributionConfidence,
            ParticipantMemoryState.Confirmed,
            request.Provenance,
            request.ConsentReceipts,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            Space.Id);

    private static ParticipantMemoryRecord GeneralRecord(string memoryId)
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            memoryId,
            "tenant",
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
            .99,
            ParticipantMemoryState.Confirmed,
            new ParticipantMemoryProvenance("turn", "message", "test", now, "alice"),
            [],
            null,
            null,
            null,
            now,
            now,
            null,
            null,
            null,
            Space.Id);
    }

    private static ParticipantRosterSnapshot Roster(
        string selected,
        params string[] participants) =>
        new ParticipantRosterSnapshot(
            "tenant",
            "turn",
            "conversation",
            DateTimeOffset.UtcNow,
            "selection-1",
            SelectedParticipantRosterAuthority.NoPresenceGeneration,
            selected,
            participants.Select(reference => new ParticipantReference(
                reference,
                reference,
                ParticipantReferenceKind.Registered,
                ParticipantPresenceState.Present,
                "test",
                1)).ToArray()).Normalize();

    private static CoordinatorTurnContext Turn(ParticipantRosterSnapshot roster) => new(
        "conversation",
        "user-message",
        "assistant-message",
        "test",
        static _ => { },
        ActiveUserSelectionSnapshot.Resolved(User("alice", "Alice")),
        new TurnIdentity("alice", "conversation", "assistant-message"),
        roster);

    private static ActiveUser User(string id, string name) =>
        new(id, name, false, "explicit-selection");

    private static LocalVectorLibrarySettings VectorSettings() => new()
    {
        EmbeddingProvider = "custom",
        EmbeddingEndpoint = "http://127.0.0.1:9123/v1/embeddings",
        EmbeddingModel = "configured-model",
        EmbeddingDimensions = 768,
        UseManagedLocalQdrant = false,
        AutoStartQdrant = false
    };

    private static ParticipantMemoryEmbeddingIdentity VerifiedIdentity(DateTimeOffset verifiedUtc) =>
        new(
            "custom",
            "openai-compatible-embeddings-v1",
            new Uri("http://127.0.0.1:9123/v1/embeddings"),
            "configured-model",
            "configured-model",
            "fixed-probe-sha256:abc123",
            768,
            0,
            "none-v1",
            "none-v1",
            string.Empty,
            string.Empty,
            "live-fixed-vector-and-4096-character-boundary-probe-v1",
            true,
            verifiedUtc);

    private sealed class FixedIdentitySource(ParticipantMemoryEmbeddingIdentity identity) :
        IParticipantMemoryEmbeddingIdentitySource
    {
        public ParticipantMemoryEmbeddingIdentity Resolve(LocalVectorLibrarySettings settings) => identity;
    }

    private sealed class AlwaysCurrentRosterAuthority : IParticipantRosterAuthority
    {
        public ParticipantRosterSnapshot CaptureAtAdmission(
            string turnId,
            string conversationId,
            DateTimeOffset capturedUtc) =>
            throw new NotSupportedException();

        public ParticipantRosterFreshness CheckCurrent(ParticipantRosterSnapshot admittedRoster) =>
            new(true, admittedRoster.SelectionGeneration, admittedRoster.PresenceGeneration);
    }

    private sealed class RecordingTransport(
        Mem0EmbeddingSpaceConfiguration space,
        Func<JsonElement, CancellationToken, Task<Mem0Response>> handler) : IParticipantMemoryTransport
    {
        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
        private readonly object _sync = new();

        public string DataRoot => space.DataRoot;

        public List<JsonElement> Requests { get; } = [];

        public ValueTask<Mem0EmbeddingSpaceConfiguration> ResolveCurrentEmbeddingSpaceAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(space);
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

    private sealed class MutableActiveUserSession(
        ActiveUser current,
        IReadOnlyList<ActiveUser> available) : IActiveUserSession
    {
        public ActiveUser Current { get; set; } = current;

        public IReadOnlyList<ActiveUser> AvailableUsers { get; set; } = available;

        public bool RequiresSelection => false;

        public ActiveUserSelectionSnapshot CaptureSelectionSnapshot() =>
            ActiveUserSelectionSnapshot.Resolved(Current);

        public string CaptureSelectionRevision() => "selection:" + Current.StableId;

        public event EventHandler<ActiveUser>? Changed
        {
            add { }
            remove { }
        }

        public ActiveUser Select(string stableId)
        {
            Current = AvailableUsers.Single(user => string.Equals(
                user.StableId,
                stableId,
                StringComparison.Ordinal));
            return Current;
        }

        public void Refresh()
        {
        }
    }

    private sealed class FakeCredentialVerifier(
        bool result,
        Action? onVerify = null) : ILocalParticipantCredentialVerifier
    {
        public int CallCount { get; private set; }

        public bool VerifyCurrentWindowsPrincipal(string reason)
        {
            CallCount++;
            onVerify?.Invoke();
            return result;
        }
    }
}
