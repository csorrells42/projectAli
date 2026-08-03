using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class ProtectedTurnInputFileSafetyTests
{
    [Fact]
    public async Task EvidenceKeyFile_RejectsOversizedDpapiBlobBeforeReadAllocation()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var keyPath = Path.Combine(directory.Path, "evidence.key.protected");
        await SetFileLengthAsync(
            keyPath,
            WindowsCurrentUserEvidenceProtector.MaximumProtectedKeyBytes + 1L);

        var protector = new WindowsCurrentUserEvidenceProtector(directory.Path, "profile-a");
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            protector.OpenSessionAsync(TestContext.Current.CancellationToken));

        Assert.Contains("invalid file length", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundedReader_RejectsOversizedFileBeforeValidatedLengthObserver()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var path = Path.Combine(directory.Path, "oversized.bin");
        await SetFileLengthAsync(path, 33);
        var observerCalled = false;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WindowsBoundedFileReader.TryReadExactlyAsync(
                path,
                minimumLength: 1,
                maximumLength: 32,
                "not regular",
                "invalid length",
                "changed",
                TestContext.Current.CancellationToken,
                _ =>
                {
                    observerCalled = true;
                    return ValueTask.CompletedTask;
                }));

        Assert.False(observerCalled);
    }

    [Fact]
    public async Task BoundedReader_ReadsRegularFileExactly()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var path = Path.Combine(directory.Path, "regular.bin");
        var expected = Enumerable.Range(0, 64).Select(index => (byte)index).ToArray();
        await File.WriteAllBytesAsync(
            path,
            expected,
            TestContext.Current.CancellationToken);

        var actual = await WindowsBoundedFileReader.TryReadExactlyAsync(
            path,
            minimumLength: 1,
            maximumLength: 128,
            "not regular",
            "invalid length",
            "changed",
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundedReader_RejectsGrowthOrTruncationAfterLengthValidation(bool grow)
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var path = Path.Combine(directory.Path, "changing.bin");
        await File.WriteAllBytesAsync(
            path,
            Enumerable.Range(0, 64).Select(index => (byte)index).ToArray(),
            TestContext.Current.CancellationToken);
        await using var writer = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            WindowsBoundedFileReader.TryReadExactlyAsync(
                path,
                minimumLength: 1,
                maximumLength: 128,
                "not regular",
                "invalid length",
                "changed",
                TestContext.Current.CancellationToken,
                _ =>
                {
                    writer.SetLength(grow ? 65 : 32);
                    writer.Flush(flushToDisk: true);
                    return ValueTask.CompletedTask;
                }));

        Assert.Equal("changed", exception.Message);
    }

    [Fact]
    public async Task BoundedReader_RejectsDirectoryTarget()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WindowsBoundedFileReader.TryReadExactlyAsync(
                directory.Path,
                minimumLength: 1,
                maximumLength: 128,
                "not regular",
                "invalid length",
                "changed",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BoundedReader_RejectsFinalComponentSymbolicLinkWhenSupported()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var targetPath = Path.Combine(directory.Path, "target.bin");
        var linkPath = Path.Combine(directory.Path, "link.bin");
        await File.WriteAllBytesAsync(
            targetPath,
            [0x41],
            TestContext.Current.CancellationToken);
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

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            WindowsBoundedFileReader.TryReadExactlyAsync(
                linkPath,
                minimumLength: 1,
                maximumLength: 128,
                "not regular",
                "invalid length",
                "changed",
                TestContext.Current.CancellationToken));

        Assert.Equal("not regular", exception.Message);
    }

    [Fact]
    public async Task ProtectedTurnInputStore_RejectsOversizedEnvelopeThroughBoundedReader()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var protector = new WindowsCurrentUserEvidenceProtector(directory.Path, "profile-a");
        using var keys = await protector.OpenSessionAsync(TestContext.Current.CancellationToken);
        var store = new ProtectedTurnInputStore(directory.Path);
        var reference = await store.StoreAsync(
            identity,
            TurnInputPurposes.OriginalRequest,
            "request-1",
            "exact request",
            keys,
            TestContext.Current.CancellationToken);
        var payloadPath = Assert.Single(Directory.GetFiles(
            directory.Path,
            "*.turn-input",
            SearchOption.AllDirectories));
        await SetFileLengthAsync(
            payloadPath,
            ProtectedTurnInputStore.MaximumPlaintextBytes + 41L);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => store.OpenAsync(
            identity,
            TurnInputPurposes.OriginalRequest,
            reference,
            keys,
            TestContext.Current.CancellationToken));

        Assert.Contains("invalid length", exception.Message, StringComparison.Ordinal);
    }

    private static async Task SetFileLengthAsync(string path, long length)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        stream.SetLength(length);
        await stream.FlushAsync(TestContext.Current.CancellationToken);
        stream.Flush(flushToDisk: true);
    }
}
