using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Coding.Languages;

namespace Ali.Modules.Coding.Python;

/// <summary>
/// Portable Python provider. The bundled CPython runtime is sufficient for bounded
/// syntax/AST analysis, byte-code validation, and unittest discovery. Optional
/// developer tools are discovered from the same runtime without changing PATH.
/// </summary>
internal sealed class AliPythonLanguageProvider : IAliLanguageProvider
{
    private static readonly TimeSpan AnalyzeLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FormatLimit = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan BuildLimit = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TestLimit = TimeSpan.FromMinutes(10);
    private readonly AliToolchainLocator _locator;

    public AliPythonLanguageProvider(AliToolchainLocator locator) => _locator = locator;

    public string Id => "python-cpython";
    public string DisplayName => "Python / CPython";
    public IReadOnlySet<AliProgrammingLanguage> Languages { get; } =
        new HashSet<AliProgrammingLanguage> { AliProgrammingLanguage.Python };
    public AliLanguageCapability Capabilities =>
        AliLanguageCapability.Inspect | AliLanguageCapability.Index | AliLanguageCapability.Analyze
        | AliLanguageCapability.Format | AliLanguageCapability.Build | AliLanguageCapability.Test
        | AliLanguageCapability.Dependencies | AliLanguageCapability.Architecture
        | AliLanguageCapability.Coverage | AliLanguageCapability.Debug | AliLanguageCapability.Profile;

    public bool CanHandle(AliResolvedLanguageProject project) => project.Language == AliProgrammingLanguage.Python;

    public IReadOnlyList<AliLanguageToolchainStatus> InspectToolchains(AliResolvedLanguageProject? project = null)
    {
        var python = ResolvePython(project?.ProjectDirectory);
        var profiler = ResolveOptionalExecutable("py-spy", "ALI_PY_SPY_PATH", project?.ProjectDirectory);
        return
        [
            new("python", python is not null, python, "Portable runtime, AST analysis, compile validation, and unittest"),
            ModuleStatus("ruff", python, "Fast linting, import repair, and formatting"),
            ModuleStatus("basedpyright", python, "Language Server Protocol and static type analysis"),
            ModuleStatus("pytest", python, "Test discovery and execution"),
            ModuleStatus("debugpy", python, "Debug Adapter Protocol"),
            ModuleStatus("coverage", python, "Line and branch coverage"),
            new("py-spy", profiler is not null,
                profiler, "Sampling performance profiler")
        ];
    }

    public async Task<AliLanguageOperationResult> AnalyzeAsync(
        AliResolvedLanguageProject project,
        CancellationToken cancellationToken)
    {
        var python = RequirePython(project);
        var analyzer = ResolveAnalyzer();
        if (analyzer is null)
        {
            return Unavailable("analyze", "Ali's bounded Python AST analyzer is missing from the application bundle.");
        }

        var core = await AliBoundedProcessRunner.RunAsync(
            python,
            project.ProjectDirectory,
            [analyzer, project.ProjectDirectory],
            AnalyzeLimit,
            cancellationToken).ConfigureAwait(false);

        var output = core.Output;
        var ruff = await TryRunModuleAsync(
            python, project.ProjectDirectory, "ruff",
            ["check", "--output-format=json", "."], AnalyzeLimit, cancellationToken).ConfigureAwait(false);
        if (ruff is not null)
        {
            output = JoinOutput(output, "ruff", ruff.Output);
        }

        return FromProcess(core, "analyze",
            core.Success ? "Python AST and syntax analysis completed." : "Python analysis could not complete.", output);
    }

    public async Task<AliLanguageOperationResult> FormatAsync(
        AliResolvedLanguageProject project,
        CancellationToken cancellationToken)
    {
        var python = RequirePython(project);
        var lint = await TryRunModuleAsync(
            python, project.ProjectDirectory, "ruff",
            ["check", "--fix", "."], FormatLimit, cancellationToken).ConfigureAwait(false);
        if (lint is null)
        {
            return Unavailable("format", "Python formatting requires the optional Ruff package; no source files were changed.");
        }

        var format = await TryRunModuleAsync(
            python, project.ProjectDirectory, "ruff",
            ["format", "."], FormatLimit, cancellationToken).ConfigureAwait(false);
        if (format is null)
        {
            return Unavailable("format", "Ruff disappeared before formatting could run; no formatting result is claimed.");
        }

        var success = lint.Success && format.Success;
        return new AliLanguageOperationResult(
            success, Id, "format",
            success ? "Ruff repaired and formatted the Python project." : "Ruff reported a formatting or repair failure.",
            success ? 0 : Math.Max(lint.ExitCode, format.ExitCode),
            lint.DurationMilliseconds + format.DurationMilliseconds,
            JoinOutput(lint.Output, "ruff format", format.Output), []);
    }

    public async Task<AliLanguageOperationResult> BuildAsync(
        AliResolvedLanguageProject project,
        string? configuration,
        CancellationToken cancellationToken)
    {
        var python = RequirePython(project);
        var result = await AliBoundedProcessRunner.RunAsync(
            python,
            project.ProjectDirectory,
            ["-m", "compileall", "-q", "-f", "."],
            BuildLimit,
            cancellationToken).ConfigureAwait(false);
        return FromProcess(result, "build",
            result.Success
                ? "CPython compiled every discovered source file successfully."
                : "CPython found a syntax or byte-code compilation failure.");
    }

    public async Task<AliLanguageOperationResult> TestAsync(
        AliResolvedLanguageProject project,
        string? configuration,
        CancellationToken cancellationToken)
    {
        var python = RequirePython(project);
        var evidenceDirectory = Path.Combine(project.ProjectDirectory, ".ali", "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        var report = Path.Combine(evidenceDirectory, "python-tests.xml");
        var pytest = await TryRunModuleAsync(
            python, project.ProjectDirectory, "pytest",
            ["-q", "--disable-warnings", "--maxfail=25", $"--junitxml={report}"],
            TestLimit, cancellationToken).ConfigureAwait(false);

        BoundedProcessResult result;
        IReadOnlyList<string> artifacts;
        string runner;
        if (pytest is not null)
        {
            result = pytest;
            artifacts = File.Exists(report) ? [report] : [];
            runner = "pytest";
        }
        else
        {
            result = await AliBoundedProcessRunner.RunAsync(
                python, project.ProjectDirectory,
                ["-m", "unittest", "discover", "-v"],
                TestLimit, cancellationToken).ConfigureAwait(false);
            artifacts = [];
            runner = "unittest";
        }

        return new AliLanguageOperationResult(
            result.Success, Id, "test",
            result.Success ? $"Python {runner} tests passed." : $"Python {runner} tests failed or timed out.",
            result.ExitCode, result.DurationMilliseconds, result.Output, artifacts);
    }

    private string? ResolvePython(string? projectDirectory)
    {
        var configured = Environment.GetEnvironmentVariable("ALI_PYTHON_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        // Prefer Ali's verified portable runtime. Never execute a project-supplied
        // .venv interpreter merely because an untrusted checkout contains one.
        return ResolveBundledPython()
            ?? _locator.Resolve(OperatingSystem.IsWindows() ? "python.exe" : "python3");
    }

    private static string? ResolveBundledPython()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtime", "python", OperatingSystem.IsWindows() ? "python.exe" : "python3"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts",
                "runtime-assets", "win-x64", "runtime", "python", "python.exe"))
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private string RequirePython(AliResolvedLanguageProject project) =>
        ResolvePython(project.ProjectDirectory)
        ?? throw new InvalidOperationException("No trusted Python runtime is available for this project.");

    private static string? ResolveAnalyzer()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Modules", "Coding", "Python", "Tools", "ali_python_analyzer.py"),
            Path.Combine(AppContext.BaseDirectory, "dependencies", "coding", "python", "ali_python_analyzer.py")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private string? ResolveOptionalExecutable(string name, string environmentVariable, string? projectDirectory) =>
        _locator.Resolve(OperatingSystem.IsWindows() ? name + ".exe" : name, environmentVariable, projectDirectory);

    private static AliLanguageToolchainStatus ModuleStatus(string module, string? python, string purpose)
    {
        var packageRoot = Path.Combine(AppContext.BaseDirectory, "runtime", "python-coding-packages");
        var available = python is not null &&
            (Directory.Exists(Path.Combine(packageRoot, module.Replace('-', '_')))
             || Directory.Exists(packageRoot) && Directory.EnumerateDirectories(packageRoot, module.Replace('-', '_') + "-*.dist-info", SearchOption.TopDirectoryOnly)
                 .Any());
        return new AliLanguageToolchainStatus(module, available, available ? python : null, purpose);
    }

    private static async Task<BoundedProcessResult?> TryRunModuleAsync(
        string python,
        string workingDirectory,
        string module,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var check = await AliBoundedProcessRunner.RunAsync(
            python, workingDirectory,
            ["-c", $"import importlib.util,sys;sys.exit(0 if importlib.util.find_spec('{module}') else 17)"],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (!check.Success)
        {
            return null;
        }

        return await AliBoundedProcessRunner.RunAsync(
            python, workingDirectory, ["-m", module, .. arguments], timeout, cancellationToken).ConfigureAwait(false);
    }

    private AliLanguageOperationResult FromProcess(
        BoundedProcessResult result,
        string operation,
        string summary,
        string? output = null) =>
        new(result.Success, Id, operation, summary, result.ExitCode, result.DurationMilliseconds,
            output ?? result.Output, []);

    private AliLanguageOperationResult Unavailable(string operation, string summary) =>
        new(false, Id, operation, summary, null, 0, string.Empty, []);

    private static string JoinOutput(string first, string label, string second) =>
        string.IsNullOrWhiteSpace(second) ? first : $"{first}{Environment.NewLine}{label}:{Environment.NewLine}{second}".Trim();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
