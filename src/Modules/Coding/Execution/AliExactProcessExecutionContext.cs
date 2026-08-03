namespace Ali.Modules.Coding.Execution;

/// <summary>
/// Exact process files carried from durable binding revalidation into a fixed executor. This is
/// structural authorization state; it cannot select a tool, operation, or user intent.
/// </summary>
internal sealed record AliExactProcessExecutionBinding(
    AliBoundExecutionFile? DotNetHost,
    AliBoundExecutionFile? ApplicationArtifact,
    AliApplicationLaunchClosure? ApplicationLaunchClosure = null,
    AliPostBuildApplicationArtifactPolicy? PostBuildApplicationArtifact = null)
{
    internal string RequireStableDotNetHost()
    {
        var approved = DotNetHost
            ?? throw new InvalidOperationException(
                "The durable invocation did not bind an exact .NET host executable.");
        return RequireStable(approved, "The approved .NET host executable");
    }

    internal string RequireStableApplicationArtifact()
    {
        var approved = ApplicationArtifact
            ?? throw new InvalidOperationException(
                "The durable invocation did not bind an exact application artifact.");
        return RequireStable(approved, "The approved application artifact");
    }

    internal string RequireStableApplicationLaunchClosure()
    {
        var approved = ApplicationLaunchClosure
            ?? throw new InvalidOperationException(
                "The durable invocation did not bind the complete application launch output directory.");
        return approved.RequireStable();
    }

    /// <summary>
    /// Fulfils the explicit derived-output policy only after the caller's successful build gate.
    /// The policy contains exact statically parsed literal candidate paths, so this never searches outside the
    /// output root authorized by the durable invocation.
    /// </summary>
    internal AliExactProcessExecutionBinding BindPostBuildApplicationArtifact(
        string projectArgument,
        string? configuration)
    {
        if (ApplicationArtifact is not null)
        {
            throw new InvalidOperationException(
                "The exact process binding already contains an application artifact.");
        }
        var policy = PostBuildApplicationArtifact
            ?? throw new InvalidOperationException(
                "The durable invocation did not authorize a post-build application artifact.");
        var captured = policy.CaptureAfterSuccessfulBuild(projectArgument, configuration);
        return this with
        {
            ApplicationArtifact = captured.Artifact,
            ApplicationLaunchClosure = captured.Closure
        };
    }

    private static string RequireStable(
        AliBoundExecutionFile approved,
        string description)
    {
        var current = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            approved.PhysicalPath,
            description);
        if (current != approved)
        {
            throw new InvalidOperationException(
                description + " changed after durable authorization.");
        }
        return approved.PhysicalPath;
    }
}

/// <summary>
/// A durable, statically parsed literal boundary for an application artifact that does not exist yet or is
/// expected to change during the authorized delivery build. Candidate paths are fixed before
/// execution; fulfilment reads only those paths after a successful build.
/// </summary>
internal sealed record AliPostBuildApplicationArtifactPolicy
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private AliPostBuildApplicationArtifactPolicy(
        string projectArgument,
        string physicalProjectPath,
        string configuration,
        string authorizedOutputRoot,
        IReadOnlyList<string> candidateArtifactPaths)
    {
        ProjectArgument = projectArgument;
        PhysicalProjectPath = physicalProjectPath;
        Configuration = configuration;
        AuthorizedOutputRoot = authorizedOutputRoot;
        CandidateArtifactPaths = candidateArtifactPaths;
    }

    internal string ProjectArgument { get; }

    internal string PhysicalProjectPath { get; }

    internal string Configuration { get; }

    internal string AuthorizedOutputRoot { get; }

    internal IReadOnlyList<string> CandidateArtifactPaths { get; }

    internal static AliPostBuildApplicationArtifactPolicy Create(
        string projectArgument,
        string physicalProjectPath,
        string configuration,
        string authorizedOutputRoot,
        IEnumerable<string> candidateArtifactPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectArgument);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalProjectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizedOutputRoot);
        ArgumentNullException.ThrowIfNull(candidateArtifactPaths);

        var project = AliCodingExecutionAssetFingerprint.NormalizePath(physicalProjectPath);
        var outputRoot = AliCodingExecutionAssetFingerprint.NormalizePath(authorizedOutputRoot);
        var candidates = candidateArtifactPaths
            .Select(AliCodingExecutionAssetFingerprint.NormalizePath)
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length is < 1 or > 2)
        {
            throw new InvalidDataException(
                "A post-build application policy must contain one or two exact artifact paths.");
        }
        foreach (var candidate in candidates)
        {
            RequireWithinOutputRoot(outputRoot, candidate);
        }

        return new AliPostBuildApplicationArtifactPolicy(
            projectArgument,
            project,
            NormalizeConfiguration(configuration),
            outputRoot,
            Array.AsReadOnly(candidates));
    }

    internal void AddTo(IDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        values["postBuildApplication.projectArgument"] = ProjectArgument;
        values["postBuildApplication.physicalProject"] = PhysicalProjectPath;
        values["postBuildApplication.configuration"] = Configuration;
        values["postBuildApplication.outputRoot"] = AuthorizedOutputRoot;
        values["postBuildApplication.candidateCount"] =
            CandidateArtifactPaths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var index = 0; index < CandidateArtifactPaths.Count; index++)
        {
            values[$"postBuildApplication.candidate.{index}"] = CandidateArtifactPaths[index];
        }
    }

    internal (AliBoundExecutionFile Artifact, AliApplicationLaunchClosure Closure)
        CaptureAfterSuccessfulBuild(
        string projectArgument,
        string? configuration)
    {
        if (!string.Equals(projectArgument, ProjectArgument, StringComparison.Ordinal)
            || !string.Equals(
                NormalizeConfiguration(configuration),
                Configuration,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The post-build application request does not match its durable project/configuration binding.");
        }

        AliCodingExecutionAssetFingerprint.ValidateRegularDirectoryNoFollow(
            AuthorizedOutputRoot,
            "The authorized post-build application output root is not a regular local directory.");
        foreach (var candidate in CandidateArtifactPaths)
        {
            RequireWithinOutputRoot(AuthorizedOutputRoot, candidate);
            if (!File.Exists(candidate))
            {
                continue;
            }
            var artifact = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
                candidate,
                "The derived post-build application artifact");
            var closure = AliApplicationLaunchClosure.Capture(artifact);
            if (!string.Equals(
                    closure.OutputDirectoryPath,
                    AuthorizedOutputRoot,
                    PathComparison))
            {
                throw new InvalidOperationException(
                    "The derived post-build application output closure does not match its authorized output root.");
            }
            return (artifact, closure);
        }
        throw new FileNotFoundException(
            "The successful build did not produce an application artifact at an authorized evaluated path.",
            CandidateArtifactPaths[0]);
    }

    private static void RequireWithinOutputRoot(string outputRoot, string candidate)
    {
        if (!string.Equals(outputRoot, candidate, PathComparison)
            && !candidate.StartsWith(
                Path.TrimEndingDirectorySeparator(outputRoot) + Path.DirectorySeparatorChar,
                PathComparison))
        {
            throw new InvalidDataException(
                "A post-build application artifact escapes its authorized output root.");
        }
    }

    private static string NormalizeConfiguration(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "Release"
            : value.Trim() switch
            {
                var text when text.Equals("Debug", StringComparison.OrdinalIgnoreCase) =>
                    "Debug",
                var text when text.Equals("Release", StringComparison.OrdinalIgnoreCase) =>
                    "Release",
                _ => throw new ArgumentException(
                    "Configuration must be Debug or Release.",
                    nameof(value))
            };
}

internal static class AliExactProcessExecutionContext
{
    private static readonly AsyncLocal<Frame?> CurrentFrame = new();

    internal static AliExactProcessExecutionBinding? Current => CurrentFrame.Value?.Binding;

    internal static IDisposable Enter(AliExactProcessExecutionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var prior = CurrentFrame.Value;
        CurrentFrame.Value = new Frame(binding, prior);
        return new Scope(CurrentFrame.Value);
    }

    private sealed record Frame(
        AliExactProcessExecutionBinding Binding,
        Frame? Prior);

    private sealed class Scope(Frame frame) : IDisposable
    {
        private Frame? _frame = frame;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _frame, null);
            if (current is null)
            {
                return;
            }
            if (!ReferenceEquals(CurrentFrame.Value, current))
            {
                throw new InvalidOperationException(
                    "The exact process execution scope was disposed out of order.");
            }
            CurrentFrame.Value = current.Prior;
        }
    }
}
