using Ali.Modules.Coordinator;
using Ali.Modules.WorkstationFiles;
using Ali.Modules.Permissions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Reflection;
using System.IO.Compression;

namespace Ali.Framework.Tests;

public sealed class WorkstationFileAccessTests
{
    [Fact]
    public async Task CoreAssistant_FileAuditJournalIsSkipped()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliCoreFileAuditTests", Guid.NewGuid().ToString("N"));
        try
        {
            var audit = new AgentFileActionAuditStore(root, activeUsers: null);
            using (AliCoreAssistantExecutionContext.Enter())
            {
                await audit.AppendAsync(
                    "write",
                    "Workspace/Program.cs",
                    succeeded: true,
                    "completed",
                    TestContext.Current.CancellationToken);
            }

            Assert.False(File.Exists(audit.Path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CoreAssistant_WpfGeneratedMembersDoNotBlockSourceWrite()
    {
        await WithAccessAsync(async (_, access, _) =>
        {
            const string path = "Workspace/WpfApp/MainWindow.xaml.cs";
            await access.Store.WriteAsync(
                path,
                "namespace WpfApp; public partial class MainWindow { public MainWindow() { InitializeComponent(); } }",
                TestContext.Current.CancellationToken);

            const string codeBehind =
                "namespace WpfApp; public partial class MainWindow { public MainWindow() { InitializeComponent(); ParticleCanvas.ToString(); } private void StartButton_Click(object sender, object e) { ParticleCanvas.ToString(); } }";
            using (AliCoreAssistantExecutionContext.Enter())
            {
                await access.Store.WriteAsync(path, codeBehind, TestContext.Current.CancellationToken);
            }

            Assert.Equal(
                codeBehind,
                await access.Store.ReadAsync(path, TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task CoreAssistant_NewCSharpSyntaxErrorStillBlocksSourceWrite()
    {
        await WithAccessAsync(async (_, access, _) =>
        {
            const string path = "Workspace/App/Program.cs";
            const string original = "namespace App; public static class Program { public static void Main() { } }";
            await access.Store.WriteAsync(path, original, TestContext.Current.CancellationToken);

            using (AliCoreAssistantExecutionContext.Enter())
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                    access.Store.WriteAsync(
                        path,
                        "namespace App; public static class Program { public static void Main( { } }",
                        TestContext.Current.CancellationToken));
                Assert.Contains("syntax errors", error.Message, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Equal(
                original,
                await access.Store.ReadAsync(path, TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task CoreAssistant_ValidFullSourceRewriteIsAllowed()
    {
        await WithAccessAsync(async (_, access, _) =>
        {
            const string path = "Workspace/App/Program.cs";
            var original = "namespace App; public static class Program { "
                + string.Concat(Enumerable.Repeat("private static int Value() => 1; ", 20))
                + "public static void Main() { } }";
            const string replacement =
                "namespace App; public static class Program { public static void Main() { } }";
            await access.Store.WriteAsync(path, original, TestContext.Current.CancellationToken);

            using (AliCoreAssistantExecutionContext.Enter())
            {
                await access.Store.WriteAsync(
                    path,
                    replacement,
                    TestContext.Current.CancellationToken);
            }

            Assert.Equal(
                replacement,
                await access.Store.ReadAsync(path, TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task ModelPresentedDoubleQuotes_AreRemovedFromVirtualPathBoundary()
    {
        await WithAccessAsync(async (root, access, _) =>
        {
            await access.Store.WriteAsync(
                "\"Workspace/quoted.txt\"",
                "value",
                TestContext.Current.CancellationToken);

            Assert.Equal(
                "value",
                await File.ReadAllTextAsync(
                    Path.Combine(root, "workspace", "quoted.txt"),
                    TestContext.Current.CancellationToken));
        });
    }

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
    public async Task FrameworkProvider_CanOverwriteApprovedAbsoluteExistingFile()
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
            var path = Path.Combine(root, "workspace", ".existing.txt");
            var createArguments = access.NormalizeProviderToolArguments(
                AliCapabilityCatalog.FileWriteName,
                new Dictionary<string, object?> { ["fileName"] = path });
            var providerPath = Assert.IsType<string>(createArguments["fileName"]);

            Assert.Equal("Workspace/.existing.txt", providerPath);
            var outside = Path.Combine(Path.GetPathRoot(root)!, "Windows", "win.ini");
            var rejectedArguments = access.NormalizeProviderToolArguments(
                AliCapabilityCatalog.FileWriteName,
                new Dictionary<string, object?> { ["fileName"] = outside });
            Assert.Equal(outside, Assert.IsType<string>(rejectedArguments["fileName"]));

            _ = await InvokeProviderAsync<string>(
                provider,
                "WriteAsync",
                providerPath,
                "original",
                false,
                TestContext.Current.CancellationToken);
            _ = await InvokeProviderAsync<string>(
                provider,
                "WriteAsync",
                providerPath,
                "replacement",
                true,
                TestContext.Current.CancellationToken);

            Assert.Equal("replacement", await File.ReadAllTextAsync(
                path,
                TestContext.Current.CancellationToken));
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
                AliCapabilityCatalog.LoadAgentSkillName,
                new Dictionary<string, object?> { ["skillName"] = "example" },
                TestContext.Current.CancellationToken));
            Assert.True(await access.ShouldAutoApproveAsync(
                AliCapabilityCatalog.ReadAgentSkillResourceName,
                new Dictionary<string, object?> { ["resourcePath"] = "example/reference.md" },
                TestContext.Current.CancellationToken));
            Assert.False(await access.ShouldAutoApproveAsync(
                AliCapabilityCatalog.RunAgentSkillScriptName,
                new Dictionary<string, object?> { ["scriptPath"] = "example/run.ps1" },
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
            Assert.False(await access.ShouldAutoApproveAsync(
                AliCapabilityCatalog.LoadAgentSkillName,
                new Dictionary<string, object?> { ["skillName"] = "example" },
                TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task Harness_TrustedWorkstationExecutesNewFileWriteWithoutPrompting()
    {
        await WithAccessAsync(async (root, access, permissions) =>
        {
            using var client = new ScriptedChatClient(
            [
                ToolCall(FileAccessProvider.WriteToolName, new Dictionary<string, object?>
                {
                    ["fileName"] = "Workspace/touch.txt",
                    ["content"] = "approval integration test",
                    ["overwrite"] = false
                }),
                FinalAnswer("created")
            ]);
            var agent = client.AsHarnessAgent(new HarnessAgentOptions
            {
                MaximumIterationsPerRequest = 4,
                DisableWebSearch = true,
                DisableFileMemory = true,
                DisableAgentSkillsProvider = true,
                DisableTodoProvider = true,
                DisableAgentModeProvider = true,
                FileAccessStore = access.Store,
                FileAccessProviderOptions = new FileAccessProviderOptions
                {
                    Instructions = access.Instructions,
                    DisableReadOnlyToolApproval = false,
                    DisableWriteToolApproval = false
                },
                ToolApprovalAgentOptions = new ToolApprovalAgentOptions
                {
                    AutoApprovalRules = [access.ShouldAutoApproveAsync]
                }
            });

            var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
            var response = await agent.RunAsync(
                "Create Workspace/touch.txt as a new file.",
                session,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.DoesNotContain(response.Messages.SelectMany(message => message.Contents),
                content => content is ToolApprovalRequestContent);
            Assert.Equal("approval integration test",
                await access.Store.ReadAsync("Workspace/touch.txt", TestContext.Current.CancellationToken));
            Assert.Equal(2, client.CallCount);
        });
    }

    [Fact]
    public async Task Harness_ApprovedWriteResumesAndExecutesTheOriginalToolCall()
    {
        await WithAccessAsync(async (root, access, permissions) =>
        {
            permissions.SetProfile(AgentPermissionProfile.LockedDown);
            using var client = new ScriptedChatClient(
            [
                ToolCall(FileAccessProvider.WriteToolName, new Dictionary<string, object?>
                {
                    ["fileName"] = "Workspace/touch.txt",
                    ["content"] = "approved integration test",
                    ["overwrite"] = false
                }),
                FinalAnswer("created")
            ]);
            var agent = client.AsHarnessAgent(new HarnessAgentOptions
            {
                MaximumIterationsPerRequest = 4,
                DisableWebSearch = true,
                DisableFileMemory = true,
                DisableAgentSkillsProvider = true,
                DisableTodoProvider = true,
                DisableAgentModeProvider = true,
                FileAccessStore = access.Store,
                FileAccessProviderOptions = new FileAccessProviderOptions
                {
                    Instructions = access.Instructions,
                    DisableReadOnlyToolApproval = false,
                    DisableWriteToolApproval = false
                },
                ToolApprovalAgentOptions = new ToolApprovalAgentOptions
                {
                    AutoApprovalRules = [access.ShouldAutoApproveAsync]
                }
            });

            var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
            var first = await agent.RunAsync(
                "Create Workspace/touch.txt.",
                session,
                cancellationToken: TestContext.Current.CancellationToken);
            var request = Assert.Single(first.Messages
                .SelectMany(message => message.Contents)
                .OfType<ToolApprovalRequestContent>());

            var approval = new ChatMessage(ChatRole.User,
            [
                request.CreateResponse(true, "Approved once by the user.")
            ]);
            var second = await agent.RunAsync(
                approval,
                session,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("created", second.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("approved integration test",
                await access.Store.ReadAsync("Workspace/touch.txt", TestContext.Current.CancellationToken));
            Assert.Equal(2, client.CallCount);
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
    public async Task WorkstationStore_AcceptsAbsolutePathsOnlyInsideApprovedMounts()
    {
        await WithAccessAsync(async (root, access, permissions) =>
        {
            var approvedAbsolutePath = Path.Combine(root, "workspace", "absolute.txt");

            Assert.True(await access.ShouldAutoApproveAsync(
                AliCapabilityCatalog.FileWriteName,
                new Dictionary<string, object?>
                {
                    ["fileName"] = approvedAbsolutePath,
                    ["overwrite"] = false
                },
                TestContext.Current.CancellationToken));
            await access.Store.WriteAsync(
                approvedAbsolutePath,
                "inside approved mount",
                TestContext.Current.CancellationToken);

            Assert.Equal(
                "inside approved mount",
                await access.Store.ReadAsync("Workspace/absolute.txt", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                access.Store.WriteAsync(
                    Path.Combine(root, "outside.txt"),
                    "outside approved mount",
                    TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task InvalidBarePath_TellsAgentHowToRetryWithoutAskingForAbsolutePath()
    {
        await WithAccessAsync(async (root, access, permissions) =>
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                access.Store.WriteAsync("touch.txt", "test", TestContext.Current.CancellationToken));

            Assert.Contains("Workspace/touch.txt", error.Message, StringComparison.Ordinal);
            Assert.Contains("do not ask the user for an absolute path", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Workspace/", access.Instructions, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task UniqueBareExistingFile_CanBeFoundAndDeletedWithoutInventingAPath()
    {
        await WithAccessAsync(async (root, access, permissions) =>
        {
            await access.Store.WriteAsync("Workspace/touch.txt", "old", TestContext.Current.CancellationToken);

            Assert.Equal("old", await access.Store.ReadAsync("touch.txt", TestContext.Current.CancellationToken));
            Assert.True(await access.Store.DeleteAsync("touch.txt", TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(root, "workspace", "touch.txt")));
        });
    }

    [Fact]
    public async Task ExistingDirectoryTree_IsMovedToRecoverableTrashAndMountRootCannotBeDeleted()
    {
        await WithAccessAsync(async (root, access, permissions) =>
        {
            var directory = Path.Combine(root, "workspace", "delete-tree", "nested");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "payload.txt"),
                "recoverable folder payload",
                TestContext.Current.CancellationToken);

            Assert.True(await access.Store.DeleteAsync(
                "Workspace/delete-tree",
                TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(Path.Combine(root, "workspace", "delete-tree")));
            var recovered = Assert.Single(Directory.EnumerateFiles(
                access.RecoverableTrashPath,
                "payload.txt",
                SearchOption.AllDirectories));
            Assert.Equal(
                "recoverable folder payload",
                await File.ReadAllTextAsync(recovered, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => access.Store.DeleteAsync(
                "Workspace",
                TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task GitRepositoryControlDataAndRepositoryDeletionAreRejected()
    {
        await WithAccessAsync(async (root, access, _) =>
        {
            var repository = Path.Combine(root, "workspace", "repository");
            Directory.CreateDirectory(Path.Combine(repository, ".git"));
            await File.WriteAllTextAsync(
                Path.Combine(repository, ".git", "config"),
                "protected",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(repository, "Program.cs"),
                "namespace Repository;",
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                access.Store.WriteAsync(
                    "Workspace/repository/.git/config",
                    "changed",
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                access.Store.DeleteAsync(
                    "Workspace/repository/.git",
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                access.Store.DeleteAsync(
                    "Workspace/repository",
                    TestContext.Current.CancellationToken));

            Assert.True(Directory.Exists(repository));
            Assert.True(Directory.Exists(Path.Combine(repository, ".git")));
            Assert.True(File.Exists(Path.Combine(repository, "Program.cs")));
        });
    }

    [Fact]
    public async Task BareExistingFile_FailsClearlyWhenMoreThanOneRootMatches()
    {
        await WithAccessAsync(async (root, access, permissions) =>
        {
            await access.Store.WriteAsync("Workspace/touch.txt", "workspace", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Exports/touch.txt", "exports", TestContext.Current.CancellationToken);

            var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                access.Store.ReadAsync("touch.txt", TestContext.Current.CancellationToken));
            Assert.Contains("more than one approved root", error.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task MoveTool_RenamesWithoutRecreatingAndNeverOverwritesDestination()
    {
        await WithAccessAsync(async (root, access, permissions) =>
        {
            await access.Store.WriteAsync("Workspace/touch.txt", "preserved", TestContext.Current.CancellationToken);

            var moved = await access.MoveAsync(
                "touch.txt",
                "touch.cs",
                TestContext.Current.CancellationToken);

            Assert.True(moved.Success, moved.Message);
            Assert.False(File.Exists(Path.Combine(root, "workspace", "touch.txt")));
            Assert.Equal("preserved", await File.ReadAllTextAsync(
                Path.Combine(root, "workspace", "touch.cs"),
                TestContext.Current.CancellationToken));

            await access.Store.WriteAsync("Workspace/other.txt", "other", TestContext.Current.CancellationToken);
            var collision = await access.MoveAsync(
                "Workspace/other.txt",
                "Workspace/touch.cs",
                TestContext.Current.CancellationToken);
            Assert.False(collision.Success);
            Assert.Equal("preserved", await File.ReadAllTextAsync(
                Path.Combine(root, "workspace", "touch.cs"),
                TestContext.Current.CancellationToken));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.FileMoveName));
        });
    }

    [Fact]
    public async Task BinaryUtilities_CopyFoldersCreateDirectoriesAndHashFilesWithoutOverwrite()
    {
        await WithAccessAsync(async (root, access, permissions) =>
        {
            var utilities = new AliWorkstationFileUtilities(access);
            var source = Path.Combine(root, "workspace", "source");
            Directory.CreateDirectory(source);
            await File.WriteAllBytesAsync(
                Path.Combine(source, "payload.bin"),
                [0, 1, 2, 3, 255],
                TestContext.Current.CancellationToken);

            var copied = await utilities.CopyAsync(
                "Workspace/source",
                "Exports/copied",
                TestContext.Current.CancellationToken);
            Assert.True(copied.Success, copied.Message);
            Assert.Equal(
                new byte[] { 0, 1, 2, 3, 255 },
                await File.ReadAllBytesAsync(Path.Combine(root, "exports", "copied", "payload.bin"), TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateFileSystemEntries(
                Path.Combine(root, "exports"),
                ".ali-copy-*",
                SearchOption.TopDirectoryOnly));

            var collision = await utilities.CopyAsync(
                "Workspace/source",
                "Exports/copied",
                TestContext.Current.CancellationToken);
            Assert.False(collision.Success);

            var directory = await utilities.CreateDirectoryAsync(
                "Exports/empty-folder",
                TestContext.Current.CancellationToken);
            Assert.True(directory.Success, directory.Message);
            Assert.True(Directory.Exists(Path.Combine(root, "exports", "empty-folder")));

            var metadata = await utilities.GetMetadataAsync(
                "Exports/copied/payload.bin",
                includeSha256: true,
                TestContext.Current.CancellationToken);
            Assert.True(metadata.Success, metadata.Message);
            Assert.Equal(5, metadata.SizeBytes);
            Assert.Equal("ff5d8507b6a72bee2debce2c0054798deaccdc5d8a1b945b6280ce8aa9cba52e", metadata.Sha256);

            var moved = await access.MoveAsync(
                "Exports/copied",
                "Exports/renamed",
                TestContext.Current.CancellationToken);
            Assert.True(moved.Success, moved.Message);
            Assert.False(Directory.Exists(Path.Combine(root, "exports", "copied")));
            Assert.True(File.Exists(Path.Combine(root, "exports", "renamed", "payload.bin")));
        });
    }

    [Theory]
    [InlineData(null, "Exports/default", "zip")]
    [InlineData("tar", "Exports/sample.tar", "tar")]
    [InlineData("tar.gz", "Exports/sample.tar.gz", "tar.gz")]
    public async Task ArchiveUtilities_CreateListAndExtractPortableFormats(
        string? requestedFormat,
        string archivePath,
        string expectedFormat)
    {
        await WithAccessAsync(async (root, access, permissions) =>
        {
            var utilities = new AliWorkstationFileUtilities(access);
            var source = Path.Combine(root, "workspace", "archive-source");
            Directory.CreateDirectory(Path.Combine(source, "nested"));
            await File.WriteAllTextAsync(Path.Combine(source, "nested", "note.txt"), "portable archive", TestContext.Current.CancellationToken);

            var created = await utilities.CreateArchiveAsync(
                "Workspace/archive-source",
                archivePath,
                requestedFormat,
                TestContext.Current.CancellationToken);
            Assert.True(created.Success, created.Message);
            Assert.Equal(expectedFormat, created.Format);
            Assert.Empty(Directory.EnumerateFileSystemEntries(
                Path.Combine(root, "exports"),
                ".ali-archive-*",
                SearchOption.TopDirectoryOnly));

            var listed = await utilities.ListArchiveAsync(created.ArchivePath, TestContext.Current.CancellationToken);
            Assert.True(listed.Success, listed.Message);
            Assert.Contains(listed.Entries, entry => entry.Path.Replace('\\', '/').EndsWith("nested/note.txt", StringComparison.Ordinal));

            var extracted = await utilities.ExtractArchiveAsync(
                created.ArchivePath,
                $"Exports/extracted-{expectedFormat.Replace('.', '-')}",
                TestContext.Current.CancellationToken);
            Assert.True(extracted.Success, extracted.Message);
            Assert.Equal(
                "portable archive",
                await File.ReadAllTextAsync(
                    Path.Combine(root, "exports", $"extracted-{expectedFormat.Replace('.', '-')}", "nested", "note.txt"),
                    TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task ArchiveUtilities_GZipSingleFileRoundTripsAndTraversalIsRejected()
    {
        await WithAccessAsync(async (root, access, permissions) =>
        {
            var utilities = new AliWorkstationFileUtilities(access);
            await File.WriteAllTextAsync(Path.Combine(root, "workspace", "single.txt"), "gzip payload", TestContext.Current.CancellationToken);
            var gzip = await utilities.CreateArchiveAsync(
                "Workspace/single.txt",
                "Exports/single.txt.gz",
                "gzip",
                TestContext.Current.CancellationToken);
            Assert.True(gzip.Success, gzip.Message);
            var extracted = await utilities.ExtractArchiveAsync(
                gzip.ArchivePath,
                "Exports/gzip-output",
                TestContext.Current.CancellationToken);
            Assert.True(extracted.Success, extracted.Message);
            Assert.Equal("gzip payload", await File.ReadAllTextAsync(
                Path.Combine(root, "exports", "gzip-output", "single.txt"),
                TestContext.Current.CancellationToken));

            var maliciousPath = Path.Combine(root, "exports", "malicious.zip");
            using (var archive = ZipFile.Open(maliciousPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../escape.txt");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("blocked");
            }

            var rejected = await utilities.ExtractArchiveAsync(
                "Exports/malicious.zip",
                "Exports/malicious-output",
                TestContext.Current.CancellationToken);
            Assert.False(rejected.Success);
            Assert.False(File.Exists(Path.Combine(root, "exports", "escape.txt")));
            Assert.False(Directory.Exists(Path.Combine(root, "exports", "malicious-output")));
        });
    }

    [Fact]
    public async Task ArchiveUtilities_CanPlaceArchiveInsideSourceFolderWithoutArchivingItsStagingFile()
    {
        await WithAccessAsync(async (root, access, permissions) =>
        {
            var utilities = new AliWorkstationFileUtilities(access);
            var source = Path.Combine(root, "workspace", "inside-source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(
                Path.Combine(source, "beta.txt"),
                "beta",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(source, "gamma.txt"),
                "gamma",
                TestContext.Current.CancellationToken);

            var created = await utilities.CreateArchiveAsync(
                "Workspace/inside-source",
                "Workspace/inside-source/bundle.zip",
                "zip",
                TestContext.Current.CancellationToken);

            Assert.True(created.Success, created.Message);
            var listed = await utilities.ListArchiveAsync(
                created.ArchivePath,
                TestContext.Current.CancellationToken);
            Assert.True(listed.Success, listed.Message);
            Assert.Equal(
                ["beta.txt", "gamma.txt"],
                listed.Entries.Select(entry => entry.Path.Replace('\\', '/')).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(listed.Entries, entry => entry.Path.Contains(".ali-archive-", StringComparison.Ordinal));
            Assert.Empty(Directory.EnumerateFileSystemEntries(
                Path.Combine(root, "workspace"),
                ".ali-archive-*",
                SearchOption.TopDirectoryOnly));
        });
    }

    [Fact]
    public async Task ArchiveUtilities_UsesInstalled7ZipOnlyWhenExplicitlyRequested()
    {
        var sevenZip = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
        }.FirstOrDefault(File.Exists);
        if (sevenZip is null)
        {
            return;
        }

        await WithAccessAsync(async (root, access, permissions) =>
        {
            var utilities = new AliWorkstationFileUtilities(access);
            var source = Path.Combine(root, "workspace", "seven-source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "seven.txt"), "explicit seven zip", TestContext.Current.CancellationToken);

            var created = await utilities.CreateArchiveAsync(
                "Workspace/seven-source",
                "Exports/explicit.7z",
                "7z",
                TestContext.Current.CancellationToken);
            Assert.True(created.Success, created.Message);
            Assert.Equal("7z", created.Format);

            var listed = await utilities.ListArchiveAsync(created.ArchivePath, TestContext.Current.CancellationToken);
            Assert.True(listed.Success, listed.Message);
            Assert.Contains(listed.Entries, entry => entry.Path.EndsWith("seven.txt", StringComparison.OrdinalIgnoreCase));

            var extracted = await utilities.ExtractArchiveAsync(
                created.ArchivePath,
                "Exports/seven-output",
                TestContext.Current.CancellationToken);
            Assert.True(extracted.Success, extracted.Message);
            var payload = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(root, "exports", "seven-output"),
                "seven.txt",
                SearchOption.AllDirectories));
            Assert.Equal("explicit seven zip", await File.ReadAllTextAsync(payload, TestContext.Current.CancellationToken));
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

    private static ChatResponse ToolCall(string name, IDictionary<string, object?> arguments)
    {
        var message = new ChatMessage(ChatRole.Assistant, string.Empty);
        message.Contents.Add(new FunctionCallContent($"call-{Guid.NewGuid():N}", name, arguments));
        return new ChatResponse(message) { FinishReason = ChatFinishReason.ToolCalls };
    }

    private static ChatResponse FinalAnswer(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = ChatFinishReason.Stop };

    private sealed class ScriptedChatClient(IEnumerable<ChatResponse> responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : FinalAnswer("script exhausted"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
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
