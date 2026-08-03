using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;
using Microsoft.Win32.SafeHandles;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class TurnTransitionJournalBoundedTests
{
    [Fact]
    public async Task ProductionRead_RetainsOnlyFixedJournalAndFactBounds()
    {
        using var directory = new TemporaryDirectory();
        using var writer = Writer(directory.Path);
        var identity = Identity();
        var state = (await StartAsync(writer, identity)).State!;
        var appendedCount = Math.Max(
            Math.Max(
                TurnTransitionJournal.MaximumCachedEntries,
                TurnTransitionJournal.MaximumCachedFactsPerKind),
            TurnTransitionJournal.MaximumResumeProgressAttempts) + 32;

        for (var index = 0; index < appendedCount; index++)
        {
            var appended = await writer.WriteAsync(
                identity,
                state.Revision,
                Progress("bounded-progress-" + index, index),
                TestContext.Current.CancellationToken);
            Assert.Equal(TurnTransitionWriteStatus.Committed, appended.Status);
            state = appended.State!;
        }

        var recovered = await writer.ReadAsync(
            identity,
            TestContext.Current.CancellationToken);
        var resume = await writer.ReadResumeProjectionAsync(
            identity,
            TestContext.Current.CancellationToken);
        var diagnostics = writer.GetDiagnostics(identity);

        Assert.Equal(appendedCount + 1L, recovered!.Revision);
        Assert.Equal(recovered.Revision, resume.State!.Revision);
        Assert.Equal(
            TurnTransitionJournal.MaximumResumeProgressAttempts,
            resume.ProgressAttempts.Count);
        Assert.Equal(
            "bounded-progress-32",
            resume.ProgressAttempts[0].CorrelationKey);
        Assert.Equal(TurnTransitionJournal.MaximumCachedEntries, diagnostics.CachedEntryCount);
        Assert.Equal(
            TurnTransitionJournal.MaximumCachedFactsPerKind,
            diagnostics.CachedCorrelationFactCount);
        Assert.Equal(0, diagnostics.CachedActionFactCount);
        Assert.Equal(0, diagnostics.CachedEvidenceFactCount);
    }

    [Fact]
    public async Task ExplicitReplay_ReturnsFullAuditWithoutExpandingProductionCache()
    {
        using var directory = new TemporaryDirectory();
        using var writer = Writer(directory.Path);
        var identity = Identity();
        var state = (await StartAsync(writer, identity)).State!;
        var appendedCount = TurnTransitionJournal.MaximumCachedEntries + 19;

        for (var index = 0; index < appendedCount; index++)
        {
            var appended = await writer.WriteAsync(
                identity,
                state.Revision,
                Progress("audit-progress-" + index, index),
                TestContext.Current.CancellationToken);
            state = appended.State!;
        }

        _ = await writer.ReadAsync(identity, TestContext.Current.CancellationToken);
        var replay = await writer.ReplayAsync(
            identity,
            TestContext.Current.CancellationToken);
        var diagnostics = writer.GetDiagnostics(identity);

        Assert.Equal(appendedCount + 1, replay.Entries.Count);
        Assert.Equal(state.Revision, replay.State!.Revision);
        Assert.True(diagnostics.CachedEntryCount <= TurnTransitionJournal.MaximumCachedEntries);
        Assert.True(
            diagnostics.CachedCorrelationFactCount
            <= TurnTransitionJournal.MaximumCachedFactsPerKind);
        Assert.True(
            diagnostics.CachedActionFactCount
            <= TurnTransitionJournal.MaximumCachedFactsPerKind);
        Assert.True(
            diagnostics.CachedEvidenceFactCount
            <= TurnTransitionJournal.MaximumCachedFactsPerKind);
    }

    [Fact]
    public async Task ResumeProjection_RetainsOnlyNewestStructuredUserResolutions()
    {
        using var directory = new TemporaryDirectory();
        using var writer = Writer(directory.Path);
        var identity = Identity();
        var state = (await StartAsync(writer, identity)).State!;
        var intent = new PreparedActionIntent(
            "repeatable-recovery-action",
            "work-1",
            "write_file",
            "filesystem.write",
            Digest("arguments"),
            Digest("target-version"),
            Digest("permission-receipt"),
            Bindings().CapabilityRegistryDigest,
            Digest("execution-registry"),
            "filesystem-observer",
            "root-binding",
            RequiresApproval: true);
        var recovery = new TurnRecoveryService(
            writer,
            [new UnknownActionReconciler(intent.ReconcilerId)]);
        var resolutionCount = TurnTransitionJournal.MaximumResumeUserResolutions + 4;

        for (var index = 0; index < resolutionCount; index++)
        {
            var prepared = await writer.PrepareActionAsync(
                identity,
                state.Revision,
                intent,
                "bounded-resolution-action-" + index,
                TestContext.Current.CancellationToken);
            var recovered = await recovery.RecoverAsync(
                identity,
                Bindings(),
                explicitlyRequested: true,
                TestContext.Current.CancellationToken);
            var interim = recovered.State!.InterimPublication!;
            var committed = await writer.CommitInterimPublicationAsync(
                identity,
                recovered.State.Revision,
                interim.PublicationId,
                interim.Kind,
                interim.TextDigest,
                "bounded-resolution-display-" + index,
                TestContext.Current.CancellationToken);
            var waiting = await writer.ChangeControlAsync(
                identity,
                committed.State!.Revision,
                TurnControlState.AwaitingUser,
                "structured-reconciliation-prompt-displayed",
                "bounded-resolution-waiting-" + index,
                TestContext.Current.CancellationToken);
            var pending = Assert.Single(waiting.State!.PendingActions);
            var resolved = await writer.ResolveUnknownActionAsync(
                identity,
                waiting.State.Revision,
                "command-" + index,
                interim.PublicationId,
                interim.TextDigest,
                interim.SubjectId,
                pending.PreparedAtRevision,
                ActionUserResolution.ConfirmAbsent,
                "bounded-resolution-choice-" + index,
                TestContext.Current.CancellationToken);
            Assert.Equal(TurnTransitionWriteStatus.Committed, prepared.Status);
            Assert.Equal(TurnTransitionWriteStatus.Committed, resolved.Status);
            state = resolved.State!;
        }

        using var reopened = Writer(directory.Path);
        var projection = await reopened.ReadResumeProjectionAsync(
            identity,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            TurnTransitionJournal.MaximumResumeUserResolutions,
            projection.UserResolutions.Count);
        Assert.Equal("command-4", projection.UserResolutions[0].SourceCommandId);
        Assert.Equal(
            "command-" + (resolutionCount - 1),
            projection.UserResolutions[^1].SourceCommandId);
        Assert.All(
            projection.UserResolutions,
            item => Assert.Equal(
                TurnUserResolutionOutcome.ActionConfirmedAbsent,
                item.Outcome));
    }

    [Fact]
    public async Task OldCorrelationBeyondHotBounds_IsResolvedExactlyFromAuthenticatedDisk()
    {
        using var directory = new TemporaryDirectory();
        using var writer = Writer(directory.Path);
        var identity = Identity();
        var state = (await StartAsync(writer, identity)).State!;
        var original = Progress("old-correlation", 0);
        var first = await writer.WriteAsync(
            identity,
            state.Revision,
            original,
            TestContext.Current.CancellationToken);
        state = first.State!;

        var fillerCount = Math.Max(
            TurnTransitionJournal.MaximumCachedEntries,
            TurnTransitionJournal.MaximumCachedFactsPerKind) + 32;
        for (var index = 0; index < fillerCount; index++)
        {
            var appended = await writer.WriteAsync(
                identity,
                state.Revision,
                Progress("new-correlation-" + index, index + 1),
                TestContext.Current.CancellationToken);
            state = appended.State!;
        }

        var readsBeforeRetry = writer.GetDiagnostics(identity).RecordsReadFromDisk;
        var retry = await writer.WriteAsync(
            identity,
            state.Revision,
            original with { },
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnTransitionWriteStatus.AlreadyRecorded, retry.Status);
        Assert.Equal(2, retry.Entry!.Cursor);
        Assert.Equal(state.Revision, retry.State!.Revision);
        Assert.Equal(
            readsBeforeRetry + 1,
            writer.GetDiagnostics(identity).RecordsReadFromDisk);

        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(
            identity,
            state.Revision,
            original with { AfterMaterialFingerprint = Digest("rebound") },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AppendingAKey_DoesNotInvalidateUntouchedAuthenticatedIndexPages()
    {
        using var directory = new TemporaryDirectory();
        using var writer = Writer(directory.Path);
        var identity = Identity();
        var state = (await StartAsync(writer, identity)).State!;
        var coldCandidates = new List<ProgressAttemptRecordedTransition>();

        for (var index = 0; index < 32; index++)
        {
            var transition = Progress("cross-page-cold-" + index, index);
            coldCandidates.Add(transition);
            state = (await writer.WriteAsync(
                identity,
                state.Revision,
                transition,
                TestContext.Current.CancellationToken)).State!;
        }

        var newest = Progress("cross-page-new-32", 32);
        state = (await writer.WriteAsync(
            identity,
            state.Revision,
            newest,
            TestContext.Current.CancellationToken)).State!;
        for (var index = 33; index < 544; index++)
        {
            newest = Progress("cross-page-new-" + index, index);
            state = (await writer.WriteAsync(
                identity,
                state.Revision,
                newest,
                TestContext.Current.CancellationToken)).State!;
        }

        const long tablePageBytes = 4096;
        var newestPage = FindExactStringSlot(
            directory.Path,
            factKind: 1,
            newest.CorrelationKey).TableOffset / tablePageBytes;
        var cold = coldCandidates.First(candidate =>
            FindExactStringSlot(
                directory.Path,
                factKind: 1,
                candidate.CorrelationKey).TableOffset / tablePageBytes != newestPage);
        var readsBeforeRetry = writer.GetDiagnostics(identity).RecordsReadFromDisk;

        var retry = await writer.WriteAsync(
            identity,
            state.Revision,
            cold with { },
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnTransitionWriteStatus.AlreadyRecorded, retry.Status);
        Assert.Equal(state.Revision, retry.State!.Revision);
        Assert.Equal(
            readsBeforeRetry + 1,
            writer.GetDiagnostics(identity).RecordsReadFromDisk);
    }

    [Fact]
    public async Task HighCardinalityReplay_UsesNearLinearExactIndex_AndSurvivesCrashReopen()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        const int appendedCount = TurnTransitionJournal.MaximumCachedFactsPerKind * 8;
        ProgressAttemptRecordedTransition oldest;
        TurnState state;

        using (var writer = Writer(directory.Path))
        {
            state = (await StartAsync(writer, identity)).State!;
            oldest = Progress("high-cardinality-0", 0);
            for (var index = 0; index < appendedCount; index++)
            {
                var transition = index == 0
                    ? oldest
                    : Progress("high-cardinality-" + index, index);
                var appended = await writer.WriteAsync(
                    identity,
                    state.Revision,
                    transition,
                    TestContext.Current.CancellationToken);
                Assert.Equal(TurnTransitionWriteStatus.Committed, appended.Status);
                state = appended.State!;
            }

            Assert.True(
                writer.GetDiagnostics(identity).ExactIndexCapacity > 1024,
                "The live exact index should cross its initial capacity and exercise growth.");
        }

        using (var reopened = Writer(directory.Path))
        {
            var recovered = await reopened.ReadAsync(
                identity,
                TestContext.Current.CancellationToken);
            var diagnostics = reopened.GetDiagnostics(identity);

            Assert.Equal(state.Revision, recovered!.Revision);
            Assert.Equal(state.Revision, diagnostics.RecordsReadFromDisk);
            Assert.Equal(state.Revision, diagnostics.ExactIndexKeyCount);
            Assert.True(diagnostics.ExactIndexCapacity >= diagnostics.ExactIndexKeyCount);
            Assert.InRange(
                diagnostics.ExactIndexProbeCount,
                state.Revision,
                state.Revision * 8);

            var readsBeforeOldRetry = diagnostics.RecordsReadFromDisk;
            var retry = await reopened.WriteAsync(
                identity,
                recovered.Revision,
                oldest with { },
                TestContext.Current.CancellationToken);

            Assert.Equal(TurnTransitionWriteStatus.AlreadyRecorded, retry.Status);
            Assert.Equal(2, retry.Entry!.Cursor);
            Assert.Equal(
                readsBeforeOldRetry + 1,
                reopened.GetDiagnostics(identity).RecordsReadFromDisk);

            var tablePath = Assert.Single(Directory.GetFiles(
                directory.Path,
                "turn.fact-index.table.bin",
                SearchOption.AllDirectories));
            var occupied = FindExactStringSlot(
                directory.Path,
                factKind: 1,
                value: oldest.CorrelationKey);
            OverwritePreservingBasicInfo(
                tablePath,
                occupied.TableOffset,
                new byte[32]);

            var readsBeforeIndexRecovery = reopened
                .GetDiagnostics(identity)
                .RecordsReadFromDisk;
            var afterTableAuthenticationRecovery = await reopened.WriteAsync(
                identity,
                state.Revision,
                oldest with { },
                TestContext.Current.CancellationToken);

            Assert.Equal(
                TurnTransitionWriteStatus.AlreadyRecorded,
                afterTableAuthenticationRecovery.Status);
            Assert.Equal(state.Revision, afterTableAuthenticationRecovery.State!.Revision);
            Assert.True(
                reopened.GetDiagnostics(identity).RecordsReadFromDisk
                >= readsBeforeIndexRecovery + state.Revision);

            var keyPath = Assert.Single(Directory.GetFiles(
                directory.Path,
                "turn.fact-index.keys.bin",
                SearchOption.AllDirectories));
            var exactKey = FindExactStringSlot(
                directory.Path,
                factKind: 1,
                value: oldest.CorrelationKey);
            OverwritePreservingBasicInfo(
                keyPath,
                exactKey.KeyOffset + 1,
                [(byte)'X']);
            var readsBeforeKeyAuthenticationRecovery = reopened
                .GetDiagnostics(identity)
                .RecordsReadFromDisk;
            var retryAfterKeyAuthenticationRecovery = await reopened.WriteAsync(
                identity,
                state.Revision,
                oldest with { },
                TestContext.Current.CancellationToken);

            Assert.Equal(
                TurnTransitionWriteStatus.AlreadyRecorded,
                retryAfterKeyAuthenticationRecovery.Status);
            Assert.Equal(2, retryAfterKeyAuthenticationRecovery.Entry!.Cursor);
            Assert.True(
                reopened.GetDiagnostics(identity).RecordsReadFromDisk
                >= readsBeforeKeyAuthenticationRecovery + state.Revision);
        }

        var injected = false;
        using (var faulted = new TurnTransitionWriter(
                   directory.Path,
                   "profile",
                   referenceValidator: null,
                   faultInjector: boundary =>
                   {
                       if (!injected && boundary == TurnJournalCommitBoundary.CommitMarkerFlushed)
                       {
                           injected = true;
                           throw new InjectedJournalFault();
                       }
                   }))
        {
            await Assert.ThrowsAsync<InjectedJournalFault>(() => faulted.WriteAsync(
                identity,
                state.Revision,
                Progress("uncommitted-high-cardinality-tail", appendedCount),
                TestContext.Current.CancellationToken));
        }

        using var crashReopened = Writer(directory.Path);
        var turnDirectory = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(
            directory.Path,
            "turn.fact-index.table.bin",
            SearchOption.AllDirectories)))!;
        var legacyGrowthOrphan = Path.Combine(
            turnDirectory,
            "turn.fact-index.table.bin.building-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(
            legacyGrowthOrphan,
            [0x01],
            TestContext.Current.CancellationToken);
        var replay = await crashReopened.ReplayAsync(
            identity,
            TestContext.Current.CancellationToken);

        Assert.True(replay.RecoveredUncommittedTail);
        Assert.Equal(state.Revision, replay.State!.Revision);
        Assert.Equal(state.Revision, replay.Entries.Count);
        Assert.Empty(Directory.GetFiles(
            directory.Path,
            "*.building-*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FaultDuringExactIndexGrowth_RebuildsFromCommittedJournalOnRestart()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var injected = false;
        TurnState state;
        using (var faulted = new TurnTransitionWriter(
                   directory.Path,
                   "profile",
                   referenceValidator: null,
                   faultInjector: boundary =>
                   {
                       if (!injected
                           && boundary
                           == TurnJournalCommitBoundary.FactIndexGrowthRebuildStarted)
                       {
                           injected = true;
                           throw new InjectedIndexGrowthFault();
                       }
                   }))
        {
            state = (await StartAsync(faulted, identity)).State!;
            for (var index = 0; index < 767; index++)
            {
                state = (await faulted.WriteAsync(
                    identity,
                    state.Revision,
                    Progress("growth-primer-" + index, index),
                    TestContext.Current.CancellationToken)).State!;
            }

            await Assert.ThrowsAsync<InjectedIndexGrowthFault>(() => faulted.WriteAsync(
                identity,
                state.Revision,
                Progress("growth-committed-before-index-fault", 768),
                TestContext.Current.CancellationToken));
        }

        Assert.True(injected);
        using var reopened = Writer(directory.Path);
        var replay = await reopened.ReplayAsync(
            identity,
            TestContext.Current.CancellationToken);

        Assert.False(replay.RecoveredUncommittedTail);
        Assert.Equal(state.Revision + 1, replay.State!.Revision);
        Assert.Equal(replay.State.Revision, (long)replay.Entries.Count);
        Assert.Equal(
            "growth-committed-before-index-fault",
            Assert.IsType<ProgressAttemptRecordedTransition>(
                replay.Entries[^1].Transition).CorrelationKey);
        Assert.Empty(Directory.GetFiles(
            directory.Path,
            "*.building-*",
            SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("turn.fact-index.auth-tree.bin", "delete")]
    [InlineData("turn.fact-index.auth-tree.bin", "truncate")]
    [InlineData("turn.fact-index.manifest.json", "delete")]
    [InlineData("turn.fact-index.manifest.json", "truncate")]
    public async Task MissingOrTornAuthenticationSidecar_RebuildsFromJournal(
        string fileName,
        string mutation)
    {
        using var directory = new TemporaryDirectory();
        using var writer = Writer(directory.Path);
        var identity = Identity();
        var state = (await StartAsync(writer, identity)).State!;
        state = (await writer.WriteAsync(
            identity,
            state.Revision,
            Progress("sidecar-recovery", 1),
            TestContext.Current.CancellationToken)).State!;
        _ = await writer.ReadAsync(identity, TestContext.Current.CancellationToken);

        var path = Assert.Single(Directory.GetFiles(
            directory.Path,
            fileName,
            SearchOption.AllDirectories));
        if (string.Equals(mutation, "delete", StringComparison.Ordinal))
        {
            File.Delete(path);
        }
        else
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            stream.SetLength(Math.Max(1, stream.Length / 2));
        }

        var readsBefore = writer.GetDiagnostics(identity).RecordsReadFromDisk;
        var recovered = await writer.ReadAsync(
            identity,
            TestContext.Current.CancellationToken);

        Assert.Equal(state.Revision, recovered!.Revision);
        Assert.Equal(
            readsBefore + state.Revision,
            writer.GetDiagnostics(identity).RecordsReadFromDisk);
        Assert.Single(Directory.GetFiles(
            directory.Path,
            fileName,
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ColdCommittedAction_AfterRestart_CannotBePreparedAgain()
    {
        using var directory = new TemporaryDirectory();
        var validator = new ReferenceValidator();
        var identity = Identity();
        var targetIntent = Intent("cold-committed-action");
        TurnState state;

        using (var setup = Writer(directory.Path, validator))
        {
            state = (await StartAsync(setup, identity)).State!;
            state = (await setup.PrepareActionAsync(
                identity,
                state.Revision,
                targetIntent,
                "cold-target-prepare",
                TestContext.Current.CancellationToken)).State!;
            var evidence = Evidence("cold-target-evidence", 1);
            validator.AcceptEvidence(evidence);
            state = (await setup.RecordEvidenceAsync(
                identity,
                state.Revision,
                evidence,
                targetIntent.IdempotencyKey,
                "cold-target-evidence-reference",
                TestContext.Current.CancellationToken)).State!;
            state = (await setup.CommitActionAsync(
                identity,
                state.Revision,
                targetIntent.IdempotencyKey,
                evidence.EvidenceId,
                evidence.Cursor,
                "cold-target-commit",
                TestContext.Current.CancellationToken)).State!;

            for (var index = 0;
                 index <= TurnTransitionJournal.MaximumCachedFactsPerKind;
                 index++)
            {
                var filler = Intent("cold-filler-action-" + index);
                state = (await setup.PrepareActionAsync(
                    identity,
                    state.Revision,
                    filler,
                    "cold-filler-prepare-" + index,
                    TestContext.Current.CancellationToken)).State!;
                state = (await setup.WriteAsync(
                    identity,
                    state.Revision,
                    new ActionMarkedInDoubtTransition(
                        "cold-filler-indoubt-" + index,
                        filler.IdempotencyKey),
                    TestContext.Current.CancellationToken)).State!;
                state = (await setup.WriteAsync(
                    identity,
                    state.Revision,
                    new ActionReconciledTransition(
                        "cold-filler-absent-" + index,
                        filler.IdempotencyKey,
                        ActionReconciliationDisposition.Absent,
                        EvidenceId: null,
                        EvidenceCursor: null,
                        OutcomeCode: "absent"),
                    TestContext.Current.CancellationToken)).State!;
            }
        }

        using var reopened = Writer(directory.Path, validator);
        var recovered = await reopened.ReadAsync(
            identity,
            TestContext.Current.CancellationToken);
        Assert.NotNull(recovered);
        var readsBeforeRetry = reopened.GetDiagnostics(identity).RecordsReadFromDisk;
        var retry = await reopened.PrepareActionAsync(
            identity,
            recovered.Revision,
            targetIntent,
            "cold-target-new-correlation",
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnTransitionWriteStatus.AlreadyRecorded, retry.Status);
        Assert.Equal(state.Revision, retry.State!.Revision);
        Assert.Equal(
            readsBeforeRetry + 1,
            reopened.GetDiagnostics(identity).RecordsReadFromDisk);
    }

    [Fact]
    public async Task ColdEvidence_AfterRestart_ResolvesExactlyAndRejectsDuplicatePair()
    {
        using var directory = new TemporaryDirectory();
        var validator = new ReferenceValidator();
        var identity = Identity();
        var target = Evidence("cold-evidence-target", 1);
        TurnState state;

        using (var setup = Writer(directory.Path, validator))
        {
            state = (await StartAsync(setup, identity)).State!;
            validator.AcceptEvidence(target);
            state = (await setup.RecordEvidenceAsync(
                identity,
                state.Revision,
                target,
                actionIdempotencyKey: null,
                "cold-evidence-target-reference",
                TestContext.Current.CancellationToken)).State!;
            for (var index = 0;
                 index <= TurnTransitionJournal.MaximumCachedFactsPerKind;
                 index++)
            {
                var filler = Evidence("cold-evidence-filler-" + index, index + 2L);
                validator.AcceptEvidence(filler);
                state = (await setup.RecordEvidenceAsync(
                    identity,
                    state.Revision,
                    filler,
                    actionIdempotencyKey: null,
                    "cold-evidence-filler-reference-" + index,
                    TestContext.Current.CancellationToken)).State!;
            }
        }

        using var reopened = Writer(directory.Path, validator);
        var recovered = Assert.IsType<TurnState>(await reopened.ReadAsync(
            identity,
            TestContext.Current.CancellationToken));
        var readsBeforeResolution = reopened.GetDiagnostics(identity).RecordsReadFromDisk;
        var resolved = await reopened.ResolveEvidenceReferencesAsync(
            identity,
            [target.EvidenceId],
            TestContext.Current.CancellationToken);

        Assert.Equal(target, Assert.Single(resolved).Value);
        Assert.Equal(
            readsBeforeResolution + 1,
            reopened.GetDiagnostics(identity).RecordsReadFromDisk);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.RecordEvidenceAsync(
            identity,
            recovered.Revision,
            target,
            actionIdempotencyKey: null,
            "cold-evidence-duplicate-pair",
            TestContext.Current.CancellationToken));
        Assert.Equal(
            recovered.Revision,
            (await reopened.ReadAsync(
                identity,
                TestContext.Current.CancellationToken))!.Revision);
    }

    [Fact]
    public async Task WriterLeaseWait_IsCancellationAware()
    {
        using var directory = new TemporaryDirectory();
        using var writer = Writer(directory.Path);
        var identity = Identity();
        var state = (await StartAsync(writer, identity)).State!;
        var leasePath = Assert.Single(Directory.GetFiles(
            directory.Path,
            ".writer.lock",
            SearchOption.AllDirectories));
        using (var heldLease = new FileStream(
                   leasePath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   TestContext.Current.CancellationToken))
        {
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.ReadAsync(
                identity,
                cancellation.Token));
        }

        Assert.Equal(
            state.Revision,
            (await writer.ReadAsync(
                identity,
                TestContext.Current.CancellationToken))!.Revision);
    }

    [Fact]
    public async Task SameLengthJournalTampering_InvalidatesWarmProductionCache()
    {
        using var directory = new TemporaryDirectory();
        using var writer = Writer(directory.Path);
        var identity = Identity();
        var state = (await StartAsync(writer, identity)).State!;
        var appended = await writer.WriteAsync(
            identity,
            state.Revision,
            Progress("tamper-target", 1),
            TestContext.Current.CancellationToken);
        Assert.Equal(TurnTransitionWriteStatus.Committed, appended.Status);

        _ = await writer.ReadAsync(identity, TestContext.Current.CancellationToken);
        var journalPath = Assert.Single(
            Directory.GetFiles(directory.Path, "turn.journal.jsonl", SearchOption.AllDirectories));
        var bytes = await File.ReadAllBytesAsync(
            journalPath,
            TestContext.Current.CancellationToken);
        var originalLength = bytes.LongLength;
        var marker = Encoding.UTF8.GetBytes("tamper-target");
        var markerOffset = bytes.AsSpan().IndexOf(marker);
        Assert.True(markerOffset >= 0);
        bytes[markerOffset] = bytes[markerOffset] == (byte)'t' ? (byte)'u' : (byte)'t';

        await File.WriteAllBytesAsync(
            journalPath,
            bytes,
            TestContext.Current.CancellationToken);

        Assert.Equal(originalLength, new FileInfo(journalPath).Length);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ReadAsync(
            identity,
            TestContext.Current.CancellationToken));
    }

    private static ProgressAttemptRecordedTransition Progress(string correlationKey, int index) =>
        new(
            correlationKey,
            Digest("action-" + index),
            Digest("effect-" + index),
            NoEffectFingerprint: null,
            Digest("before-" + index),
            Digest("after-" + index),
            MateriallyAdvanced: true);

    private static PreparedActionIntent Intent(string idempotencyKey) =>
        new(
            idempotencyKey,
            "work-" + idempotencyKey,
            "write_file",
            "filesystem.write",
            Digest("arguments-" + idempotencyKey),
            Digest("target-" + idempotencyKey),
            Digest("permission-" + idempotencyKey),
            Bindings().CapabilityRegistryDigest,
            Digest("execution-registry-" + idempotencyKey),
            "test-reconciler",
            "root-binding-" + idempotencyKey,
            RequiresApproval: false);

    private static CommittedEvidenceReference Evidence(string id, long cursor) =>
        new(id, cursor, Digest("evidence-" + id));

    private static ExactSlotLocation FindExactStringSlot(
        string root,
        byte factKind,
        string value)
    {
        var tablePath = Assert.Single(Directory.GetFiles(
            root,
            "turn.fact-index.table.bin",
            SearchOption.AllDirectories));
        var keyPath = Assert.Single(Directory.GetFiles(
            root,
            "turn.fact-index.keys.bin",
            SearchOption.AllDirectories));
        var table = File.ReadAllBytes(tablePath);
        var keys = File.ReadAllBytes(keyPath);
        Assert.Equal(0, table.Length % 32);
        var capacity = table.LongLength / 32;
        Assert.True(capacity > 0 && (capacity & (capacity - 1)) == 0);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        var exactKey = new byte[valueBytes.Length + 1];
        exactKey[0] = factKind;
        valueBytes.CopyTo(exactKey, 1);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(exactKey, digest);
        var hash = BinaryPrimitives.ReadUInt64LittleEndian(digest);
        var index = (long)(hash & (ulong)(capacity - 1));
        for (long probe = 0; probe < capacity; probe++)
        {
            var offset = checked((int)(index * 32));
            var slot = table.AsSpan(offset, 32);
            var keyOffset = BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(8, 8));
            if (keyOffset == 0)
            {
                break;
            }

            var keyLength = BinaryPrimitives.ReadInt32LittleEndian(slot.Slice(24, 4));
            if (BinaryPrimitives.ReadUInt64LittleEndian(slot[..8]) == hash
                && keyLength == exactKey.Length
                && keyOffset >= 0
                && keyOffset + keyLength <= keys.LongLength
                && keys.AsSpan(checked((int)keyOffset), keyLength).SequenceEqual(exactKey))
            {
                return new ExactSlotLocation(offset, keyOffset, keyLength);
            }

            index = (index + 1) & (capacity - 1);
        }

        throw new Xunit.Sdk.XunitException(
            "The expected exact fact was not present in the derived sidecar.");
    }

    private static void OverwritePreservingBasicInfo(
        string path,
        long offset,
        ReadOnlySpan<byte> replacement)
    {
        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        Assert.True(GetFileInformationByHandleEx(
            handle,
            FileInfoByHandleClass.FileBasicInfo,
            out var original,
            (uint)Marshal.SizeOf<FileBasicInformation>()));
        var length = RandomAccess.GetLength(handle);
        Assert.InRange(offset, 0, length - replacement.Length);
        RandomAccess.Write(handle, replacement, offset);
        RandomAccess.FlushToDisk(handle);
        var restored = original;
        if (!SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileBasicInfo,
                ref restored,
                (uint)Marshal.SizeOf<FileBasicInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        Assert.True(GetFileInformationByHandleEx(
            handle,
            FileInfoByHandleClass.FileBasicInfo,
            out var after,
            (uint)Marshal.SizeOf<FileBasicInformation>()));
        Assert.Equal(original.ChangeTime, after.ChangeTime);
        Assert.Equal(length, RandomAccess.GetLength(handle));
    }

    private static TurnIdentity Identity() =>
        new("user", "conversation", "assistant-message");

    private static TurnTransitionWriter Writer(string path) =>
        new(path, "profile");

    private static TurnTransitionWriter Writer(
        string path,
        ITurnCommittedReferenceValidator validator) =>
        new(path, "profile", validator);

    private static Task<TurnTransitionWriteResult> StartAsync(
        TurnTransitionWriter writer,
        TurnIdentity identity) =>
        writer.StartAsync(
            identity,
            "Original request",
            Bindings(),
            "turn-start",
            TestContext.Current.CancellationToken);

    private static TurnRuntimeBindings Bindings() =>
        new(
            Digest("profile"),
            Digest("runtime"),
            Digest("model"),
            Digest("settings"),
            Digest("capabilities"),
            Digest("permissions"),
            Digest("mcp"),
            Digest("attachments"),
            Digest("artifacts"));

    private static string Digest(string value) =>
        TurnStateIntegrity.Digest(Encoding.UTF8.GetBytes(value));

    private sealed class UnknownActionReconciler(string reconcilerId) : ITurnActionReconciler
    {
        public string ReconcilerId { get; } = reconcilerId;

        public ValueTask<ActionReconciliationResult> ReconcileAsync(
            TurnIdentity identity,
            PreparedActionIntent intent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ActionReconciliationResult.Unknown("target-state-unknown"));
    }

    private sealed class ReferenceValidator : ITurnCommittedReferenceValidator
    {
        private readonly HashSet<CommittedEvidenceReference> _evidence = [];

        internal void AcceptEvidence(CommittedEvidenceReference evidence) =>
            _evidence.Add(evidence);

        public ValueTask<bool> IsEvidenceCommittedAsync(
            TurnIdentity identity,
            CommittedEvidenceReference evidence,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_evidence.Contains(evidence));

        public ValueTask<bool> IsWorkGraphCommittedAsync(
            TurnIdentity identity,
            CommittedWorkGraphReference workGraph,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
    }

    private readonly record struct ExactSlotLocation(
        long TableOffset,
        long KeyOffset,
        int KeyLength);

    private enum FileInfoByHandleClass
    {
        FileBasicInfo = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        out FileBasicInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        ref FileBasicInformation fileInformation,
        uint bufferSize);

    private sealed class InjectedJournalFault : Exception;

    private sealed class InjectedIndexGrowthFault : Exception;

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Ali-TurnJournalBounds-" + Guid.NewGuid().ToString("N"));
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
