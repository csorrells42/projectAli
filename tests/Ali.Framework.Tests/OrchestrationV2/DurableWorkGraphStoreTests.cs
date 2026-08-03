using System.Collections.Immutable;
using System.Text;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class DurableWorkGraphStoreTests
{
    [Fact]
    public async Task CommitAndReload_ProtectsObjectivesAndReturnsExactStateReference()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity("user-secret-canary");
        const string objective = "objective-secret-canary-f329c7f6";
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");

        var committed = await store.CommitAsync(
            identity,
            expectedRevision: 0,
            Snapshot(1, objective),
            TestContext.Current.CancellationToken);
        var entry = Assert.IsType<CommittedWorkGraphSnapshot>(committed.Current);

        Assert.Equal(WorkGraphCommitStatus.Committed, committed.Status);
        Assert.Equal(1, entry.Reference.Revision);
        Assert.Matches("^[0-9a-f]{64}$", entry.Reference.RecordDigest);
        Assert.True(await store.IsCommittedAsync(
            identity,
            entry.Reference,
            TestContext.Current.CancellationToken));

        using var reopened = new DurableWorkGraphStore(directory.Path, "profile-a");
        var loaded = Assert.IsType<CommittedWorkGraphSnapshot>(
            await reopened.ReadAsync(
                identity,
                entry.Reference,
                TestContext.Current.CancellationToken));
        Assert.Equal(entry.Reference, loaded.Reference);
        Assert.Equal(objective, loaded.Snapshot.Nodes["node-0001"].Objective);

        var objectiveBytes = Encoding.UTF8.GetBytes(objective);
        var userBytes = Encoding.UTF8.GetBytes(identity.UserId);
        foreach (var path in Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.True(bytes.AsSpan().IndexOf(objectiveBytes) < 0, $"Objective leaked to {path}.");
            Assert.True(bytes.AsSpan().IndexOf(userBytes) < 0, $"User identity leaked to {path}.");
        }
    }

    [Fact]
    public async Task StateTransitionAdapter_AcceptsOnlyTheExactCommittedReference()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        var committed = await store.CommitAsync(
            identity,
            0,
            Snapshot(1, "Durable objective"),
            TestContext.Current.CancellationToken);
        var reference = committed.Current!.Reference;
        using var writer = new TurnTransitionWriter(
            directory.Path,
            "profile-a",
            new WorkGraphReferenceAdapter(store));
        var started = await writer.StartAsync(
            identity,
            "Original request",
            Bindings(),
            "turn-start",
            TestContext.Current.CancellationToken);

        var accepted = await writer.WriteAsync(
            identity,
            started.State!.Revision,
            new WorkGraphReferencedTransition("work-graph-1", reference),
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnTransitionWriteStatus.Committed, accepted.Status);
        Assert.Equal(reference.Revision, accepted.State!.WorkGraphRevision);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(
            identity,
            accepted.State.Revision,
            new WorkGraphReferencedTransition(
                "work-graph-forged",
                reference with { RecordDigest = new string('0', 64) }),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConflictingSameRevisionCandidatesRemainImmutableAndIsolated()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var first = new DurableWorkGraphStore(directory.Path, "profile-a");
        using var second = new DurableWorkGraphStore(directory.Path, "profile-a");

        var writes = await Task.WhenAll(
            first.CommitAsync(
                identity,
                0,
                Snapshot(1, "First candidate"),
                TestContext.Current.CancellationToken),
            second.CommitAsync(
                identity,
                0,
                Snapshot(1, "Second candidate"),
                TestContext.Current.CancellationToken));

        Assert.All(writes, static result =>
            Assert.Equal(WorkGraphCommitStatus.Committed, result.Status));
        var firstCandidate = Assert.IsType<CommittedWorkGraphSnapshot>(writes[0].Current);
        var secondCandidate = Assert.IsType<CommittedWorkGraphSnapshot>(writes[1].Current);
        Assert.NotEqual(firstCandidate.Reference, secondCandidate.Reference);
        var firstLoaded = Assert.IsType<CommittedWorkGraphSnapshot>(
            await first.ReadAsync(
                identity,
                firstCandidate.Reference,
                TestContext.Current.CancellationToken));
        var secondLoaded = Assert.IsType<CommittedWorkGraphSnapshot>(
            await second.ReadAsync(
                identity,
                secondCandidate.Reference,
                TestContext.Current.CancellationToken));
        Assert.Equal("First candidate", firstLoaded.Snapshot.Nodes["node-0001"].Objective);
        Assert.Equal("Second candidate", secondLoaded.Snapshot.Nodes["node-0001"].Objective);
        Assert.Equal(
            2,
            Directory.GetFiles(directory.Path, "revision-*.protected", SearchOption.AllDirectories).Length);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task IdenticalCandidateRetryIsIdempotentAndDoesNotRewriteTheRecord()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        var first = await store.CommitAsync(
            identity,
            0,
            Snapshot(1, "Accepted"),
            TestContext.Current.CancellationToken);

        var path = CandidatePath(directory.Path, identity, first.Current!.Reference);
        var originalBytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var retry = await store.CommitAsync(
            identity,
            0,
            Snapshot(1, "Accepted"),
            TestContext.Current.CancellationToken);

        Assert.Equal(WorkGraphCommitStatus.AlreadyRecorded, retry.Status);
        Assert.Equal(first.Current.Reference, retry.Current!.Reference);
        Assert.Equal(
            originalBytes,
            await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(
            Directory.GetFiles(directory.Path, "revision-*.protected", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TamperingOneConflictingCandidateDoesNotDamageItsSibling()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        var first = await store.CommitAsync(
            identity,
            0,
            Snapshot(1, "Candidate one"),
            TestContext.Current.CancellationToken);
        var second = await store.CommitAsync(
            identity,
            0,
            Snapshot(1, "Candidate two"),
            TestContext.Current.CancellationToken);
        var firstReference = first.Current!.Reference;
        var secondReference = second.Current!.Reference;
        var secondPath = CandidatePath(directory.Path, identity, secondReference);
        var bytes = await File.ReadAllBytesAsync(secondPath, TestContext.Current.CancellationToken);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(secondPath, bytes, TestContext.Current.CancellationToken);

        var surviving = Assert.IsType<CommittedWorkGraphSnapshot>(await store.ReadAsync(
            identity,
            firstReference,
            TestContext.Current.CancellationToken));
        Assert.Equal("Candidate one", surviving.Snapshot.Nodes["node-0001"].Objective);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(
            identity,
            secondReference,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExactOldReferenceRemainsReadableAfterANewerCandidateIsPersisted()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        var first = await store.CommitAsync(
            identity,
            0,
            Snapshot(1, 1),
            TestContext.Current.CancellationToken);
        var second = await store.CommitAsync(
            identity,
            1,
            Snapshot(2, 2),
            TestContext.Current.CancellationToken);

        var oldTruth = Assert.IsType<CommittedWorkGraphSnapshot>(await store.ReadAsync(
            identity,
            first.Current!.Reference,
            TestContext.Current.CancellationToken));
        var newerCandidate = Assert.IsType<CommittedWorkGraphSnapshot>(await store.ReadAsync(
            identity,
            second.Current!.Reference,
            TestContext.Current.CancellationToken));

        Assert.Equal(1, oldTruth.Snapshot.Revision);
        Assert.Single(oldTruth.Snapshot.Nodes);
        Assert.Equal(2, newerCandidate.Snapshot.Revision);
        Assert.Equal(2, newerCandidate.Snapshot.Nodes.Count);
    }

    [Fact]
    public async Task TamperAndCrossTurnCopiesFailClosed()
    {
        using var directory = new TemporaryDirectory();
        var firstIdentity = Identity("first-user");
        var secondIdentity = Identity("second-user");
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        var committed = await store.CommitAsync(
            firstIdentity,
            0,
            Snapshot(1, "Protected objective"),
            TestContext.Current.CancellationToken);

        Assert.False(await store.IsCommittedAsync(
            secondIdentity,
            committed.Current!.Reference,
            TestContext.Current.CancellationToken));

        var sourcePath = CandidatePath(directory.Path, firstIdentity, committed.Current.Reference);
        var copiedPath = CandidatePath(directory.Path, secondIdentity, committed.Current.Reference);
        Directory.CreateDirectory(Path.GetDirectoryName(copiedPath)!);
        File.Copy(sourcePath, copiedPath);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.ReadAsync(
                secondIdentity,
                committed.Current.Reference,
                TestContext.Current.CancellationToken));

        var bytes = await File.ReadAllBytesAsync(sourcePath, TestContext.Current.CancellationToken);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(sourcePath, bytes, TestContext.Current.CancellationToken);
        using var reopened = new DurableWorkGraphStore(directory.Path, "profile-a");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reopened.ReadAsync(
                firstIdentity,
                committed.Current.Reference,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DifferentProfileBindingCannotOpenTheCurrentUserProtectedKey()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        CommittedWorkGraphReference reference;
        using (var store = new DurableWorkGraphStore(directory.Path, "profile-a"))
        {
            var committed = await store.CommitAsync(
                identity,
                0,
                Snapshot(1, "Protected objective"),
                TestContext.Current.CancellationToken);
            reference = committed.Current!.Reference;
        }

        using var wrongProfile = new DurableWorkGraphStore(directory.Path, "profile-b");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            wrongProfile.ReadAsync(
                identity,
                reference,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MalformedSnapshotIsRejectedBeforePersistence()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        var malformed = new WorkGraphSnapshot(
            1,
            ImmutableDictionary.CreateRange(
                StringComparer.Ordinal,
                new[]
                {
                    new KeyValuePair<string, WorkNode>(
                        "map-key",
                        new WorkNode(
                            "different-node-id",
                            "Objective",
                            null,
                            WorkNodeStatus.Pending,
                            ImmutableArray<string>.Empty,
                            ImmutableArray<string>.Empty))
                }));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitAsync(
            identity,
            0,
            malformed,
            TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(directory.Path, "revision-*.protected", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FiveHundredAdvancingRevisionsRemainDurableWithoutAStepCap()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        CommittedWorkGraphSnapshot? latest = null;
        for (var revision = 1; revision <= 500; revision++)
        {
            var result = await store.CommitAsync(
                identity,
                latest?.Reference,
                Snapshot(revision, revision),
                TestContext.Current.CancellationToken);
            Assert.Equal(WorkGraphCommitStatus.Committed, result.Status);
            latest = result.Current;
        }

        Assert.NotNull(latest);
        Assert.Equal(500, latest.Snapshot.Revision);
        Assert.Equal(500, latest.Snapshot.Nodes.Count);
        Assert.InRange(
            store.Diagnostics.RecordsReadFromDisk,
            1,
            500L * DurableWorkGraphStore.MaximumDeltaChainLength);
        using var reopened = new DurableWorkGraphStore(directory.Path, "profile-a");
        var reloaded = Assert.IsType<CommittedWorkGraphSnapshot>(
            await reopened.ReadAsync(
                identity,
                latest.Reference,
                TestContext.Current.CancellationToken));
        Assert.Equal(latest.Reference, reloaded.Reference);
        Assert.Equal(500, reloaded.Snapshot.Nodes.Count);
        Assert.InRange(
            reopened.Diagnostics.RecordsReadFromDisk,
            1,
            DurableWorkGraphStore.MaximumDeltaChainLength);
        var candidates = Directory.GetFiles(
            directory.Path,
            "revision-*.protected",
            SearchOption.AllDirectories);
        Assert.Equal(500, candidates.Length);
        Assert.True(
            candidates.Sum(path => new FileInfo(path).Length) < 2_000_000,
            "Delta-backed immutable revisions should grow with changed nodes, not with every full historical snapshot.");
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FiveHundredAdvancingRevisions_WithProductionPruningRemainBounded()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        CommittedWorkGraphSnapshot? latest = null;
        for (var revision = 1; revision <= 500; revision++)
        {
            var result = await store.CommitAsync(
                identity,
                latest?.Reference,
                Snapshot(revision, revision),
                TestContext.Current.CancellationToken);
            latest = Assert.IsType<CommittedWorkGraphSnapshot>(result.Current);

            var pruned = await store.PruneUnreachableAsync(
                identity,
                latest.Reference,
                TestContext.Current.CancellationToken);
            Assert.InRange(
                pruned.RetainedCandidates,
                1,
                DurableWorkGraphStore.MaximumDeltaChainLength);
        }

        Assert.NotNull(latest);
        var candidates = Directory.GetFiles(
            directory.Path,
            "revision-*.protected",
            SearchOption.AllDirectories);
        Assert.InRange(candidates.Length, 1, DurableWorkGraphStore.MaximumDeltaChainLength);
        Assert.InRange(
            store.Diagnostics.RecordsReadFromDisk,
            1,
            500L * DurableWorkGraphStore.MaximumDeltaChainLength * 2);

        using var reopened = new DurableWorkGraphStore(directory.Path, "profile-a");
        var reloaded = Assert.IsType<CommittedWorkGraphSnapshot>(
            await reopened.ReadAsync(
                identity,
                latest.Reference,
                TestContext.Current.CancellationToken));
        Assert.Equal(500, reloaded.Snapshot.Revision);
        Assert.Equal(500, reloaded.Snapshot.Nodes.Count);
        Assert.InRange(
            reopened.Diagnostics.RecordsReadFromDisk,
            1,
            DurableWorkGraphStore.MaximumDeltaChainLength);
    }

    [Fact]
    public async Task MoreThanCandidateCeilingRevisions_WithCheckpointPruningRemainResumable()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        var applied = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(0, [Node("node", "Long-running objective")]),
            new HashSet<string>(StringComparer.Ordinal));
        Assert.True(applied.Accepted);
        var committed = Assert.IsType<CommittedWorkGraphSnapshot>(
            (await store.CommitValidatedAsync(
                identity,
                expectedParent: null,
                applied,
                TestContext.Current.CancellationToken)).Current);
        var finalRevision = DurableWorkGraphStore.MaximumCandidateEnumerationCount + 1L;

        for (var revision = 2L; revision <= finalRevision; revision++)
        {
            var status = revision % 2 == 0
                ? WorkNodeStatus.Active
                : WorkNodeStatus.Pending;
            applied = WorkGraphApplier.Apply(
                committed.Snapshot,
                new WorkGraphDelta(
                    committed.Snapshot.Revision,
                    [committed.Snapshot.Nodes["node"] with { Status = status }]),
                new HashSet<string>(StringComparer.Ordinal));
            Assert.True(applied.Accepted);
            committed = Assert.IsType<CommittedWorkGraphSnapshot>(
                (await store.CommitValidatedAsync(
                    identity,
                    committed.Reference,
                    applied,
                    TestContext.Current.CancellationToken)).Current);

            if (revision % DurableWorkGraphStore.MaximumDeltaChainLength == 0)
            {
                await store.PruneUnreachableAsync(
                    identity,
                    committed.Reference,
                    TestContext.Current.CancellationToken);
            }
        }

        var candidates = Directory.GetFiles(
            directory.Path,
            "revision-*.protected",
            SearchOption.AllDirectories);
        Assert.InRange(candidates.Length, 1, DurableWorkGraphStore.MaximumDeltaChainLength);

        using var reopened = new DurableWorkGraphStore(directory.Path, "profile-a");
        var reloaded = Assert.IsType<CommittedWorkGraphSnapshot>(
            await reopened.ReadAsync(
                identity,
                committed.Reference,
                TestContext.Current.CancellationToken));
        Assert.Equal(finalRevision, reloaded.Snapshot.Revision);
        Assert.Equal(WorkNodeStatus.Pending, reloaded.Snapshot.Nodes["node"].Status);
        Assert.InRange(
            reopened.Diagnostics.RecordsReadFromDisk,
            1,
            DurableWorkGraphStore.MaximumDeltaChainLength);
    }

    [Fact]
    public async Task PruneUnreachable_RemovesOnlyOrphansAfterAuthenticatingSelectedLineage()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        var selectedRoot = (await store.CommitAsync(
            identity,
            expectedRevision: 0,
            Snapshot(1, "Selected root"),
            TestContext.Current.CancellationToken)).Current!;
        var orphanRoot = (await store.CommitAsync(
            identity,
            expectedRevision: 0,
            Snapshot(1, "Orphan root"),
            TestContext.Current.CancellationToken)).Current!;
        var selectedChild = (await store.CommitAsync(
            identity,
            selectedRoot.Reference,
            Snapshot(2, "Selected child"),
            TestContext.Current.CancellationToken)).Current!;

        var pruned = await store.PruneUnreachableAsync(
            identity,
            selectedChild.Reference,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, pruned.RetainedCandidates);
        Assert.Equal(1, pruned.RemovedOrphanCandidates);
        Assert.True(pruned.RemovedBytes > 0);
        Assert.NotNull(await store.ReadAsync(
            identity,
            selectedRoot.Reference,
            TestContext.Current.CancellationToken));
        Assert.NotNull(await store.ReadAsync(
            identity,
            selectedChild.Reference,
            TestContext.Current.CancellationToken));
        Assert.Null(await store.ReadAsync(
            identity,
            orphanRoot.Reference,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AmbiguousParentRequiresExactJournalSelectedReference()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        var selected = (await store.CommitAsync(
            identity,
            0,
            Snapshot(1, "Selected"),
            TestContext.Current.CancellationToken)).Current!;
        await store.CommitAsync(
            identity,
            0,
            Snapshot(1, "Competing orphan"),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(
            identity,
            1,
            Snapshot(2, "Ambiguous"),
            TestContext.Current.CancellationToken));

        var exact = await store.CommitAsync(
            identity,
            selected.Reference,
            Snapshot(2, "Exact selected branch"),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, exact.Current!.Reference.Revision);
    }

    [Fact]
    public async Task PruneUnreachable_DeletesNothingWhenSelectedLineageCannotAuthenticate()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        var selectedRoot = (await store.CommitAsync(
            identity,
            0,
            Snapshot(1, "Selected root"),
            TestContext.Current.CancellationToken)).Current!;
        await store.CommitAsync(
            identity,
            0,
            Snapshot(1, "Orphan root"),
            TestContext.Current.CancellationToken);
        var selectedChild = (await store.CommitAsync(
            identity,
            selectedRoot.Reference,
            Snapshot(2, "Selected child"),
            TestContext.Current.CancellationToken)).Current!;
        var before = Directory.GetFiles(
            directory.Path,
            "revision-*.protected",
            SearchOption.AllDirectories);
        var selectedRootPath = CandidatePath(directory.Path, identity, selectedRoot.Reference);
        var bytes = await File.ReadAllBytesAsync(
            selectedRootPath,
            TestContext.Current.CancellationToken);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(
            selectedRootPath,
            bytes,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.PruneUnreachableAsync(
            identity,
            selectedChild.Reference,
            TestContext.Current.CancellationToken));

        Assert.Equal(
            before.Order(StringComparer.Ordinal),
            Directory.GetFiles(directory.Path, "revision-*.protected", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RootDirectoryRedirect_IsRejectedBeforeDurableFilesAreWritten()
    {
        using var external = new TemporaryDirectory();
        using var parent = new TemporaryDirectory();
        var redirectedRoot = Path.Combine(parent.Path, "redirected-root");
        CreateDirectorySymbolicLinkOrSkip(redirectedRoot, external.Path);
        try
        {
            using var store = new DurableWorkGraphStore(redirectedRoot, "profile-a");

            await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitAsync(
                Identity(),
                0,
                Snapshot(1, "Must not cross the root redirect"),
                TestContext.Current.CancellationToken));

            Assert.Empty(Directory.EnumerateFileSystemEntries(external.Path));
        }
        finally
        {
            Directory.Delete(redirectedRoot);
        }
    }

    [Fact]
    public async Task CandidateDirectoryRedirect_IsRejectedWithoutReadingRedirectTarget()
    {
        using var external = new TemporaryDirectory();
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        CommittedWorkGraphReference reference;
        using (var store = new DurableWorkGraphStore(directory.Path, "profile-a"))
        {
            reference = (await store.CommitAsync(
                identity,
                0,
                Snapshot(1, "Durable objective"),
                TestContext.Current.CancellationToken)).Current!.Reference;
        }

        var candidates = Path.GetDirectoryName(CandidatePath(directory.Path, identity, reference))!;
        var displaced = candidates + ".displaced";
        Directory.Move(candidates, displaced);
        var marker = Path.Combine(external.Path, "marker.txt");
        await File.WriteAllTextAsync(
            marker,
            "redirect-target-canary",
            TestContext.Current.CancellationToken);
        CreateDirectorySymbolicLinkOrSkip(candidates, external.Path);
        try
        {
            using var reopened = new DurableWorkGraphStore(directory.Path, "profile-a");

            await Assert.ThrowsAsync<InvalidDataException>(() => reopened.ReadAsync(
                identity,
                reference,
                TestContext.Current.CancellationToken));

            Assert.Equal(
                "redirect-target-canary",
                await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(candidates);
            Directory.Move(displaced, candidates);
        }
    }

    [Fact]
    public async Task WriterLeaseRedirect_IsRejectedWithoutOpeningItsTargetForWrite()
    {
        using var external = new TemporaryDirectory();
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        CommittedWorkGraphReference reference;
        using (var store = new DurableWorkGraphStore(directory.Path, "profile-a"))
        {
            reference = (await store.CommitAsync(
                identity,
                0,
                Snapshot(1, "Durable objective"),
                TestContext.Current.CancellationToken)).Current!.Reference;
        }

        var leasePath = Path.Combine(
            directory.Path,
            "turns",
            identity.StorageKey,
            "work-graph",
            ".work-graph.writer.lock");
        File.Delete(leasePath);
        var target = Path.Combine(external.Path, "lease-target.bin");
        var canary = Encoding.UTF8.GetBytes("lease-target-canary");
        await File.WriteAllBytesAsync(target, canary, TestContext.Current.CancellationToken);
        CreateFileSymbolicLinkOrSkip(leasePath, target);
        try
        {
            using var reopened = new DurableWorkGraphStore(directory.Path, "profile-a");

            await Assert.ThrowsAsync<InvalidDataException>(() => reopened.ReadAsync(
                identity,
                reference,
                TestContext.Current.CancellationToken));

            Assert.Equal(
                canary,
                await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(leasePath);
        }
    }

    [Fact]
    public async Task ReadAsync_RejectsFinalCandidateRedirectWithoutReadingTarget()
    {
        using var external = new TemporaryDirectory();
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        var committed = (await store.CommitAsync(
            identity,
            0,
            Snapshot(1, "Durable objective"),
            TestContext.Current.CancellationToken)).Current!;
        var candidatePath = CandidatePath(directory.Path, identity, committed.Reference);
        File.Delete(candidatePath);
        var target = Path.Combine(external.Path, "candidate-target.bin");
        var canary = Encoding.UTF8.GetBytes("candidate-target-canary");
        await File.WriteAllBytesAsync(target, canary, TestContext.Current.CancellationToken);
        CreateFileSymbolicLinkOrSkip(candidatePath, target);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(
                identity,
                committed.Reference,
                TestContext.Current.CancellationToken));

            Assert.Equal(
                canary,
                await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(candidatePath);
        }
    }

    [Fact]
    public async Task PruneUnreachable_RefusesRedirectedOrphanAndPreservesExternalTarget()
    {
        using var external = new TemporaryDirectory();
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        var selected = (await store.CommitAsync(
            identity,
            0,
            Snapshot(1, "Selected"),
            TestContext.Current.CancellationToken)).Current!;
        var orphan = (await store.CommitAsync(
            identity,
            0,
            Snapshot(1, "Orphan"),
            TestContext.Current.CancellationToken)).Current!;
        var orphanPath = CandidatePath(directory.Path, identity, orphan.Reference);
        File.Delete(orphanPath);
        var target = Path.Combine(external.Path, "must-survive.bin");
        var canary = Encoding.UTF8.GetBytes("prune-target-canary");
        await File.WriteAllBytesAsync(target, canary, TestContext.Current.CancellationToken);
        CreateFileSymbolicLinkOrSkip(orphanPath, target);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => store.PruneUnreachableAsync(
                identity,
                selected.Reference,
                TestContext.Current.CancellationToken));

            Assert.True(File.Exists(orphanPath));
            Assert.Equal(
                canary,
                await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
            Assert.NotNull(await store.ReadAsync(
                identity,
                selected.Reference,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(orphanPath);
        }
    }

    [Fact]
    public async Task ValidatedFiveThousandNodeDelta_DoesNotReconstructDiffOrCloneWholeGraph()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        var nodes = Enumerable.Range(0, 5_000)
            .Select(index => Node($"node-{index:D5}", $"Objective {index}"))
            .ToImmutableArray();
        var initial = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(0, nodes),
            new HashSet<string>(StringComparer.Ordinal));
        Assert.True(initial.Accepted);
        var committed = Assert.IsType<CommittedWorkGraphSnapshot>(
            (await store.CommitValidatedAsync(
                identity,
                expectedParent: null,
                initial,
                TestContext.Current.CancellationToken)).Current);
        var priorNode = committed.Snapshot.Nodes["node-02500"];
        var activatedNode = priorNode with { Status = WorkNodeStatus.Active };
        var activated = WorkGraphApplier.Apply(
            committed.Snapshot,
            new WorkGraphDelta(committed.Snapshot.Revision, [activatedNode]),
            new HashSet<string>(StringComparer.Ordinal));
        Assert.True(activated.Accepted);

        committed = Assert.IsType<CommittedWorkGraphSnapshot>(
            (await store.CommitValidatedAsync(
                identity,
                committed.Reference,
                activated,
                TestContext.Current.CancellationToken)).Current);

        var diagnostics = store.Diagnostics;
        Assert.Equal(2L, diagnostics.ValidatedDeltaCommits);
        Assert.Equal(5_001L, diagnostics.ValidatedDeltaNodesSerialized);
        Assert.Equal(0L, diagnostics.ValidatedCheckpointNodesSerialized);
        Assert.Equal(0L, diagnostics.ParentSnapshotReconstructions);
        Assert.Equal(0L, diagnostics.NewCandidateReconstructions);
        Assert.Equal(0L, diagnostics.FullSnapshotDiffPasses);
        Assert.Equal(0L, diagnostics.FullSnapshotCloneValidationPasses);
        Assert.Equal(WorkNodeStatus.Active, committed.Snapshot.Nodes["node-02500"].Status);

        using var reopened = new DurableWorkGraphStore(directory.Path, "profile-a");
        var reloaded = Assert.IsType<CommittedWorkGraphSnapshot>(
            await reopened.ReadAsync(
                identity,
                committed.Reference,
                TestContext.Current.CancellationToken));
        Assert.Equal(5_000, reloaded.Snapshot.Nodes.Count);
        Assert.Equal(WorkNodeStatus.Active, reloaded.Snapshot.Nodes["node-02500"].Status);
    }

    [Fact]
    public async Task ValidatedCommit_RejectsEquivalentCallerSnapshotWithoutStoreProvenance()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        var initial = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(0, [Node("node", "Objective")]),
            new HashSet<string>(StringComparer.Ordinal));
        var committed = Assert.IsType<CommittedWorkGraphSnapshot>(
            (await store.CommitValidatedAsync(
                identity,
                expectedParent: null,
                initial,
                TestContext.Current.CancellationToken)).Current);
        var callerCopy = new WorkGraphSnapshot(
            committed.Snapshot.Revision,
            committed.Snapshot.Nodes);
        var candidate = WorkGraphApplier.Apply(
            callerCopy,
            new WorkGraphDelta(
                callerCopy.Revision,
                [callerCopy.Nodes["node"] with { Status = WorkNodeStatus.Active }]),
            new HashSet<string>(StringComparer.Ordinal));
        Assert.True(candidate.Accepted);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.CommitValidatedAsync(
                identity,
                committed.Reference,
                candidate,
                TestContext.Current.CancellationToken));

        Assert.Contains("exact committed parent", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1L, store.Diagnostics.ValidatedDeltaCommits);
        Assert.Equal(0L, store.Diagnostics.ParentSnapshotReconstructions);
    }

    [Fact]
    public async Task ValidatedCheckpoint_WritesOneFullSnapshotAndReloadsExactState()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        var applied = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(0, [Node("node", "Objective")]),
            new HashSet<string>(StringComparer.Ordinal));
        var committed = Assert.IsType<CommittedWorkGraphSnapshot>(
            (await store.CommitValidatedAsync(
                identity,
                expectedParent: null,
                applied,
                TestContext.Current.CancellationToken)).Current);
        var evidenceIds = ImmutableArray<string>.Empty;

        for (var revision = 2; revision <= DurableWorkGraphStore.MaximumDeltaChainLength; revision++)
        {
            var evidenceId = $"e-{revision:D3}";
            evidenceIds = evidenceIds.Add(evidenceId);
            var candidate = committed.Snapshot.Nodes["node"] with { EvidenceIds = evidenceIds };
            applied = WorkGraphApplier.Apply(
                committed.Snapshot,
                new WorkGraphDelta(committed.Snapshot.Revision, [candidate]),
                evidenceIds.ToHashSet(StringComparer.Ordinal));
            Assert.True(applied.Accepted);
            committed = Assert.IsType<CommittedWorkGraphSnapshot>(
                (await store.CommitValidatedAsync(
                    identity,
                    committed.Reference,
                    applied,
                    TestContext.Current.CancellationToken)).Current);
        }

        var diagnostics = store.Diagnostics;
        Assert.Equal(
            (long)DurableWorkGraphStore.MaximumDeltaChainLength,
            diagnostics.ValidatedDeltaCommits);
        Assert.Equal(
            (long)DurableWorkGraphStore.MaximumDeltaChainLength - 1,
            diagnostics.ValidatedDeltaNodesSerialized);
        Assert.Equal(1L, diagnostics.ValidatedCheckpointNodesSerialized);
        Assert.Equal(0L, diagnostics.ParentSnapshotReconstructions);
        Assert.Equal(0L, diagnostics.NewCandidateReconstructions);
        Assert.Equal(0L, diagnostics.FullSnapshotDiffPasses);
        Assert.Equal(0L, diagnostics.FullSnapshotCloneValidationPasses);

        using var reopened = new DurableWorkGraphStore(directory.Path, "profile-a");
        var reloaded = Assert.IsType<CommittedWorkGraphSnapshot>(
            await reopened.ReadAsync(
                identity,
                committed.Reference,
                TestContext.Current.CancellationToken));
        Assert.Equal(DurableWorkGraphStore.MaximumDeltaChainLength, reloaded.Snapshot.Revision);
        Assert.Equal(evidenceIds, reloaded.Snapshot.Nodes["node"].EvidenceIds);
        Assert.InRange(
            reopened.Diagnostics.RecordsReadFromDisk,
            1,
            DurableWorkGraphStore.MaximumDeltaChainLength);
    }

    [Fact]
    public async Task ExactValidatedCandidateRetry_ReconstructsOnlyAfterNoFollowExistenceCheck()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        using var store = new DurableWorkGraphStore(directory.Path, "profile-a");
        var applied = WorkGraphApplier.Apply(
            WorkGraphSnapshot.Empty,
            new WorkGraphDelta(0, [Node("node", "Objective")]),
            new HashSet<string>(StringComparer.Ordinal));
        var first = await store.CommitValidatedAsync(
            identity,
            expectedParent: null,
            applied,
            TestContext.Current.CancellationToken);
        Assert.Equal(0L, store.Diagnostics.NewCandidateReconstructions);

        var retry = await store.CommitValidatedAsync(
            identity,
            expectedParent: null,
            applied,
            TestContext.Current.CancellationToken);

        Assert.Equal(WorkGraphCommitStatus.Committed, first.Status);
        Assert.Equal(WorkGraphCommitStatus.AlreadyRecorded, retry.Status);
        Assert.Equal(first.Current!.Reference, retry.Current!.Reference);
        Assert.Equal(1L, store.Diagnostics.NewCandidateReconstructions);
        Assert.Equal(0L, store.Diagnostics.ParentSnapshotReconstructions);
    }

    [Fact]
    public void CandidateEnumeration_IsRejectedAtTheDeterministicCeiling()
    {
        var exact = Enumerable.Range(0, DurableWorkGraphStore.MaximumCandidateEnumerationCount)
            .Select(index => $"candidate-{index}");
        var overflow = Enumerable.Range(0, DurableWorkGraphStore.MaximumCandidateEnumerationCount + 1)
            .Select(index => $"candidate-{index}");

        Assert.Equal(
            DurableWorkGraphStore.MaximumCandidateEnumerationCount,
            DurableWorkGraphStore.MaterializeCandidateEnumerationBounded(
                exact,
                TestContext.Current.CancellationToken).Length);
        var exception = Assert.Throws<InvalidDataException>(() =>
            DurableWorkGraphStore.MaterializeCandidateEnumerationBounded(
                overflow,
                TestContext.Current.CancellationToken));
        Assert.Contains(
            DurableWorkGraphStore.MaximumCandidateEnumerationCount.ToString(),
            exception.Message,
            StringComparison.Ordinal);
    }

    private static WorkGraphSnapshot Snapshot(long revision, string objective) =>
        new(
            revision,
            ImmutableDictionary.CreateRange(
                StringComparer.Ordinal,
                new[]
                {
                    new KeyValuePair<string, WorkNode>(
                        "node-0001",
                        Node("node-0001", objective))
                }));

    private static WorkGraphSnapshot Snapshot(long revision, int nodeCount)
    {
        var nodes = ImmutableDictionary.CreateBuilder<string, WorkNode>(StringComparer.Ordinal);
        for (var index = 1; index <= nodeCount; index++)
        {
            var id = $"node-{index:D4}";
            nodes.Add(id, Node(id, "Objective " + index));
        }

        return new WorkGraphSnapshot(revision, nodes.ToImmutable());
    }

    private static WorkNode Node(string id, string objective) =>
        new(
            id,
            objective,
            ParentId: null,
            WorkNodeStatus.Pending,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty);

    private static TurnIdentity Identity(string userId = "user") =>
        new(userId, "conversation", "assistant-message");

    private static string CandidatePath(
        string root,
        TurnIdentity identity,
        CommittedWorkGraphReference reference) =>
        Path.Combine(
            root,
            "turns",
            identity.StorageKey,
            "work-graph",
            "candidates",
            $"revision-{reference.Revision:D20}.{reference.RecordDigest}.protected");

    private static TurnRuntimeBindings Bindings() =>
        new(
            Digest("profile"),
            Digest("runtime"),
            Digest("model"),
            Digest("generation"),
            Digest("registry"),
            Digest("permission"),
            Digest("mcp"),
            Digest("attachment"),
            Digest("artifact"));

    private static string Digest(string value) =>
        TurnStateIntegrity.Digest(Encoding.UTF8.GetBytes(value));

    private static void CreateFileSymbolicLinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or PlatformNotSupportedException)
        {
            Assert.Skip("Symbolic-link creation is unavailable: " + ex.Message);
        }
    }

    private static void CreateDirectorySymbolicLinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or PlatformNotSupportedException)
        {
            Assert.Skip("Symbolic-link creation is unavailable: " + ex.Message);
        }
    }

    private sealed class WorkGraphReferenceAdapter(
        DurableWorkGraphStore store) : ITurnCommittedReferenceValidator
    {
        public ValueTask<bool> IsEvidenceCommittedAsync(
            TurnIdentity identity,
            CommittedEvidenceReference evidence,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> IsWorkGraphCommittedAsync(
            TurnIdentity identity,
            CommittedWorkGraphReference workGraph,
            CancellationToken cancellationToken) =>
            store.IsCommittedAsync(identity, workGraph, cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Ali-WorkGraphStore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
