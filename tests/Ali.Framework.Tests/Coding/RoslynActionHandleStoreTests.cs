using Ali.Modules.Coding.RoslynActions;

namespace Ali.Framework.Tests.Coding;

public sealed class RoslynActionHandleStoreTests
{
    [Fact]
    public async Task RoundTrip_ProtectsSensitiveHandleFieldsAtRest()
    {
        using var directory = new TemporaryDirectory();
        var store = new AliRoslynActionHandleStore(directory.Path, "profile-a");
        var handle = CreateHandle();

        await store.CreateAsync(handle, Digest('D'), TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(handle.Id, TestContext.Current.CancellationToken);
        var bytes = await File.ReadAllBytesAsync(
            System.IO.Path.Combine(directory.Path, handle.Id + ".handle.protected"),
            TestContext.Current.CancellationToken);
        var raw = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Equal(handle.Id, loaded.Id);
        Assert.Equal(handle.ActionIdentitySha256, loaded.ActionIdentitySha256);
        Assert.Equal(handle.DiagnosticIds, loaded.DiagnosticIds);
        Assert.Equal(handle.ChangeSetManifestDigest, loaded.ChangeSetManifestDigest);
        Assert.Equal(handle.State, loaded.State);
        Assert.DoesNotContain(handle.Title, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(handle.TargetPath, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(handle.RequestedValue, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TamperedHandle_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var store = new AliRoslynActionHandleStore(directory.Path, "profile-a");
        var handle = CreateHandle();
        await store.CreateAsync(handle, Digest('D'), TestContext.Current.CancellationToken);
        var path = System.IO.Path.Combine(directory.Path, handle.Id + ".handle.protected");
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        bytes[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.LoadAsync(handle.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifiedHandle_IsConsumeOnceUnderConcurrency()
    {
        using var directory = new TemporaryDirectory();
        var store = new AliRoslynActionHandleStore(directory.Path, "profile-a");
        var handle = CreateHandle();
        await store.CreateAsync(handle, Digest('D'), TestContext.Current.CancellationToken);
        var verified = await store.RecordVerificationAsync(
            handle.Id,
            handle.Revision,
            CreateReceipt(handle),
            TestContext.Current.CancellationToken);

        var attempts = await Task.WhenAll(
            TryBeginAsync(store, verified),
            TryBeginAsync(store, verified));

        Assert.Single(attempts, result => result);
        Assert.Single(attempts, result => !result);
        var loaded = await store.LoadAsync(handle.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AliRoslynActionHandleState.Applying, loaded.State);
        Assert.Equal(3, loaded.Revision);
    }

    [Fact]
    public async Task ExpiredVerification_CannotEnterApplying()
    {
        using var directory = new TemporaryDirectory();
        var store = new AliRoslynActionHandleStore(directory.Path, "profile-a");
        var now = DateTimeOffset.UtcNow;
        var handle = CreateHandle(now.AddHours(-2), now.AddHours(1));
        await store.CreateAsync(handle, Digest('D'), TestContext.Current.CancellationToken);
        var receipt = CreateReceipt(handle) with
        {
            CreatedAtUtc = now.AddHours(-2),
            ExpiresAtUtc = now.AddMinutes(-1)
        };
        var verified = await store.RecordVerificationAsync(
            handle.Id,
            1,
            receipt,
            TestContext.Current.CancellationToken);

        var expired = await store.BeginApplyAsync(
            handle.Id,
            verified.Revision,
            TestContext.Current.CancellationToken);

        Assert.Equal(AliRoslynActionHandleState.Expired, expired.State);
        Assert.Equal("verification-expired", expired.FailureCode);
    }

    [Fact]
    public async Task Retention_AllowsMoreThan1024SequentialPreviewsAndKeepsRecoveryScansBounded()
    {
        using var directory = new TemporaryDirectory();
        var store = new AliRoslynActionHandleStore(directory.Path, "profile-a");
        var now = DateTimeOffset.UtcNow;
        AliRoslynActionHandle? latest = null;

        for (var index = 1;
             index <= AliRoslynActionHandleStore.MaximumRetainedHandleFiles + 5;
             index++)
        {
            latest = CreateHandle(
                now.AddHours(-3),
                now.AddHours(-2),
                seed: index);
            await store.CreateAsync(latest, Digest('D'), TestContext.Current.CancellationToken);
            await MarkExpiredSafeToForgetAsync(store, latest);
        }

        var retainedLatest = Assert.IsType<AliRoslynActionHandle>(latest);
        Assert.Equal(
            AliRoslynActionHandleStore.MaximumRetainedHandleFiles,
            Directory.EnumerateFiles(directory.Path, "*.handle.protected").Count());
        Assert.Equal(
            retainedLatest.Id,
            (await store.FindByChangeSetIdAsync(
                retainedLatest.ChangeSetId,
                TestContext.Current.CancellationToken))?.Id);
        Assert.Equal(64, store.CaptureStoreRevisionDigest().Length);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            store.LoadAsync(
                CreateHandle(now.AddHours(-3), now.AddHours(-2), seed: 1).Id,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Retention_NeverPrunesExpiredNonterminalHandles()
    {
        using var directory = new TemporaryDirectory();
        var store = new AliRoslynActionHandleStore(directory.Path, "profile-a", maximumRetainedHandleFiles: 4);
        var now = DateTimeOffset.UtcNow;
        var previewed = CreateHandle(now.AddHours(-3), now.AddHours(-2), seed: 1);
        var verifiedSource = CreateHandle(seed: 2);
        var applyingSource = CreateHandle(seed: 3);
        var terminal = CreateHandle(now.AddHours(-3), now.AddHours(-2), seed: 4);
        var incoming = CreateHandle(seed: 5);

        await store.CreateAsync(previewed, Digest('D'), TestContext.Current.CancellationToken);
        await store.CreateAsync(verifiedSource, Digest('D'), TestContext.Current.CancellationToken);
        var verified = await store.RecordVerificationAsync(
            verifiedSource.Id,
            verifiedSource.Revision,
            CreateReceipt(verifiedSource),
            TestContext.Current.CancellationToken);
        await store.CreateAsync(applyingSource, Digest('D'), TestContext.Current.CancellationToken);
        var applyingVerification = await store.RecordVerificationAsync(
            applyingSource.Id,
            applyingSource.Revision,
            CreateReceipt(applyingSource),
            TestContext.Current.CancellationToken);
        var applying = await store.BeginApplyAsync(
            applyingVerification.Id,
            applyingVerification.Revision,
            TestContext.Current.CancellationToken);
        await store.CreateAsync(terminal, Digest('D'), TestContext.Current.CancellationToken);
        await MarkExpiredSafeToForgetAsync(store, terminal);
        await store.CreateAsync(incoming, Digest('D'), TestContext.Current.CancellationToken);

        Assert.Equal(AliRoslynActionHandleState.Previewed, (
            await store.LoadAsync(previewed.Id, TestContext.Current.CancellationToken)).State);
        Assert.Equal(AliRoslynActionHandleState.Verified, (
            await store.LoadAsync(verified.Id, TestContext.Current.CancellationToken)).State);
        Assert.Equal(AliRoslynActionHandleState.Applying, (
            await store.LoadAsync(applying.Id, TestContext.Current.CancellationToken)).State);
        Assert.Equal(
            incoming.Id,
            (await store.LoadAsync(incoming.Id, TestContext.Current.CancellationToken)).Id);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            store.LoadAsync(terminal.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Retention_CorruptAuthenticatedCandidateFailsClosedBeforeAnyDeletion()
    {
        using var directory = new TemporaryDirectory();
        var store = new AliRoslynActionHandleStore(directory.Path, "profile-a", maximumRetainedHandleFiles: 2);
        var now = DateTimeOffset.UtcNow;
        var corrupt = CreateHandle(now.AddHours(-3), now.AddHours(-2), seed: 1);
        var intact = CreateHandle(now.AddHours(-3), now.AddHours(-2), seed: 2);
        var incoming = CreateHandle(seed: 3);
        await store.CreateAsync(corrupt, Digest('D'), TestContext.Current.CancellationToken);
        await store.CreateAsync(intact, Digest('D'), TestContext.Current.CancellationToken);
        await MarkExpiredSafeToForgetAsync(store, corrupt);
        await MarkExpiredSafeToForgetAsync(store, intact);
        var corruptPath = System.IO.Path.Combine(directory.Path, corrupt.Id + ".handle.protected");
        var corruptBytes = await File.ReadAllBytesAsync(
            corruptPath,
            TestContext.Current.CancellationToken);
        corruptBytes[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(
            corruptPath,
            corruptBytes,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.CreateAsync(incoming, Digest('D'), TestContext.Current.CancellationToken));

        Assert.True(File.Exists(corruptPath));
        Assert.Equal(
            intact.Id,
            (await store.LoadAsync(intact.Id, TestContext.Current.CancellationToken)).Id);
        Assert.False(File.Exists(System.IO.Path.Combine(
            directory.Path,
            incoming.Id + ".handle.protected")));
    }

    [Fact]
    public async Task Retention_PreservesExpiredRecoveryRelevantFailure()
    {
        using var directory = new TemporaryDirectory();
        var store = new AliRoslynActionHandleStore(directory.Path, "profile-a", maximumRetainedHandleFiles: 1);
        var now = DateTimeOffset.UtcNow;
        var recoveryRelevant = CreateHandle(now.AddHours(-3), now.AddHours(-2), seed: 1);
        var incoming = CreateHandle(seed: 2);
        await store.CreateAsync(recoveryRelevant, Digest('D'), TestContext.Current.CancellationToken);
        var verified = await store.RecordVerificationAsync(
            recoveryRelevant.Id,
            recoveryRelevant.Revision,
            CreateReceipt(recoveryRelevant),
            TestContext.Current.CancellationToken);
        var failed = await store.MarkFailedAsync(
            verified.Id,
            verified.Revision,
            "canonical-postverify-failed",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.CreateAsync(incoming, Digest('D'), TestContext.Current.CancellationToken));

        var retained = await store.LoadAsync(failed.Id, TestContext.Current.CancellationToken);
        Assert.Equal(failed.Id, retained.Id);
        Assert.Equal(AliRoslynActionHandleState.Failed, retained.State);
        Assert.Equal("canonical-postverify-failed", retained.FailureCode);
    }

    [Fact]
    public async Task VerificationReceipt_CannotReplaceAuthenticatedPreviewedStagedFingerprint()
    {
        using var directory = new TemporaryDirectory();
        var store = new AliRoslynActionHandleStore(directory.Path, "profile-a");
        var handle = CreateHandle();
        await store.CreateAsync(handle, Digest('D'), TestContext.Current.CancellationToken);
        var mismatched = CreateReceipt(handle) with
        {
            StagedSolutionFingerprint = Digest('9')
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.RecordVerificationAsync(
                handle.Id,
                handle.Revision,
                mismatched,
                TestContext.Current.CancellationToken));

        var retained = await store.LoadAsync(handle.Id, TestContext.Current.CancellationToken);
        Assert.Equal(handle.Id, retained.Id);
        Assert.Equal(AliRoslynActionHandleState.Previewed, retained.State);
        Assert.Equal(1, retained.Revision);
        Assert.Null(retained.Verification);
    }

    private static async Task<bool> TryBeginAsync(
        AliRoslynActionHandleStore store,
        AliRoslynActionHandle handle)
    {
        try
        {
            var result = await store.BeginApplyAsync(
                handle.Id,
                handle.Revision,
                TestContext.Current.CancellationToken);
            return result.State == AliRoslynActionHandleState.Applying;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static Task<AliRoslynActionHandle> MarkExpiredSafeToForgetAsync(
        AliRoslynActionHandleStore store,
        AliRoslynActionHandle handle) =>
        store.TransitionAsync(
            handle.Id,
            handle.Revision,
            current => current with
            {
                State = AliRoslynActionHandleState.Expired,
                Revision = checked(current.Revision + 1),
                FailureCode = "publication-not-started"
            },
            TestContext.Current.CancellationToken);

    private static AliRoslynActionHandle CreateHandle(
        DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null,
        int seed = 0)
    {
        var created = createdAt ?? DateTimeOffset.UtcNow;
        return new AliRoslynActionHandle(
            seed == 0 ? "0123456789abcdef0123456789abcdef" : seed.ToString("x32"),
            Digest('A'),
            "ali.roslyn.semantic-rename",
            "1.0.0",
            "semantic-rename",
            "Semantic rename",
            ["CS0001"],
            @"C:\approved\Project.csproj",
            @"C:\approved",
            "project-id",
            "document-id",
            @"C:\approved\Program.cs",
            10,
            4,
            "RenamedValue",
            Digest('B'),
            seed == 0
                ? "abcdefabcdefabcdefabcdefabcdefab"
                : checked(seed + 100_000).ToString("x32"),
            Digest('C'),
            created,
            expiresAt ?? created.AddHours(1),
            AliRoslynActionHandleState.Previewed,
            1);
    }

    private static AliRoslynPreverificationReceipt CreateReceipt(AliRoslynActionHandle handle) =>
        new(
            "fedcbafedcbafedcbafedcbafedcbafe",
            handle.ChangeSetId,
            handle.ChangeSetManifestDigest,
            handle.CanonicalSolutionFingerprint,
            Digest('D'),
            Digest('E'),
            Digest('F'),
            "0123456789abcdef0123456789abcdef",
            Digest('7'),
            Digest('8'),
            Digest('9'),
            Digest('A'),
            RoslynSucceeded: true,
            BuildSucceeded: true,
            TestsRun: 0,
            TestsSucceeded: true,
            VerificationDigest: Digest('1'),
            CreatedAtUtc: handle.CreatedAtUtc,
            ExpiresAtUtc: handle.ExpiresAtUtc);

    private static string Digest(char value) => new(value, 64);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "AliRoslynHandleTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
