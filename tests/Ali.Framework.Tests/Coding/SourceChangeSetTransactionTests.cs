using System.Text;
using System.Text.Json;
using Ali.Modules.Coding.Changesets;

namespace Ali.Framework.Tests.Coding;

public sealed class SourceChangeSetTransactionTests
{
    private static readonly string AuthorizationDigest =
        AliSourceChangeSetStore.Hash("test-authorization-binding"u8);

    [Fact]
    public async Task TypedAddReplaceDeleteRename_CommitsAndEmitsOnlyRelativePaths()
    {
        using var tree = new TemporaryTree();
        tree.WriteText("src/Replace.cs", "internal sealed class Before { }");
        tree.WriteText("src/Delete.json", "{\"delete\":true}");
        tree.WriteText("src/Rename.xml", "<root />");
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [
                AliSourceChangeRequest.AddText("src/Added.cs", "internal sealed class Added { }"),
                AliSourceChangeRequest.ReplaceText("src/Replace.cs", "internal sealed class After { }"),
                AliSourceChangeRequest.Delete("src/Delete.json"),
                AliSourceChangeRequest.Rename("src/Rename.xml", "src/Renamed.xml")
            ],
            Cancellation);

        var protectedPostimage = await store.ReadPostImageAsync(changeSet, 0, Cancellation);
        Assert.Equal("internal sealed class Added { }", Encoding.UTF8.GetString(protectedPostimage));

        var publisher = CreatePublisher(store);
        var receipt = await publisher.PublishAsync(changeSet, Grant(changeSet), Cancellation);

        Assert.Equal(AliSourcePublicationState.Committed, receipt.State);
        Assert.Equal("internal sealed class Added { }", tree.ReadText("src/Added.cs"));
        Assert.Equal("internal sealed class After { }", tree.ReadText("src/Replace.cs"));
        Assert.False(File.Exists(tree.SourcePath("src/Delete.json")));
        Assert.False(File.Exists(tree.SourcePath("src/Rename.xml")));
        Assert.Equal("<root />", tree.ReadText("src/Renamed.xml"));
        Assert.All(receipt.AffectedPaths, path => Assert.False(System.IO.Path.IsPathRooted(path)));
        Assert.DoesNotContain(tree.Root, JsonSerializer.Serialize(receipt), StringComparison.OrdinalIgnoreCase);

        var durable = await publisher.ReadReceiptAsync(changeSet.Id, Cancellation);
        Assert.NotNull(durable);
        Assert.Equal(receipt.ChangeSetId, durable.ChangeSetId);
        Assert.Equal(receipt.ManifestDigest, durable.ManifestDigest);
        Assert.Equal(receipt.GrantId, durable.GrantId);
        Assert.Equal(receipt.AuthorizationBindingDigest, durable.AuthorizationBindingDigest);
        Assert.Equal(receipt.State, durable.State);
        Assert.Equal(receipt.JournalSequence, durable.JournalSequence);
        Assert.Equal(receipt.JournalAuthenticationTag, durable.JournalAuthenticationTag);
        Assert.Equal(receipt.AffectedPaths.ToArray(), durable.AffectedPaths.ToArray());
        Assert.Equal(receipt.Summary, durable.Summary);
    }

    [Fact]
    public async Task ExpectedPreimageMismatchAndZeroEffect_AreRejectedBeforeManifestCreation()
    {
        using var tree = new TemporaryTree();
        tree.WriteText("Target.cs", "internal sealed class Same { }");
        var store = tree.CreateStore();
        var actual = AliSourceFileImage.FromBytes(await File.ReadAllBytesAsync(tree.SourcePath("Target.cs"), Cancellation));
        var stale = AliSourceExpectedFile.Present(new string('a', 64), actual.Length);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("Target.cs", "internal sealed class New { }", stale)],
            Cancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("Target.cs", "internal sealed class Same { }")],
            Cancellation));
    }

    [Fact]
    public async Task Utf16Bom_IsPreservedInProtectedPostimageAndPublishedFile()
    {
        using var tree = new TemporaryTree();
        var original = WithPreamble(Encoding.Unicode, "internal sealed class Before { }");
        tree.WriteBytes("Utf16.cs", original);
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("Utf16.cs", "internal sealed class After { }")],
            Cancellation);

        Assert.Equal("FFFE", changeSet.Operations[0].Encoding?.PreambleHex);
        var postimage = await store.ReadPostImageAsync(changeSet, 0, Cancellation);
        Assert.True(postimage.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()));

        var receipt = await CreatePublisher(store).PublishAsync(changeSet, Grant(changeSet), Cancellation);

        Assert.Equal(AliSourcePublicationState.Committed, receipt.State);
        Assert.Equal(postimage, await File.ReadAllBytesAsync(tree.SourcePath("Utf16.cs"), Cancellation));
    }

    [Fact]
    public async Task InvalidCSharpJsonAndXml_AreRejectedWithoutCanonicalWrites()
    {
        using var tree = new TemporaryTree();
        tree.WriteText("Bad.cs", "internal sealed class Good { }");
        tree.WriteText("Bad.json", "{\"good\":true}");
        tree.WriteText("Bad.xml", "<root />");
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [
                AliSourceChangeRequest.ReplaceText("Bad.cs", "internal sealed class {"),
                AliSourceChangeRequest.ReplaceText("Bad.json", "{not-json}"),
                AliSourceChangeRequest.ReplaceText("Bad.xml", "<!DOCTYPE root><root />")
            ],
            Cancellation);
        var validator = new AliSourceChangeSetValidator(store);

        var validation = await validator.ValidateAsync(changeSet, Cancellation);
        var receipt = await new AliSourceChangeSetPublisher(store, validator)
            .PublishAsync(changeSet, Grant(changeSet), Cancellation);

        Assert.False(validation.Valid);
        Assert.Contains(validation.Errors, error => error.StartsWith("Bad.cs:", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.StartsWith("Bad.json:", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.StartsWith("Bad.xml:", StringComparison.Ordinal));
        Assert.Equal(AliSourcePublicationState.RolledBack, receipt.State);
        Assert.Equal("internal sealed class Good { }", tree.ReadText("Bad.cs"));
        Assert.Equal("{\"good\":true}", tree.ReadText("Bad.json"));
        Assert.Equal("<root />", tree.ReadText("Bad.xml"));
    }

    [Fact]
    public async Task ProtectedArtifacts_ProfileMismatchManifestTamperAndBlobTamper_AreRejected()
    {
        using var tree = new TemporaryTree();
        tree.WriteText("Secret.cs", "internal sealed class Before { }");
        var store = tree.CreateStore("profile-a");
        var changeSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("Secret.cs", "internal sealed class SecretMarker { }")],
            Cancellation);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            tree.CreateStore("profile-b").LoadAsync(changeSet.Id, Cancellation));

        var blobPath = System.IO.Path.Combine(
            tree.Store,
            "changesets",
            changeSet.Id,
            "blobs",
            $"0000-{changeSet.Operations[0].ContentBlobSha256}.protected");
        var protectedBytes = await File.ReadAllBytesAsync(blobPath, Cancellation);
        Assert.DoesNotContain("SecretMarker", Encoding.UTF8.GetString(protectedBytes), StringComparison.Ordinal);
        protectedBytes[protectedBytes.Length / 2] ^= 0x5A;
        await File.WriteAllBytesAsync(blobPath, protectedBytes, Cancellation);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(changeSet.Id, Cancellation));

        var second = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("Secret.cs", "internal sealed class SecondMarker { }")],
            Cancellation);
        var manifestPath = System.IO.Path.Combine(tree.Store, "changesets", second.Id, "manifest.json");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, Cancellation);
        manifestBytes[manifestBytes.Length / 2] ^= 0x01;
        await File.WriteAllBytesAsync(manifestPath, manifestBytes, Cancellation);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(second.Id, Cancellation));
    }

    [Fact]
    public async Task AuthenticatedManifestCopiedUnderAnotherId_IsRejectedAsReplay()
    {
        using var tree = new TemporaryTree();
        tree.WriteText("Replay.cs", "internal sealed class Before { }");
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("Replay.cs", "internal sealed class After { }")],
            Cancellation);
        var replayId = Guid.NewGuid().ToString("N");
        CopyDirectory(
            System.IO.Path.Combine(tree.Store, "changesets", changeSet.Id),
            System.IO.Path.Combine(tree.Store, "changesets", replayId));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(replayId, Cancellation));
    }

    [Fact]
    public async Task StaleManifestAndBothGrantReplayForms_AreRejected()
    {
        using var tree = new TemporaryTree();
        tree.WriteText("Stale.cs", "internal sealed class Before { }");
        var store = tree.CreateStore();
        var staleSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("Stale.cs", "internal sealed class Proposed { }")],
            Cancellation);
        tree.WriteText("Stale.cs", "internal sealed class External { }");

        var staleReceipt = await CreatePublisher(store)
            .PublishAsync(staleSet, Grant(staleSet), Cancellation);

        Assert.Equal(AliSourcePublicationState.RolledBack, staleReceipt.State);
        Assert.Equal("internal sealed class External { }", tree.ReadText("Stale.cs"));

        tree.WriteText("Replay.cs", "internal sealed class Before { }");
        var replaySet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("Replay.cs", "internal sealed class After { }")],
            Cancellation);
        var publisher = CreatePublisher(store);
        var grant = Grant(replaySet);
        Assert.Equal(
            AliSourcePublicationState.Committed,
            (await publisher.PublishAsync(replaySet, grant, Cancellation)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishAsync(replaySet, grant, Cancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishAsync(replaySet, Grant(replaySet), Cancellation));
    }

    [Theory]
    [InlineData((int)AliSourceTransactionBoundary.GrantConsumed, (int)AliSourcePublicationState.RolledBack)]
    [InlineData((int)AliSourceTransactionBoundary.PublicationLeaseAcquired, (int)AliSourcePublicationState.RolledBack)]
    [InlineData((int)AliSourceTransactionBoundary.PreparedReceiptPersisted, (int)AliSourcePublicationState.RolledBack)]
    [InlineData((int)AliSourceTransactionBoundary.OperationIntentPersisted, (int)AliSourcePublicationState.RolledBack)]
    [InlineData((int)AliSourceTransactionBoundary.OperationMutationCompleted, (int)AliSourcePublicationState.InDoubt)]
    [InlineData((int)AliSourceTransactionBoundary.OperationAppliedPersisted, (int)AliSourcePublicationState.Committed)]
    [InlineData((int)AliSourceTransactionBoundary.OperationVerifiedPersisted, (int)AliSourcePublicationState.Committed)]
    [InlineData((int)AliSourceTransactionBoundary.TerminalJournalPersisted, (int)AliSourcePublicationState.Committed)]
    [InlineData((int)AliSourceTransactionBoundary.TerminalReceiptPersisted, (int)AliSourcePublicationState.Committed)]
    public async Task RestartRecovery_IsDeterministicAtEveryForwardBoundary(
        int boundaryValue,
        int expectedStateValue)
    {
        var boundary = (AliSourceTransactionBoundary)boundaryValue;
        var expectedState = (AliSourcePublicationState)expectedStateValue;
        using var tree = new TemporaryTree();
        tree.WriteText("Boundary.cs", "internal sealed class Before { }");
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("Boundary.cs", "internal sealed class After { }")],
            Cancellation);
        var publisher = new AliSourceChangeSetPublisher(
            store,
            new AliSourceChangeSetValidator(store),
            fault =>
            {
                if (fault.Boundary == boundary)
                {
                    throw new AliSourceSimulatedInterruptionException(fault.Boundary, fault.OperationSequence);
                }
            });

        await Assert.ThrowsAsync<AliSourceSimulatedInterruptionException>(() =>
            publisher.PublishAsync(changeSet, Grant(changeSet), Cancellation));
        var recoveryPublisher = CreatePublisher(store);
        var result = await new AliSourceChangeSetReconciler(store, recoveryPublisher)
            .ReconcileAsync(changeSet.Id, Cancellation);

        Assert.Equal(expectedState, result.State);
        Assert.Equal(
            expectedState is AliSourcePublicationState.Committed or AliSourcePublicationState.RolledBack,
            result.SafeToContinue);
        Assert.Equal(
            expectedState is AliSourcePublicationState.Committed or AliSourcePublicationState.InDoubt
                ? "internal sealed class After { }"
                : "internal sealed class Before { }",
            tree.ReadText("Boundary.cs"));
        Assert.Equal(expectedState, (await recoveryPublisher.ReadReceiptAsync(changeSet.Id, Cancellation))?.State);
    }

    [Theory]
    [InlineData((int)AliSourceTransactionBoundary.RollbackIntentPersisted)]
    [InlineData((int)AliSourceTransactionBoundary.RollbackMutationCompleted)]
    [InlineData((int)AliSourceTransactionBoundary.RollbackAppliedPersisted)]
    [InlineData((int)AliSourceTransactionBoundary.RollbackVerifiedPersisted)]
    [InlineData((int)AliSourceTransactionBoundary.TerminalJournalPersisted)]
    [InlineData((int)AliSourceTransactionBoundary.TerminalReceiptPersisted)]
    public async Task RestartRecovery_IsDeterministicAtEveryRollbackBoundary(
        int boundaryValue)
    {
        var boundary = (AliSourceTransactionBoundary)boundaryValue;
        using var tree = new TemporaryTree();
        tree.WriteText("First.cs", "internal sealed class FirstBefore { }");
        tree.WriteText("Second.cs", "internal sealed class SecondBefore { }");
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [
                AliSourceChangeRequest.ReplaceText("First.cs", "internal sealed class FirstAfter { }"),
                AliSourceChangeRequest.ReplaceText("Second.cs", "internal sealed class SecondAfter { }")
            ],
            Cancellation);
        var publisher = new AliSourceChangeSetPublisher(
            store,
            new AliSourceChangeSetValidator(store),
            fault =>
            {
                if (fault.Boundary == AliSourceTransactionBoundary.OperationIntentPersisted
                    && fault.OperationSequence == 1)
                {
                    throw new IOException("Injected ordinary publication failure.");
                }
                if (fault.Boundary == boundary)
                {
                    throw new AliSourceSimulatedInterruptionException(fault.Boundary, fault.OperationSequence);
                }
            });

        await Assert.ThrowsAsync<AliSourceSimulatedInterruptionException>(() =>
            publisher.PublishAsync(changeSet, Grant(changeSet), Cancellation));
        var recoveryPublisher = CreatePublisher(store);
        var result = await new AliSourceChangeSetReconciler(store, recoveryPublisher)
            .ReconcileAsync(changeSet.Id, Cancellation);

        Assert.Equal(AliSourcePublicationState.RolledBack, result.State);
        Assert.True(result.SafeToContinue);
        Assert.Equal("internal sealed class FirstBefore { }", tree.ReadText("First.cs"));
        Assert.Equal("internal sealed class SecondBefore { }", tree.ReadText("Second.cs"));
    }

    [Fact]
    public async Task MixedCrashBeforeAppliedIdentity_IsInDoubtAndRestartDoesNotMutate()
    {
        using var tree = new TemporaryTree();
        tree.WriteText("First.cs", "internal sealed class FirstBefore { }");
        tree.WriteText("Second.cs", "internal sealed class SecondBefore { }");
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [
                AliSourceChangeRequest.ReplaceText("First.cs", "internal sealed class FirstAfter { }"),
                AliSourceChangeRequest.ReplaceText("Second.cs", "internal sealed class SecondAfter { }")
            ],
            Cancellation);
        var interrupted = new AliSourceChangeSetPublisher(
            store,
            new AliSourceChangeSetValidator(store),
            fault =>
            {
                if (fault is
                    {
                        Boundary: AliSourceTransactionBoundary.OperationMutationCompleted,
                        OperationSequence: 0
                    })
                {
                    throw new AliSourceSimulatedInterruptionException(fault.Boundary, fault.OperationSequence);
                }
            });

        await Assert.ThrowsAsync<AliSourceSimulatedInterruptionException>(() =>
            interrupted.PublishAsync(changeSet, Grant(changeSet), Cancellation));
        Assert.Equal("internal sealed class FirstAfter { }", tree.ReadText("First.cs"));
        Assert.Equal("internal sealed class SecondBefore { }", tree.ReadText("Second.cs"));

        var publisher = CreatePublisher(store);
        var recovered = await new AliSourceChangeSetReconciler(store, publisher)
            .ReconcileAsync(changeSet.Id, Cancellation);

        Assert.Equal(AliSourcePublicationState.InDoubt, recovered.State);
        Assert.False(recovered.SafeToContinue);
        Assert.Equal("internal sealed class FirstAfter { }", tree.ReadText("First.cs"));
        Assert.Equal("internal sealed class SecondBefore { }", tree.ReadText("Second.cs"));
    }

    [Fact]
    public async Task HeldFuturePreimage_BlocksLateLockAndRollsBackWithoutPathLeak()
    {
        using var tree = new TemporaryTree();
        tree.WriteText("First.cs", "internal sealed class FirstBefore { }");
        tree.WriteText("Second.cs", "internal sealed class SecondBefore { }");
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [
                AliSourceChangeRequest.ReplaceText("First.cs", "internal sealed class FirstAfter { }"),
                AliSourceChangeRequest.ReplaceText("Second.cs", "internal sealed class SecondAfter { }")
            ],
            Cancellation);
        FileStream? heldLock = null;
        try
        {
            var publisher = new AliSourceChangeSetPublisher(
                store,
                new AliSourceChangeSetValidator(store),
                fault =>
                {
                    if (fault is
                        {
                            Boundary: AliSourceTransactionBoundary.OperationVerifiedPersisted,
                            OperationSequence: 0
                        })
                    {
                        heldLock ??= new FileStream(
                            tree.SourcePath("Second.cs"),
                            FileMode.Open,
                            FileAccess.ReadWrite,
                            FileShare.None);
                    }
                });

            var failure = await Assert.ThrowsAsync<AliSourcePublicationException>(() =>
                publisher.PublishAsync(changeSet, Grant(changeSet), Cancellation));

            Assert.Equal(AliSourcePublicationState.RolledBack, failure.Receipt.State);
            Assert.DoesNotContain(tree.Root, JsonSerializer.Serialize(failure.Receipt), StringComparison.OrdinalIgnoreCase);
            Assert.All(failure.Receipt.AffectedPaths, path => Assert.False(System.IO.Path.IsPathRooted(path)));
        }
        finally
        {
            heldLock?.Dispose();
        }

        var recoveryPublisher = CreatePublisher(store);
        var recovered = await new AliSourceChangeSetReconciler(store, recoveryPublisher)
            .ReconcileAsync(changeSet.Id, Cancellation);

        Assert.Equal(AliSourcePublicationState.RolledBack, recovered.State);
        Assert.True(recovered.SafeToContinue);
        Assert.Equal("internal sealed class FirstBefore { }", tree.ReadText("First.cs"));
        Assert.Equal("internal sealed class SecondBefore { }", tree.ReadText("Second.cs"));
    }

    [Fact]
    public async Task RollbackRevalidatesSkippedAbsentPreimage_AndDoesNotCertifyExternalCreation()
    {
        using var tree = new TemporaryTree();
        tree.WriteText("First.cs", "internal sealed class FirstBefore { }");
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [
                AliSourceChangeRequest.ReplaceText("First.cs", "internal sealed class FirstAfter { }"),
                AliSourceChangeRequest.AddText("Future.cs", "internal sealed class AliFuture { }")
            ],
            Cancellation);
        var publisher = new AliSourceChangeSetPublisher(
            store,
            new AliSourceChangeSetValidator(store),
            fault =>
            {
                if (fault is
                    {
                        Boundary: AliSourceTransactionBoundary.OperationVerifiedPersisted,
                        OperationSequence: 0
                    })
                {
                    tree.WriteText("Future.cs", "internal sealed class ExternalFuture { }");
                    throw new IOException("Injected failure after an external absent-name race.");
                }
            });

        var failure = await Assert.ThrowsAsync<AliSourcePublicationException>(() =>
            publisher.PublishAsync(changeSet, Grant(changeSet), Cancellation));

        Assert.Equal(AliSourcePublicationState.InDoubt, failure.Receipt.State);
        Assert.Equal("internal sealed class FirstBefore { }", tree.ReadText("First.cs"));
        Assert.Equal("internal sealed class ExternalFuture { }", tree.ReadText("Future.cs"));

        var recovered = await new AliSourceChangeSetReconciler(store, CreatePublisher(store))
            .ReconcileAsync(changeSet.Id, Cancellation);
        Assert.Equal(AliSourcePublicationState.InDoubt, recovered.State);
        Assert.False(recovered.SafeToContinue);
        Assert.Equal("internal sealed class ExternalFuture { }", tree.ReadText("Future.cs"));
    }

    [Fact]
    public async Task ConcurrentPublishers_SerializeOnRootLeaseAndSecondBecomesStale()
    {
        using var tree = new TemporaryTree();
        tree.WriteText("Shared.cs", "internal sealed class Before { }");
        var store = tree.CreateStore();
        var firstSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("Shared.cs", "internal sealed class First { }")],
            Cancellation);
        var secondSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("Shared.cs", "internal sealed class Second { }")],
            Cancellation);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        var firstPublisher = new AliSourceChangeSetPublisher(
            store,
            new AliSourceChangeSetValidator(store),
            fault =>
            {
                if (fault is
                    {
                        Boundary: AliSourceTransactionBoundary.OperationIntentPersisted,
                        OperationSequence: 0
                    })
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("The concurrent publication test was not released.");
                    }
                }
            });
        var secondPublisher = CreatePublisher(store);

        var firstTask = Task.Run(
            () => firstPublisher.PublishAsync(firstSet, Grant(firstSet), Cancellation),
            Cancellation);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        var secondTask = Task.Run(
            () => secondPublisher.PublishAsync(secondSet, Grant(secondSet), Cancellation),
            Cancellation);
        try
        {
            await Task.Delay(100, Cancellation);
            Assert.False(secondTask.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        var first = await firstTask;
        var second = await secondTask;
        Assert.Equal(AliSourcePublicationState.Committed, first.State);
        Assert.Equal(AliSourcePublicationState.RolledBack, second.State);
        Assert.Equal("internal sealed class First { }", tree.ReadText("Shared.cs"));
    }

    [Fact]
    public async Task Materializer_UsesBoundedExclusionsRecordsPolicyAndRejectsExcludedChanges()
    {
        using var tree = new TemporaryTree();
        tree.WriteText("src/App.cs", "internal sealed class Before { }");
        tree.WriteText(".git/config", "must-not-copy");
        tree.WriteText("bin/cache.bin", "must-not-copy");
        tree.WriteText("secret/Secret.cs", "internal sealed class SecretBefore { }");
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("src/App.cs", "internal sealed class After { }")],
            Cancellation);
        var stage = tree.CreateEmptyDirectory("stage-default");

        var receipt = await store.MaterializeStagedTreeAsync(changeSet, stage, Cancellation);

        Assert.Equal(AliSourceTreeMaterializationPolicy.SafeDefault.Digest, receipt.PolicyDigest);
        Assert.Equal("internal sealed class After { }", File.ReadAllText(System.IO.Path.Combine(stage, "src", "App.cs")));
        Assert.False(Directory.Exists(System.IO.Path.Combine(stage, ".git")));
        Assert.False(Directory.Exists(System.IO.Path.Combine(stage, "bin")));
        var durablePath = System.IO.Path.Combine(
            store.GetTransactionDirectory(changeSet.Id),
            "materializations",
            receipt.ReceiptId + ".json");
        var durable = await store.ReadAuthenticatedDocumentAsync<AliSourceTreeMaterializationReceipt>(
            durablePath,
            $"materialization:{changeSet.Id}:{receipt.ReceiptId}",
            AliSourceChangeSetStore.MaximumReceiptBytes,
            Cancellation);
        Assert.Equal(receipt, durable);

        var excludedSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("secret/Secret.cs", "internal sealed class SecretAfter { }")],
            Cancellation);
        var policy = AliSourceTreeMaterializationPolicy.Create(
            excludedRelativePaths: ["secret"],
            excludedDirectoryNames: ["obj"],
            maximumEntries: 100,
            maximumBytes: 1024 * 1024);
        var excludedStage = tree.CreateEmptyDirectory("stage-excluded");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MaterializeStagedTreeAsync(
            excludedSet,
            excludedStage,
            policy,
            Cancellation));
        Assert.NotEqual(policy.Digest, AliSourceTreeMaterializationPolicy.SafeDefault.Digest);
        Assert.Empty(Directory.EnumerateFileSystemEntries(excludedStage));
    }

    [Fact]
    public async Task Materializer_HeldSourceFileBlocksInterposedNamespaceReplacement()
    {
        using var tree = new TemporaryTree();
        tree.WriteText("src/App.cs", "internal sealed class Before { }");
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("src/App.cs", "internal sealed class After { }")],
            Cancellation);
        var stage = tree.CreateEmptyDirectory("stage-source-interposition");
        var blocked = false;

        var receipt = await store.MaterializeStagedTreeForTestingAsync(
            changeSet,
            stage,
            AliSourceTreeMaterializationPolicy.SafeDefault,
            fault =>
            {
                if (fault is
                    {
                        Boundary: AliSourceTreeMaterializationBoundary.SourceFileBound,
                        RelativePath: "src/App.cs"
                    })
                {
                    try
                    {
                        File.Move(tree.SourcePath("src/App.cs"), tree.RootPath("stolen-source.cs"));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        blocked = true;
                    }
                }
            },
            Cancellation);

        Assert.True(blocked);
        Assert.Equal(changeSet.Id, receipt.ChangeSetId);
        Assert.Equal("internal sealed class Before { }", tree.ReadText("src/App.cs"));
        Assert.Equal(
            "internal sealed class After { }",
            File.ReadAllText(System.IO.Path.Combine(stage, "src", "App.cs")));
        Assert.False(File.Exists(tree.RootPath("stolen-source.cs")));
    }

    [Fact]
    public async Task Materializer_HeldStagedSpineBlocksInterposedDirectoryRebind()
    {
        using var tree = new TemporaryTree();
        tree.WriteText("src/App.cs", "internal sealed class Before { }");
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("src/App.cs", "internal sealed class After { }")],
            Cancellation);
        var stage = tree.CreateEmptyDirectory("stage-spine-interposition");
        var stolen = tree.RootPath("stolen-stage-src");
        var blocked = false;

        var receipt = await store.MaterializeStagedTreeForTestingAsync(
            changeSet,
            stage,
            AliSourceTreeMaterializationPolicy.SafeDefault,
            fault =>
            {
                if (fault.Boundary == AliSourceTreeMaterializationBoundary.StagedPreimagesBound)
                {
                    try
                    {
                        Directory.Move(System.IO.Path.Combine(stage, "src"), stolen);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        blocked = true;
                    }
                }
            },
            Cancellation);

        Assert.True(blocked);
        Assert.Equal(changeSet.Id, receipt.ChangeSetId);
        Assert.Equal(
            "internal sealed class After { }",
            File.ReadAllText(System.IO.Path.Combine(stage, "src", "App.cs")));
        Assert.False(Directory.Exists(stolen));
    }

    [Fact]
    public async Task UnsafePathsOperationBoundsAndReparseTargets_AreRejected()
    {
        using var tree = new TemporaryTree();
        var store = tree.CreateStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.AddText("../escape.cs", "class Escape { }")],
            Cancellation));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.CreateAsync(
            tree.Source,
            Enumerable.Range(0, AliSourceChangeSetStore.MaximumOperations + 1)
                .Select(index => AliSourceChangeRequest.AddText($"File{index}.cs", $"class File{index} {{ }}"))
                .ToArray(),
            Cancellation));

        var outside = tree.RootPath("outside.cs");
        await File.WriteAllTextAsync(outside, "internal sealed class Outside { }", Cancellation);
        var link = tree.SourcePath("Link.cs");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return;
        }

        await Assert.ThrowsAnyAsync<Exception>(() => store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("Link.cs", "internal sealed class Replaced { }")],
            Cancellation));
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static AliSourceChangeSetPublisher CreatePublisher(AliSourceChangeSetStore store) =>
        new(store, new AliSourceChangeSetValidator(store));

    private static AliSourcePublicationGrant Grant(AliSourceChangeSet changeSet) =>
        AliSourcePublicationGrant.Issue(changeSet, AuthorizationDigest);

    private static byte[] WithPreamble(Encoding encoding, string content)
    {
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(content);
        return [.. preamble, .. body];
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(destination + directory[source.Length..]);
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, destination + file[source.Length..]);
        }
    }

    private sealed class TemporaryTree : IDisposable
    {
        internal TemporaryTree()
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "AliSourceChangeSetTests",
                Guid.NewGuid().ToString("N"));
            Source = System.IO.Path.Combine(Root, "source");
            Store = System.IO.Path.Combine(Root, "store");
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Store);
        }

        internal string Root { get; }
        internal string Source { get; }
        internal string Store { get; }

        internal AliSourceChangeSetStore CreateStore(string profile = "transaction-test-profile") =>
            new(Store, profile);

        internal string RootPath(string relativePath) =>
            System.IO.Path.Combine(Root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        internal string SourcePath(string relativePath) =>
            System.IO.Path.Combine(Source, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        internal string CreateEmptyDirectory(string name)
        {
            var path = RootPath(name);
            Directory.CreateDirectory(path);
            return path;
        }

        internal void WriteText(string relativePath, string content) =>
            WriteBytes(relativePath, Encoding.UTF8.GetBytes(content));

        internal void WriteBytes(string relativePath, byte[] content)
        {
            var path = SourcePath(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        internal string ReadText(string relativePath) => File.ReadAllText(SourcePath(relativePath));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
