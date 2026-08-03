using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class ProtectedEvidenceLedgerTests
{
    [Fact]
    public async Task DesktopComposition_SeparatesEvidenceKeysByAssistantProfileRoot()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var profileARoot = global::Ali.AliServices.GetOrchestrationEvidenceRoot(
            Path.Combine(directory.Path, "Profiles", "profile-a"));
        var profileBRoot = global::Ali.AliServices.GetOrchestrationEvidenceRoot(
            Path.Combine(directory.Path, "Profiles", "profile-b"));
        var identity = new TurnIdentity("user", "conversation", "message");

        Assert.NotEqual(profileARoot, profileBRoot);
        Assert.Equal(
            Path.Combine(directory.Path, "Profiles", "profile-a", "Orchestration", "Evidence"),
            profileARoot);
        Assert.Equal(
            Path.Combine(directory.Path, "Profiles", "profile-b", "Orchestration", "Evidence"),
            profileBRoot);

        await new EvidenceLedger(profileARoot, "profile-a").AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft(
                "call-a",
                "tool",
                JsonSerializer.SerializeToElement(new { profile = "a" }),
                JsonSerializer.SerializeToElement(new { success = true })),
            TestContext.Current.CancellationToken);
        await new EvidenceLedger(profileBRoot, "profile-b").AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft(
                "call-b",
                "tool",
                JsonSerializer.SerializeToElement(new { profile = "b" }),
                JsonSerializer.SerializeToElement(new { success = true })),
            TestContext.Current.CancellationToken);

        Assert.Single(await new EvidenceLedger(profileARoot, "profile-a").ReplayAsync(
            identity,
            TestContext.Current.CancellationToken));
        Assert.Single(await new EvidenceLedger(profileBRoot, "profile-b").ReplayAsync(
            identity,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProtectedPayload_LongPathRoundTripsThroughBoundedNoFollowRead()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var root = Path.Combine(directory.Path, new string('r', 64));
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(root, "profile-a");
        var committed = await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft(
                "long-path-call",
                "tool",
                OutcomeAndEvidenceTests.Json("{}"),
                OutcomeAndEvidenceTests.Json("{\"success\":true}")),
            TestContext.Current.CancellationToken);
        var payloadPath = Assert.Single(Directory.GetFiles(
            root,
            "*.evidence",
            SearchOption.AllDirectories));
        Assert.True(payloadPath.Length >= 260);

        var protectedContent = await new EvidenceLedger(root, "profile-a").ReadProtectedAsync(
            identity,
            committed.Evidence.EvidenceId,
            TestContext.Current.CancellationToken);

        Assert.Equal("long-path-call", protectedContent.Identity.CallId);
    }

    [Fact]
    public async Task IndependentWriters_ProduceOneContiguousJournal()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var writers = Enumerable.Range(0, 32)
            .Select(index => new EvidenceLedger(directory.Path, "profile-a").AppendAsync(
                identity,
                OutcomeAndEvidenceTests.CreateDraft(
                    $"call-{index}",
                    "tool",
                    JsonSerializer.SerializeToElement(new { index }),
                    JsonSerializer.SerializeToElement(new { success = true })),
                TestContext.Current.CancellationToken))
            .ToArray();

        await Task.WhenAll(writers);
        var replay = await new EvidenceLedger(directory.Path, "profile-a").ReplayAsync(
            identity,
            TestContext.Current.CancellationToken);

        Assert.Equal(32, replay.Count);
        Assert.Equal(Enumerable.Range(1, 32).Select(value => (long)value), replay.Select(item => item.Cursor));
        Assert.Equal(32, replay.Select(item => item.Evidence.EvidenceId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task StableEvidenceIndex_EvictsWithJournalLru_AndRehydratesExactRetry()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        var firstIdentity = new TurnIdentity("user", "conversation", "message-0");
        var firstDraft = OutcomeAndEvidenceTests.CreateDraft(
            "call-0",
            "tool",
            OutcomeAndEvidenceTests.Json("{}"),
            OutcomeAndEvidenceTests.Json("{\"success\":true}")) with
        {
            EvidenceId = "stable-evidence-0"
        };
        var first = await ledger.AppendAsync(
            firstIdentity,
            firstDraft,
            TestContext.Current.CancellationToken);

        // The journal cache retains 64 turns. Adding 64 more turns evicts the
        // first turn and must evict its stable-ID generation at the same boundary.
        for (var index = 1; index <= 64; index++)
        {
            await ledger.AppendAsync(
                new TurnIdentity("user", "conversation", $"message-{index}"),
                OutcomeAndEvidenceTests.CreateDraft(
                    $"call-{index}",
                    "tool",
                    JsonSerializer.SerializeToElement(new { index }),
                    OutcomeAndEvidenceTests.Json("{\"success\":true}")) with
                {
                    EvidenceId = $"stable-evidence-{index}"
                },
                TestContext.Current.CancellationToken);
        }

        var afterEviction = ledger.CaptureCacheCounts();
        Assert.Equal(64, afterEviction.TurnJournals);
        Assert.Equal(64, afterEviction.StableEvidenceTurns);
        Assert.Equal(64, afterEviction.StableEvidenceRecords);
        Assert.Equal(0, afterEviction.ActiveStableEvidenceGates);

        var committedReference = new CommittedEvidenceReference(
            first.Evidence.EvidenceId,
            first.Cursor,
            first.Checksum);
        Assert.True(await ledger.IsCommittedAsync(
            firstIdentity,
            committedReference,
            TestContext.Current.CancellationToken));
        Assert.False(await ledger.IsCommittedAsync(
            firstIdentity,
            committedReference with { Cursor = committedReference.Cursor + 1 },
            TestContext.Current.CancellationToken));
        Assert.False(await ledger.IsCommittedAsync(
            firstIdentity,
            committedReference with { RecordDigest = new string('0', 64) },
            TestContext.Current.CancellationToken));

        var retry = await ledger.AppendAsync(
            firstIdentity,
            firstDraft with { },
            TestContext.Current.CancellationToken);

        Assert.Equal(first.Cursor, retry.Cursor);
        Assert.Equal(first.Checksum, retry.Checksum);
        Assert.Equal(first.Evidence.ProjectionDigest, retry.Evidence.ProjectionDigest);
        Assert.Single(await ledger.ReplayAsync(
            firstIdentity,
            TestContext.Current.CancellationToken));
        var afterRehydration = ledger.CaptureCacheCounts();
        Assert.Equal(64, afterRehydration.TurnJournals);
        Assert.Equal(64, afterRehydration.StableEvidenceTurns);
        Assert.Equal(64, afterRehydration.StableEvidenceRecords);
        Assert.Equal(0, afterRehydration.ActiveStableEvidenceGates);
    }

    [Fact]
    public async Task ConcurrentStableEvidenceFirstUse_AppendsExactlyOnce()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        var identity = new TurnIdentity("user", "conversation", "message");
        var draft = OutcomeAndEvidenceTests.CreateDraft(
            "call",
            "tool",
            OutcomeAndEvidenceTests.Json("{}"),
            OutcomeAndEvidenceTests.Json("{\"success\":true}")) with
        {
            EvidenceId = "concurrent-stable-evidence"
        };

        var attempts = Enumerable.Range(0, 32)
            .Select(_ => ledger.AppendAsync(
                identity,
                draft with { },
                TestContext.Current.CancellationToken))
            .ToArray();
        var results = await Task.WhenAll(attempts);

        Assert.All(results, result =>
        {
            Assert.Equal(results[0].Cursor, result.Cursor);
            Assert.Equal(results[0].Checksum, result.Checksum);
            Assert.Equal(results[0].Evidence.ProjectionDigest, result.Evidence.ProjectionDigest);
        });
        Assert.Single(await ledger.ReplayAsync(
            identity,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, ledger.CaptureCacheCounts().ActiveStableEvidenceGates);
    }

    [Fact]
    public async Task IndependentWriters_WithSameStableEvidenceId_CommitExactlyOnce()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var draft = OutcomeAndEvidenceTests.CreateDraft(
            "call",
            "tool",
            OutcomeAndEvidenceTests.Json("{}"),
            OutcomeAndEvidenceTests.Json("{\"success\":true}")) with
        {
            EvidenceId = "shared-stable-evidence"
        };

        var attempts = Enumerable.Range(0, 8)
            .Select(_ => new EvidenceLedger(directory.Path, "profile-a").AppendAsync(
                identity,
                draft with { },
                TestContext.Current.CancellationToken))
            .ToArray();
        var results = await Task.WhenAll(attempts);

        Assert.All(results, result =>
        {
            Assert.Equal(results[0].Cursor, result.Cursor);
            Assert.Equal(results[0].Checksum, result.Checksum);
            Assert.Equal(results[0].Evidence.ProjectionDigest, result.Evidence.ProjectionDigest);
        });
        Assert.Single(await new EvidenceLedger(directory.Path, "profile-a").ReplayAsync(
            identity,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EvidenceHotCache_IsBounded_AndColdExactLookupStillWorks()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var indexedReads = new List<int>();
        var ledger = new EvidenceLedger(
            directory.Path,
            "profile-a",
            faultInjector: null,
            journalFaultInjector: null,
            journalTailReadObserver: null,
            journalStampUnavailable: null,
            journalIndexedRecordReadObserver: count => indexedReads.Add(count));
        EvidenceCursorRecord? first = null;

        for (var index = 0; index <= EvidenceLedger.MaximumHotEvidenceRecordsPerTurn; index++)
        {
            var stored = await ledger.AppendAsync(
                identity,
                OutcomeAndEvidenceTests.CreateDraft(
                    $"call-{index}",
                    "tool",
                    JsonSerializer.SerializeToElement(new { index }),
                    OutcomeAndEvidenceTests.Json("{\"success\":true}")),
                TestContext.Current.CancellationToken);
            first ??= stored;
        }

        Assert.Equal(
            EvidenceLedger.MaximumHotEvidenceRecordsPerTurn,
            ledger.CaptureCacheCounts().StableEvidenceRecords);
        var protectedFirst = await ledger.ReadProtectedAsync(
            identity,
            first!.Evidence.EvidenceId,
            TestContext.Current.CancellationToken);

        Assert.Equal("call-0", protectedFirst.Identity.CallId);
        Assert.Single(indexedReads);
        Assert.InRange(indexedReads[0], 1, (4 * 1024 * 1024) + 1);
        Assert.Equal(
            EvidenceLedger.MaximumHotEvidenceRecordsPerTurn,
            ledger.CaptureCacheCounts().StableEvidenceRecords);
    }

    [Fact]
    public async Task CommittedEvidenceBatch_PerformsExactColdRehydration()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var writer = new EvidenceLedger(directory.Path, "profile-a");
        var stored = new List<EvidenceCursorRecord>();
        for (var index = 0; index < 16; index++)
        {
            stored.Add(await writer.AppendAsync(
                identity,
                OutcomeAndEvidenceTests.CreateDraft(
                    $"call-{index}",
                    "tool",
                    JsonSerializer.SerializeToElement(new { index }),
                    OutcomeAndEvidenceTests.Json("{\"success\":true}")),
                TestContext.Current.CancellationToken));
        }

        var selected = new[] { stored[0], stored[7], stored[15] };
        var references = selected
            .Select(item => new CommittedEvidenceReference(
                item.Evidence.EvidenceId,
                item.Cursor,
                item.Checksum))
            .ToArray();
        var indexedReads = new List<int>();
        var reader = new EvidenceLedger(
            directory.Path,
            "profile-a",
            faultInjector: null,
            journalFaultInjector: null,
            journalTailReadObserver: null,
            journalStampUnavailable: null,
            journalIndexedRecordReadObserver: count => indexedReads.Add(count));
        var recovered = await reader.ReadCommittedBatchAsync(
            identity,
            references,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, recovered.Count);
        Assert.Equal("call-0", recovered[stored[0].Evidence.EvidenceId].ProtectedContent.Identity.CallId);
        Assert.Equal("call-7", recovered[stored[7].Evidence.EvidenceId].ProtectedContent.Identity.CallId);
        Assert.Equal("call-15", recovered[stored[15].Evidence.EvidenceId].ProtectedContent.Identity.CallId);
        Assert.Equal(3, indexedReads.Count);
        Assert.All(indexedReads, count => Assert.InRange(count, 1, (4 * 1024 * 1024) + 1));
        Assert.Equal(3, reader.CaptureCacheCounts().StableEvidenceRecords);
    }

    [Fact]
    public async Task TamperedExactIndex_RebuildsFromJournal_ThenReturnsToSingleFrameReads()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var writer = new EvidenceLedger(directory.Path, "profile-a");
        var stored = new List<EvidenceCursorRecord>();
        for (var index = 0; index < 8; index++)
        {
            stored.Add(await writer.AppendAsync(
                identity,
                OutcomeAndEvidenceTests.CreateDraft(
                    $"call-{index}",
                    "tool",
                    JsonSerializer.SerializeToElement(new { index }),
                    OutcomeAndEvidenceTests.Json("{\"success\":true}")),
                TestContext.Current.CancellationToken));
        }

        var tablePath = Assert.Single(Directory.GetFiles(
            directory.Path,
            EvidenceExactIndex.TableFileName,
            SearchOption.AllDirectories));
        var table = await File.ReadAllBytesAsync(tablePath, TestContext.Current.CancellationToken);
        table[0] ^= 0x01;
        await File.WriteAllBytesAsync(tablePath, table, TestContext.Current.CancellationToken);

        var rebuildReads = new List<int>();
        var rebuildingReader = new EvidenceLedger(
            directory.Path,
            "profile-a",
            faultInjector: null,
            journalFaultInjector: null,
            journalTailReadObserver: null,
            journalStampUnavailable: null,
            journalIndexedRecordReadObserver: count => rebuildReads.Add(count));
        var rebuilt = await rebuildingReader.ReadProtectedAsync(
            identity,
            stored[0].Evidence.EvidenceId,
            TestContext.Current.CancellationToken);
        Assert.Equal("call-0", rebuilt.Identity.CallId);
        Assert.Empty(rebuildReads);

        var indexedReads = new List<int>();
        var indexedReader = new EvidenceLedger(
            directory.Path,
            "profile-a",
            faultInjector: null,
            journalFaultInjector: null,
            journalTailReadObserver: null,
            journalStampUnavailable: null,
            journalIndexedRecordReadObserver: count => indexedReads.Add(count));
        var exact = await indexedReader.ReadProtectedAsync(
            identity,
            stored[0].Evidence.EvidenceId,
            TestContext.Current.CancellationToken);
        Assert.Equal("call-0", exact.Identity.CallId);
        Assert.Single(indexedReads);
    }

    [Fact]
    public async Task CorruptExactIndexDuringAppend_DoesNotHideTheCommittedEvidence()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        var first = await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft(
                "call-1",
                "tool",
                OutcomeAndEvidenceTests.Json("{}"),
                OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);
        var manifestPath = Assert.Single(Directory.GetFiles(
            directory.Path,
            EvidenceExactIndex.ManifestFileName,
            SearchOption.AllDirectories));
        var manifest = await File.ReadAllBytesAsync(
            manifestPath,
            TestContext.Current.CancellationToken);
        manifest[manifest.Length / 2] ^= 0x01;
        await File.WriteAllBytesAsync(
            manifestPath,
            manifest,
            TestContext.Current.CancellationToken);

        var second = await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft(
                "call-2",
                "tool",
                OutcomeAndEvidenceTests.Json("{}"),
                OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, first.Cursor);
        Assert.Equal(2, second.Cursor);
        var replay = await new EvidenceLedger(directory.Path, "profile-a").ReplayAsync(
            identity,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, replay.Count);
    }

    [Fact]
    public async Task NonIoExactIndexFailureAfterHeadCommit_ReturnsCommittedIdAndColdRetryRebuilds()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var maintenanceAttempts = 0;
        var faulting = new EvidenceLedger(
            directory.Path,
            "profile-a",
            faultInjector: null,
            journalFaultInjector: null,
            journalTailReadObserver: null,
            journalStampUnavailable: null,
            journalIndexedRecordReadObserver: null,
            exactIndexMaintenanceFaultInjector: () =>
            {
                maintenanceAttempts++;
                throw new InvalidOperationException("Injected disposable sidecar failure.");
            });
        var draft = OutcomeAndEvidenceTests.CreateDraft(
            "call",
            "tool",
            OutcomeAndEvidenceTests.Json("{}"),
            OutcomeAndEvidenceTests.Json("{\"success\":true}"));

        var committed = await faulting.AppendAsync(
            identity,
            draft,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, maintenanceAttempts);
        Assert.Equal(1, committed.Cursor);
        Assert.False(string.IsNullOrWhiteSpace(committed.Evidence.EvidenceId));

        var rebuildFrameReads = new List<int>();
        var coldRetry = new EvidenceLedger(
            directory.Path,
            "profile-a",
            faultInjector: null,
            journalFaultInjector: null,
            journalTailReadObserver: null,
            journalStampUnavailable: null,
            journalIndexedRecordReadObserver: count => rebuildFrameReads.Add(count));
        var retried = await coldRetry.AppendAsync(
            identity,
            draft with { EvidenceId = committed.Evidence.EvidenceId },
            TestContext.Current.CancellationToken);

        Assert.Equal(committed.Cursor, retried.Cursor);
        Assert.Equal(committed.Checksum, retried.Checksum);
        Assert.Empty(rebuildFrameReads);

        var indexedFrameReads = new List<int>();
        var indexedReader = new EvidenceLedger(
            directory.Path,
            "profile-a",
            faultInjector: null,
            journalFaultInjector: null,
            journalTailReadObserver: null,
            journalStampUnavailable: null,
            journalIndexedRecordReadObserver: count => indexedFrameReads.Add(count));
        var protectedContent = await indexedReader.ReadProtectedAsync(
            identity,
            committed.Evidence.EvidenceId,
            TestContext.Current.CancellationToken);

        Assert.Equal("call", protectedContent.Identity.CallId);
        Assert.Single(indexedFrameReads);
        Assert.Single(await coldRetry.ReplayAsync(
            identity,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CachedCommit_IsInvalidatedWhenJournalChangesInPlace()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        var stored = await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft(
                "call",
                "tool",
                OutcomeAndEvidenceTests.Json("{}"),
                OutcomeAndEvidenceTests.Json("{\"success\":true}")) with
            {
                EvidenceId = "cached-stable-evidence"
            },
            TestContext.Current.CancellationToken);
        var committed = new CommittedEvidenceReference(
            stored.Evidence.EvidenceId,
            stored.Cursor,
            stored.Checksum);
        Assert.True(await ledger.IsCommittedAsync(
            identity,
            committed,
            TestContext.Current.CancellationToken));

        var journalPath = Directory.GetFiles(
            directory.Path,
            "evidence.journal.jsonl",
            SearchOption.AllDirectories).Single();
        var bytes = await File.ReadAllBytesAsync(
            journalPath,
            TestContext.Current.CancellationToken);
        bytes[bytes.Length / 2] ^= 0x01;
        await File.WriteAllBytesAsync(
            journalPath,
            bytes,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await ledger.IsCommittedAsync(
                identity,
                committed,
                TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task AppendReadsOnlyABoundedTail_NotTheGrowingJournal()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var tailReads = new List<int>();
        var ledger = new EvidenceLedger(
            directory.Path,
            "profile-a",
            faultInjector: null,
            journalFaultInjector: null,
            journalTailReadObserver: count => tailReads.Add(count));

        for (var index = 0; index < 120; index++)
        {
            await ledger.AppendAsync(
                identity,
                OutcomeAndEvidenceTests.CreateDraft(
                    $"call-{index}",
                    "tool",
                    JsonSerializer.SerializeToElement(new { index }),
                    OutcomeAndEvidenceTests.Json("{}")),
                TestContext.Current.CancellationToken);
        }

        var journalPath = Directory.GetFiles(directory.Path, "evidence.journal.jsonl", SearchOption.AllDirectories).Single();
        Assert.True(new FileInfo(journalPath).Length > 100_000);
        Assert.Equal(119, tailReads.Count);
        Assert.All(tailReads, count => Assert.InRange(count, 1, 8192));
    }

    [Fact]
    public async Task UnavailableJournalStamp_FallsBackToFullReplayWithoutChangingCommits()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var tailReads = new List<int>();
        var ledger = new EvidenceLedger(
            directory.Path,
            "profile-a",
            faultInjector: null,
            journalFaultInjector: null,
            journalTailReadObserver: count => tailReads.Add(count),
            journalStampUnavailable: () => true);

        var first = await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call-1", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);
        var second = await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call-2", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, first.Cursor);
        Assert.Equal(2, second.Cursor);
        Assert.Empty(tailReads);
        Assert.Equal(2, (await ledger.ReplayAsync(identity, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task MissingProtectedMasterKey_FailsClosedWithoutForkingTheTurn()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call-1", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);
        var turnDirectoriesBefore = Directory.GetDirectories(
            Path.Combine(directory.Path, "turns"),
            "*",
            SearchOption.TopDirectoryOnly);
        Assert.Single(turnDirectoriesBefore);

        File.Delete(Path.Combine(directory.Path, "evidence.key.protected"));

        await Assert.ThrowsAsync<InvalidDataException>(() => ledger.ReplayAsync(
            identity,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call-2", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken));
        Assert.Equal(
            turnDirectoriesBefore,
            Directory.GetDirectories(Path.Combine(directory.Path, "turns"), "*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task FaultAfterProtectedBlob_LeavesOnlyAnEncryptedOrphan()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        const string secret = "orphan-secret-canary";
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(
            directory.Path,
            "profile-a",
            boundary => throw new InjectedFailureException(boundary.ToString()),
            journalFaultInjector: null,
            journalTailReadObserver: null);

        await Assert.ThrowsAsync<InjectedFailureException>(() => ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft(
                "call",
                "tool",
                JsonSerializer.SerializeToElement(new { secret }),
                OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken));

        var replay = await new EvidenceLedger(directory.Path, "profile-a").ReplayAsync(
            identity,
            TestContext.Current.CancellationToken);
        Assert.Empty(replay);
        Assert.Single(Directory.GetFiles(directory.Path, "*.evidence", SearchOption.AllDirectories));
        foreach (var path in Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0);
        }
    }

    [Fact]
    public async Task TornJournalBody_IsDiscarded_AndItsCursorIsReused()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var faulting = new EvidenceLedger(
            directory.Path,
            "profile-a",
            faultInjector: null,
            boundary =>
            {
                if (boundary == EvidenceJournalCommitBoundary.BodyFlushed)
                {
                    throw new InjectedFailureException(boundary.ToString());
                }
            },
            journalTailReadObserver: null);
        await Assert.ThrowsAsync<InjectedFailureException>(() => faulting.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call-torn", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken));

        var recovered = new EvidenceLedger(directory.Path, "profile-a");
        var committed = await recovered.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call-committed", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, committed.Cursor);
        var replay = Assert.Single(await recovered.ReplayAsync(identity, TestContext.Current.CancellationToken));
        Assert.Equal(committed.Evidence.CallIdDigest, replay.Evidence.CallIdDigest);
        var protectedContent = await recovered.ReadProtectedAsync(
            identity,
            replay.Evidence.EvidenceId,
            TestContext.Current.CancellationToken);
        Assert.Equal("call-committed", protectedContent.Identity.CallId);
    }

    [Fact]
    public async Task FaultAfterCommitMarker_IsRecoveredAsUncommitted()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var faulting = new EvidenceLedger(
            directory.Path,
            "profile-a",
            faultInjector: null,
            boundary =>
            {
                if (boundary == EvidenceJournalCommitBoundary.CommitMarkerFlushed)
                {
                    throw new InjectedFailureException(boundary.ToString());
                }
            },
            journalTailReadObserver: null);

        await Assert.ThrowsAsync<InjectedFailureException>(() => faulting.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken));

        var replay = await new EvidenceLedger(directory.Path, "profile-a").ReplayAsync(
            identity,
            TestContext.Current.CancellationToken);
        Assert.Empty(replay);
    }

    [Fact]
    public async Task FaultAfterAuthenticatedHead_ReplaysTheCommittedRecordExactlyOnce()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var faulting = new EvidenceLedger(
            directory.Path,
            "profile-a",
            faultInjector: null,
            boundary =>
            {
                if (boundary == EvidenceJournalCommitBoundary.HeadCommitted)
                {
                    throw new InjectedFailureException(boundary.ToString());
                }
            },
            journalTailReadObserver: null);

        await Assert.ThrowsAsync<InjectedFailureException>(() => faulting.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken));

        var replay = await new EvidenceLedger(directory.Path, "profile-a").ReplayAsync(
            identity,
            TestContext.Current.CancellationToken);
        Assert.Single(replay);
        Assert.Equal(1, replay[0].Cursor);
    }

    [Fact]
    public async Task CiphertextAndJournalTampering_FailClosed()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);

        var payloadPath = Directory.GetFiles(directory.Path, "*.evidence", SearchOption.AllDirectories).Single();
        var payload = await File.ReadAllBytesAsync(payloadPath, TestContext.Current.CancellationToken);
        payload[^1] ^= 0x01;
        await File.WriteAllBytesAsync(payloadPath, payload, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => ledger.ReplayAsync(identity, TestContext.Current.CancellationToken));

        using var otherDirectory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var otherLedger = new EvidenceLedger(otherDirectory.Path, "profile-a");
        await otherLedger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);
        var journalPath = Directory.GetFiles(otherDirectory.Path, "evidence.journal.jsonl", SearchOption.AllDirectories).Single();
        var journal = await File.ReadAllBytesAsync(journalPath, TestContext.Current.CancellationToken);
        journal[journal.Length / 2] ^= 0x01;
        await File.WriteAllBytesAsync(journalPath, journal, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => otherLedger.ReplayAsync(identity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TurnAndProfilePartitions_DoNotCrossOpenEvidence()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var firstIdentity = new TurnIdentity("user-a", "conversation", "message");
        var otherIdentity = new TurnIdentity("user-b", "conversation", "message");
        var firstLedger = new EvidenceLedger(directory.Path, "profile-a");
        var stored = await firstLedger.AppendAsync(
            firstIdentity,
            OutcomeAndEvidenceTests.CreateDraft("call", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);

        Assert.Empty(await firstLedger.ReplayAsync(otherIdentity, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => firstLedger.ReadProtectedAsync(
            otherIdentity,
            stored.Evidence.EvidenceId,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => new EvidenceLedger(directory.Path, "profile-b").ReplayAsync(
            firstIdentity,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthenticatedHead_RejectsFinalMarkerOrCommittedSuffixRemoval(bool removeOnlyMarker)
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call-1", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);
        await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call-2", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);

        var journalPath = Directory.GetFiles(directory.Path, "evidence.journal.jsonl", SearchOption.AllDirectories).Single();
        var bytes = await File.ReadAllBytesAsync(journalPath, TestContext.Current.CancellationToken);
        var shortenedLength = removeOnlyMarker
            ? bytes.Length - 1
            : Array.LastIndexOf(bytes, (byte)'\n', bytes.Length - 2) + 1;
        await using (var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(shortenedLength);
            stream.Flush(flushToDisk: true);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => new EvidenceLedger(
            directory.Path,
            "profile-a").ReplayAsync(identity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FirstAppendOnANewLedger_RejectsEarlierCommittedCorruption()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call-1", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);
        await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call-2", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);

        var journalPath = Directory.GetFiles(directory.Path, "evidence.journal.jsonl", SearchOption.AllDirectories).Single();
        var bytes = await File.ReadAllBytesAsync(journalPath, TestContext.Current.CancellationToken);
        bytes[bytes.Length / 4] ^= 0x01;
        await File.WriteAllBytesAsync(journalPath, bytes, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => new EvidenceLedger(
            directory.Path,
            "profile-a").AppendAsync(
                identity,
                OutcomeAndEvidenceTests.CreateDraft("call-3", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SameLedgerAppend_RejectsEarlierCommittedCorruption()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call-1", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);
        await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call-2", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);

        var journalPath = Directory.GetFiles(directory.Path, "evidence.journal.jsonl", SearchOption.AllDirectories).Single();
        var bytes = await File.ReadAllBytesAsync(journalPath, TestContext.Current.CancellationToken);
        bytes[bytes.Length / 4] ^= 0x01;
        await File.WriteAllBytesAsync(journalPath, bytes, TestContext.Current.CancellationToken);
        var corruptedLength = new FileInfo(journalPath).Length;

        await Assert.ThrowsAsync<InvalidDataException>(() => ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call-3", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken));
        Assert.Equal(corruptedLength, new FileInfo(journalPath).Length);
    }

    [Fact]
    public async Task OversizedUncommittedSuffix_FailsWithoutScanningOrTruncatingIt()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);
        var journalPath = Directory.GetFiles(directory.Path, "evidence.journal.jsonl", SearchOption.AllDirectories).Single();
        await using (var stream = new FileStream(journalPath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(new byte[(4 * 1024 * 1024) + 2], TestContext.Current.CancellationToken);
            stream.Flush(flushToDisk: true);
        }
        var lengthBeforeReplay = new FileInfo(journalPath).Length;

        await Assert.ThrowsAsync<InvalidDataException>(() => new EvidenceLedger(
            directory.Path,
            "profile-a").ReplayAsync(identity, TestContext.Current.CancellationToken));
        Assert.Equal(lengthBeforeReplay, new FileInfo(journalPath).Length);
    }

    [Fact]
    public async Task OversizedProtectedPayload_IsRejectedBeforeAnyEvidenceIsCommitted()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var oversizedResult = JsonSerializer.SerializeToElement(
            new string('x', (4 * 1024 * 1024) + 1));
        var draft = OutcomeAndEvidenceTests.CreateDraft(
            "oversized-call",
            "tool",
            OutcomeAndEvidenceTests.Json("{}"),
            oversizedResult);

        await Assert.ThrowsAsync<InvalidDataException>(() => new EvidenceLedger(
            directory.Path,
            "profile-a").AppendAsync(
                identity,
                draft,
                TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetFiles(directory.Path, "*.evidence", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(directory.Path, "evidence.journal.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OversizedProtectedEnvelope_IsRejectedBeforeWholeFileMaterialization()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft(
                "call",
                "tool",
                OutcomeAndEvidenceTests.Json("{}"),
                OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);
        var payloadPath = Directory.GetFiles(
            directory.Path,
            "*.evidence",
            SearchOption.AllDirectories).Single();
        await using (var stream = new FileStream(
                         payloadPath,
                         FileMode.Open,
                         FileAccess.Write,
                         FileShare.None))
        {
            stream.SetLength(ProtectedEvidencePayloadStore.MaximumPlaintextBytes + 65L);
            stream.Flush(flushToDisk: true);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => new EvidenceLedger(
            directory.Path,
            "profile-a").ReplayAsync(identity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EvidenceArtifactCount_IsBoundedBeforeProjection()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var draft = OutcomeAndEvidenceTests.CreateDraft(
            "call",
            "tool",
            OutcomeAndEvidenceTests.Json("{}"),
            OutcomeAndEvidenceTests.Json("{}")) with
        {
            Artifacts = Enumerable.Range(0, 257)
                .Select(index => new EvidenceArtifactDraft(
                    "artifact-" + index,
                    "file",
                    BeforeVersion: null,
                    AfterVersion: "after"))
                .ToArray()
        };

        await Assert.ThrowsAsync<ArgumentException>(() => new EvidenceLedger(
            directory.Path,
            "profile-a").AppendAsync(
                identity,
                draft,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriterLeaseWait_HonorsCancellation_AndProgressesAfterRelease()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call-1", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);
        var lockPath = Directory.GetFiles(directory.Path, ".writer.lock", SearchOption.AllDirectories).Single();
        await using (var heldLease = new FileStream(
                         lockPath,
                         FileMode.OpenOrCreate,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EvidenceLedger(
                directory.Path,
                "profile-a").AppendAsync(
                    identity,
                    OutcomeAndEvidenceTests.CreateDraft("call-cancelled", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
                    cancellation.Token));
        }

        var committed = await ledger.AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft("call-2", "tool", OutcomeAndEvidenceTests.Json("{}"), OutcomeAndEvidenceTests.Json("{}")),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, committed.Cursor);
    }

    private sealed class InjectedFailureException(string message) : Exception(message);
}
