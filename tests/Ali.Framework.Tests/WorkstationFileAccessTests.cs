using Ali.Modules.Coordinator;
using Ali.Modules.WorkstationFiles;
using Ali.Modules.Permissions;
using Microsoft.Agents.AI;
using System.Reflection;

namespace Ali.Framework.Tests;

public sealed class WorkstationFileAccessTests
{
    [Fact]
    public void CapabilityCatalog_MatchesEveryFrameworkFileToolName()
    {
        var expected = new[]
        {
            FileAccessProvider.WriteToolName,
            FileAccessProvider.ReadFileToolName,
            FileAccessProvider.DeleteFileToolName,
            FileAccessProvider.LsToolName,
            FileAccessProvider.GrepToolName,
            FileAccessProvider.ReplaceToolName,
            FileAccessProvider.ReplaceLinesToolName
        };
        var catalogNames = AliCapabilityCatalog.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected.Length, expected.Distinct(StringComparer.Ordinal).Count());
        Assert.All(expected, name => Assert.Contains(name, catalogNames));
        Assert.Equal(FileAccessProvider.WriteToolName, AliCapabilityCatalog.FileWriteName);
        Assert.Equal(FileAccessProvider.ReadFileToolName, AliCapabilityCatalog.FileReadName);
        Assert.Equal(FileAccessProvider.DeleteFileToolName, AliCapabilityCatalog.FileDeleteName);
        Assert.Equal(FileAccessProvider.LsToolName, AliCapabilityCatalog.FileListName);
        Assert.Equal(FileAccessProvider.GrepToolName, AliCapabilityCatalog.FileSearchName);
        Assert.Equal(FileAccessProvider.ReplaceToolName, AliCapabilityCatalog.FileReplaceName);
        Assert.Equal(FileAccessProvider.ReplaceLinesToolName, AliCapabilityCatalog.FileReplaceLinesName);
    }

    [Fact]
    public async Task FrameworkProvider_CanCreateReadSearchEditAndRecoverablyDelete()
    {
        await WithAccessAsync(async (root, access, permissions) =>
        {
            using var provider = new FileAccessProvider(
                access.Store,
                new FileAccessProviderOptions
                {
                    DisableReadOnlyToolApproval = true,
                    DisableWriteToolApproval = true
                });

            _ = await InvokeProviderAsync<string>(
                provider,
                "WriteAsync",
                "Workspace/job.txt",
                "alpha\nbeta\n",
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                "alpha\nbeta\n",
                await InvokeProviderAsync<string>(
                    provider,
                    "ReadAsync",
                    "Workspace/job.txt",
                    TestContext.Current.CancellationToken));

            var roots = await InvokeProviderAsync<IReadOnlyList<FileStoreEntry>>(
                provider,
                "LsAsync",
                string.Empty,
                null,
                TestContext.Current.CancellationToken);
            Assert.Contains(roots, entry => entry.Name == "Workspace" && entry.Type == FileStoreEntry.Directory);
            Assert.Contains(roots, entry => entry.Name == "Exports" && entry.Type == FileStoreEntry.Directory);

            var listing = await InvokeProviderAsync<IReadOnlyList<FileStoreEntry>>(
                provider,
                "LsAsync",
                "Workspace",
                "*.txt",
                TestContext.Current.CancellationToken);
            Assert.Contains(listing, entry => entry.Name == "job.txt" && entry.Type == FileStoreEntry.File);

            var matches = await InvokeProviderAsync<IReadOnlyList<FileSearchResult>>(
                provider,
                "GrepAsync",
                "beta",
                "*.txt",
                "Workspace",
                TestContext.Current.CancellationToken);
            Assert.Contains(matches, match => match.FileName == "Workspace/job.txt");

            _ = await InvokeProviderAsync<string>(
                provider,
                "ReplaceAsync",
                "Workspace/job.txt",
                "beta",
                "gamma",
                false,
                TestContext.Current.CancellationToken);
            Assert.Contains(
                "gamma",
                await InvokeProviderAsync<string>(
                    provider,
                    "ReadAsync",
                    "Workspace/job.txt",
                    TestContext.Current.CancellationToken));

            _ = await InvokeProviderAsync<string>(
                provider,
                "ReplaceLinesAsync",
                "Workspace/job.txt",
                new List<FileLineEdit>
                {
                    new() { LineNumber = 1, NewLine = "delta\n" }
                },
                TestContext.Current.CancellationToken);
            Assert.Equal(
                "delta\ngamma\n",
                await InvokeProviderAsync<string>(
                    provider,
                    "ReadAsync",
                    "Workspace/job.txt",
                    TestContext.Current.CancellationToken));

            var deleted = await InvokeProviderAsync<string>(
                provider,
                "DeleteAsync",
                "Workspace/job.txt",
                TestContext.Current.CancellationToken);
            Assert.Contains("deleted", deleted, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, "workspace", "job.txt")));
            Assert.Single(Directory.EnumerateFiles(access.RecoverableTrashPath, "job.txt", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public async Task TrustedWorkstation_AutoApprovesReadsAndOnlyNewNonOverwriteWrites()
    {
        await WithAccessAsync(async (root, access, permissions) =>
        {
            Assert.True(await access.ShouldAutoApproveAsync(
                AliCapabilityCatalog.FileReadName,
                new Dictionary<string, object?> { ["fileName"] = "Workspace/missing.txt" },
                TestContext.Current.CancellationToken));
            Assert.True(await access.ShouldAutoApproveAsync(
                AliCapabilityCatalog.FileWriteName,
                new Dictionary<string, object?>
                {
                    ["fileName"] = "Workspace/new.txt",
                    ["overwrite"] = false
                },
                TestContext.Current.CancellationToken));

            await access.Store.WriteAsync("Workspace/new.txt", "created", TestContext.Current.CancellationToken);

            Assert.False(await access.ShouldAutoApproveAsync(
                AliCapabilityCatalog.FileWriteName,
                new Dictionary<string, object?>
                {
                    ["fileName"] = "Workspace/new.txt",
                    ["overwrite"] = false
                },
                TestContext.Current.CancellationToken));
            Assert.False(await access.ShouldAutoApproveAsync(
                AliCapabilityCatalog.FileReplaceName,
                new Dictionary<string, object?> { ["fileName"] = "Workspace/new.txt" },
                TestContext.Current.CancellationToken));
            Assert.False(await access.ShouldAutoApproveAsync(
                AliCapabilityCatalog.FileDeleteName,
                new Dictionary<string, object?> { ["fileName"] = "Workspace/new.txt" },
                TestContext.Current.CancellationToken));
            Assert.False(await access.ShouldAutoApproveAsync(
                AliCapabilityCatalog.FileWriteName,
                new Dictionary<string, object?>
                {
                    ["fileName"] = "Workspace/another.txt",
                    ["overwrite"] = true
                },
                TestContext.Current.CancellationToken));

            permissions.SetProfile(AgentPermissionProfile.LockedDown);
            Assert.False(await access.ShouldAutoApproveAsync(
                AliCapabilityCatalog.FileReadName,
                new Dictionary<string, object?> { ["fileName"] = "Workspace/new.txt" },
                TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task WorkstationStore_RejectsAbsoluteTraversalAndUnknownRoots()
    {
        await WithAccessAsync(async (root, access, permissions) =>
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                access.Store.ReadAsync("C:/Windows/win.ini", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                access.Store.ReadAsync("Workspace/../outside.txt", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                access.Store.ReadAsync("AppData/secret.txt", TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task AuditLog_RecordsMetadataButNeverFileContent()
    {
        await WithAccessAsync(async (root, access, permissions) =>
        {
            const string secret = "private-file-content-should-not-be-logged";
            await access.Store.WriteAsync("Exports/report.txt", secret, TestContext.Current.CancellationToken);
            _ = await access.Store.ReadAsync("Exports/report.txt", TestContext.Current.CancellationToken);

            var audit = await File.ReadAllTextAsync(access.Audit.Path, TestContext.Current.CancellationToken);
            Assert.Contains("Exports/report.txt", audit, StringComparison.Ordinal);
            Assert.Contains("write", audit, StringComparison.Ordinal);
            Assert.Contains("read", audit, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
        });
    }

    private static async Task<T> InvokeProviderAsync<T>(
        FileAccessProvider provider,
        string methodName,
        params object?[] arguments)
    {
        var method = typeof(FileAccessProvider).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(FileAccessProvider).FullName, methodName);
        var task = method.Invoke(provider, arguments) as Task
            ?? throw new InvalidOperationException($"{methodName} did not return a Task.");
        await task.ConfigureAwait(false);
        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        return result is T typed
            ? typed
            : throw new InvalidOperationException(
                $"{methodName} returned {result?.GetType().FullName ?? "null"}, not {typeof(T).FullName}.");
    }

    private static async Task WithAccessAsync(
        Func<string, AliWorkstationFileAccess, AgentToolPermissionStore, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliFileAccessTests", Guid.NewGuid().ToString("N"));
        try
        {
            var permissions = new AgentToolPermissionStore(root);
            var store = new AliWorkstationFileStore(
            [
                new AliWorkstationFileMount("Workspace", Path.Combine(root, "workspace")),
                new AliWorkstationFileMount("Exports", Path.Combine(root, "exports"))
            ], Path.Combine(root, "trash"));
            var audit = new AgentFileActionAuditStore(root, activeUsers: null);
            var access = new AliWorkstationFileAccess(store, audit, permissions);
            await action(root, access, permissions);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
