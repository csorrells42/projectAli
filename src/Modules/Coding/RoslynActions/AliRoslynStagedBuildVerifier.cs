using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Ali.Modules.Coding.Changesets;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Orchestration.Evidence;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Graph;

namespace Ali.Modules.Coding.RoslynActions;

internal sealed record AliRoslynInputFileIdentity(
    string RelativePath,
    long Length,
    string Sha256);

internal sealed record AliRoslynInputManifest(
    string PolicyDigest,
    string ManifestSha256,
    int FileCount,
    long TotalBytes,
    AliRoslynInputFileIdentity[] Files)
{
    internal static AliRoslynInputManifest Create(
        string policyDigest,
        IReadOnlyCollection<AliRoslynInputFileIdentity> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyDigest);
        ArgumentNullException.ThrowIfNull(files);
        var ordered = files
            .OrderBy(file => file.RelativePath, PathComparer)
            .Select(file => file with { })
            .ToArray();
        var totalBytes = ordered.Aggregate(
            0L,
            static (total, file) => checked(total + file.Length));
        return new(
            policyDigest,
            ComputeDigest(policyDigest, ordered),
            ordered.Length,
            totalBytes,
            ordered);
    }

    internal void Validate()
    {
        if (PolicyDigest?.Length != 64
            || PolicyDigest.Any(character => !char.IsAsciiHexDigit(character))
            || ManifestSha256?.Length != 64
            || ManifestSha256.Any(character => !char.IsAsciiHexDigit(character))
            || Files is null
            || FileCount != Files.Length
            || FileCount < 0
            || FileCount > AliSourceTreeMaterializationPolicy.SafeDefault.MaximumEntries
            || TotalBytes < 0
            || TotalBytes > AliSourceTreeMaterializationPolicy.SafeDefault.MaximumBytes)
        {
            throw new InvalidDataException("A Roslyn input manifest is invalid or unbounded.");
        }

        var seen = new HashSet<string>(PathComparer);
        long totalBytes = 0;
        string? previous = null;
        foreach (var file in Files)
        {
            if (file is null
                || string.IsNullOrWhiteSpace(file.RelativePath)
                || file.RelativePath.Length > AliSourceChangeSetStore.MaximumRelativePathCharacters
                || file.RelativePath != AliSourceChangeSetStore.NormalizeRelativePath(file.RelativePath)
                || file.Length < 0
                || file.Sha256?.Length != 64
                || file.Sha256.Any(character => !char.IsAsciiHexDigit(character))
                || !seen.Add(file.RelativePath)
                || (previous is not null && PathComparer.Compare(previous, file.RelativePath) >= 0))
            {
                throw new InvalidDataException("A Roslyn input manifest file identity is invalid.");
            }
            previous = file.RelativePath;
            totalBytes = checked(totalBytes + file.Length);
        }

        if (totalBytes != TotalBytes
            || !string.Equals(
                ComputeDigest(PolicyDigest, Files),
                ManifestSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("A Roslyn input manifest digest does not match its exact files.");
        }
    }

    internal bool ExactlyEquals(AliRoslynInputManifest expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return string.Equals(PolicyDigest, expected.PolicyDigest, StringComparison.Ordinal)
            && string.Equals(ManifestSha256, expected.ManifestSha256, StringComparison.Ordinal)
            && FileCount == expected.FileCount
            && TotalBytes == expected.TotalBytes
            && Files.Length == expected.Files.Length
            && Files.Zip(expected.Files).All(pair =>
                PathComparer.Equals(pair.First.RelativePath, pair.Second.RelativePath)
                && pair.First.Length == pair.Second.Length
                && string.Equals(pair.First.Sha256, pair.Second.Sha256, StringComparison.Ordinal));
    }

    private static string ComputeDigest(
        string policyDigest,
        IEnumerable<AliRoslynInputFileIdentity> files)
    {
        var manifest = string.Join(
            "\n",
            files.Select(file => string.Join(
                '\0',
                file.RelativePath,
                file.Length.ToString(CultureInfo.InvariantCulture),
                file.Sha256)));
        return HashText("ali-roslyn-input-manifest-v1\0" + policyDigest + "\0" + manifest);
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

internal sealed record AliRoslynStagedInputBinding(
    string StagedRoot,
    AliRoslynInputManifest Manifest)
{
    private static readonly AliSourceTreeMaterializationPolicy Policy =
        AliSourceTreeMaterializationPolicy.SafeDefault;
    private static readonly IReadOnlySet<string> ExcludedDirectoryNames =
        Policy.ExcludedDirectoryNames
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    internal static string PolicyDigest => Policy.Digest;

    internal string ManifestSha256 => Manifest.ManifestSha256;

    internal static AliRoslynStagedInputBinding Capture(
        string stagedRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagedRoot));
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            root,
            "The staged input root is not a regular local directory.");

        var identities = new List<AliRoslynInputFileIdentity>();
        var pending = new Stack<string>();
        pending.Push(root);
        var entries = 0;
        long bytes = 0;
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                directory,
                "A staged input directory is not a regular local directory.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory)
                         .OrderBy(path => path, PathComparer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                var relative = Path.GetRelativePath(root, entry)
                    .Replace(Path.DirectorySeparatorChar, '/');
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (IsExcluded(relative, Path.GetFileName(entry), isDirectory))
                {
                    continue;
                }
                if (++entries > Policy.MaximumEntries)
                {
                    throw new InvalidOperationException(
                        "The Roslyn input binding exceeds its fixed entry bound.");
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "The Roslyn input binding cannot traverse a reparse point.");
                }
                if (isDirectory)
                {
                    pending.Push(entry);
                    continue;
                }

                using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                    entry,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    writeThrough: false,
                    "A staged input is not a regular local file.");
                bytes = checked(bytes + stream.Length);
                if (bytes > Policy.MaximumBytes)
                {
                    throw new InvalidOperationException(
                        "The Roslyn input binding exceeds its fixed byte bound.");
                }
                var digest = Convert.ToHexString(SHA256.HashData(stream));
                identities.Add(new(relative, stream.Length, digest));
            }
        }

        return new(root, AliRoslynInputManifest.Create(PolicyDigest, identities));
    }

    internal void RequireStable(string stagedRoot, CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagedRoot));
        if (!PathComparer.Equals(root, StagedRoot))
        {
            throw new InvalidOperationException(
                "The staged input binding was presented for a different root.");
        }

        var current = Capture(root, cancellationToken);
        if (!current.Manifest.ExactlyEquals(Manifest))
        {
            throw new InvalidOperationException(
                "A bounded non-generated staged input changed during exact verification.");
        }
    }

    internal bool Matches(AliRoslynInputManifest expected)
    {
        Manifest.Validate();
        expected.Validate();
        return Manifest.ExactlyEquals(expected);
    }

    private static bool IsExcluded(string relativePath, string name, bool isDirectory) =>
        Policy.ExcludedRelativePaths.Any(excluded =>
            string.Equals(relativePath, excluded, StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith(excluded + "/", StringComparison.OrdinalIgnoreCase))
        || (isDirectory && ExcludedDirectoryNames.Contains(name));

    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

internal enum AliRoslynStagedRunnerOperation
{
    RestoreAndBuild,
    TestNoBuildNoRestore
}

internal sealed record AliRoslynAffectedProject(
    string RoslynProjectId,
    string CanonicalRelativeProjectPath);

internal sealed record AliRoslynStagedToolsetIdentity(
    string Name,
    string Version,
    string LocationSha256);

internal sealed record AliRoslynStagedRunnerRequest(
    AliRoslynStagedRunnerOperation Operation,
    string StagedRoot,
    string TargetRelativePath,
    string Configuration,
    TimeSpan Timeout,
    AliBoundExecutionFile DotNetHost);

internal sealed record AliRoslynStagedRunnerResult(
    bool Success,
    int ExitCode,
    bool TimedOut,
    long DurationMilliseconds,
    string Output,
    int TotalTests = 0,
    int PassedTests = 0,
    int FailedTests = 0,
    int SkippedTests = 0);

internal interface IAliRoslynStagedBuildRunner
{
    AliRoslynStagedToolsetIdentity ToolsetIdentity { get; }

    Task<AliRoslynStagedRunnerResult> RunAsync(
        AliRoslynStagedRunnerRequest request,
        CancellationToken cancellationToken);
}

internal sealed record AliRoslynStagedVerificationStepReceipt(
    AliRoslynStagedRunnerOperation Operation,
    string TargetRelativePath,
    string TargetSha256,
    bool Success,
    int ExitCode,
    bool TimedOut,
    long DurationMilliseconds,
    int OutputCharacters,
    string OutputSha256,
    int TotalTests,
    int PassedTests,
    int FailedTests,
    int SkippedTests);

internal sealed record AliRoslynStagedBuildVerificationReceipt(
    bool Success,
    string TargetRelativePath,
    string TargetSha256,
    string Configuration,
    AliRoslynStagedToolsetIdentity Toolset,
    int EvaluatedProjectCount,
    int AffectedProjectCount,
    int PlannedBuildTargetCount,
    int CompletedBuildTargetCount,
    int SelectedTestProjectCount,
    int CompletedTestProjectCount,
    int TotalTests,
    int PassedTests,
    int FailedTests,
    int SkippedTests,
    string OutcomeCode,
    string Summary,
    IReadOnlyList<AliRoslynStagedVerificationStepReceipt> Steps);

/// <summary>
/// Evaluates an authenticated staged MSBuild graph and runs only fixed build and
/// test operations selected from exact project dependency edges and IsTestProject.
/// </summary>
internal sealed class AliRoslynStagedBuildVerifier
{
    internal const int MaximumEvaluatedProjects = 4_096;
    internal const int MaximumAffectedProjects = 256;
    internal const int MaximumSelectedTestProjects = 512;
    internal const int MaximumRunnerOutputCharacters = 30_000;
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(15);
    private static readonly IReadOnlySet<string> TargetExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".csproj", ".sln", ".slnx" };
    private static readonly IReadOnlySet<string> ProjectExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".csproj" };
    private static readonly AliRoslynStagedToolsetIdentity UnknownToolset =
        new("unknown", "unknown", HashText(string.Empty));

    private readonly IAliRoslynStagedBuildRunner _runner;
    private readonly TimeSpan _operationTimeout;

    public AliRoslynStagedBuildVerifier(
        IAliRoslynStagedBuildRunner? runner = null,
        TimeSpan? operationTimeout = null)
    {
        if (runner is null)
        {
            _ = AliMsBuildRuntime.EnsureRegistered();
            _runner = new AliRoslynStagedBuildProcessRunner();
        }
        else
        {
            _runner = runner;
        }
        _operationTimeout = operationTimeout ?? DefaultOperationTimeout;
        if (_operationTimeout <= TimeSpan.Zero || _operationTimeout > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationTimeout),
                "The staged verification timeout must be greater than zero and no more than 24 hours.");
        }
    }

    public async Task<AliRoslynStagedBuildVerificationReceipt> VerifyAsync(
        string stagedRoot,
        string canonicalRelativeTargetPath,
        IReadOnlyList<AliRoslynAffectedProject> affectedProjects,
        string configuration,
        CancellationToken cancellationToken)
    {
        var dotNetHost = AliExactDotNetHost.CaptureCurrent();
        var stagedInputs = AliRoslynStagedInputBinding.Capture(stagedRoot, cancellationToken);
        return await VerifyAsync(
                stagedRoot,
                canonicalRelativeTargetPath,
                affectedProjects,
                configuration,
                dotNetHost,
                stagedInputs,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<AliRoslynStagedBuildVerificationReceipt> VerifyAsync(
        string stagedRoot,
        string canonicalRelativeTargetPath,
        IReadOnlyList<AliRoslynAffectedProject> affectedProjects,
        string configuration,
        AliBoundExecutionFile dotNetHost,
        AliRoslynStagedInputBinding stagedInputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(affectedProjects);
        ArgumentNullException.ThrowIfNull(dotNetHost);
        ArgumentNullException.ThrowIfNull(stagedInputs);
        var rawTarget = canonicalRelativeTargetPath ?? string.Empty;
        var targetDigest = HashText(rawTarget);
        var configurationValue = SafeConfiguration(configuration);
        var toolset = UnknownToolset;
        var steps = new List<AliRoslynStagedVerificationStepReceipt>();
        GraphPlan? plan = null;

        try
        {
            configurationValue = ValidateConfiguration(configuration);
            toolset = SafeToolsetIdentity(_runner.ToolsetIdentity);
            _ = AliMsBuildRuntime.EnsureRegistered();
            var root = ValidateStagedRoot(stagedRoot);
            stagedInputs.RequireStable(root, cancellationToken);
            var target = ResolveContainedFile(
                root,
                rawTarget,
                TargetExtensions,
                "staged target");
            var targetRelativePath = CanonicalRelativePath(root, target);
            targetDigest = HashRegularFile(target);
            if (affectedProjects.Count == 0 || affectedProjects.Count > MaximumAffectedProjects)
            {
                throw new InvalidOperationException(
                    $"Staged verification requires between 1 and {MaximumAffectedProjects} affected Roslyn projects.");
            }

            var affected = ResolveAffectedProjects(root, affectedProjects);
            plan = EvaluatePlan(root, target, targetRelativePath, affected, configurationValue, cancellationToken);
            stagedInputs.RequireStable(root, cancellationToken);
            if (plan.Projects.Count > MaximumEvaluatedProjects)
            {
                throw new InvalidOperationException(
                    $"The staged graph contains {plan.Projects.Count} projects; the verification bound is {MaximumEvaluatedProjects}.");
            }
            if (plan.TestTargets.Count > MaximumSelectedTestProjects)
            {
                throw new InvalidOperationException(
                    $"The staged graph selected {plan.TestTargets.Count} test projects; the verification bound is {MaximumSelectedTestProjects}.");
            }

            var completedBuilds = 0;
            foreach (var buildTarget in plan.BuildTargets)
            {
                var result = await RunBoundedAsync(
                        new(
                            AliRoslynStagedRunnerOperation.RestoreAndBuild,
                            root,
                            buildTarget,
                            configurationValue,
                            _operationTimeout,
                            dotNetHost),
                        cancellationToken)
                    .ConfigureAwait(false);
                stagedInputs.RequireStable(root, cancellationToken);
                AddStep(
                    steps,
                    ToStepReceipt(
                        AliRoslynStagedRunnerOperation.RestoreAndBuild,
                        root,
                        buildTarget,
                        result));
                if (!IsSuccessfulBuild(result))
                {
                    return CreateReceipt(
                        false,
                        plan,
                        configurationValue,
                        toolset,
                        steps,
                        completedBuilds,
                        completedTests: 0,
                        "build-failed",
                        result.TimedOut
                            ? "Staged verification stopped because a bounded restore/build timed out."
                            : "Staged verification stopped because a fixed restore/build failed.");
                }

                completedBuilds++;
            }

            var completedTests = 0;
            foreach (var testTarget in plan.TestTargets)
            {
                var result = await RunBoundedAsync(
                        new(
                            AliRoslynStagedRunnerOperation.TestNoBuildNoRestore,
                            root,
                            testTarget,
                            configurationValue,
                            _operationTimeout,
                            dotNetHost),
                        cancellationToken)
                    .ConfigureAwait(false);
                stagedInputs.RequireStable(root, cancellationToken);
                AddStep(
                    steps,
                    ToStepReceipt(
                        AliRoslynStagedRunnerOperation.TestNoBuildNoRestore,
                        root,
                        testTarget,
                        result));
                if (!IsValidTestResult(result, out var outcomeCode, out var summary))
                {
                    return CreateReceipt(
                        false,
                        plan,
                        configurationValue,
                        toolset,
                        steps,
                        completedBuilds,
                        completedTests,
                        outcomeCode,
                        summary);
                }

                completedTests++;
            }

            stagedInputs.RequireStable(root, cancellationToken);
            return CreateReceipt(
                true,
                plan,
                configurationValue,
                toolset,
                steps,
                completedBuilds,
                completedTests,
                "verified",
                plan.TestTargets.Count == 0
                    ? "The smallest affected staged targets restored and built successfully; no test project depends on an affected project in the evaluated graph."
                    : "The smallest affected staged targets restored and built successfully, and every dependency-selected test project passed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            var safeTarget = plan?.TargetRelativePath ?? SafeRelativeTarget(rawTarget);
            return new(
                false,
                safeTarget,
                targetDigest,
                configurationValue,
                toolset,
                plan?.Projects.Count ?? 0,
                plan?.AffectedTargets.Count ?? affectedProjects.Count,
                plan?.BuildTargets.Count ?? 0,
                steps.Count(step => step.Operation == AliRoslynStagedRunnerOperation.RestoreAndBuild && step.Success),
                plan?.TestTargets.Count ?? 0,
                steps.Count(step => step.Operation == AliRoslynStagedRunnerOperation.TestNoBuildNoRestore && step.Success),
                steps.Sum(step => step.TotalTests),
                steps.Sum(step => step.PassedTests),
                steps.Sum(step => step.FailedTests),
                steps.Sum(step => step.SkippedTests),
                exception is TimeoutException ? "runner-timeout" : "verification-failed-closed",
                exception is TimeoutException
                    ? "Staged verification failed closed because the bounded runner timed out."
                    : "Staged verification failed closed before it could produce authoritative build-and-test evidence.",
                steps);
        }
    }

    private async Task<AliRoslynStagedRunnerResult> RunBoundedAsync(
        AliRoslynStagedRunnerRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var result = await _runner.RunAsync(request, linked.Token)
                .WaitAsync(linked.Token)
                .ConfigureAwait(false);
            if (result is null)
            {
                throw new InvalidDataException("The staged runner returned no result.");
            }
            if (result.DurationMilliseconds < 0
                || (result.Output?.Length ?? 0) > MaximumRunnerOutputCharacters)
            {
                throw new InvalidDataException("The staged runner returned an invalid or over-bound result.");
            }
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The staged verification runner exceeded its bounded operation timeout.");
        }
    }

    private GraphPlan EvaluatePlan(
        string root,
        string target,
        string targetRelativePath,
        IReadOnlyDictionary<string, string> affected,
        string configuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var projects = new ProjectCollection(new Dictionary<string, string>
        {
            ["Configuration"] = configuration
        });
        var graph = new ProjectGraph(
            target,
            new Dictionary<string, string>
            {
                ["Configuration"] = configuration
            },
            projects);
        if (graph.ProjectNodes.Count > MaximumEvaluatedProjects)
        {
            throw new InvalidOperationException(
                $"The staged graph contains {graph.ProjectNodes.Count} projects; the verification bound is {MaximumEvaluatedProjects}.");
        }
        var grouped = graph.ProjectNodes
            .GroupBy(node => Path.GetFullPath(node.ProjectInstance.FullPath), PathComparer)
            .ToArray();
        var nodes = new Dictionary<string, EvaluatedProject>(PathComparer);
        foreach (var group in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = ValidateGraphProject(root, group.Key);
            var testValues = group
                .Select(node => ParseIsTestProject(node.ProjectInstance.GetPropertyValue("IsTestProject"), fullPath))
                .Distinct()
                .ToArray();
            if (testValues.Length != 1)
            {
                throw new InvalidOperationException(
                    "An evaluated multi-target project produced inconsistent IsTestProject values.");
            }

            var dependencies = group
                .SelectMany(node => node.ProjectReferences)
                .Select(reference => ValidateGraphProject(root, reference.ProjectInstance.FullPath))
                .Distinct(PathComparer)
                .ToArray();
            nodes.Add(
                fullPath,
                new(
                    fullPath,
                    CanonicalRelativePath(root, fullPath),
                    testValues[0],
                    dependencies));
        }

        if (nodes.Count == 0)
        {
            throw new InvalidOperationException("MSBuild evaluated no projects for the staged target.");
        }

        if (nodes.Values.Any(node => node.Dependencies.Any(dependency => !nodes.ContainsKey(dependency))))
        {
            throw new InvalidOperationException(
                "The evaluated staged graph contains an unresolved project-reference edge.");
        }

        foreach (var affectedPath in affected.Keys)
        {
            if (!nodes.ContainsKey(affectedPath))
            {
                throw new InvalidOperationException(
                    "An affected Roslyn project is not part of the exact evaluated staged target graph.");
            }
        }

        var affectedSet = affected.Keys.ToHashSet(PathComparer);
        var tests = nodes.Values
            .Where(node => node.IsTestProject && DependsOnAny(node.FullPath, affectedSet, nodes))
            .Select(node => node.FullPath)
            .ToHashSet(PathComparer);
        var candidateBuilds = affectedSet.Concat(tests).ToHashSet(PathComparer);
        var minimalBuilds = candidateBuilds
            .Where(candidate => !candidateBuilds.Any(other =>
                !PathComparer.Equals(candidate, other)
                && DependsOn(other, candidate, nodes)))
            .Select(path => nodes[path].RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var testTargets = tests
            .Select(path => nodes[path].RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new(
            targetRelativePath,
            HashRegularFile(target),
            nodes.Values.OrderBy(node => node.RelativePath, StringComparer.Ordinal).ToArray(),
            affectedSet.Select(path => nodes[path].RelativePath).Order(StringComparer.Ordinal).ToArray(),
            minimalBuilds,
            testTargets);
    }

    private AliRoslynStagedBuildVerificationReceipt CreateReceipt(
        bool success,
        GraphPlan plan,
        string configuration,
        AliRoslynStagedToolsetIdentity toolset,
        IReadOnlyList<AliRoslynStagedVerificationStepReceipt> steps,
        int completedBuilds,
        int completedTests,
        string outcomeCode,
        string summary) =>
        new(
            success,
            plan.TargetRelativePath,
            plan.TargetSha256,
            configuration,
            toolset,
            plan.Projects.Count,
            plan.AffectedTargets.Count,
            plan.BuildTargets.Count,
            completedBuilds,
            plan.TestTargets.Count,
            completedTests,
            steps.Sum(step => step.TotalTests),
            steps.Sum(step => step.PassedTests),
            steps.Sum(step => step.FailedTests),
            steps.Sum(step => step.SkippedTests),
            outcomeCode,
            summary,
            steps);

    private static AliRoslynStagedVerificationStepReceipt ToStepReceipt(
        AliRoslynStagedRunnerOperation operation,
        string root,
        string targetRelativePath,
        AliRoslynStagedRunnerResult result)
    {
        var output = result.Output ?? string.Empty;
        var successful = operation == AliRoslynStagedRunnerOperation.RestoreAndBuild
            ? IsSuccessfulBuild(result)
            : result.Success && !result.TimedOut && result.ExitCode == 0
                && result.TotalTests > 0 && result.FailedTests == 0;
        return new(
            operation,
            targetRelativePath,
            HashRegularFile(ResolveContainedFile(
                root,
                targetRelativePath,
                ProjectExtensions,
                "verification step target")),
            successful,
            result.ExitCode,
            result.TimedOut,
            Math.Max(0, result.DurationMilliseconds),
            output.Length,
            HashText(output),
            Math.Max(0, result.TotalTests),
            Math.Max(0, result.PassedTests),
            Math.Max(0, result.FailedTests),
            Math.Max(0, result.SkippedTests));
    }

    private static void AddStep(
        List<AliRoslynStagedVerificationStepReceipt> steps,
        AliRoslynStagedVerificationStepReceipt step)
    {
        if (steps.Sum(existing => (long)existing.TotalTests) + step.TotalTests > int.MaxValue
            || steps.Sum(existing => (long)existing.PassedTests) + step.PassedTests > int.MaxValue
            || steps.Sum(existing => (long)existing.FailedTests) + step.FailedTests > int.MaxValue
            || steps.Sum(existing => (long)existing.SkippedTests) + step.SkippedTests > int.MaxValue)
        {
            throw new InvalidDataException("The staged runner returned aggregate test counts outside the receipt bound.");
        }

        steps.Add(step);
    }

    private static bool IsSuccessfulBuild(AliRoslynStagedRunnerResult result) =>
        result.Success && !result.TimedOut && result.ExitCode == 0;

    private static bool IsValidTestResult(
        AliRoslynStagedRunnerResult result,
        out string outcomeCode,
        out string summary)
    {
        if (result.TimedOut)
        {
            outcomeCode = "test-timeout";
            summary = "Staged verification stopped because a dependency-selected test project timed out.";
            return false;
        }
        if (!result.Success || result.ExitCode != 0 || result.FailedTests > 0)
        {
            outcomeCode = "test-failed";
            summary = "Staged verification stopped because a dependency-selected test project failed.";
            return false;
        }
        if (result.TotalTests <= 0)
        {
            outcomeCode = "zero-tests-discovered";
            summary = "Staged verification failed closed because a selected test project discovered zero tests.";
            return false;
        }
        var accountedTests = (long)result.PassedTests + result.FailedTests + result.SkippedTests;
        if (result.PassedTests < 0 || result.FailedTests < 0 || result.SkippedTests < 0
            || accountedTests != result.TotalTests)
        {
            outcomeCode = "invalid-test-receipt";
            summary = "Staged verification failed closed because the runner returned inconsistent test counts.";
            return false;
        }

        outcomeCode = "tests-passed";
        summary = "The dependency-selected staged test project passed.";
        return true;
    }

    private static IReadOnlyDictionary<string, string> ResolveAffectedProjects(
        string root,
        IReadOnlyList<AliRoslynAffectedProject> affectedProjects)
    {
        var resolved = new Dictionary<string, string>(PathComparer);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var affected in affectedProjects)
        {
            if (affected is null
                || string.IsNullOrWhiteSpace(affected.RoslynProjectId)
                || affected.RoslynProjectId.Length > 256
                || affected.RoslynProjectId.Any(char.IsControl)
                || !ids.Add(affected.RoslynProjectId))
            {
                throw new InvalidOperationException("Affected Roslyn project identities must be non-empty and unique.");
            }

            var path = ResolveContainedFile(
                root,
                affected.CanonicalRelativeProjectPath,
                ProjectExtensions,
                "affected project");
            if (!resolved.TryAdd(path, affected.RoslynProjectId))
            {
                throw new InvalidOperationException("Affected Roslyn project paths must be unique.");
            }
        }

        return resolved;
    }

    private static string ValidateStagedRoot(string stagedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagedRoot));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The authenticated staged root no longer exists.");
        }
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The authenticated staged root cannot be a reparse point.");
        }
        return root;
    }

    private static string ResolveContainedFile(
        string root,
        string relativePath,
        IReadOnlySet<string> allowedExtensions,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidOperationException($"The {description} must be relative to the authenticated staged root.");
        }

        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        ValidateContainedPath(root, path, description);
        if (!allowedExtensions.Contains(Path.GetExtension(path)))
        {
            throw new InvalidOperationException($"The {description} has an unsupported file type.");
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The {description} no longer exists.");
        }
        RejectReparsePoints(root, path, description);
        return path;
    }

    private static string ValidateGraphProject(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        ValidateContainedPath(root, fullPath, "evaluated project");
        if (!Path.GetExtension(fullPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath))
        {
            throw new InvalidOperationException("The evaluated graph contains a missing or unsupported project.");
        }
        RejectReparsePoints(root, fullPath, "evaluated project");
        return fullPath;
    }

    private static void ValidateContainedPath(string root, string path, string description)
    {
        var prefix = root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, PathComparison))
        {
            throw new InvalidOperationException($"The {description} escapes the authenticated staged root.");
        }
    }

    private static void RejectReparsePoints(string root, string path, string description)
    {
        var current = new FileInfo(path);
        if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"The {description} cannot be a reparse point.");
        }

        var directory = current.Directory;
        while (directory is not null && !PathComparer.Equals(directory.FullName, root))
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"The {description} cannot traverse a reparse point.");
            }
            directory = directory.Parent;
        }
        if (directory is null)
        {
            throw new InvalidOperationException($"The {description} escaped the authenticated staged root.");
        }
    }

    private static bool DependsOnAny(
        string project,
        IReadOnlySet<string> affected,
        IReadOnlyDictionary<string, EvaluatedProject> nodes)
    {
        if (affected.Contains(project))
        {
            return true;
        }
        return affected.Any(target => DependsOn(project, target, nodes));
    }

    private static bool DependsOn(
        string project,
        string target,
        IReadOnlyDictionary<string, EvaluatedProject> nodes)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(PathComparer);
        pending.Push(project);
        while (pending.Count != 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current) || !nodes.TryGetValue(current, out var node))
            {
                continue;
            }
            foreach (var dependency in node.Dependencies)
            {
                if (PathComparer.Equals(dependency, target))
                {
                    return true;
                }
                pending.Push(dependency);
            }
        }
        return false;
    }

    private static bool ParseIsTestProject(string value, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        if (bool.TryParse(value, out var result))
        {
            return result;
        }
        throw new InvalidOperationException(
            $"MSBuild evaluated an invalid IsTestProject value for '{Path.GetFileName(projectPath)}'.");
    }

    private static string ValidateConfiguration(string configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        if (!IsSafeConfiguration(configuration))
        {
            throw new ArgumentException("The exact configuration value is invalid.", nameof(configuration));
        }
        return configuration;
    }

    private static string SafeConfiguration(string? configuration) =>
        IsSafeConfiguration(configuration) ? configuration! : "<invalid>";

    private static bool IsSafeConfiguration(string? configuration) =>
        !string.IsNullOrWhiteSpace(configuration)
        && configuration.Length <= 128
        && configuration.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is ' ' or '.' or '_' or '-');

    private static string CanonicalRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string SafeRelativeTarget(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathFullyQualified(value))
        {
            return "<invalid>";
        }
        var normalized = value.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        if (normalized.Length > 256)
        {
            return "<overlong>";
        }
        return normalized.Split('/').Any(segment => segment == "..") ? "<invalid>" : normalized;
    }

    private static AliRoslynStagedToolsetIdentity SafeToolsetIdentity(AliRoslynStagedToolsetIdentity value)
    {
        ArgumentNullException.ThrowIfNull(value);
        static string Safe(string? text, string fallback) =>
            string.IsNullOrWhiteSpace(text)
            || text.Length > 256
            || text.Any(character =>
                char.IsControl(character)
                || character == Path.DirectorySeparatorChar
                || character == Path.AltDirectorySeparatorChar
                || character == Path.VolumeSeparatorChar)
                ? fallback
                : text;
        var location = value.LocationSha256 ?? string.Empty;
        if (location.Length != 64 || location.Any(character => !Uri.IsHexDigit(character)))
        {
            location = HashText(location);
        }
        return new(Safe(value.Name, "unknown"), Safe(value.Version, "unknown"), location.ToUpperInvariant());
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));

    private static string HashRegularFile(string path)
    {
        using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            "The staged target must remain a regular no-follow file.");
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsRecoverableFailure(Exception exception) =>
        exception is not OperationCanceledException
            and not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record EvaluatedProject(
        string FullPath,
        string RelativePath,
        bool IsTestProject,
        IReadOnlyList<string> Dependencies);

    private sealed record GraphPlan(
        string TargetRelativePath,
        string TargetSha256,
        IReadOnlyList<EvaluatedProject> Projects,
        IReadOnlyList<string> AffectedTargets,
        IReadOnlyList<string> BuildTargets,
        IReadOnlyList<string> TestTargets);
}

/// <summary>Production runner for the verifier's two fixed operations.</summary>
internal sealed class AliRoslynStagedBuildProcessRunner : IAliRoslynStagedBuildRunner
{
    private const long MaximumTrxBytes = 64L * 1024 * 1024;
    private static readonly object ToolsetIdentityLock = new();
    private static AliRoslynStagedToolsetIdentity? _cachedToolsetIdentity;

    public AliRoslynStagedBuildProcessRunner()
    {
        var registered = AliMsBuildRuntime.EnsureRegistered();
        ToolsetIdentity = ReadRegisteredToolsetIdentity(registered);
    }

    public AliRoslynStagedToolsetIdentity ToolsetIdentity { get; }

    private static AliRoslynStagedToolsetIdentity ReadRegisteredToolsetIdentity(
        string registeredPath)
    {
        lock (ToolsetIdentityLock)
        {
            if (_cachedToolsetIdentity is not null)
            {
                return _cachedToolsetIdentity;
            }

            var candidate = Directory.Exists(registeredPath)
                ? Path.Combine(Path.GetFullPath(registeredPath), "Microsoft.Build.dll")
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                    candidate,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    writeThrough: false,
                    "The registered MSBuild identity must be a regular no-follow file.");
                if (stream.Length is > 0 and <= 1024L * 1024 * 1024)
                {
                    var digest = Convert.ToHexString(SHA256.HashData(stream));
                    stream.Position = 0;
                    using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
                    var metadata = peReader.GetMetadataReader();
                    var assemblyVersion = metadata.IsAssembly
                        ? metadata.GetAssemblyDefinition().Version.ToString()
                        : "unknown";
                    _cachedToolsetIdentity = new(
                        "dotnet-msbuild",
                        assemblyVersion,
                        digest);
                    return _cachedToolsetIdentity;
                }
            }

            _cachedToolsetIdentity = new(
                "dotnet-msbuild",
                "unknown",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(registeredPath))));
            return _cachedToolsetIdentity;
        }
    }

    public async Task<AliRoslynStagedRunnerResult> RunAsync(
        AliRoslynStagedRunnerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Operation switch
        {
            AliRoslynStagedRunnerOperation.RestoreAndBuild =>
                await RunRestoreAndBuildAsync(request, cancellationToken).ConfigureAwait(false),
            AliRoslynStagedRunnerOperation.TestNoBuildNoRestore =>
                await RunTestsAsync(request, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("The staged runner received an unsupported operation.")
        };
    }

    private static async Task<AliRoslynStagedRunnerResult> RunRestoreAndBuildAsync(
        AliRoslynStagedRunnerRequest request,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(
                request,
                [
                    "msbuild",
                    request.TargetRelativePath,
                    "/restore",
                    "/t:Build",
                    "/p:Configuration=" + request.Configuration,
                    "/nologo",
                    "/m:1",
                    "/nr:false"
                ],
                cancellationToken)
            .ConfigureAwait(false);
        return new(
            result.Success,
            result.ExitCode,
            result.TimedOut,
            result.DurationMilliseconds,
            result.Output);
    }

    private static async Task<AliRoslynStagedRunnerResult> RunTestsAsync(
        AliRoslynStagedRunnerRequest request,
        CancellationToken cancellationToken)
    {
        var targetIdentity = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(request.TargetRelativePath)))[..16];
        const string resultsRootRelative = ".ali-preview-verification";
        var resultsRoot = Path.Combine(request.StagedRoot, resultsRootRelative);
        if (Directory.Exists(resultsRoot)
            && (File.GetAttributes(resultsRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The staged test-results root cannot be a reparse point.");
        }
        Directory.CreateDirectory(resultsRoot);
        if ((File.GetAttributes(resultsRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The staged test-results root cannot be a reparse point.");
        }

        var resultsRelative = Path.Combine(resultsRootRelative, targetIdentity);
        var resultsDirectory = Path.Combine(request.StagedRoot, resultsRelative);
        if (Directory.Exists(resultsDirectory)
            && (File.GetAttributes(resultsDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The staged test-results directory cannot be a reparse point.");
        }
        Directory.CreateDirectory(resultsDirectory);
        if ((File.GetAttributes(resultsDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The staged test-results directory cannot be a reparse point.");
        }
        var trxName = "tests.trx";
        var trxPath = Path.Combine(resultsDirectory, trxName);
        if (File.Exists(trxPath))
        {
            if ((File.GetAttributes(trxPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("The staged test receipt cannot be a reparse point.");
            }
            File.Delete(trxPath);
        }
        var result = await RunProcessAsync(
                request,
                [
                    "test",
                    request.TargetRelativePath,
                    "--configuration",
                    request.Configuration,
                    "--no-build",
                    "--no-restore",
                    "--logger",
                    "trx;LogFileName=" + trxName,
                    "--results-directory",
                    resultsRelative,
                    "--nologo"
                ],
                cancellationToken)
            .ConfigureAwait(false);
        var counts = result.TimedOut ? default : ReadTestCounts(trxPath);
        return new(
            result.Success,
            result.ExitCode,
            result.TimedOut,
            result.DurationMilliseconds,
            result.Output,
            counts.Total,
            counts.Passed,
            counts.Failed,
            counts.Skipped);
    }

    private static async Task<BoundedProcessResult> RunProcessAsync(
        AliRoslynStagedRunnerRequest request,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await AliBoundedProcessRunner.RunAsync(
                    request.DotNetHost,
                    request.StagedRoot,
                    arguments,
                    request.Timeout,
                    linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return new(false, -1, string.Empty, 0, true);
        }
    }

    private static (int Total, int Passed, int Failed, int Skipped) ReadTestCounts(string trxPath)
    {
        if (!File.Exists(trxPath))
        {
            return default;
        }
        var receipt = new FileInfo(trxPath);
        if ((receipt.Attributes & FileAttributes.ReparsePoint) != 0
            || receipt.Length > MaximumTrxBytes)
        {
            throw new InvalidDataException("The staged test receipt is unsafe or exceeds its fixed size bound.");
        }
        using var reader = XmlReader.Create(
            trxPath,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumTrxBytes
            });
        var document = XDocument.Load(reader, LoadOptions.None);
        var counters = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Counters");
        return (
            ReadInt(counters, "total"),
            ReadInt(counters, "passed"),
            ReadInt(counters, "failed"),
            ReadInt(counters, "notExecuted"));
    }

    private static int ReadInt(XElement? counters, string name) =>
        int.TryParse(
            (string?)counters?.Attribute(name),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0;

}
