using System.Security.Cryptography;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coding.RoslynActions;

namespace Ali.Framework.Tests;

public sealed class RoslynStagedBuildVerifierTests
{
    [Fact]
    public async Task SelectsOnlyDependencyRelatedTestsAndSmallestBuildRoot()
    {
        using var graph = new StagedProjectGraph();
        var runner = new FakeRunner((request, _) => Task.FromResult(
            request.Operation == AliRoslynStagedRunnerOperation.RestoreAndBuild
                ? SuccessfulBuild("restore/build output")
                : SuccessfulTests(total: 3, passed: 2, skipped: 1, "test output")));
        var verifier = new AliRoslynStagedBuildVerifier(runner, TimeSpan.FromSeconds(5));

        var receipt = await verifier.VerifyAsync(
            graph.Path,
            "Graph.csproj",
            [new("roslyn-project-core", "Affected/Core.csproj")],
            "Release",
            TestContext.Current.CancellationToken);

        Assert.True(receipt.Success, receipt.Summary);
        Assert.Equal("verified", receipt.OutcomeCode);
        Assert.Equal(6, receipt.EvaluatedProjectCount);
        Assert.Equal(1, receipt.AffectedProjectCount);
        Assert.Equal(1, receipt.PlannedBuildTargetCount);
        Assert.Equal(1, receipt.CompletedBuildTargetCount);
        Assert.Equal(1, receipt.SelectedTestProjectCount);
        Assert.Equal(1, receipt.CompletedTestProjectCount);
        Assert.Equal(3, receipt.TotalTests);
        Assert.Equal(2, receipt.PassedTests);
        Assert.Equal(1, receipt.SkippedTests);
        Assert.Collection(
            runner.Requests,
            request =>
            {
                Assert.Equal(AliRoslynStagedRunnerOperation.RestoreAndBuild, request.Operation);
                Assert.Equal("Related/RuntimeVerifier.csproj", request.TargetRelativePath);
                Assert.Equal("Release", request.Configuration);
            },
            request =>
            {
                Assert.Equal(AliRoslynStagedRunnerOperation.TestNoBuildNoRestore, request.Operation);
                Assert.Equal("Related/RuntimeVerifier.csproj", request.TargetRelativePath);
                Assert.Equal("Release", request.Configuration);
            });
        Assert.DoesNotContain(runner.Requests, request =>
            request.TargetRelativePath.Contains("Unrelated", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(runner.Requests, request =>
            request.TargetRelativePath.Contains("TestsButNotMarked", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(graph.Path, receipt.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                Path.Combine(graph.Path, "Graph.csproj")))),
            receipt.TargetSha256);
        Assert.All(receipt.Steps, step =>
        {
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                    Path.Combine(graph.Path, step.TargetRelativePath)))),
                step.TargetSha256);
            Assert.Equal(64, step.OutputSha256.Length);
            Assert.True(step.OutputCharacters > 0);
        });
    }

    [Fact]
    public async Task RejectsTargetEscapeBeforeCallingRunner()
    {
        using var graph = new StagedProjectGraph();
        var runner = new FakeRunner(DefaultSuccess);
        var verifier = new AliRoslynStagedBuildVerifier(runner);

        var receipt = await verifier.VerifyAsync(
            graph.Path,
            "../outside.csproj",
            [new("roslyn-project-core", "Affected/Core.csproj")],
            "Release",
            TestContext.Current.CancellationToken);

        Assert.False(receipt.Success);
        Assert.Equal("verification-failed-closed", receipt.OutcomeCode);
        Assert.Empty(runner.Requests);
        Assert.Empty(receipt.Steps);
    }

    [Fact]
    public async Task RejectsConfigurationPropertyInjectionBeforeCallingRunner()
    {
        using var graph = new StagedProjectGraph();
        var runner = new FakeRunner(DefaultSuccess);
        var verifier = new AliRoslynStagedBuildVerifier(runner);

        var receipt = await verifier.VerifyAsync(
            graph.Path,
            "Graph.csproj",
            [new("roslyn-project-core", "Affected/Core.csproj")],
            "Release;InjectedProperty=true",
            TestContext.Current.CancellationToken);

        Assert.False(receipt.Success);
        Assert.Equal("<invalid>", receipt.Configuration);
        Assert.Equal("verification-failed-closed", receipt.OutcomeCode);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task PropagatesBuildFailureWithoutCopyingRawOutputIntoReceipt()
    {
        using var graph = new StagedProjectGraph();
        const string sensitiveOutput = "failed at C:\\private\\checkout\\Secret.cs";
        var runner = new FakeRunner((request, _) => Task.FromResult(
            request.Operation == AliRoslynStagedRunnerOperation.RestoreAndBuild
                ? new(false, 1, false, 25, sensitiveOutput)
                : SuccessfulTests(1, 1, 0, "must not run")));
        var verifier = new AliRoslynStagedBuildVerifier(runner);

        var receipt = await verifier.VerifyAsync(
            graph.Path,
            "Graph.csproj",
            [new("roslyn-project-core", "Affected/Core.csproj")],
            "Release",
            TestContext.Current.CancellationToken);

        Assert.False(receipt.Success);
        Assert.Equal("build-failed", receipt.OutcomeCode);
        Assert.Equal(0, receipt.CompletedBuildTargetCount);
        Assert.Equal(0, receipt.CompletedTestProjectCount);
        var step = Assert.Single(receipt.Steps);
        Assert.False(step.Success);
        Assert.Equal(sensitiveOutput.Length, step.OutputCharacters);
        Assert.Equal(64, step.OutputSha256.Length);
        Assert.DoesNotContain(sensitiveOutput, receipt.ToString(), StringComparison.Ordinal);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task FailsClosedWhenRunnerThrows()
    {
        using var graph = new StagedProjectGraph();
        var runner = new FakeRunner((_, _) =>
            throw new IOException("runner transport failed at C:\\private\\checkout"));
        var verifier = new AliRoslynStagedBuildVerifier(runner);

        var receipt = await verifier.VerifyAsync(
            graph.Path,
            "Graph.csproj",
            [new("roslyn-project-core", "Affected/Core.csproj")],
            "Release",
            TestContext.Current.CancellationToken);

        Assert.False(receipt.Success);
        Assert.Equal("verification-failed-closed", receipt.OutcomeCode);
        Assert.Empty(receipt.Steps);
        Assert.Single(runner.Requests);
        Assert.DoesNotContain("private", receipt.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailsClosedWhenExternalStepMutatesABoundStagedInput()
    {
        using var graph = new StagedProjectGraph();
        var runner = new FakeRunner(async (request, cancellationToken) =>
        {
            await File.AppendAllTextAsync(
                Path.Combine(request.StagedRoot, request.TargetRelativePath),
                Environment.NewLine + "<!-- mutated by external build -->",
                cancellationToken);
            return request.Operation == AliRoslynStagedRunnerOperation.RestoreAndBuild
                ? SuccessfulBuild("reported success after mutation")
                : SuccessfulTests(1, 1, 0, "must not run");
        });
        var verifier = new AliRoslynStagedBuildVerifier(runner);

        var receipt = await verifier.VerifyAsync(
            graph.Path,
            "Graph.csproj",
            [new("roslyn-project-core", "Affected/Core.csproj")],
            "Release",
            TestContext.Current.CancellationToken);

        Assert.False(receipt.Success);
        Assert.Equal("verification-failed-closed", receipt.OutcomeCode);
        Assert.Single(runner.Requests);
        Assert.Empty(receipt.Steps);
    }

    [Fact]
    public async Task ProductionRunnerRejectsMutatedPinnedDotNetHostBeforeLaunch()
    {
        using var graph = new StagedProjectGraph();
        var hostPath = Path.Combine(graph.Path, "fake-dotnet.exe");
        await File.WriteAllTextAsync(
            hostPath,
            "first host bytes",
            TestContext.Current.CancellationToken);
        var pinnedHost = AliExactDotNetHost.Capture(hostPath);
        await File.WriteAllTextAsync(
            hostPath,
            "different host bytes",
            TestContext.Current.CancellationToken);
        var request = new AliRoslynStagedRunnerRequest(
            AliRoslynStagedRunnerOperation.RestoreAndBuild,
            graph.Path,
            "Affected/Core.csproj",
            "Release",
            TimeSpan.FromSeconds(5),
            pinnedHost);
        var runner = new AliRoslynStagedBuildProcessRunner();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailsClosedWhenSelectedTestDiscoversZeroTests()
    {
        using var graph = new StagedProjectGraph();
        var runner = new FakeRunner((request, _) => Task.FromResult(
            request.Operation == AliRoslynStagedRunnerOperation.RestoreAndBuild
                ? SuccessfulBuild("built")
                : SuccessfulTests(0, 0, 0, "no tests")));
        var verifier = new AliRoslynStagedBuildVerifier(runner);

        var receipt = await verifier.VerifyAsync(
            graph.Path,
            "Graph.csproj",
            [new("roslyn-project-core", "Affected/Core.csproj")],
            "Release",
            TestContext.Current.CancellationToken);

        Assert.False(receipt.Success);
        Assert.Equal("zero-tests-discovered", receipt.OutcomeCode);
        Assert.Equal(1, receipt.CompletedBuildTargetCount);
        Assert.Equal(0, receipt.CompletedTestProjectCount);
        Assert.Equal(2, receipt.Steps.Count);
    }

    [Fact]
    public async Task FailsClosedWhenBoundedBuildTimesOut()
    {
        using var graph = new StagedProjectGraph();
        var runner = new FakeRunner((_, _) => Task.FromResult(
            new AliRoslynStagedRunnerResult(false, -1, true, 5_000, "timed out")));
        var verifier = new AliRoslynStagedBuildVerifier(runner);

        var receipt = await verifier.VerifyAsync(
            graph.Path,
            "Graph.csproj",
            [new("roslyn-project-core", "Affected/Core.csproj")],
            "Release",
            TestContext.Current.CancellationToken);

        Assert.False(receipt.Success);
        Assert.Equal("build-failed", receipt.OutcomeCode);
        Assert.True(Assert.Single(receipt.Steps).TimedOut);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task CancelsInjectedRunnerAtVerifierTimeoutAndFailsClosed()
    {
        using var graph = new StagedProjectGraph();
        var runner = new FakeRunner(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The bounded delay unexpectedly completed.");
        });
        var verifier = new AliRoslynStagedBuildVerifier(runner, TimeSpan.FromMilliseconds(50));

        var receipt = await verifier.VerifyAsync(
            graph.Path,
            "Graph.csproj",
            [new("roslyn-project-core", "Affected/Core.csproj")],
            "Release",
            TestContext.Current.CancellationToken);

        Assert.False(receipt.Success);
        Assert.Equal("runner-timeout", receipt.OutcomeCode);
        Assert.Empty(receipt.Steps);
        Assert.Single(runner.Requests);
    }

    private static Task<AliRoslynStagedRunnerResult> DefaultSuccess(
        AliRoslynStagedRunnerRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            request.Operation == AliRoslynStagedRunnerOperation.RestoreAndBuild
                ? SuccessfulBuild("built")
                : SuccessfulTests(1, 1, 0, "tested"));
    }

    private static AliRoslynStagedRunnerResult SuccessfulBuild(string output) =>
        new(true, 0, false, 10, output);

    private static AliRoslynStagedRunnerResult SuccessfulTests(
        int total,
        int passed,
        int skipped,
        string output) =>
        new(true, 0, false, 10, output, total, passed, FailedTests: 0, SkippedTests: skipped);

    private sealed class FakeRunner : IAliRoslynStagedBuildRunner
    {
        private readonly Func<AliRoslynStagedRunnerRequest, CancellationToken, Task<AliRoslynStagedRunnerResult>> _run;

        public FakeRunner(
            Func<AliRoslynStagedRunnerRequest, CancellationToken, Task<AliRoslynStagedRunnerResult>> run)
        {
            _run = run;
        }

        public AliRoslynStagedToolsetIdentity ToolsetIdentity { get; } =
            new("fake-msbuild", "1.0.0", new string('A', 64));

        public List<AliRoslynStagedRunnerRequest> Requests { get; } = [];

        public Task<AliRoslynStagedRunnerResult> RunAsync(
            AliRoslynStagedRunnerRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _run(request, cancellationToken);
        }
    }

    private sealed class StagedProjectGraph : IDisposable
    {
        public StagedProjectGraph()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "AliRoslynStagedBuildVerifierTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            WriteProject(
                "Graph.csproj",
                isTestProject: false,
                "Affected/Core.csproj",
                "Related/RuntimeVerifier.csproj",
                "Misleading/TestsButNotMarked.csproj",
                "Unrelated/Other.csproj",
                "Unrelated/UnrelatedTests.csproj");
            WriteProject("Affected/Core.csproj", isTestProject: false);
            WriteProject(
                "Related/RuntimeVerifier.csproj",
                isTestProject: true,
                "../Affected/Core.csproj");
            WriteProject(
                "Misleading/TestsButNotMarked.csproj",
                isTestProject: false,
                "../Affected/Core.csproj");
            WriteProject("Unrelated/Other.csproj", isTestProject: false);
            WriteProject(
                "Unrelated/UnrelatedTests.csproj",
                isTestProject: true,
                "Other.csproj");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }

        private void WriteProject(
            string relativePath,
            bool isTestProject,
            params string[] projectReferences)
        {
            var fullPath = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            var isTestProperty = isTestProject
                ? "    <IsTestProject>true</IsTestProject>" + Environment.NewLine
                : string.Empty;
            var references = projectReferences.Length == 0
                ? string.Empty
                : "  <ItemGroup>" + Environment.NewLine
                    + string.Join(
                        Environment.NewLine,
                        projectReferences.Select(reference => $"    <ProjectReference Include=\"{reference}\" />"))
                    + Environment.NewLine
                    + "  </ItemGroup>" + Environment.NewLine;
            File.WriteAllText(
                fullPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\">" + Environment.NewLine
                + "  <PropertyGroup>" + Environment.NewLine
                + "    <TargetFramework>net10.0</TargetFramework>" + Environment.NewLine
                + "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>" + Environment.NewLine
                + isTestProperty
                + "  </PropertyGroup>" + Environment.NewLine
                + references
                + "</Project>" + Environment.NewLine);
        }
    }
}
