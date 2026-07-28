using Ali.Modules.Coding;
using Ali.Modules.Coding.Debugging;
using Ali.Modules.Coding.Engineering;
using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

[Collection(ProcessEnvironmentIntegrationCollection.Name)]
public sealed class DotNetEngineeringAndDebuggerTests
{
    [Fact]
    public async Task EngineeringLoop_DiscoversTestsWritesTrxAndReturnsStructuredFailure()
    {
        await WithModuleAsync(async (root, access, module) =>
        {
            await WriteTestProjectAsync(access, passing: false);
            var result = await module.EngineeringLoop.TestAsync("Workspace/Tests/Tests.csproj", "Release", TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal(2, result.Total);
            Assert.Equal(1, result.Passed);
            Assert.Equal(1, result.Failed);
            Assert.Single(result.Failures);
            Assert.Contains("Expected", result.Failures[0].Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(result.ResultsPath);
            Assert.True(File.Exists(result.ResultsPath));
        });
    }

    [Fact]
    public async Task EngineeringLoop_VerifiesBuildAndPassingTests()
    {
        await WithModuleAsync(async (root, access, module) =>
        {
            await WriteTestProjectAsync(access, passing: true);
            var result = await module.EngineeringLoop.VerifyAsync(
                "Workspace/Tests/Tests.csproj", "Release", module.Tools.BuildAsync, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Tests?.Output ?? result.Build.Output);
            Assert.True(result.Build.Success);
            Assert.NotNull(result.Tests);
            Assert.Equal(2, result.Tests.Total);
            Assert.Empty(result.Failures);
        });
    }

    [Fact]
    public async Task Debugger_LaunchesBreaksInspectsEvaluatesStepsAndTerminates()
    {
        await WithModuleAsync(async (root, access, module) =>
        {
            await access.Store.WriteAsync("Workspace/DebugApp/DebugApp.csproj",
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><DebugType>portable</DebugType></PropertyGroup></Project>""",
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/DebugApp/Program.cs",
                """
                namespace DebugApp;
                public static class Program
                {
                    public static void Main()
                    {
                        var answer = 42;
                        System.Console.WriteLine(answer);
                        System.Threading.Thread.Sleep(5000);
                    }
                }
                """, TestContext.Current.CancellationToken);
            var build = await module.Tools.BuildAsync("Workspace/DebugApp/DebugApp.csproj", "Debug", TestContext.Current.CancellationToken);
            Assert.True(build.Success, build.Output);

            var started = await module.Debugger.LaunchAsync("Workspace/DebugApp/DebugApp.csproj", "Debug", false,
                "Workspace/DebugApp/Program.cs", 8, TestContext.Current.CancellationToken);
            Assert.True(started.Success, started.Summary);
            Assert.NotNull(started.SessionId);
            Assert.Single(started.Breakpoints);
            // netcoredbg may report a portable-PDB breakpoint as pending until the module loads.
            Assert.True(started.Breakpoints[0].Verified || started.Breakpoints[0].Message?.Contains("pending", StringComparison.OrdinalIgnoreCase) == true,
                started.Breakpoints[0].Message);
            var configuredBreakpoints = await module.Debugger.SetBreakpointsAsync(started.SessionId,
                "Workspace/DebugApp/DebugApp.csproj", "Workspace/DebugApp/Program.cs", [8], TestContext.Current.CancellationToken);
            Assert.Single(configuredBreakpoints);
            var handoff = module.Debugger.GetDiagnosticsHandoff(started.SessionId);
            Assert.Equal(started.ProcessId, handoff.ProcessId);
            Assert.Contains("dotnet-trace", handoff.SupportedConsumers);

            DotNetDebugSnapshot? snapshot = null;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    snapshot = await module.Debugger.InspectAsync(started.SessionId, TestContext.Current.CancellationToken);
                    if (snapshot.State == "stopped") break;
                }
                catch (InvalidOperationException) { }
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
            Assert.NotNull(snapshot);
            Assert.Equal("stopped", snapshot.State);
            Assert.NotEmpty(snapshot.Threads);
            Assert.Contains(snapshot.Threads.SelectMany(thread => thread.Frames), frame => frame.SourcePath?.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase) == true);
            Assert.Contains(snapshot.Variables, variable => variable.Name == "answer" && variable.Value.Contains("42", StringComparison.Ordinal));

            var frameId = snapshot.Threads.SelectMany(thread => thread.Frames).First().Id;
            var evaluation = await module.Debugger.EvaluateAsync(started.SessionId, "answer + 1", frameId, TestContext.Current.CancellationToken);
            Assert.True(evaluation.Success);
            Assert.Contains("43", evaluation.Value ?? "", StringComparison.Ordinal);

            var next = await module.Debugger.ControlAsync(started.SessionId, "next", TestContext.Current.CancellationToken);
            Assert.True(next.Success);
            var stopped = await module.Debugger.StopAsync(started.SessionId, TestContext.Current.CancellationToken);
            Assert.Equal("terminated", stopped.State);
        });
    }

    [Fact]
    public void EngineeringAndDebuggerTools_AreMcpReadyAndApprovalProtected()
    {
        var protectedNames = new[]
        {
            AliCapabilityCatalog.DotNetTestName, AliCapabilityCatalog.DotNetVerifyName,
            AliCapabilityCatalog.DotNetDebugLaunchName, AliCapabilityCatalog.DotNetDebugAttachName,
            AliCapabilityCatalog.DotNetDebugInspectName, AliCapabilityCatalog.DotNetDebugEvaluateName,
            AliCapabilityCatalog.DotNetDebugBreakpointsName, AliCapabilityCatalog.DotNetDebugControlName,
            AliCapabilityCatalog.DotNetDebugStopName, AliCapabilityCatalog.DotNetDebugDiagnosticsHandoffName
        };
        foreach (var name in protectedNames)
        {
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == name);
            Assert.True(AliToolPermissionPolicy.RequiresApproval(name));
            Assert.Contains(Ali.Modules.Mcp.McpServerToolCatalog.CreateDefaultPolicies(), policy => policy.Name == name && !policy.Enabled);
        }
    }

    private static async Task WriteTestProjectAsync(AliWorkstationFileAccess access, bool passing)
    {
        await access.Store.WriteAsync("Workspace/Tests/Tests.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
                <PackageReference Include="xunit.v3" Version="3.2.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5"><PrivateAssets>all</PrivateAssets></PackageReference>
              </ItemGroup>
            </Project>
            """, TestContext.Current.CancellationToken);
        await access.Store.WriteAsync("Workspace/Tests/Checks.cs",
            $$"""
            public sealed class Checks
            {
                [Xunit.Fact] public void Passing() => Xunit.Assert.True(true);
                [Xunit.Fact] public void Second() => Xunit.Assert.{{(passing ? "Equal(4, 2 + 2)" : "Equal(5, 2 + 2)")}};
            }
            """, TestContext.Current.CancellationToken);
    }

    private static async Task WithModuleAsync(Func<string, AliWorkstationFileAccess, AliCodingModule, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliEngineeringTests", Guid.NewGuid().ToString("N"));
        try
        {
            var permissions = new AgentToolPermissionStore(root);
            var store = new AliWorkstationFileStore([new AliWorkstationFileMount("Workspace", Path.Combine(root, "workspace"))], Path.Combine(root, "trash"));
            var access = new AliWorkstationFileAccess(store, new AgentFileActionAuditStore(root, null), permissions);
            await using var module = new AliCodingModule(access);
            var debuggerPath = Path.Combine(RepositoryRoot, "artifacts", "runtime-assets", "win-x64", "dependencies", "debugger", "netcoredbg", "netcoredbg.exe");
            Environment.SetEnvironmentVariable("ALI_NETCOREDBG_PATH", debuggerPath);
            await action(root, access, module);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALI_NETCOREDBG_PATH", null);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ali.sln"))) directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }
    }
}
