using Ali.Modules.Coding;
using Ali.Modules.Coding.Engineering;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

public sealed class AliDirectDotNetProcessLeaseTests
{
    [Fact]
    public async Task EngineeringTestHoldsExactHostAcrossProcessStartInterposition()
    {
        var root = CreateRoot();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var projectDirectory = Path.Combine(workspace, "App");
            Directory.CreateDirectory(projectDirectory);
            var projectPath = Path.Combine(projectDirectory, "App.csproj");
            await File.WriteAllTextAsync(
                projectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
                TestContext.Current.CancellationToken);

            var exactHostPath = Path.Combine(root, "exact-dotnet-host.exe");
            var substitutePath = Path.Combine(root, "substitute-dotnet-host.exe");
            File.Copy(SystemExecutable("where.exe"), exactHostPath);
            File.Copy(SystemExecutable("whoami.exe"), substitutePath);
            var exactHost = AliExactDotNetHost.Capture(exactHostPath);
            Exception? interpositionFailure = null;
            var resolver = CreateResolver(root, workspace);
            var engineering = new AliDotNetEngineeringLoop(
                resolver,
                () =>
                {
                    try
                    {
                        File.Move(exactHostPath, exactHostPath + ".old");
                        File.Copy(substitutePath, exactHostPath);
                    }
                    catch (Exception exception)
                    {
                        interpositionFailure = exception;
                    }
                });
            using var exactContext = AliExactProcessExecutionContext.Enter(
                new AliExactProcessExecutionBinding(exactHost, null));

            var result = await engineering.TestAsync(
                "Workspace/App/App.csproj",
                "Release",
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.IsType<IOException>(interpositionFailure);
            Assert.Equal(exactHostPath, AliExactDotNetHost.Revalidate(exactHost), ignoreCase: true);
        }
        finally
        {
            await DeleteAfterLeaseReleaseAsync(root);
        }
    }

    [Fact]
    public async Task RunHoldsFullApplicationClosureThroughInterpositionAndUntilExactExit()
    {
        var root = CreateRoot();
        int? processId = null;
        AliRoslynCodingTools? tools = null;
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var projectDirectory = Path.Combine(workspace, "App");
            Directory.CreateDirectory(projectDirectory);
            var projectPath = Path.Combine(projectDirectory, "App.csproj");
            await File.WriteAllTextAsync(
                projectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "Program.cs"),
                "Thread.Sleep(Timeout.Infinite);",
                TestContext.Current.CancellationToken);

            var resolver = CreateResolver(root, workspace);
            var tracker = new AliCodingProjectTracker();
            var runtimeConfigPath = Path.Combine(
                projectDirectory,
                "bin",
                "Release",
                "net10.0",
                "App.runtimeconfig.json");
            Exception? interpositionFailure = null;
            tools = new AliRoslynCodingTools(
                resolver,
                tracker,
                Path.Combine(root, "dotnet-actions.jsonl"),
                () =>
                {
                    try
                    {
                        File.Move(runtimeConfigPath, runtimeConfigPath + ".old");
                    }
                    catch (Exception exception)
                    {
                        interpositionFailure = exception;
                    }
                });
            var build = await tools.BuildAsync(
                "Workspace/App/App.csproj",
                "Release",
                TestContext.Current.CancellationToken);
            Assert.True(build.Success, build.Output);
            Assert.True(File.Exists(runtimeConfigPath));

            var run = await tools.RunAsync(
                "Workspace/App/App.csproj",
                "Release",
                TestContext.Current.CancellationToken);
            Assert.True(run.Success, run.Summary);
            processId = Assert.IsType<int>(run.ProcessId);
            Assert.IsType<IOException>(interpositionFailure);

            var liveMutationFailure = Record.Exception(
                () => File.AppendAllText(runtimeConfigPath, " "));
            Assert.IsType<IOException>(liveMutationFailure);

            var recoveredTools = new AliRoslynCodingTools(
                resolver,
                tracker,
                Path.Combine(root, "recovered-dotnet-actions.jsonl"));
            var recoveredBuild = await recoveredTools.BuildAsync(
                "Workspace/App/App.csproj",
                "Release",
                TestContext.Current.CancellationToken);
            Assert.False(recoveredBuild.Success);
            Assert.Equal("RunningTarget", recoveredBuild.FailureKind);
            Assert.Equal(processId, recoveredBuild.BlockingProcessId);

            var stopped = await recoveredTools.StopProjectAsync(
                "Workspace/App/App.csproj",
                "Release",
                TestContext.Current.CancellationToken);
            Assert.True(stopped.Success, stopped.Summary);
            processId = null;

            var releaseDeadline = DateTime.UtcNow.AddSeconds(5);
            Exception? afterExitFailure;
            do
            {
                afterExitFailure = Record.Exception(
                    () => File.AppendAllText(runtimeConfigPath, " "));
                if (afterExitFailure is not null)
                {
                    await Task.Delay(25, TestContext.Current.CancellationToken);
                }
            }
            while (afterExitFailure is not null && DateTime.UtcNow < releaseDeadline);
            Assert.Null(afterExitFailure);
        }
        finally
        {
            if (processId is int liveProcessId)
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(liveProcessId);
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                    }
                }
                catch (ArgumentException)
                {
                    // The tested exact process already exited.
                }
            }
            await DeleteAfterLeaseReleaseAsync(root);
        }
    }

    private static AliCodingProjectResolver CreateResolver(string root, string workspace)
    {
        var permissions = new AgentToolPermissionStore(root);
        var store = new AliWorkstationFileStore(
            [new AliWorkstationFileMount("Workspace", workspace)],
            Path.Combine(root, "trash"));
        var access = new AliWorkstationFileAccess(
            store,
            new AgentFileActionAuditStore(root, activeUsers: null),
            permissions);
        return new AliCodingProjectResolver(access);
    }

    private static string SystemExecutable(string fileName) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            fileName);

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Ali-Cp7-DotNet-Lease",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task DeleteAfterLeaseReleaseAsync(string root)
    {
        for (var attempt = 1; Directory.Exists(root); attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (Exception exception) when (
                attempt < 50
                && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100);
            }
        }
    }
}
