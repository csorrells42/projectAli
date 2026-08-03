using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class DurableFileBoundaryTests
{
    [Fact]
    public async Task PausedTurnCatalog_RejectsOversizedEntryThroughBoundedSingleHandleRead()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = Identity("oversized-catalog");
        var catalog = new DurablePausedTurnCatalog(directory.Path, "profile-a");
        await catalog.RecordAsync(identity, TestContext.Current.CancellationToken);
        var entryPath = Assert.Single(Directory.GetFiles(
            directory.Path,
            "*.paused",
            SearchOption.AllDirectories));
        await SetFileLengthAsync(
            entryPath,
            DurablePausedTurnCatalog.MaximumProtectedEntryBytes + 1L);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => catalog.FindAsync(
            identity.UserId,
            identity.ConversationId,
            TestContext.Current.CancellationToken));

        Assert.Contains("invalid size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PausedTurnCatalog_ExactRemovalNeverFollowsRedirectedEntry()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = Identity("redirected-catalog");
        var catalog = new DurablePausedTurnCatalog(directory.Path, "profile-a");
        await catalog.RecordAsync(identity, TestContext.Current.CancellationToken);
        var entryPath = Assert.Single(Directory.GetFiles(
            directory.Path,
            "*.paused",
            SearchOption.AllDirectories));
        var sentinelPath = Path.Combine(directory.Path, "catalog-sentinel.bin");
        var sentinel = new byte[] { 0x11, 0x22, 0x33 };
        await File.WriteAllBytesAsync(
            sentinelPath,
            sentinel,
            TestContext.Current.CancellationToken);
        File.Delete(entryPath);
        CreateFileSymbolicLinkOrSkip(entryPath, sentinelPath);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => catalog.RemoveExactAsync(
            identity,
            TestContext.Current.CancellationToken));

        Assert.Contains("not a regular local file", exception.Message, StringComparison.Ordinal);
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(
            sentinelPath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DirectoryBoundary_RejectsParentReparseBeforeCreatingAChild()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var targetPath = Path.Combine(directory.Path, "real-parent");
        var linkPath = Path.Combine(directory.Path, "redirected-parent");
        Directory.CreateDirectory(targetPath);
        CreateDirectorySymbolicLinkOrSkip(linkPath, targetPath);

        var exception = Assert.Throws<InvalidDataException>(() =>
            WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
                Path.Combine(linkPath, "must-not-be-created"),
                "not a regular directory"));

        Assert.Equal("not a regular directory", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(targetPath, "must-not-be-created")));
    }

    [Fact]
    public async Task FileBoundary_HandleDeletionRejectsRedirectedTarget()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var targetPath = Path.Combine(directory.Path, "delete-target.bin");
        var linkPath = Path.Combine(directory.Path, "delete-link.bin");
        var target = new byte[] { 0xde, 0xad, 0xbe, 0xef };
        await File.WriteAllBytesAsync(
            targetPath,
            target,
            TestContext.Current.CancellationToken);
        CreateFileSymbolicLinkOrSkip(linkPath, targetPath);

        Assert.Throws<InvalidDataException>(() =>
            WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                linkPath,
                "not a regular file"));

        Assert.Equal(target, await File.ReadAllBytesAsync(
            targetPath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FileBoundary_FinalReplaceRejectsRedirectedTarget()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "replace-source.bin");
        var targetPath = Path.Combine(directory.Path, "replace-target.bin");
        var linkPath = Path.Combine(directory.Path, "replace-link.bin");
        var source = new byte[] { 0x01, 0x02, 0x03 };
        var target = new byte[] { 0x04, 0x05, 0x06 };
        await File.WriteAllBytesAsync(
            sourcePath,
            source,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            targetPath,
            target,
            TestContext.Current.CancellationToken);
        CreateFileSymbolicLinkOrSkip(linkPath, targetPath);

        Assert.Throws<InvalidDataException>(() =>
            WindowsOrchestrationFileBoundary.MoveRegularFile(
                sourcePath,
                linkPath,
                replaceExisting: true,
                "not a regular file"));

        Assert.Equal(source, await File.ReadAllBytesAsync(
            sourcePath,
            TestContext.Current.CancellationToken));
        Assert.Equal(target, await File.ReadAllBytesAsync(
            targetPath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FileBoundary_MoveSupportsLongValidatedDestinationPath()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var regularDirectory = Path.Combine(directory.Path, new string('d', 96));
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            regularDirectory,
            "not a regular directory");
        var sourcePath = Path.Combine(regularDirectory, "source.tmp");
        var destinationPath = Path.Combine(
            regularDirectory,
            new string('f', 112) + ".protected");
        var content = new byte[] { 0x10, 0x20, 0x30 };
        Assert.True(destinationPath.Length >= 260);
        await File.WriteAllBytesAsync(
            sourcePath,
            content,
            TestContext.Current.CancellationToken);

        WindowsOrchestrationFileBoundary.MoveRegularFile(
            sourcePath,
            destinationPath,
            replaceExisting: false,
            "not a regular file");

        Assert.False(File.Exists(sourcePath));
        Assert.Equal(content, await File.ReadAllBytesAsync(
            destinationPath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FileBoundary_MissingTemporaryCleanupIsANoOp()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();

        var removed = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
            Path.Combine(directory.Path, "missing.tmp"),
            "not a regular file");

        Assert.False(removed);
    }

    [Fact]
    public async Task EvidenceJournal_RejectsOversizedHeadThroughBoundedSingleHandleRead()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = Identity("oversized-head");
        await AppendEvidenceAsync(directory.Path, identity);
        var headPath = Assert.Single(Directory.GetFiles(
            directory.Path,
            "evidence.head.json",
            SearchOption.AllDirectories));
        await SetFileLengthAsync(headPath, EvidenceJournal.MaximumHeadBytes + 1L);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => new EvidenceLedger(
            directory.Path,
            "profile-a").ReplayAsync(identity, TestContext.Current.CancellationToken));

        Assert.Contains("invalid size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvidenceJournal_RejectsRedirectedJournalWithoutTouchingTarget()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = Identity("redirected-journal");
        await AppendEvidenceAsync(directory.Path, identity);
        var journalPath = Assert.Single(Directory.GetFiles(
            directory.Path,
            "evidence.journal.jsonl",
            SearchOption.AllDirectories));
        var sentinelPath = Path.Combine(directory.Path, "journal-sentinel.bin");
        var sentinel = new byte[] { 0x44, 0x55, 0x66 };
        await File.WriteAllBytesAsync(
            sentinelPath,
            sentinel,
            TestContext.Current.CancellationToken);
        File.Delete(journalPath);
        CreateFileSymbolicLinkOrSkip(journalPath, sentinelPath);

        var exception = await AssertFileBoundaryRejectedAsync(() => new EvidenceLedger(
            directory.Path,
            "profile-a").ReplayAsync(identity, TestContext.Current.CancellationToken));

        Assert.Contains("not a regular local file", exception.Message, StringComparison.Ordinal);
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(
            sentinelPath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EvidenceJournal_RejectsRedirectedWriterLeaseWithoutTouchingTarget()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = Identity("redirected-lease");
        await AppendEvidenceAsync(directory.Path, identity);
        var leasePath = Assert.Single(Directory.GetFiles(
            directory.Path,
            ".writer.lock",
            SearchOption.AllDirectories));
        var sentinelPath = Path.Combine(directory.Path, "lease-sentinel.bin");
        var sentinel = new byte[] { 0x77, 0x88, 0x99 };
        await File.WriteAllBytesAsync(
            sentinelPath,
            sentinel,
            TestContext.Current.CancellationToken);
        File.Delete(leasePath);
        CreateFileSymbolicLinkOrSkip(leasePath, sentinelPath);

        var exception = await AssertFileBoundaryRejectedAsync(() => new EvidenceLedger(
            directory.Path,
            "profile-a").ReplayAsync(identity, TestContext.Current.CancellationToken));

        Assert.Contains("not a regular local file", exception.Message, StringComparison.Ordinal);
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(
            sentinelPath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProtectedEvidencePayload_RejectsFinalComponentRedirectWithoutReadingTarget()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = Identity("redirected-payload");
        await AppendEvidenceAsync(directory.Path, identity);
        var payloadPath = Assert.Single(Directory.GetFiles(
            directory.Path,
            "*.evidence",
            SearchOption.AllDirectories));
        var sentinelPath = Path.Combine(directory.Path, "payload-sentinel.bin");
        var sentinel = new byte[] { 0xaa, 0xbb, 0xcc };
        await File.WriteAllBytesAsync(
            sentinelPath,
            sentinel,
            TestContext.Current.CancellationToken);
        File.Delete(payloadPath);
        CreateFileSymbolicLinkOrSkip(payloadPath, sentinelPath);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => new EvidenceLedger(
            directory.Path,
            "profile-a").ReplayAsync(identity, TestContext.Current.CancellationToken));

        Assert.Contains("not a regular local file", exception.Message, StringComparison.Ordinal);
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(
            sentinelPath,
            TestContext.Current.CancellationToken));
    }

    private static TurnIdentity Identity(string suffix) =>
        new("user-" + suffix, "conversation-" + suffix, "message-" + suffix);

    private static async Task AppendEvidenceAsync(string root, TurnIdentity identity)
    {
        await new EvidenceLedger(root, "profile-a").AppendAsync(
            identity,
            OutcomeAndEvidenceTests.CreateDraft(
                "call-1",
                "tool-1",
                OutcomeAndEvidenceTests.Json("{}"),
                OutcomeAndEvidenceTests.Json("{\"success\":true}")),
            TestContext.Current.CancellationToken);
    }

    private static async Task SetFileLengthAsync(string path, long length)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        stream.SetLength(length);
        await stream.FlushAsync(TestContext.Current.CancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<Exception> AssertFileBoundaryRejectedAsync(Func<Task> action)
    {
        var exception = await Record.ExceptionAsync(action);
        Assert.NotNull(exception);
        Assert.True(
            exception is InvalidDataException or IOException,
            $"Expected a fail-closed file-boundary rejection, but received {exception.GetType().Name}.");
        return exception;
    }

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
            Assert.Skip("Directory symbolic-link creation is unavailable: " + ex.Message);
        }
    }
}
