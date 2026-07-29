using System.Text;
using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Coding.Languages;

namespace Ali.Modules.Coding.Cpp;

internal sealed record AliMsvcToolchain(string Compiler, IReadOnlyDictionary<string, string> Environment);

internal sealed class AliCppLanguageProvider : IAliLanguageProvider
{
    private static readonly TimeSpan CompileLimit = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TestLimit = TimeSpan.FromMinutes(5);
    private readonly AliToolchainLocator _locator;
    private readonly object _toolchainLock = new();
    private AliMsvcToolchain? _cachedMsvc;
    private bool _msvcResolved;

    public AliCppLanguageProvider(AliToolchainLocator locator) => _locator = locator;
    public string Id => "cpp-msvc";
    public string DisplayName => "C/C++ / Microsoft Visual C++";
    public IReadOnlySet<AliProgrammingLanguage> Languages { get; } = new HashSet<AliProgrammingLanguage> { AliProgrammingLanguage.Cpp };
    public AliLanguageCapability Capabilities => AliLanguageCapability.Inspect | AliLanguageCapability.Index
        | AliLanguageCapability.Analyze | AliLanguageCapability.Format | AliLanguageCapability.Build
        | AliLanguageCapability.Test | AliLanguageCapability.Run | AliLanguageCapability.Debug
        | AliLanguageCapability.Dependencies | AliLanguageCapability.Architecture
        | AliLanguageCapability.Coverage | AliLanguageCapability.Profile;

    public bool CanHandle(AliResolvedLanguageProject project) => project.Language == AliProgrammingLanguage.Cpp;

    public AliLanguageCapability GetAvailableCapabilities(AliResolvedLanguageProject? project = null)
    {
        var tools = InspectToolchains(project);
        var available = AliLanguageCapability.Inspect | AliLanguageCapability.Index
            | AliLanguageCapability.Dependencies | AliLanguageCapability.Architecture;
        if (tools.First(item => item.Name == "MSVC").Available)
        {
            available |= AliLanguageCapability.Analyze | AliLanguageCapability.Build
                | AliLanguageCapability.Test | AliLanguageCapability.Run;
        }
        if (tools.First(item => item.Name == "clang-format").Available) available |= AliLanguageCapability.Format;
        if (tools.First(item => item.Name == "OpenDebugAD7").Available) available |= AliLanguageCapability.Debug;
        return available;
    }

    public IReadOnlyList<AliLanguageToolchainStatus> InspectToolchains(AliResolvedLanguageProject? project = null)
    {
        var msvc = ResolveMsvc();
        return
        [
            new("MSVC", msvc is not null, msvc?.Compiler, "Microsoft C/C++ compiler, linker, PDB symbols, and static diagnostics"),
            Located("cmake", "CMake project configuration and build orchestration"),
            Located("ninja", "Fast native build execution"),
            Located("clangd", "Language Server Protocol"),
            Located("clang-format", "C and C++ formatting"),
            Located("clang-tidy", "Clang semantic lint and modernization checks"),
            Located("OpenDebugAD7", "Debug Adapter Protocol when Visual Studio C++ debugging components are installed")
        ];
    }

    public async Task<AliLanguageOperationResult> AnalyzeAsync(AliResolvedLanguageProject project, CancellationToken cancellationToken)
    {
        var toolchain = RequireMsvc();
        var sources = SourceFiles(project.ProjectDirectory);
        if (sources.Count == 0) return Missing("analyze", "No C or C++ translation units were found.");
        var transcript = new StringBuilder();
        long duration = 0;
        foreach (var source in sources)
        {
            var result = await RunCompilerAsync(toolchain, project.ProjectDirectory,
                ["/nologo", "/std:c++20", "/EHsc", "/W4", "/permissive-", "/Zs", $"/I{project.ProjectDirectory}", source],
                CompileLimit, cancellationToken).ConfigureAwait(false);
            duration += result.DurationMilliseconds;
            if (!string.IsNullOrWhiteSpace(result.Output)) transcript.AppendLine(result.Output);
            if (!result.Success)
                return new(false, Id, "analyze", "MSVC reported C/C++ diagnostics.", result.ExitCode, duration, transcript.ToString().Trim(), []);
        }
        return new(true, Id, "analyze", "MSVC completed C/C++ syntax, type, and warning analysis.", 0, duration, transcript.ToString().Trim(), []);
    }

    public async Task<AliLanguageOperationResult> FormatAsync(AliResolvedLanguageProject project, CancellationToken cancellationToken)
    {
        var formatter = _locator.Resolve(OperatingSystem.IsWindows() ? "clang-format.exe" : "clang-format");
        if (formatter is null) return Missing("format", "clang-format is not installed; no C/C++ source files were changed.");
        var files = SourceAndHeaderFiles(project.ProjectDirectory);
        var result = await AliBoundedProcessRunner.RunAsync(formatter, project.ProjectDirectory,
            ["-i", "--style=file", .. files], CompileLimit, cancellationToken).ConfigureAwait(false);
        return From(result, "format", result.Success ? "clang-format formatted the C/C++ project." : "clang-format failed.", []);
    }

    public async Task<AliLanguageOperationResult> BuildAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken)
    {
        var sources = ProductionSources(project.ProjectDirectory);
        if (sources.Count == 0) return Missing("build", "No production C or C++ translation units were found.");
        var toolchain = RequireMsvc();
        var outputDirectory = Path.Combine(project.ProjectDirectory, ".ali", "build", "cpp");
        var objectDirectory = Path.Combine(outputDirectory, "obj");
        Directory.CreateDirectory(objectDirectory);
        var executable = Path.Combine(outputDirectory, "application.exe");
        var optimization = string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase) ? "/O2" : "/Od";
        var result = await RunCompilerAsync(toolchain, project.ProjectDirectory,
            ["/nologo", "/std:c++20", "/EHsc", optimization, "/Zi", $"/I{project.ProjectDirectory}", $"/Fo{objectDirectory}{Path.DirectorySeparatorChar}", $"/Fe{executable}", .. sources],
            CompileLimit, cancellationToken).ConfigureAwait(false);
        return From(result, "build", result.Success ? "MSVC built the native application and PDB symbols." : "Native C/C++ build failed.",
            result.Success && File.Exists(executable) ? [executable] : []);
    }

    public async Task<AliLanguageOperationResult> TestAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken)
    {
        var allSources = SourceFiles(project.ProjectDirectory);
        var tests = allSources.Where(IsTestSource).Take(100).ToArray();
        if (tests.Length == 0) return Missing("test", "No native test translation units were found; Ali will not claim tests ran.");
        var support = allSources.Where(file => !IsTestSource(file) && !ContainsMain(file)).ToArray();
        var toolchain = RequireMsvc();
        var outputDirectory = Path.Combine(project.ProjectDirectory, ".ali", "evidence", "cpp-tests");
        Directory.CreateDirectory(outputDirectory);
        var transcript = new StringBuilder();
        var artifacts = new List<string>();
        long duration = 0;
        foreach (var test in tests)
        {
            var name = Path.GetFileNameWithoutExtension(test);
            var testDirectory = Path.Combine(outputDirectory, name);
            var objectDirectory = Path.Combine(testDirectory, "obj");
            Directory.CreateDirectory(objectDirectory);
            var executable = Path.Combine(testDirectory, name + ".exe");
            var compile = await RunCompilerAsync(toolchain, project.ProjectDirectory,
                ["/nologo", "/std:c++20", "/EHsc", "/Od", "/Zi", $"/I{project.ProjectDirectory}",
                    $"/Fo{objectDirectory}{Path.DirectorySeparatorChar}", $"/Fe{executable}", .. support, test],
                CompileLimit, cancellationToken).ConfigureAwait(false);
            duration += compile.DurationMilliseconds;
            transcript.AppendLine($"compile {name}: {compile.Output}");
            if (!compile.Success) return new(false, Id, "test", $"Native test {name} did not compile.", compile.ExitCode, duration, transcript.ToString().Trim(), []);
            var run = await AliBoundedProcessRunner.RunAsync(executable, project.ProjectDirectory, [], TestLimit, cancellationToken).ConfigureAwait(false);
            duration += run.DurationMilliseconds;
            transcript.AppendLine($"run {name}: {run.Output}");
            artifacts.Add(executable);
            if (!run.Success) return new(false, Id, "test", $"Native test {name} failed.", run.ExitCode, duration, transcript.ToString().Trim(), artifacts);
        }
        return new(true, Id, "test", $"{tests.Length} native test executable(s) compiled and passed.", 0, duration, transcript.ToString().Trim(), artifacts);
    }

    public async Task<AliLanguageOperationResult> RunAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken)
    {
        var build = await BuildAsync(project, configuration, cancellationToken).ConfigureAwait(false);
        if (!build.Success || build.Artifacts.Count == 0)
        {
            return build with { Operation = "run", Summary = "Native execution could not start because the build did not produce an executable." };
        }
        var result = await AliBoundedProcessRunner.RunAsync(
            build.Artifacts[0],
            project.ProjectDirectory,
            [],
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        return From(result, "run", result.Success ? "The native application executed successfully." : "The native application failed or timed out.", build.Artifacts);
    }

    private static Task<BoundedProcessResult> RunCompilerAsync(AliMsvcToolchain toolchain, string root, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token) =>
        AliBoundedProcessRunner.RunAsync(toolchain.Compiler, root, arguments, timeout, token, toolchain.Environment);

    private AliMsvcToolchain? ResolveMsvc()
    {
        lock (_toolchainLock)
        {
            if (_msvcResolved) return _cachedMsvc;
            _cachedMsvc = DiscoverMsvc();
            _msvcResolved = true;
            return _cachedMsvc;
        }
    }

    private static AliMsvcToolchain? DiscoverMsvc()
    {
        var configured = Environment.GetEnvironmentVariable("ALI_CL_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) && File.Exists(configured))
            return new(Path.GetFullPath(configured), CurrentEnvironment());

        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio")
        };
        var compiler = roots.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "cl.exe", SearchOption.AllDirectories))
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}VC{Path.DirectorySeparatorChar}Tools{Path.DirectorySeparatorChar}MSVC{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && path.Contains($"{Path.DirectorySeparatorChar}Hostx64{Path.DirectorySeparatorChar}x64{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (compiler is null) return null;

        var msvcRoot = Directory.GetParent(Directory.GetParent(Directory.GetParent(Directory.GetParent(compiler)!.FullName)!.FullName)!.FullName)!.FullName;
        var kits = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
        var includeRoot = Path.Combine(kits, "Include");
        var sdk = Directory.Exists(includeRoot)
            ? Directory.EnumerateDirectories(includeRoot).Select(Path.GetFileName)
                .Where(name => name is not null && Directory.Exists(Path.Combine(kits, "Lib", name, "um", "x64")))
                .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            : null;
        if (sdk is null) return null;

        var include = string.Join(';', new[]
        {
            Path.Combine(msvcRoot, "include"), Path.Combine(kits, "Include", sdk, "ucrt"),
            Path.Combine(kits, "Include", sdk, "shared"), Path.Combine(kits, "Include", sdk, "um"),
            Path.Combine(kits, "Include", sdk, "winrt"), Path.Combine(kits, "Include", sdk, "cppwinrt")
        }.Where(Directory.Exists));
        var libraries = string.Join(';', new[]
        {
            Path.Combine(msvcRoot, "lib", "x64"), Path.Combine(kits, "Lib", sdk, "ucrt", "x64"), Path.Combine(kits, "Lib", sdk, "um", "x64")
        }.Where(Directory.Exists));
        var paths = string.Join(';', new[]
        {
            Path.GetDirectoryName(compiler)!, Path.Combine(kits, "bin", sdk, "x64"), Environment.GetEnvironmentVariable("PATH") ?? string.Empty
        });
        return new(compiler, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["INCLUDE"] = include, ["LIB"] = libraries, ["PATH"] = paths });
    }

    private AliMsvcToolchain RequireMsvc() => ResolveMsvc()
        ?? throw new InvalidOperationException("Microsoft C++ Build Tools with the x64 compiler and Windows SDK are required for native C++ operations.");
    private static IReadOnlyDictionary<string, string> CurrentEnvironment() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["INCLUDE"] = Environment.GetEnvironmentVariable("INCLUDE") ?? string.Empty,
        ["LIB"] = Environment.GetEnvironmentVariable("LIB") ?? string.Empty,
        ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty
    };
    private AliLanguageToolchainStatus Located(string name, string purpose)
    {
        var executable = _locator.Resolve(name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        return new(name, executable is not null, executable, purpose);
    }
    private static IReadOnlyList<string> SourceFiles(string root) => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
        .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".c" or ".cc" or ".cpp" or ".cxx")
        .Where(NotGenerated).Take(10_000).ToArray();
    private static IReadOnlyList<string> SourceAndHeaderFiles(string root) => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
        .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hh" or ".hpp" or ".hxx")
        .Where(NotGenerated).Take(10_000).ToArray();
    private static IReadOnlyList<string> ProductionSources(string root)
    {
        var sources = SourceFiles(root).Where(file => !IsTestSource(file)).ToArray();
        return sources.Any(ContainsMain) ? sources : SourceFiles(root);
    }
    private static bool NotGenerated(string path) => !path.Split(Path.DirectorySeparatorChar)
        .Any(part => part is ".git" or ".vs" or "build" or "out" or ".ali" or "cmake-build-debug" or "cmake-build-release");
    private static bool IsTestSource(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Contains("test", StringComparison.OrdinalIgnoreCase) || name.Contains("spec", StringComparison.OrdinalIgnoreCase);
    }
    private static bool ContainsMain(string path)
    {
        try { return File.ReadAllText(path).Contains("main(", StringComparison.Ordinal); }
        catch { return false; }
    }
    private AliLanguageOperationResult From(BoundedProcessResult result, string operation, string summary, IReadOnlyList<string> artifacts) =>
        new(result.Success, Id, operation, summary, result.ExitCode, result.DurationMilliseconds, result.Output, artifacts);
    private AliLanguageOperationResult Missing(string operation, string summary) => new(false, Id, operation, summary, null, 0, "", []);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
