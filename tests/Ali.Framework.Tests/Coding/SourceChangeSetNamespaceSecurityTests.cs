using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Ali.Modules.Coding.Changesets;

namespace Ali.Framework.Tests.Coding;

public sealed class SourceChangeSetNamespaceSecurityTests
{
    private static readonly string AuthorizationDigest =
        AliSourceChangeSetStore.Hash("namespace-security-authorization"u8);

    [Fact]
    public async Task ManifestBindsFullRootAndOrderedNamespaceSpineIdentities()
    {
        using var tree = new NamespaceTree();
        tree.WriteText("src/deep/Target.cs", "internal sealed class Before { }");
        var store = tree.CreateStore();

        var changeSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("src/deep/Target.cs", "internal sealed class After { }")],
            Cancellation);
        var loaded = await store.LoadAsync(changeSet.Id, Cancellation);

        Assert.Equal(changeSet.SourceRootIdentity, loaded.SourceRootIdentity);
        Assert.Equal(
            new[] { "src", "src/deep" },
            loaded.NamespaceSpines.Select(spine => spine.RelativeDirectoryPath).ToArray());
        Assert.All(loaded.NamespaceSpines, spine =>
        {
            Assert.Equal(loaded.SourceRootIdentity.VolumeSerialNumber, spine.Identity.VolumeSerialNumber);
            Assert.Equal(64, spine.Identity.FinalNameDigest.Length);
        });
        Assert.NotNull(loaded.Operations[0].SourcePreimageIdentity);
        Assert.Equal(1u, loaded.Operations[0].SourcePreimageIdentity!.NumberOfLinks);
    }

    [Fact]
    public async Task ParentReparseRebindAfterManifest_IsRejectedWithoutOutsideMutation()
    {
        using var tree = new NamespaceTree();
        tree.WriteText("src/Target.cs", "internal sealed class Before { }");
        var outside = tree.CreateDirectory("outside");
        await File.WriteAllTextAsync(
            Path.Combine(outside, "Target.cs"),
            "internal sealed class Outside { }",
            Cancellation);
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("src/Target.cs", "internal sealed class After { }")],
            Cancellation);

        var parent = tree.SourcePath("src");
        var displaced = tree.RootPath("displaced-src");
        Directory.Move(parent, displaced);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(parent, outside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                                               or PlatformNotSupportedException
                                               or IOException)
            {
                return;
            }

            var receipt = await CreatePublisher(store)
                .PublishAsync(changeSet, Grant(changeSet), Cancellation);

            Assert.Equal(AliSourcePublicationState.RolledBack, receipt.State);
            Assert.Equal(
                "internal sealed class Outside { }",
                await File.ReadAllTextAsync(Path.Combine(outside, "Target.cs"), Cancellation));
            Assert.Equal(
                "internal sealed class Before { }",
                await File.ReadAllTextAsync(Path.Combine(displaced, "Target.cs"), Cancellation));
        }
        finally
        {
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent);
            }
            if (Directory.Exists(displaced) && !Directory.Exists(parent))
            {
                Directory.Move(displaced, parent);
            }
        }
    }

    [Fact]
    public async Task HeldParentSpine_DeniesLiveRenameBeforeCanonicalMutation()
    {
        using var tree = new NamespaceTree();
        tree.WriteText("src/Target.cs", "internal sealed class Before { }");
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("src/Target.cs", "internal sealed class After { }")],
            Cancellation);
        var blocked = false;
        var publisher = new AliSourceChangeSetPublisher(
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
                    try
                    {
                        Directory.Move(tree.SourcePath("src"), tree.RootPath("stolen-src"));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        blocked = true;
                    }
                }
            });

        var receipt = await publisher.PublishAsync(changeSet, Grant(changeSet), Cancellation);

        Assert.True(blocked);
        Assert.Equal(AliSourcePublicationState.Committed, receipt.State);
        Assert.Equal("internal sealed class After { }", tree.ReadText("src/Target.cs"));
        Assert.False(Directory.Exists(tree.RootPath("stolen-src")));
    }

    [Fact]
    public async Task RenameSourceRecreatedAfterIndividualVerification_CannotBecomeCommitted()
    {
        using var tree = new NamespaceTree();
        tree.WriteText("RenameBefore.cs", "internal sealed class Before { }");
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.Rename("RenameBefore.cs", "RenameAfter.cs")],
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
                    tree.WriteText("RenameBefore.cs", "internal sealed class External { }");
                }
            });

        var failure = await Assert.ThrowsAsync<AliSourcePublicationException>(() =>
            publisher.PublishAsync(changeSet, Grant(changeSet), Cancellation));

        Assert.Equal(AliSourcePublicationState.InDoubt, failure.Receipt.State);
        Assert.Equal("internal sealed class External { }", tree.ReadText("RenameBefore.cs"));
        Assert.Equal("internal sealed class Before { }", tree.ReadText("RenameAfter.cs"));
        var recovered = await new AliSourceChangeSetReconciler(store, CreatePublisher(store))
            .ReconcileAsync(changeSet.Id, Cancellation);
        Assert.Equal(AliSourcePublicationState.InDoubt, recovered.State);
        Assert.False(recovered.SafeToContinue);
    }

    [Fact]
    public async Task SameBytesWithReplacementFileIdentity_IsRejectedAsStale()
    {
        using var tree = new NamespaceTree();
        const string before = "internal sealed class Before { }";
        tree.WriteText("Target.cs", before);
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("Target.cs", "internal sealed class After { }")],
            Cancellation);

        tree.WriteText("Replacement.tmp", before);
        File.Delete(tree.SourcePath("Target.cs"));
        File.Move(tree.SourcePath("Replacement.tmp"), tree.SourcePath("Target.cs"));
        using (var sourceNamespace = AliSourceWindowsNamespace.Capture(tree.Source))
        {
            var replacement = await sourceNamespace.CaptureOptionalFileAsync("Target.cs", Cancellation);
            Assert.NotNull(replacement);
            try
            {
                Assert.False(changeSet.Operations[0].SourcePreimageIdentity!.SameObject(replacement!.Identity));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(replacement!.Bytes);
            }
        }

        var receipt = await CreatePublisher(store)
            .PublishAsync(changeSet, Grant(changeSet), Cancellation);

        Assert.Equal(AliSourcePublicationState.RolledBack, receipt.State);
        Assert.Equal(before, tree.ReadText("Target.cs"));
    }

    [Theory]
    [InlineData("Target.cs:secret")]
    [InlineData("TrailingDot.cs.")]
    [InlineData("TrailingSpace.cs ")]
    [InlineData("CON.cs")]
    [InlineData("NUL.txt")]
    [InlineData("COM¹.log")]
    [InlineData("LPT²")]
    [InlineData("CONOUT$.txt")]
    public async Task NativeAmbiguousLeafNames_AreRejected(string relativePath)
    {
        using var tree = new NamespaceTree();
        var store = tree.CreateStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.AddText(relativePath, "internal sealed class Added { }")],
            Cancellation));
    }

    [Fact]
    public async Task ExistingEightDotThreeAlias_CannotEnterAsSecondOperation_WhenAvailable()
    {
        using var tree = new NamespaceTree();
        const string longName = "LongSourceFileNameForAliasCollision.cs";
        tree.WriteText(longName, "internal sealed class Before { }");
        var shortSource = TryGetShortPath(tree.Source);
        var shortFile = TryGetShortPath(tree.SourcePath(longName));
        if (shortSource is null || shortFile is null)
        {
            return;
        }
        var shortRelative = Path.GetRelativePath(shortSource, shortFile).Replace('\\', '/');
        if (string.Equals(shortRelative, longName, StringComparison.OrdinalIgnoreCase)
            || shortRelative.Contains("..", StringComparison.Ordinal))
        {
            return;
        }

        var store = tree.CreateStore();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CreateAsync(
            tree.Source,
            [
                AliSourceChangeRequest.ReplaceText(longName, "internal sealed class After { }"),
                AliSourceChangeRequest.Delete(shortRelative)
            ],
            Cancellation));
    }

    [Fact]
    public async Task ExistingEightDotThreeAlias_IsRejectedAsNonCanonical_WhenAvailable()
    {
        using var tree = new NamespaceTree();
        const string longName = "LongSourceFileNameForSingleAlias.cs";
        tree.WriteText(longName, "internal sealed class Before { }");
        var shortSource = TryGetShortPath(tree.Source);
        var shortFile = TryGetShortPath(tree.SourcePath(longName));
        if (shortSource is null || shortFile is null)
        {
            return;
        }
        var shortRelative = Path.GetRelativePath(shortSource, shortFile).Replace('\\', '/');
        if (string.Equals(shortRelative, longName, StringComparison.OrdinalIgnoreCase)
            || shortRelative.Contains("..", StringComparison.Ordinal))
        {
            return;
        }

        var store = tree.CreateStore();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText(shortRelative, "internal sealed class After { }")],
            Cancellation));
    }

    [Fact]
    public async Task Materializer_RejectsPhysicalRootOverlapThroughEightDotThreeAlias_WhenAvailable()
    {
        using var tree = new NamespaceTree();
        tree.WriteText("Target.cs", "internal sealed class Before { }");
        var shortSource = TryGetShortPath(tree.Source);
        if (shortSource is null
            || string.Equals(shortSource, tree.Source, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [AliSourceChangeRequest.ReplaceText("Target.cs", "internal sealed class After { }")],
            Cancellation);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.MaterializeStagedTreeAsync(changeSet, shortSource, Cancellation));
    }

    [Theory]
    [InlineData((int)AliSourceChangeKind.Add)]
    [InlineData((int)AliSourceChangeKind.Replace)]
    [InlineData((int)AliSourceChangeKind.Delete)]
    [InlineData((int)AliSourceChangeKind.Rename)]
    public async Task AppliedIdentity_AllOperationKinds_RestartRollbackExactPreimage(int kindValue)
    {
        var kind = (AliSourceChangeKind)kindValue;
        using var tree = new NamespaceTree();
        var first = ArrangeOperation(tree, kind);
        tree.WriteText("Trigger.cs", "internal sealed class TriggerBefore { }");
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [first, AliSourceChangeRequest.ReplaceText("Trigger.cs", "internal sealed class TriggerAfter { }")],
            Cancellation);
        var interrupted = new AliSourceChangeSetPublisher(
            store,
            new AliSourceChangeSetValidator(store),
            fault =>
            {
                if (fault is
                    {
                        Boundary: AliSourceTransactionBoundary.OperationAppliedPersisted,
                        OperationSequence: 0
                    })
                {
                    throw new AliSourceSimulatedInterruptionException(fault.Boundary, fault.OperationSequence);
                }
            });

        await Assert.ThrowsAsync<AliSourceSimulatedInterruptionException>(() =>
            interrupted.PublishAsync(changeSet, Grant(changeSet), Cancellation));
        var recoveryPublisher = CreatePublisher(store);
        var recovered = await new AliSourceChangeSetReconciler(store, recoveryPublisher)
            .ReconcileAsync(changeSet.Id, Cancellation);

        Assert.Equal(AliSourcePublicationState.RolledBack, recovered.State);
        Assert.True(recovered.SafeToContinue);
        AssertRestored(tree, kind);
        Assert.Equal("internal sealed class TriggerBefore { }", tree.ReadText("Trigger.cs"));
    }

    [Theory]
    [InlineData((int)AliSourceChangeKind.Add)]
    [InlineData((int)AliSourceChangeKind.Replace)]
    [InlineData((int)AliSourceChangeKind.Delete)]
    [InlineData((int)AliSourceChangeKind.Rename)]
    public async Task MutationWithoutAppliedIdentity_AllOperationKinds_RestartIsInDoubt(int kindValue)
    {
        var kind = (AliSourceChangeKind)kindValue;
        using var tree = new NamespaceTree();
        var store = tree.CreateStore();
        var changeSet = await store.CreateAsync(
            tree.Source,
            [ArrangeOperation(tree, kind)],
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
        var beforeRecovery = CaptureVisibleState(tree, kind);
        var recovered = await new AliSourceChangeSetReconciler(store, CreatePublisher(store))
            .ReconcileAsync(changeSet.Id, Cancellation);

        Assert.Equal(AliSourcePublicationState.InDoubt, recovered.State);
        Assert.False(recovered.SafeToContinue);
        Assert.Equal(beforeRecovery, CaptureVisibleState(tree, kind));
    }

    private static AliSourceChangeRequest ArrangeOperation(NamespaceTree tree, AliSourceChangeKind kind)
    {
        switch (kind)
        {
            case AliSourceChangeKind.Add:
                return AliSourceChangeRequest.AddText("Added.cs", "internal sealed class Added { }");
            case AliSourceChangeKind.Replace:
                tree.WriteText("Replace.cs", "internal sealed class ReplaceBefore { }");
                return AliSourceChangeRequest.ReplaceText(
                    "Replace.cs",
                    "internal sealed class ReplaceAfter { }");
            case AliSourceChangeKind.Delete:
                tree.WriteText("Delete.cs", "internal sealed class DeleteBefore { }");
                return AliSourceChangeRequest.Delete("Delete.cs");
            case AliSourceChangeKind.Rename:
                tree.WriteText("RenameBefore.cs", "internal sealed class RenameBefore { }");
                return AliSourceChangeRequest.Rename("RenameBefore.cs", "RenameAfter.cs");
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static void AssertRestored(NamespaceTree tree, AliSourceChangeKind kind)
    {
        switch (kind)
        {
            case AliSourceChangeKind.Add:
                Assert.False(File.Exists(tree.SourcePath("Added.cs")));
                break;
            case AliSourceChangeKind.Replace:
                Assert.Equal("internal sealed class ReplaceBefore { }", tree.ReadText("Replace.cs"));
                break;
            case AliSourceChangeKind.Delete:
                Assert.Equal("internal sealed class DeleteBefore { }", tree.ReadText("Delete.cs"));
                break;
            case AliSourceChangeKind.Rename:
                Assert.Equal("internal sealed class RenameBefore { }", tree.ReadText("RenameBefore.cs"));
                Assert.False(File.Exists(tree.SourcePath("RenameAfter.cs")));
                break;
        }
    }

    private static string CaptureVisibleState(NamespaceTree tree, AliSourceChangeKind kind) => kind switch
    {
        AliSourceChangeKind.Add => File.Exists(tree.SourcePath("Added.cs"))
            ? tree.ReadText("Added.cs")
            : "absent",
        AliSourceChangeKind.Replace => tree.ReadText("Replace.cs"),
        AliSourceChangeKind.Delete => File.Exists(tree.SourcePath("Delete.cs"))
            ? tree.ReadText("Delete.cs")
            : "absent",
        AliSourceChangeKind.Rename => string.Join(
            '|',
            File.Exists(tree.SourcePath("RenameBefore.cs")) ? "source" : "no-source",
            File.Exists(tree.SourcePath("RenameAfter.cs")) ? tree.ReadText("RenameAfter.cs") : "no-destination"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string? TryGetShortPath(string path)
    {
        var buffer = new StringBuilder(4096);
        var length = GetShortPathNameW(Path.GetFullPath(path), buffer, (uint)buffer.Capacity);
        return length == 0 || length >= buffer.Capacity ? null : buffer.ToString();
    }

    private static AliSourceChangeSetPublisher CreatePublisher(AliSourceChangeSetStore store) =>
        new(store, new AliSourceChangeSetValidator(store));

    private static AliSourcePublicationGrant Grant(AliSourceChangeSet changeSet) =>
        AliSourcePublicationGrant.Issue(changeSet, AuthorizationDigest);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetShortPathNameW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern uint GetShortPathNameW(
        string longPath,
        StringBuilder shortPath,
        uint bufferLength);

    private sealed class NamespaceTree : IDisposable
    {
        internal NamespaceTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "AliSourceNamespaceTests", Guid.NewGuid().ToString("N"));
            Source = Path.Combine(Root, "source");
            Store = Path.Combine(Root, "store");
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Store);
        }

        internal string Root { get; }
        internal string Source { get; }
        internal string Store { get; }

        internal AliSourceChangeSetStore CreateStore() => new(Store, "namespace-security-profile");

        internal string RootPath(string relativePath) =>
            Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        internal string SourcePath(string relativePath) =>
            Path.Combine(Source, relativePath.Replace('/', Path.DirectorySeparatorChar));

        internal string CreateDirectory(string relativePath)
        {
            var path = RootPath(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        internal void WriteText(string relativePath, string content)
        {
            var path = SourcePath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Encoding.UTF8);
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
