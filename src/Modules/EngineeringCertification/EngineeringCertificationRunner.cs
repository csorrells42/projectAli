using System.Diagnostics;

namespace Ali.Modules.EngineeringCertification;

/// <summary>
/// Repeatable certification orchestration only. The runner binds candidates, stages isolated
/// fixtures, invokes the authoritative agent loop, and mechanically scores typed evidence.
/// It never interprets task prose, selects a tool, or routes ordinary user requests.
/// </summary>
internal sealed class EngineeringCertificationRunner(
    IEngineeringCertificationCandidateSource candidateSource,
    IAuthoritativeEngineeringAgentLoop authoritativeAgentLoop,
    IEngineeringCertificationVerifier verifier,
    EngineeringCertificationRunStorage storage)
{
    private readonly IEngineeringCertificationCandidateSource _candidateSource = candidateSource
        ?? throw new ArgumentNullException(nameof(candidateSource));
    private readonly IAuthoritativeEngineeringAgentLoop _authoritativeAgentLoop = authoritativeAgentLoop
        ?? throw new ArgumentNullException(nameof(authoritativeAgentLoop));
    private readonly IEngineeringCertificationVerifier _verifier = verifier
        ?? throw new ArgumentNullException(nameof(verifier));
    private readonly EngineeringCertificationRunStorage _storage = storage
        ?? throw new ArgumentNullException(nameof(storage));

    internal async Task<EngineeringCertificationRunResult> RunAsync(
        EngineeringCertificationRunRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate();
        var suiteDigest = EngineeringCertificationCatalog.ComputeDigest(request.Suite);
        var discovery = await _candidateSource.DiscoverAsync(
            request.ConfiguredRuntimes,
            cancellationToken).ConfigureAwait(false);
        var initialization = await _storage.InitializeRunAsync(
            request,
            suiteDigest,
            discovery,
            cancellationToken).ConfigureAwait(false);
        var runDirectory = initialization.RunDirectory;
        discovery = initialization.Discovery;

        var executedTasks = 0;
        var resumedTasks = 0;
        foreach (var candidate in discovery.Candidates)
        {
            foreach (var task in request.Suite.Tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var existing = await _storage.TryReadEvidenceAsync(
                    runDirectory,
                    candidate,
                    task,
                    suiteDigest,
                    cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    resumedTasks++;
                    continue;
                }

                await ExecuteTaskAsync(
                    request,
                    suiteDigest,
                    runDirectory,
                    candidate,
                    task,
                    cancellationToken).ConfigureAwait(false);
                executedTasks++;
            }
        }

        var allEvidence = await _storage.ReadAllEvidenceAsync(
            runDirectory,
            cancellationToken).ConfigureAwait(false);
        var comparison = EngineeringCertificationScoring.BuildComparison(
            request.Suite,
            suiteDigest,
            request.RunId,
            discovery.Candidates,
            discovery.Issues,
            allEvidence,
            DateTimeOffset.UtcNow);
        await WriteReportsAsync(runDirectory, comparison, cancellationToken).ConfigureAwait(false);
        return new EngineeringCertificationRunResult(
            runDirectory,
            comparison,
            executedTasks,
            resumedTasks);
    }

    private async Task ExecuteTaskAsync(
        EngineeringCertificationRunRequest request,
        string suiteDigest,
        string runDirectory,
        EngineeringCertificationCandidate candidate,
        EngineeringCertificationTask task,
        CancellationToken cancellationToken)
    {
        var firstAttempt = _storage.GetNextAttempt(runDirectory, candidate, task);
        Exception? lastFailure = null;
        for (var attempt = firstAttempt; attempt <= request.MaximumAttemptsPerTask; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = DateTimeOffset.UtcNow;
            var elapsed = Stopwatch.StartNew();
            string workspace = string.Empty;
            try
            {
                workspace = await _storage.PrepareIsolatedWorkspaceAsync(
                    runDirectory,
                    candidate,
                    task,
                    attempt,
                    cancellationToken).ConfigureAwait(false);
                using var taskTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                taskTimeout.CancelAfter(task.CompletionBudget);
                var baseline = await _verifier.CaptureBaselineAsync(
                    task,
                    workspace,
                    taskTimeout.Token).ConfigureAwait(false);

                var agentTimer = Stopwatch.StartNew();
                var agent = await _authoritativeAgentLoop.ExecuteAsync(
                    new EngineeringAgentExecutionRequest(
                        request.Suite.Version,
                        suiteDigest,
                        request.RunId,
                        candidate,
                        task,
                        workspace,
                        task.InjectFirstRequiredToolFailure),
                    taskTimeout.Token).ConfigureAwait(false);
                agentTimer.Stop();
                var verification = await _verifier.VerifyAsync(
                    task,
                    workspace,
                    taskTimeout.Token).ConfigureAwait(false);
                var score = EngineeringCertificationScoring.Score(
                    task,
                    agent,
                    baseline,
                    verification,
                    agentTimer.Elapsed);
                var evidence = new EngineeringCertificationTaskEvidence(
                    request.Suite.Version,
                    suiteDigest,
                    request.RunId,
                    candidate.CandidateId,
                    candidate.BindingDigest,
                    candidate.ModelId,
                    task.Id,
                    attempt,
                    started,
                    DateTimeOffset.UtcNow,
                    checked((long)agentTimer.Elapsed.TotalMilliseconds),
                    agent,
                    baseline,
                    verification,
                    score,
                    workspace,
                    RawEvidencePath: string.Empty);
                await _storage.SaveEvidenceAsync(
                    runDirectory,
                    candidate,
                    task,
                    evidence,
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                if (attempt == request.MaximumAttemptsPerTask)
                {
                    await SaveInfrastructureFailureAsync(
                        request,
                        suiteDigest,
                        runDirectory,
                        candidate,
                        task,
                        attempt,
                        started,
                        elapsed.Elapsed,
                        workspace,
                        ex).ConfigureAwait(false);
                    return;
                }
            }
        }

        await SaveInfrastructureFailureAsync(
            request,
            suiteDigest,
            runDirectory,
            candidate,
            task,
            Math.Max(1, request.MaximumAttemptsPerTask),
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            workspace: string.Empty,
            lastFailure ?? new InvalidOperationException(
                "Certification resume exhausted the bounded task attempts before durable evidence was written."))
            .ConfigureAwait(false);
    }

    private async Task SaveInfrastructureFailureAsync(
        EngineeringCertificationRunRequest request,
        string suiteDigest,
        string runDirectory,
        EngineeringCertificationCandidate candidate,
        EngineeringCertificationTask task,
        int attempt,
        DateTimeOffset started,
        TimeSpan elapsed,
        string workspace,
        Exception exception)
    {
        var failureText = $"{exception.GetType().Name}: {exception.Message}";
        var agent = new EngineeringAgentExecutionReceipt(
            Completed: false,
            SelectedPrimitiveId: null,
            ToolIds: [],
            RecoveredAfterInjectedFailure: false,
            InputTokens: null,
            OutputTokens: null,
            RawEvidence: failureText);
        var baseline = new EngineeringVerificationBaseline(0, 0);
        var verification = new EngineeringVerificationReceipt(
            BuildSucceeded: false,
            UnitTestsSucceeded: false,
            RoslynErrorCount: 0,
            RoslynWarningCount: 0,
            HallucinatedApiDiagnostics: [],
            RawEvidence: "Independent verification did not complete because certification infrastructure failed.");
        var score = EngineeringCertificationScoring.Score(task, agent, baseline, verification, elapsed);
        var evidence = new EngineeringCertificationTaskEvidence(
            request.Suite.Version,
            suiteDigest,
            request.RunId,
            candidate.CandidateId,
            candidate.BindingDigest,
            candidate.ModelId,
            task.Id,
            attempt,
            started,
            DateTimeOffset.UtcNow,
            checked((long)elapsed.TotalMilliseconds),
            agent,
            baseline,
            verification,
            score,
            workspace,
            RawEvidencePath: string.Empty);
        await _storage.SaveEvidenceAsync(
            runDirectory,
            candidate,
            task,
            evidence,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WriteReportsAsync(
        string runDirectory,
        EngineeringCertificationComparisonReport comparison,
        CancellationToken cancellationToken)
    {
        await _storage.WriteComparisonJsonAsync(runDirectory, comparison, cancellationToken)
            .ConfigureAwait(false);
        await _storage.WriteComparisonMarkdownAsync(
            runDirectory,
            EngineeringCertificationScoring.RenderComparisonMarkdown(comparison),
            cancellationToken).ConfigureAwait(false);
        foreach (var candidate in comparison.Candidates)
        {
            await _storage.WriteCandidateReportAsync(runDirectory, candidate, cancellationToken)
                .ConfigureAwait(false);
            await _storage.WriteCandidateMarkdownAsync(
                runDirectory,
                candidate,
                EngineeringCertificationScoring.RenderCandidateMarkdown(comparison, candidate),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
