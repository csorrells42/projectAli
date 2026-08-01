using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;

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
