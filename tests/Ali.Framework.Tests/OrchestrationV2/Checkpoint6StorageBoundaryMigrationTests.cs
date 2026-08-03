using System.Text;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class Checkpoint6StorageBoundaryMigrationTests
{
    [Fact]
    public async Task EvidenceKey_WriteThroughPublicationReopensTheSameKeyWithoutTemporaryFiles()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var protector = new WindowsCurrentUserEvidenceProtector(directory.Path, "profile-a");
        string firstDigest;
        using (var first = await protector.OpenSessionAsync(TestContext.Current.CancellationToken))
        {
            firstDigest = first.HmacHex(EvidenceKeyPurpose.Identifier, [0x01, 0x02, 0x03]);
        }

        using var reopened = await protector.OpenSessionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            firstDigest,
            reopened.HmacHex(EvidenceKeyPurpose.Identifier, [0x01, 0x02, 0x03]));
        Assert.True(File.Exists(Path.Combine(directory.Path, "evidence.key.protected")));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ProtectedTurnInput_WriteThroughPublicationReopensExactTextWithoutTemporaryFiles()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "input-publication");
        var protector = new WindowsCurrentUserEvidenceProtector(directory.Path, "profile-a");
        using var keys = await protector.OpenSessionAsync(TestContext.Current.CancellationToken);
        var store = new ProtectedTurnInputStore(directory.Path);
        var reference = await store.StoreAsync(
            identity,
            TurnInputPurposes.OriginalRequest,
            "request-1",
            "exact durable request",
            keys,
            TestContext.Current.CancellationToken);

        var reopened = await store.OpenAsync(
            identity,
            TurnInputPurposes.OriginalRequest,
            reference,
            keys,
            TestContext.Current.CancellationToken);

        Assert.Equal("exact durable request", reopened);
        Assert.Single(Directory.GetFiles(
            directory.Path,
            "*.turn-input",
            SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TurnJournal_WriteThroughHeadPublicationReplaysAfterWriterRestart()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "head-publication");
        using (var writer = new TurnTransitionWriter(directory.Path, "profile-a"))
        {
            var started = await writer.StartAsync(
                identity,
                "exact durable request",
                Bindings(),
                "turn-start",
                TestContext.Current.CancellationToken);
            Assert.Equal(TurnTransitionWriteStatus.Committed, started.Status);
        }

        using var reopened = new TurnTransitionWriter(directory.Path, "profile-a");
        var replay = await reopened.ReplayAsync(identity, TestContext.Current.CancellationToken);

        Assert.NotNull(replay.State);
        Assert.Equal(1, replay.State.Revision);
        Assert.False(replay.RecoveredUncommittedTail);
        Assert.Single(Directory.GetFiles(
            directory.Path,
            "turn.head.json",
            SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(".evidence-key.writer.lock")]
    [InlineData("evidence.key.protected")]
    public async Task EvidenceKeyMutableEndpoints_RejectRedirectedFinalComponent(
        string endpointName)
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var sentinelPath = Path.Combine(directory.Path, "key-endpoint-sentinel.bin");
        var sentinel = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        await File.WriteAllBytesAsync(
            sentinelPath,
            sentinel,
            TestContext.Current.CancellationToken);
        CreateFileSymbolicLinkOrSkip(
            Path.Combine(directory.Path, endpointName),
            sentinelPath);

        var protector = new WindowsCurrentUserEvidenceProtector(directory.Path, "profile-a");
        var exception = await AssertBoundaryRejectedAsync(() => protector.OpenSessionAsync(
            TestContext.Current.CancellationToken));

        Assert.Contains("not a regular local file", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            sentinel,
            await File.ReadAllBytesAsync(
                sentinelPath,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProtectedTurnInputStore_RejectsRedirectedInputDirectoryBeforeWriting()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "redirected-input-directory");
        var protector = new WindowsCurrentUserEvidenceProtector(directory.Path, "profile-a");
        using var keys = await protector.OpenSessionAsync(TestContext.Current.CancellationToken);
        var turnDirectory = Path.Combine(directory.Path, "turns", identity.StorageKey);
        Directory.CreateDirectory(turnDirectory);
        var redirectedTarget = Path.Combine(directory.Path, "redirected-input-target");
        Directory.CreateDirectory(redirectedTarget);
        CreateDirectorySymbolicLinkOrSkip(
            Path.Combine(turnDirectory, "inputs"),
            redirectedTarget);
        var store = new ProtectedTurnInputStore(directory.Path);

        var exception = await AssertBoundaryRejectedAsync(() => store.StoreAsync(
            identity,
            TurnInputPurposes.OriginalRequest,
            "request-1",
            "exact request",
            keys,
            TestContext.Current.CancellationToken));

        Assert.Contains("not a regular local directory", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(redirectedTarget));
    }

    [Theory]
    [InlineData(".writer.lock")]
    [InlineData("turn.journal.jsonl")]
    [InlineData("turn.head.json")]
    public async Task TurnJournalMutableEndpoints_RejectRedirectedFinalComponent(
        string endpointName)
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "redirected-" + endpointName);
        var protector = new WindowsCurrentUserEvidenceProtector(directory.Path, "profile-a");
        using var keys = await protector.OpenSessionAsync(TestContext.Current.CancellationToken);
        var turnDirectory = Path.Combine(directory.Path, "turns", identity.StorageKey);
        Directory.CreateDirectory(turnDirectory);
        var sentinelPath = Path.Combine(directory.Path, "turn-endpoint-sentinel.bin");
        var sentinel = new byte[] { 0x50, 0x60, 0x70, 0x80 };
        await File.WriteAllBytesAsync(
            sentinelPath,
            sentinel,
            TestContext.Current.CancellationToken);
        CreateFileSymbolicLinkOrSkip(
            Path.Combine(turnDirectory, endpointName),
            sentinelPath);
        var journal = new TurnTransitionJournal(
            turnDirectory,
            identity.StorageKey,
            _ => Task.FromResult(keys));

        var exception = await AssertBoundaryRejectedAsync(() => journal.ReplayAsync(
            identity,
            TestContext.Current.CancellationToken));

        Assert.Contains("not a regular local file", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            sentinel,
            await File.ReadAllBytesAsync(
                sentinelPath,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TurnJournal_RejectsRedirectedTurnDirectoryBeforeCreatingMutableFiles()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "redirected-turn-directory");
        var protector = new WindowsCurrentUserEvidenceProtector(directory.Path, "profile-a");
        using var keys = await protector.OpenSessionAsync(TestContext.Current.CancellationToken);
        var turnsDirectory = Path.Combine(directory.Path, "turns");
        Directory.CreateDirectory(turnsDirectory);
        var redirectedTarget = Path.Combine(directory.Path, "redirected-turn-target");
        Directory.CreateDirectory(redirectedTarget);
        var turnDirectory = Path.Combine(turnsDirectory, identity.StorageKey);
        CreateDirectorySymbolicLinkOrSkip(turnDirectory, redirectedTarget);
        var journal = new TurnTransitionJournal(
            turnDirectory,
            identity.StorageKey,
            _ => Task.FromResult(keys));

        var exception = await AssertBoundaryRejectedAsync(() => journal.ReplayAsync(
            identity,
            TestContext.Current.CancellationToken));

        Assert.Contains("regular local directory", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(redirectedTarget));
    }

    private static async Task<Exception> AssertBoundaryRejectedAsync(Func<Task> action)
    {
        var exception = await Record.ExceptionAsync(action);
        Assert.NotNull(exception);
        Assert.True(
            exception is InvalidDataException or IOException,
            $"Expected a fail-closed file-boundary rejection, but received {exception.GetType().Name}.");
        return exception;
    }

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
}
