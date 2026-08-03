using Ali.Modules.Coding;
using Ali.Modules.Coding.Architecture;
using Ali.Modules.Coding.Dependencies;
using Ali.Modules.Coding.SourceControl;
using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

[Collection(ProcessEnvironmentIntegrationCollection.Name)]
public sealed class DotNetDeliveryFoundationTests
{
    [Fact]
    public void DependencyInspection_DisablesImplicitRestore()
    {
        var vulnerabilityArguments = AliDependencyEngineering.BuildVulnerabilityInspectionArguments(
            @"C:\work\App.csproj");
        var deprecationArguments = AliDependencyEngineering.BuildDeprecationInspectionArguments(
            @"C:\work\App.csproj");

        Assert.Equal(
            new[]
            {
                "list",
                @"C:\work\App.csproj",
                "package",
                "--no-restore",
                "--include-transitive",
                "--vulnerable",
                "--format",
                "json"
            },
            vulnerabilityArguments);
        Assert.Equal(
            new[]
            {
                "list",
                @"C:\work\App.csproj",
                "package",
                "--no-restore",
                "--include-transitive",
                "--deprecated",
                "--format",
                "json"
            },
            deprecationArguments);
    }

    [Fact]
    public async Task DependencyArchitectureQualityAndSourceControl_AreRealModularTools()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliDeliveryFoundation", Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            var access = CreateAccess(root, workspace);
            await access.Store.WriteAsync("Workspace/App/App.csproj",
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Example.Package" Version="1.0.0" /></ItemGroup></Project>""",
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/App/Code.cs",
                """
                namespace Foundation.Core { public static class Service { public static void Work() { } } }
                namespace Foundation.UI { public static class Screen { public static void Show() => Foundation.Core.Service.Work(); } }
                """, TestContext.Current.CancellationToken);
            await InitializeRepositoryAsync(workspace);
            await using var module = new AliCodingModule(access);

            var preview = await module.Dependencies.PreviewChangeAsync("Workspace/App/App.csproj", "update", "Example.Package", "2.0.0", TestContext.Current.CancellationToken);
            Assert.False(preview.Applied);
            Assert.Contains("1.0.0", await File.ReadAllTextAsync(Path.Combine(workspace, "App", "App.csproj"), TestContext.Current.CancellationToken));
            var applied = await module.Dependencies.ApplyChangeAsync("Workspace/App/App.csproj", "update", "Example.Package", "2.0.0", TestContext.Current.CancellationToken);
            Assert.True(applied.Applied);
            Assert.Contains("2.0.0", await File.ReadAllTextAsync(Path.Combine(workspace, "App", "App.csproj"), TestContext.Current.CancellationToken));

            var architecture = await module.Architecture.InspectAsync("Workspace/App/App.csproj", TestContext.Current.CancellationToken);
            Assert.Contains(architecture.CallEdges, edge => edge.Caller.Contains("Screen.Show", StringComparison.Ordinal) && edge.Callee.Contains("Service.Work", StringComparison.Ordinal));
            var boundary = await module.Architecture.CheckBoundariesAsync("Workspace/App/App.csproj",
                [new ArchitectureBoundaryRule("Foundation.UI", "Foundation.Core")], TestContext.Current.CancellationToken);
            Assert.False(boundary.Success);
            Assert.Single(boundary.Violations);

            var quality = await module.Quality.ScanAsync("Workspace/App/App.csproj", TestContext.Current.CancellationToken);
            Assert.True(File.Exists(quality.SarifPath));
            Assert.Contains("2.1.0", await File.ReadAllTextAsync(quality.SarifPath, TestContext.Current.CancellationToken));

            var status = await module.SourceControl.StatusAsync(
                "Workspace/App/App.csproj",
                TestContext.Current.CancellationToken);
            Assert.True(status.Success, status.Output);
            Assert.Contains("##", status.Output);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeliveryFoundationTools_AreCatalogedMcpReadyAndMutationsProtected()
    {
        var names = new[]
        {
            AliCapabilityCatalog.DotNetDependencyInspectName, AliCapabilityCatalog.DotNetDependencyPreviewName,
            AliCapabilityCatalog.DotNetDependencyApplyName, AliCapabilityCatalog.GitStatusName,
            AliCapabilityCatalog.GitDiffName, AliCapabilityCatalog.GitHistoryName, AliCapabilityCatalog.GitBlameName,
            AliCapabilityCatalog.GitCreateBranchName, AliCapabilityCatalog.GitCommitName, AliCapabilityCatalog.GitPushName,
            AliCapabilityCatalog.ArchitectureInspectName, AliCapabilityCatalog.ArchitectureCheckName,
            AliCapabilityCatalog.DotNetQualityScanName
        };
        foreach (var name in names)
        {
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == name);
            Assert.Contains(Ali.Modules.Mcp.McpServerToolCatalog.CreateDefaultPolicies(), policy => policy.Name == name && !policy.Enabled);
        }
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetDependencyApplyName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.GitPushName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.ArchitectureInspectName));
    }

    private static AliWorkstationFileAccess CreateAccess(string dataRoot, string workspace)
    {
        var permissions = new AgentToolPermissionStore(dataRoot);
        var store = new AliWorkstationFileStore([new AliWorkstationFileMount("Workspace", workspace)], Path.Combine(dataRoot, "trash"));
        return new AliWorkstationFileAccess(store, new AgentFileActionAuditStore(dataRoot, null), permissions);
    }

    private static async Task InitializeRepositoryAsync(string workspace)
    {
        var provider = AliGitProviderIdentity.Pin();
        var startInfo = provider.CreateStartInfo(workspace);
        startInfo.ArgumentList.Add("init");
        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git did not start for the isolated test repository.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var output = string.Join(
            Environment.NewLine,
            await standardOutput,
            await standardError);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git init failed for the isolated test repository: {output}");
        }
    }
}
