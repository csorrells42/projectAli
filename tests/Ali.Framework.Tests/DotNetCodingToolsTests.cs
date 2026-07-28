using Ali.Modules.Coding;
using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

public sealed class DotNetCodingToolsTests
{
    [Fact]
    public async Task BuildAndRun_CompilesAndLaunchesApprovedTemporaryProject()
    {
        await WithCodingToolsAsync(async (root, access, tools, auditPath) =>
        {
            await access.Store.WriteAsync(
                "Workspace/TinyGame/TinyGame.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync(
                "Workspace/TinyGame/Program.cs",
                """
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ali-run-proof.txt"), "launched");
                """,
                TestContext.Current.CancellationToken);

            var build = await tools.BuildAsync(
                "Workspace/TinyGame/TinyGame.csproj",
                "Release",
                TestContext.Current.CancellationToken);

            Assert.True(build.Success, build.Output);
            Assert.Equal(0, build.ExitCode);
            Assert.NotNull(build.ArtifactPath);
            Assert.True(File.Exists(build.ArtifactPath));

            var run = await tools.RunAsync(
                "Workspace/TinyGame/TinyGame.csproj",
                "Release",
                TestContext.Current.CancellationToken);
            Assert.True(run.Success, run.Summary);
            Assert.NotNull(run.ProcessId);

            var proofPath = Path.Combine(Path.GetDirectoryName(run.ArtifactPath!)!, "ali-run-proof.txt");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(proofPath) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            Assert.True(File.Exists(proofPath), "The launched test application did not write its proof file.");
            Assert.Equal("launched", await File.ReadAllTextAsync(proofPath, TestContext.Current.CancellationToken));
            var audit = await File.ReadAllTextAsync(auditPath, TestContext.Current.CancellationToken);
            Assert.Contains("\"operation\":\"build\"", audit, StringComparison.Ordinal);
            Assert.Contains("\"operation\":\"run\"", audit, StringComparison.Ordinal);
            Assert.DoesNotContain("File.WriteAllText", audit, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Build_ReturnsCompilerDiagnosticsWithoutClaimingSuccess()
    {
        await WithCodingToolsAsync(async (root, access, tools, auditPath) =>
        {
            await access.Store.WriteAsync(
                "Workspace/Broken/Broken.csproj",
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""",
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync(
                "Workspace/Broken/Program.cs",
                "this is not valid C#;",
                TestContext.Current.CancellationToken);

            var result = await tools.BuildAsync(
                "Workspace/Broken/Broken.csproj",
                "Debug",
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("error CS", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("correct", result.Summary, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Tools_RejectOutsidePathsInvalidConfigurationsAndRequireApproval()
    {
        await WithCodingToolsAsync(async (root, access, tools, auditPath) =>
        {
            await Assert.ThrowsAsync<ArgumentException>(() => tools.BuildAsync(
                Path.Combine(root, "outside.csproj"),
                "Release",
                TestContext.Current.CancellationToken));
            await access.Store.WriteAsync(
                "Workspace/App/App.csproj",
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""",
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ArgumentException>(() => tools.BuildAsync(
                "Workspace/App/App.csproj",
                "Release --target Clean",
                TestContext.Current.CancellationToken));

            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetBuildName));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetRunName));
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.DotNetBuildName);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.DotNetRunName);
        });
    }

    private static async Task WithCodingToolsAsync(
        Func<string, AliWorkstationFileAccess, AliDotNetCodingTools, string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliDotNetCodingTests", Guid.NewGuid().ToString("N"));
        try
        {
            var permissions = new AgentToolPermissionStore(root);
            var store = new AliWorkstationFileStore(
            [
                new AliWorkstationFileMount("Workspace", Path.Combine(root, "workspace"))
            ], Path.Combine(root, "trash"));
            var audit = new AgentFileActionAuditStore(root, activeUsers: null);
            var access = new AliWorkstationFileAccess(store, audit, permissions);
            var codingAuditPath = Path.Combine(root, "dotnet-actions.jsonl");
            var tools = new AliDotNetCodingTools(access, codingAuditPath);
            await action(root, access, tools, codingAuditPath);
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
