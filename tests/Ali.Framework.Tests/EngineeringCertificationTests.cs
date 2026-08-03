using System.Net;
using System.Text;
using Ali.Modules.EngineeringCertification;

namespace Ali.Framework.Tests;

public sealed class EngineeringCertificationTests
{
    [Fact]
    public void CurrentCatalog_IsVersionedStableAndContainsOneHundredTasks()
    {
        var first = EngineeringCertificationCatalog.CreateCurrent();
        var second = EngineeringCertificationCatalog.CreateCurrent();

        Assert.Equal(EngineeringCertificationCatalog.CurrentVersion, first.Version);
        Assert.Equal(100, first.Tasks.Count);
        Assert.Equal(100, first.Tasks.Select(task => task.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            EngineeringCertificationCatalog.ComputeDigest(first),
            EngineeringCertificationCatalog.ComputeDigest(second));
        Assert.All(first.Tasks, task =>
        {
            Assert.Equal(4, task.RequiredToolIds.Count);
            Assert.Equal(4, task.FixtureFiles.Count);
            Assert.True(task.CompletionBudget > TimeSpan.Zero);
            Assert.True(task.TokenBudget > 0);
        });
        Assert.Equal(20, first.Tasks.Count(task => task.InjectFirstRequiredToolFailure));
    }

    [Fact]
    public void SuiteValidation_RejectsOutOfRangeAndDuplicateTaskSets()
    {
        var suite = EngineeringCertificationCatalog.CreateCurrent();

        Assert.Throws<InvalidDataException>(() =>
            new EngineeringCertificationSuite(suite.Version, suite.Tasks.Take(99).ToArray()).Validate());
        Assert.Throws<InvalidDataException>(() =>
            new EngineeringCertificationSuite(
                suite.Version,
                suite.Tasks.Take(99).Append(suite.Tasks[0]).ToArray()).Validate());
    }

    [Fact]
    public async Task CandidateDiscovery_UsesRuntimeInventoryWithoutModelNameBranches()
    {
        var handler = new DelegateHandler(request =>
        {
            Assert.Equal("http://127.0.0.1:5555/v1/models", request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"id\":\"model-z\"},{\"id\":\"deepseek-coder-v2\"},{\"id\":\"model-z\"}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        using var http = new HttpClient(handler);
        var source = new OpenAiCertificationCandidateSource(http);

        var result = await source.DiscoverAsync(
            [new ConfiguredCertificationRuntime(
                "runtime-a",
                new Uri("http://127.0.0.1:5555/v1/"),
                Enabled: true,
                AllowPrivateLanEndpoint: false,
                AllowRemoteHttpsEndpoint: false)],
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Issues);
        Assert.Equal(["deepseek-coder-v2", "model-z"], result.Candidates.Select(item => item.ModelId));
        Assert.All(result.Candidates, candidate => Assert.Equal("runtime-a", candidate.RuntimeId));
        Assert.Equal(2, result.Candidates.Select(candidate => candidate.BindingDigest).Distinct().Count());
    }

    [Fact]
    public async Task CandidateDiscovery_ReportsDisabledOrUnreachableRuntimesWithoutInventingCandidates()
    {
        using var http = new HttpClient(new DelegateHandler(_ =>
            throw new HttpRequestException("runtime unavailable")));
        var source = new OpenAiCertificationCandidateSource(http);

        var result = await source.DiscoverAsync(
            [
                new ConfiguredCertificationRuntime(
                    "disabled",
                    new Uri("http://127.0.0.1:5001/v1/"),
                    Enabled: false,
                    AllowPrivateLanEndpoint: false,
                    AllowRemoteHttpsEndpoint: false),
                new ConfiguredCertificationRuntime(
                    "unreachable",
                    new Uri("http://127.0.0.1:5002/v1/"),
                    Enabled: true,
                    AllowPrivateLanEndpoint: false,
                    AllowRemoteHttpsEndpoint: false)
            ],
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Candidates);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("unreachable", issue.RuntimeId);
        Assert.Contains("runtime unavailable", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scoring_UsesTypedReceiptsAndDisclosesEveryRequiredComponent()
    {
        var task = EngineeringCertificationCatalog.CreateCurrent().Tasks[0];
        var agent = new EngineeringAgentExecutionReceipt(
            Completed: true,
            SelectedPrimitiveId: task.ExpectedPrimitiveId,
            ToolIds: task.RequiredToolIds,
            RecoveredAfterInjectedFailure: true,
            InputTokens: 100,
            OutputTokens: 50,
            RawEvidence: "typed agent trace");
        var baseline = new EngineeringVerificationBaseline(1, 2);
        var verification = new EngineeringVerificationReceipt(
            BuildSucceeded: true,
            UnitTestsSucceeded: true,
            RoslynErrorCount: 0,
            RoslynWarningCount: 1,
            HallucinatedApiDiagnostics: [],
            RawEvidence: "typed verifier trace");

        var score = EngineeringCertificationScoring.Score(
            task,
            agent,
            baseline,
            verification,
            TimeSpan.FromSeconds(1));

        Assert.Equal(100m, score.Percent);
        Assert.Equal(
            [
                "build-success",
                "completion-time",
                "correct-engineering-primitive",
                "correct-tool-selection",
                "failure-recovery",
                "no-hallucinated-apis",
                "no-roslyn-diagnostics-introduced",
                "tokens-consumed",
                "unit-tests-success"
            ],
            score.Components.Select(component => component.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Scoring_FailsMissingTokenToolApiAndDiagnosticEvidenceMechanically()
    {
        var task = EngineeringCertificationCatalog.CreateCurrent().Tasks[1];
        var score = EngineeringCertificationScoring.Score(
            task,
            new EngineeringAgentExecutionReceipt(true, "wrong-primitive", [], null, null, null, ""),
            new EngineeringVerificationBaseline(0, 0),
            new EngineeringVerificationReceipt(false, false, 2, 1, ["error CS1061"], ""),
            task.CompletionBudget + TimeSpan.FromSeconds(1));

        Assert.Equal(0m, score.Percent);
        Assert.DoesNotContain(score.Components, component =>
            component.Id == "failure-recovery" && component.Applicable);
    }

    [Fact]
    public async Task Storage_MaterializesOnlyAnIsolatedFixtureAndBoundsRawEvidence()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var suite = EngineeringCertificationCatalog.CreateCurrent();
            var candidate = Candidate();
            var request = Request("isolation-run", suite);
            var storage = new EngineeringCertificationRunStorage(root);
            var digest = EngineeringCertificationCatalog.ComputeDigest(suite);
            var initialization = await storage.InitializeRunAsync(
                request,
                digest,
                new EngineeringCandidateDiscoveryResult([candidate], []),
                TestContext.Current.CancellationToken);
            var runDirectory = initialization.RunDirectory;
            var task = suite.Tasks[0];
            var workspace = await storage.PrepareIsolatedWorkspaceAsync(
                runDirectory,
                candidate,
                task,
                1,
                TestContext.Current.CancellationToken);

            Assert.StartsWith(runDirectory, workspace, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(workspace, "Fixture.csproj")));
            Assert.True(File.Exists(Path.Combine(workspace, ".certification-fixture.sha256")));
            var oversized = new string('x', EngineeringCertificationRunStorage.MaximumRawEvidenceCharacters + 200);
            var evidence = Evidence(request, digest, candidate, task, workspace, oversized);
            var stored = await storage.SaveEvidenceAsync(
                runDirectory,
                candidate,
                task,
                evidence,
                TestContext.Current.CancellationToken);

            Assert.Equal(EngineeringCertificationRunStorage.MaximumRawEvidenceCharacters, stored.Agent.RawEvidence.Length);
            Assert.True(new FileInfo(stored.RawEvidencePath).Length > 0);
            Assert.NotNull(await storage.TryReadEvidenceAsync(
                runDirectory,
                candidate,
                task,
                digest,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Runner_ExecutesSameHundredTasksThenResumesWithoutReexecution()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var suite = EngineeringCertificationCatalog.CreateCurrent();
            var loop = new RecordingAgentLoop();
            var runner = new EngineeringCertificationRunner(
                new FixedCandidateSource(Candidate()),
                loop,
                new PassingVerifier(),
                new EngineeringCertificationRunStorage(root));
            var request = Request("resume-run", suite);

            var first = await runner.RunAsync(request, TestContext.Current.CancellationToken);
            var second = await runner.RunAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(100, first.ExecutedTasks);
            Assert.Equal(0, first.ResumedTasks);
            Assert.Equal(0, second.ExecutedTasks);
            Assert.Equal(100, second.ResumedTasks);
            Assert.Equal(100, loop.Requests.Count);
            Assert.Equal(suite.Tasks.Select(task => task.Id), loop.Requests.Select(item => item.Task.Id));
            Assert.Single(first.Comparison.Candidates);
            Assert.Equal(100, first.Comparison.Candidates[0].CompletedTasks);
            Assert.True(File.Exists(Path.Combine(first.RunDirectory, "comparison.json")));
            Assert.True(File.Exists(Path.Combine(first.RunDirectory, "comparison.md")));
            Assert.Contains("not user-request routing rules", first.Comparison.Disclaimer, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Runner_HonorsCallerCancellationBeforeModelExecution()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var loop = new RecordingAgentLoop();
            var runner = new EngineeringCertificationRunner(
                new FixedCandidateSource(Candidate()),
                loop,
                new PassingVerifier(),
                new EngineeringCertificationRunStorage(root));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runner.RunAsync(
                    Request("cancel-run", EngineeringCertificationCatalog.CreateCurrent()),
                    cancellation.Token));
            Assert.Empty(loop.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Resume_RefusesCandidateInventoryDriftForTheSameRunId()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var suite = EngineeringCertificationCatalog.CreateCurrent();
            var request = Request("inventory-drift-run", suite);
            var firstRunner = new EngineeringCertificationRunner(
                new FixedCandidateSource(Candidate()),
                new RecordingAgentLoop(),
                new PassingVerifier(),
                new EngineeringCertificationRunStorage(root));
            await firstRunner.RunAsync(request, TestContext.Current.CancellationToken);
            var changed = Candidate() with
            {
                CandidateId = "candidate-b",
                ModelId = "model-b",
                BindingDigest = new string('b', 64)
            };
            var resumedRunner = new EngineeringCertificationRunner(
                new FixedCandidateSource(changed),
                new RecordingAgentLoop(),
                new PassingVerifier(),
                new EngineeringCertificationRunStorage(root));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                resumedRunner.RunAsync(request, TestContext.Current.CancellationToken));

            Assert.Contains("candidate inventory changed", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DotNetVerifier_CountsDistinctRoslynCompilerDiagnostics()
    {
        var result = DotNetEngineeringCertificationVerifier.CountRoslynDiagnostics("""
            Subject.cs(1,1): error CS0103: name missing
            Subject.cs(1,1): error CS0103: name missing
            Subject.cs(2,1): warning CS8600: nullable conversion
            1 Error(s)
            """);

        Assert.Equal(1, result.Errors);
        Assert.Equal(1, result.Warnings);
    }

    [Fact]
    public async Task DotNetVerifier_UsesRealReleaseBuildAndUnitTestExitCodes()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var suite = EngineeringCertificationCatalog.CreateCurrent();
            var task = suite.Tasks[0];
            var candidate = Candidate();
            var request = Request("real-verifier-run", suite);
            var storage = new EngineeringCertificationRunStorage(root);
            var digest = EngineeringCertificationCatalog.ComputeDigest(suite);
            var initialization = await storage.InitializeRunAsync(
                request,
                digest,
                new EngineeringCandidateDiscoveryResult([candidate], []),
                TestContext.Current.CancellationToken);
            var workspace = await storage.PrepareIsolatedWorkspaceAsync(
                initialization.RunDirectory,
                candidate,
                task,
                1,
                TestContext.Current.CancellationToken);
            var verifier = new DotNetEngineeringCertificationVerifier();

            var baseline = await verifier.CaptureBaselineAsync(
                task,
                workspace,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "Subject.cs"),
                "namespace CertificationFixture; public static class Calculator { public static int AddOffset(int value) => value + 1; }",
                TestContext.Current.CancellationToken);
            var result = await verifier.VerifyAsync(
                task,
                workspace,
                TestContext.Current.CancellationToken);

            Assert.True(baseline.RoslynErrorCount == 0, baseline.RawEvidence);
            Assert.True(result.BuildSucceeded, result.RawEvidence);
            Assert.True(result.UnitTestsSucceeded, result.RawEvidence);
            Assert.Empty(result.HallucinatedApiDiagnostics);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static EngineeringCertificationCandidate Candidate() =>
        new(
            "candidate-a",
            "runtime-a",
            new Uri("http://127.0.0.1:5555/v1/"),
            "model-a",
            new string('a', 64));

    private static EngineeringCertificationRunRequest Request(
        string runId,
        EngineeringCertificationSuite suite) =>
        new(
            runId,
            suite,
            [new ConfiguredCertificationRuntime(
                "runtime-a",
                new Uri("http://127.0.0.1:5555/v1/"),
                Enabled: true,
                AllowPrivateLanEndpoint: false,
                AllowRemoteHttpsEndpoint: false)]);

    private static EngineeringCertificationTaskEvidence Evidence(
        EngineeringCertificationRunRequest request,
        string digest,
        EngineeringCertificationCandidate candidate,
        EngineeringCertificationTask task,
        string workspace,
        string raw)
    {
        var agent = new EngineeringAgentExecutionReceipt(
            true,
            task.ExpectedPrimitiveId,
            task.RequiredToolIds,
            task.InjectFirstRequiredToolFailure ? true : null,
            10,
            10,
            raw);
        var baseline = new EngineeringVerificationBaseline(0, 0);
        var verification = new EngineeringVerificationReceipt(true, true, 0, 0, [], raw);
        return new EngineeringCertificationTaskEvidence(
            request.Suite.Version,
            digest,
            request.RunId,
            candidate.CandidateId,
            candidate.BindingDigest,
            candidate.ModelId,
            task.Id,
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            1,
            agent,
            baseline,
            verification,
            EngineeringCertificationScoring.Score(task, agent, baseline, verification, TimeSpan.FromMilliseconds(1)),
            workspace,
            string.Empty);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "AliEngineeringCertificationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedCandidateSource(EngineeringCertificationCandidate candidate)
        : IEngineeringCertificationCandidateSource
    {
        public Task<EngineeringCandidateDiscoveryResult> DiscoverAsync(
            IReadOnlyList<ConfiguredCertificationRuntime> runtimes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new EngineeringCandidateDiscoveryResult([candidate], []));
        }
    }

    private sealed class RecordingAgentLoop : IAuthoritativeEngineeringAgentLoop
    {
        internal List<EngineeringAgentExecutionRequest> Requests { get; } = [];

        public Task<EngineeringAgentExecutionReceipt> ExecuteAsync(
            EngineeringAgentExecutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new EngineeringAgentExecutionReceipt(
                true,
                request.Task.ExpectedPrimitiveId,
                request.Task.RequiredToolIds,
                request.InjectFirstRequiredToolFailure ? true : null,
                100,
                50,
                "typed agent trace"));
        }
    }

    private sealed class PassingVerifier : IEngineeringCertificationVerifier
    {
        public Task<EngineeringVerificationBaseline> CaptureBaselineAsync(
            EngineeringCertificationTask task,
            string workspacePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new EngineeringVerificationBaseline(0, 0));
        }

        public Task<EngineeringVerificationReceipt> VerifyAsync(
            EngineeringCertificationTask task,
            string workspacePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new EngineeringVerificationReceipt(true, true, 0, 0, [], "typed verifier trace"));
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handler(request));
        }
    }
}
