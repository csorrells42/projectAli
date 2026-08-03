namespace Ali.Modules.EngineeringCertification;

internal sealed record EngineeringCertificationSuite(
    string Version,
    IReadOnlyList<EngineeringCertificationTask> Tasks)
{
    internal const int MinimumTaskCount = 100;
    internal const int MaximumTaskCount = 200;

    internal EngineeringCertificationSuite Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Version);
        ArgumentNullException.ThrowIfNull(Tasks);
        if (Tasks.Count is < MinimumTaskCount or > MaximumTaskCount)
        {
            throw new InvalidDataException(
                $"A certification suite must contain {MinimumTaskCount}-{MaximumTaskCount} tasks.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in Tasks)
        {
            task.Validate();
            if (!ids.Add(task.Id))
            {
                throw new InvalidDataException($"Certification task id '{task.Id}' is duplicated.");
            }
        }

        return this;
    }
}

internal sealed record EngineeringCertificationTask(
    string Id,
    string Title,
    string Prompt,
    string ExpectedPrimitiveId,
    IReadOnlyList<string> RequiredToolIds,
    IReadOnlyList<EngineeringFixtureFile> FixtureFiles,
    TimeSpan CompletionBudget,
    long TokenBudget,
    bool InjectFirstRequiredToolFailure)
{
    internal EngineeringCertificationTask Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(Prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExpectedPrimitiveId);
        if (RequiredToolIds.Count == 0
            || RequiredToolIds.Any(string.IsNullOrWhiteSpace)
            || RequiredToolIds.Distinct(StringComparer.Ordinal).Count() != RequiredToolIds.Count)
        {
            throw new InvalidDataException($"Certification task '{Id}' has an invalid required-tool set.");
        }
        if (FixtureFiles.Count == 0)
        {
            throw new InvalidDataException($"Certification task '{Id}' has no isolated fixture files.");
        }
        if (CompletionBudget <= TimeSpan.Zero || TokenBudget <= 0)
        {
            throw new InvalidDataException($"Certification task '{Id}' has an invalid resource budget.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in FixtureFiles)
        {
            file.Validate();
            if (!paths.Add(file.RelativePath))
            {
                throw new InvalidDataException(
                    $"Certification task '{Id}' repeats fixture path '{file.RelativePath}'.");
            }
        }

        return this;
    }
}

internal sealed record EngineeringFixtureFile(string RelativePath, string Content)
{
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RelativePath);
        ArgumentNullException.ThrowIfNull(Content);
        var normalized = RelativePath.Replace('\\', '/');
        if (Path.IsPathRooted(RelativePath)
            || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part == "..")
            || normalized.EndsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Fixture path '{RelativePath}' is not a safe relative file path.");
        }
    }
}

internal sealed record ConfiguredCertificationRuntime(
    string RuntimeId,
    Uri Endpoint,
    bool Enabled,
    bool AllowPrivateLanEndpoint,
    bool AllowRemoteHttpsEndpoint,
    string? ApiKey = null)
{
    internal ConfiguredCertificationRuntime Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RuntimeId);
        ArgumentNullException.ThrowIfNull(Endpoint);
        return this;
    }
}

internal sealed record EngineeringCertificationCandidate(
    string CandidateId,
    string RuntimeId,
    Uri Endpoint,
    string ModelId,
    string BindingDigest);

internal sealed record EngineeringCandidateDiscoveryIssue(string RuntimeId, string Message);

internal sealed record EngineeringCandidateDiscoveryResult(
    IReadOnlyList<EngineeringCertificationCandidate> Candidates,
    IReadOnlyList<EngineeringCandidateDiscoveryIssue> Issues);

internal sealed record EngineeringAgentExecutionRequest(
    string SuiteVersion,
    string SuiteDigest,
    string RunId,
    EngineeringCertificationCandidate Candidate,
    EngineeringCertificationTask Task,
    string WorkspacePath,
    bool InjectFirstRequiredToolFailure);

/// <summary>
/// Typed evidence emitted by the same authoritative Agent Framework loop used for normal Ali work.
/// The certification runner never interprets answer prose and never chooses tools for the model.
/// </summary>
internal sealed record EngineeringAgentExecutionReceipt(
    bool Completed,
    string? SelectedPrimitiveId,
    IReadOnlyList<string> ToolIds,
    bool? RecoveredAfterInjectedFailure,
    long? InputTokens,
    long? OutputTokens,
    string RawEvidence)
{
    internal long? TotalTokens => InputTokens is >= 0 && OutputTokens is >= 0
        ? checked(InputTokens.Value + OutputTokens.Value)
        : null;
}

internal sealed record EngineeringVerificationBaseline(
    int RoslynErrorCount,
    int RoslynWarningCount,
    string RawEvidence = "");

internal sealed record EngineeringVerificationReceipt(
    bool BuildSucceeded,
    bool UnitTestsSucceeded,
    int RoslynErrorCount,
    int RoslynWarningCount,
    IReadOnlyList<string> HallucinatedApiDiagnostics,
    string RawEvidence)
{
    internal int IntroducedRoslynDiagnostics(EngineeringVerificationBaseline baseline) =>
        Math.Max(0, RoslynErrorCount - baseline.RoslynErrorCount)
        + Math.Max(0, RoslynWarningCount - baseline.RoslynWarningCount);
}

internal sealed record EngineeringCertificationScoreComponent(
    string Id,
    bool Applicable,
    bool Passed,
    string Evidence);

internal sealed record EngineeringCertificationScore(
    decimal Percent,
    IReadOnlyList<EngineeringCertificationScoreComponent> Components);

internal sealed record EngineeringCertificationTaskEvidence(
    string SuiteVersion,
    string SuiteDigest,
    string RunId,
    string CandidateId,
    string CandidateBindingDigest,
    string ModelId,
    string TaskId,
    int Attempt,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long ElapsedMilliseconds,
    EngineeringAgentExecutionReceipt Agent,
    EngineeringVerificationBaseline Baseline,
    EngineeringVerificationReceipt Verification,
    EngineeringCertificationScore Score,
    string WorkspacePath,
    string RawEvidencePath);

internal sealed record EngineeringCertificationCandidateReport(
    string CandidateId,
    string RuntimeId,
    string ModelId,
    string BindingDigest,
    int CompletedTasks,
    decimal MeanScore,
    long TotalElapsedMilliseconds,
    long? TotalTokens,
    IReadOnlyDictionary<string, EngineeringCertificationComponentSummary> Components);

internal sealed record EngineeringCertificationComponentSummary(
    int ApplicableTasks,
    int PassedTasks);

internal sealed record EngineeringCertificationComparisonReport(
    string SuiteVersion,
    string SuiteDigest,
    string RunId,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<EngineeringCertificationCandidateReport> Candidates,
    IReadOnlyList<EngineeringCandidateDiscoveryIssue> DiscoveryIssues,
    string Disclaimer);

internal interface IEngineeringCertificationCandidateSource
{
    Task<EngineeringCandidateDiscoveryResult> DiscoverAsync(
        IReadOnlyList<ConfiguredCertificationRuntime> runtimes,
        CancellationToken cancellationToken);
}

/// <summary>
/// Integration boundary for Ali's existing authoritative agent loop. Implementations must bind the
/// requested candidate and return typed usage/tool receipts; they must not add a benchmark router.
/// </summary>
internal interface IAuthoritativeEngineeringAgentLoop
{
    Task<EngineeringAgentExecutionReceipt> ExecuteAsync(
        EngineeringAgentExecutionRequest request,
        CancellationToken cancellationToken);
}

internal interface IEngineeringCertificationVerifier
{
    Task<EngineeringVerificationBaseline> CaptureBaselineAsync(
        EngineeringCertificationTask task,
        string workspacePath,
        CancellationToken cancellationToken);

    Task<EngineeringVerificationReceipt> VerifyAsync(
        EngineeringCertificationTask task,
        string workspacePath,
        CancellationToken cancellationToken);
}

internal sealed record EngineeringCertificationRunRequest(
    string RunId,
    EngineeringCertificationSuite Suite,
    IReadOnlyList<ConfiguredCertificationRuntime> ConfiguredRuntimes,
    int MaximumAttemptsPerTask = 2)
{
    internal EngineeringCertificationRunRequest Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RunId);
        Suite.Validate();
        if (ConfiguredRuntimes.Count == 0)
        {
            throw new InvalidDataException("At least one configured runtime is required for certification.");
        }
        foreach (var runtime in ConfiguredRuntimes)
        {
            runtime.Validate();
        }
        if (MaximumAttemptsPerTask is < 1 or > 3)
        {
            throw new InvalidDataException("Certification attempts per task must be between one and three.");
        }
        return this;
    }
}

internal sealed record EngineeringCertificationRunResult(
    string RunDirectory,
    EngineeringCertificationComparisonReport Comparison,
    int ExecutedTasks,
    int ResumedTasks);

internal sealed record EngineeringCertificationRunInitialization(
    string RunDirectory,
    EngineeringCandidateDiscoveryResult Discovery);
