using Ali.Modules.Coding;
using Ali.Modules.Coding.Architecture;
using Ali.Modules.Coding.Delivery;
using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

[Collection(ProcessEnvironmentIntegrationCollection.Name)]
public sealed class DotNetCompleteDeliveryTests
{
    [Fact]
    public async Task ArchitectureFailureStopsBeforeEveryLaterDeliveryStage()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliDeliveryArchitectureFailure", Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var access = CreateAccess(root, workspace);
            await access.Store.WriteAsync(
                "Workspace/App/App.csproj",
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""",
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync(
                "Workspace/App/Program.cs",
                """namespace App; public static class Program { public static void Main() { } }""",
                TestContext.Current.CancellationToken);
            await using var module = new AliCodingModule(access);
            var delivery = new AliAutonomousDelivery(
                module.Architecture,
                module.Quality,
                module.EngineeringLoop,
                module.Tools,
                module.Verification,
                module.Release,
                (_, _) => Task.FromResult(new ArchitectureInspectionResult(
                    false,
                    "Architecture inspection failed.",
                    [],
                    [],
                    [],
                    [])));

            var result = await delivery.VerifyDeliveryAsync(
                "Workspace/App/App.csproj",
                null,
                "Release",
                verifyApplication: true,
                publishRelease: true,
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            var stage = Assert.Single(result.Stages);
            Assert.Equal("architecture", stage.Name);
            Assert.False(stage.Success);
            Assert.False(Directory.Exists(Path.Combine(workspace, "App", ".ali", "quality")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PerformanceApplicationReleaseReportAndDelivery_RunEndToEnd()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliCompleteDelivery", Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var access = CreateAccess(root, workspace);
            await access.Store.WriteAsync("Workspace/App/App.csproj",
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""",
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/App/Program.cs",
                """namespace App; public static class Calculator { public static int Add(int a, int b) => a + b; } public static class Program { public static void Main() { System.Console.WriteLine(Calculator.Add(2, 2)); System.Threading.Thread.Sleep(2500); } }""",
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Tests/Tests.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../App/App.csproj" /><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" /><PackageReference Include="xunit.v3" Version="3.2.2" /><PackageReference Include="xunit.runner.visualstudio" Version="3.1.5"><PrivateAssets>all</PrivateAssets></PackageReference></ItemGroup>
                </Project>
                """, TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/Tests/Checks.cs",
                """public sealed class Checks { [Xunit.Fact] public void Adds() => Xunit.Assert.Equal(4, App.Calculator.Add(2, 2)); }""",
                TestContext.Current.CancellationToken);
            await using var module = new AliCodingModule(access);

            var build = await module.Tools.BuildAsync("Workspace/App/App.csproj", "Release", TestContext.Current.CancellationToken);
            Assert.True(build.Success, build.Output);
            var performance = await module.Performance.MeasureAsync("Workspace/App/App.csproj", "Release", 2, TestContext.Current.CancellationToken);
            Assert.True(performance.Success);
            Assert.Equal(2, performance.Samples.Count);
            var comparison = await module.Performance.CompareAsync("Workspace/App/App.csproj", performance.EvidencePath, performance.EvidencePath, TestContext.Current.CancellationToken);
            Assert.True(comparison.Success);
            Assert.Equal(0, comparison.PercentChange);

            var running = await module.Tools.RunAsync("Workspace/App/App.csproj", "Release", TestContext.Current.CancellationToken);
            Assert.True(running.Success, running.Summary);
            var trace = await module.Performance.CaptureTraceAsync("Workspace/App/App.csproj", running.ProcessId!.Value, 1, TestContext.Current.CancellationToken);
            Assert.True(trace.Success);
            Assert.True(trace.TraceSizeBytes > 0);
            Assert.True(File.Exists(trace.TracePath));

            var application = await module.Verification.SmokeTestAsync("Workspace/App/App.csproj", "Release", null, TestContext.Current.CancellationToken);
            Assert.True(application.Success, application.Output);
            Assert.Contains("4", application.Output);

            var report = await module.Release.GenerateArchitectureReportAsync("Workspace/App/App.csproj", TestContext.Current.CancellationToken);
            Assert.True(File.Exists(report.ReportPath));
            Assert.Contains("Architecture Report", await File.ReadAllTextAsync(report.ReportPath, TestContext.Current.CancellationToken));

            var release = await module.Release.PublishAsync("Workspace/App/App.csproj", "win-x64", false, TestContext.Current.CancellationToken);
            Assert.True(release.Success, release.Output);
            Assert.True(File.Exists(release.ManifestPath));
            Assert.NotEmpty(release.Files);

            var delivery = await module.Delivery.VerifyDeliveryAsync("Workspace/App/App.csproj", "Workspace/Tests/Tests.csproj", "Release",
                verifyApplication: true, publishRelease: false, TestContext.Current.CancellationToken);
            Assert.True(delivery.Success, string.Join(Environment.NewLine, delivery.Stages.Select(stage => stage.Evidence)));
            Assert.Collection(delivery.Stages,
                stage => Assert.Equal("architecture", stage.Name), stage => Assert.Equal("quality", stage.Name),
                stage => Assert.Equal("build-and-test", stage.Name), stage => Assert.Equal("application-smoke", stage.Name));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompleteDeliveryTools_AreMcpReadyAndRiskClassified()
    {
        var names = new[]
        {
            AliCapabilityCatalog.DotNetPerformanceMeasureName, AliCapabilityCatalog.DotNetPerformanceCompareName,
            AliCapabilityCatalog.DotNetPerformanceTraceName,
            AliCapabilityCatalog.DotNetApplicationVerifyName, AliCapabilityCatalog.DotNetReleasePublishName,
            AliCapabilityCatalog.DotNetArchitectureReportName, AliCapabilityCatalog.DotNetDeliveryVerifyName
        };
        foreach (var name in names)
        {
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == name);
            Assert.Contains(Ali.Modules.Mcp.McpServerToolCatalog.CreateDefaultPolicies(), policy => policy.Name == name && !policy.Enabled);
        }
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetPerformanceMeasureName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetPerformanceTraceName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetApplicationVerifyName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetReleasePublishName));
        Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetDeliveryVerifyName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetPerformanceCompareName));
        Assert.False(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetArchitectureReportName));
    }

    [Fact]
    public async Task ApplicationVerification_LaunchesRealWpfWindowAndCapturesScreenshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliWpfVerification", Guid.NewGuid().ToString("N"));
        try
        {
            var access = CreateAccess(root, Path.Combine(root, "workspace"));
            await access.Store.WriteAsync("Workspace/DesktopApp/DesktopApp.csproj",
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>""", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/DesktopApp/App.xaml",
                """<Application x:Class="DesktopApp.App" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" StartupUri="MainWindow.xaml" />""", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/DesktopApp/App.xaml.cs",
                """namespace DesktopApp; public partial class App : System.Windows.Application { }""", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/DesktopApp/MainWindow.xaml",
                """<Window x:Class="DesktopApp.MainWindow" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Ali Verification Window" Width="420" Height="240"><Grid Background="#14161A"><TextBlock Foreground="#55E6A5" FontSize="28" HorizontalAlignment="Center" VerticalAlignment="Center">Verified by Ali</TextBlock></Grid></Window>""", TestContext.Current.CancellationToken);
            await access.Store.WriteAsync("Workspace/DesktopApp/MainWindow.xaml.cs",
                """namespace DesktopApp; public partial class MainWindow : System.Windows.Window { public MainWindow() => InitializeComponent(); }""", TestContext.Current.CancellationToken);
            await using var module = new AliCodingModule(access);
            var build = await module.Tools.BuildAsync("Workspace/DesktopApp/DesktopApp.csproj", "Release", TestContext.Current.CancellationToken);
            Assert.True(build.Success, build.Output);
            var verified = await module.Verification.SmokeTestAsync("Workspace/DesktopApp/DesktopApp.csproj", "Release", null, TestContext.Current.CancellationToken);
            Assert.True(verified.Success, verified.Output);
            Assert.NotNull(verified.ScreenshotPath);
            Assert.True(File.Exists(verified.ScreenshotPath));
            Assert.True(new FileInfo(verified.ScreenshotPath).Length > 1000);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static AliWorkstationFileAccess CreateAccess(string dataRoot, string workspace)
    {
        var store = new AliWorkstationFileStore([new AliWorkstationFileMount("Workspace", workspace)], Path.Combine(dataRoot, "trash"));
        return new AliWorkstationFileAccess(store, new AgentFileActionAuditStore(dataRoot, null), new AgentToolPermissionStore(dataRoot));
    }
}
