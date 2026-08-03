using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Coding.Toolchains;

namespace Ali.Modules.Coding.Native;

public sealed record AliGnuNativeToolchainReport(
    bool Success,
    string Summary,
    string? Gcc,
    string? Gxx,
    string? Gdb,
    string? Make,
    string? CMake,
    string? Ninja,
    string Version,
    string Runtime,
    IReadOnlyList<string> Capabilities);

internal sealed class AliGnuNativeTools(
    AliLanguageProjectResolver resolver,
    AliToolchainLocator locator)
{
    public AliGnuNativeToolchainReport Inspect()
    {
        var gxx = Existing(AliDeveloperToolPaths.Gxx);
        var gcc = Existing(AliDeveloperToolPaths.Gcc);
        var gdb = Existing(AliDeveloperToolPaths.Gdb);
        var make = Existing(AliDeveloperToolPaths.MingwMake);
        var cmake = ResolveCMake();
        var ninja = ResolveNinja();
        var available = gxx is not null && gcc is not null;
        return new(
            available,
            available
                ? "MSYS2 UCRT64 GCC is ready. It uses the same modern Universal C Runtime family as Visual C++."
                : "MSYS2 UCRT64 GCC is not provisioned. Run tools/RestoreCodingToolchains.ps1.",
            gcc,
            gxx,
            gdb,
            make,
            cmake,
            ninja,
            gxx is null ? "unavailable" : ReadVersion(gxx),
            "MSYS2 UCRT64 / MinGW-w64 / libstdc++",
            [
                "C++20 compile, link, and execute",
                "GCC warnings and standards conformance diagnostics",
                "Native test executable compilation and execution",
                "CMake and Ninja generation when installed",
                "GDB-compatible debug symbols and debugger when installed"
            ]);
    }

    public Task<AliLanguageOperationResult> ExecuteAsync(
        string targetPath,
        string operation,
        string? configuration,
        CancellationToken cancellationToken)
    {
        var project = resolver.Resolve(targetPath);
        if (project.Language != AliProgrammingLanguage.Cpp)
            throw new ArgumentException("GNU native operations require a C or C++ project target.", nameof(targetPath));
        return operation.Trim().ToLowerInvariant() switch
        {
            "analyze" => AnalyzeAsync(project, cancellationToken),
            "build" => BuildAsync(project, configuration, cancellationToken),
            "test" => TestAsync(project, configuration, cancellationToken),
            "run" => RunAsync(project, configuration, cancellationToken),
            _ => throw new ArgumentException("GNU native operation must be analyze, build, test, or run.", nameof(operation))
        };
    }

    private async Task<AliLanguageOperationResult> AnalyzeAsync(
        AliResolvedLanguageProject project,
        CancellationToken cancellationToken)
    {
        var sources = SourceFiles(project.ProjectDirectory);
        if (sources.Count == 0) return Missing("analyze", "No C or C++ translation units were found.");
        var result = await RunGxxAsync(
            project.ProjectDirectory,
            ["-std=c++20", "-Wall", "-Wextra", "-Wpedantic", "-fsyntax-only", "-I", project.ProjectDirectory, .. sources],
            TimeSpan.FromMinutes(15),
            cancellationToken).ConfigureAwait(false);
        return From(result, "analyze",
            result.Success ? "GCC completed C++20 syntax, type, warning, and conformance analysis." : "GCC reported native diagnostics.",
            []);
    }

    private async Task<AliLanguageOperationResult> BuildAsync(
        AliResolvedLanguageProject project,
        string? configuration,
        CancellationToken cancellationToken)
    {
        if (HasFunctionalCMakeProject(project.ProjectDirectory))
            return await BuildCMakeAsync(project, configuration, cancellationToken).ConfigureAwait(false);

        var sources = ProductionSources(project.ProjectDirectory);
        if (sources.Count == 0) return Missing("build", "No production C or C++ translation units were found.");
        var outputDirectory = Path.Combine(project.ProjectDirectory, ".ali", "build", "gcc");
        Directory.CreateDirectory(outputDirectory);
        var executable = Path.Combine(outputDirectory, "application.exe");
        var optimization = string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase)
            ? new[] { "-O0", "-g3" }
            : new[] { "-O2", "-g" };
        var result = await RunGxxAsync(
            project.ProjectDirectory,
            ["-std=c++20", "-Wall", "-Wextra", "-Wpedantic", .. optimization, "-I", project.ProjectDirectory, .. sources, "-o", executable],
            TimeSpan.FromMinutes(20),
            cancellationToken).ConfigureAwait(false);
        return From(result, "build",
            result.Success ? "GCC linked the native application with UCRT64 and emitted GNU debug symbols." : "GCC native build failed.",
            result.Success && File.Exists(executable) ? [executable] : []);
    }

    private async Task<AliLanguageOperationResult> BuildCMakeAsync(
        AliResolvedLanguageProject project,
        string? configuration,
        CancellationToken cancellationToken)
    {
        var cmake = ResolveCMake() ?? throw new InvalidOperationException("CMake is required for this native project.");
        var ninja = ResolveNinja() ?? throw new InvalidOperationException("Ninja is required for this native CMake project.");
        var buildType = string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
        var output = Path.Combine(project.ProjectDirectory, ".ali", "build", "gcc-cmake", buildType);
        Directory.CreateDirectory(output);
        var environment = NativeEnvironment(ninja);
        var configure = await AliBoundedProcessRunner.RunAsync(
            cmake,
            project.ProjectDirectory,
            ["-S", project.ProjectDirectory, "-B", output, "-G", "Ninja", $"-DCMAKE_BUILD_TYPE={buildType}",
                $"-DCMAKE_C_COMPILER={Require(AliDeveloperToolPaths.Gcc, "GCC")}",
                $"-DCMAKE_CXX_COMPILER={Require(AliDeveloperToolPaths.Gxx, "G++")}",
                "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON"],
            TimeSpan.FromMinutes(10),
            cancellationToken,
            environment).ConfigureAwait(false);
        if (!configure.Success)
            return From(configure, "build", "CMake could not configure the GCC/Ninja project.", []);
        var build = await AliBoundedProcessRunner.RunAsync(
            cmake,
            project.ProjectDirectory,
            ["--build", output, "--parallel"],
            TimeSpan.FromMinutes(20),
            cancellationToken,
            environment).ConfigureAwait(false);
        var artifacts = build.Success
            ? Directory.EnumerateFiles(output, "*.exe", SearchOption.AllDirectories)
                .Where(path => !path.Contains("CMakeFiles", StringComparison.OrdinalIgnoreCase))
                .Take(100)
                .ToArray()
            : [];
        return From(build, "build",
            build.Success ? "CMake and Ninja built the project with GCC UCRT64." : "The GCC CMake build failed.",
            artifacts);
    }

    private async Task<AliLanguageOperationResult> TestAsync(
        AliResolvedLanguageProject project,
        string? configuration,
        CancellationToken cancellationToken)
    {
        if (HasFunctionalCMakeProject(project.ProjectDirectory))
        {
            var build = await BuildCMakeAsync(project, configuration, cancellationToken).ConfigureAwait(false);
            if (!build.Success) return build with { Operation = "test", Summary = "Native tests could not start because the GCC CMake build failed." };
            var cmake = ResolveCMake()!;
            var buildType = string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
            var output = Path.Combine(project.ProjectDirectory, ".ali", "build", "gcc-cmake", buildType);
            var test = await AliBoundedProcessRunner.RunAsync(
                cmake,
                project.ProjectDirectory,
                ["--build", output, "--target", "test"],
                TimeSpan.FromMinutes(10),
                cancellationToken,
                NativeEnvironment(ResolveNinja()!)).ConfigureAwait(false);
            return From(test, "test", test.Success ? "CTest completed the GCC native test target." : "CTest reported native failures.", build.Artifacts);
        }

        var allSources = SourceFiles(project.ProjectDirectory);
        var tests = allSources.Where(IsTestSource).Take(100).ToArray();
        if (tests.Length == 0) return Missing("test", "No native test translation units were found; Ali will not claim tests ran.");
        var support = allSources.Where(file => !IsTestSource(file) && !ContainsMain(file)).ToArray();
        var root = Path.Combine(project.ProjectDirectory, ".ali", "evidence", "gcc-tests");
        Directory.CreateDirectory(root);
        var transcript = new List<string>();
        var artifacts = new List<string>();
        long duration = 0;
        foreach (var test in tests)
        {
            var executable = Path.Combine(root, Path.GetFileNameWithoutExtension(test) + ".exe");
            var compile = await RunGxxAsync(project.ProjectDirectory,
                ["-std=c++20", "-Wall", "-Wextra", "-Wpedantic", "-O0", "-g3", "-I", project.ProjectDirectory, .. support, test, "-o", executable],
                TimeSpan.FromMinutes(15), cancellationToken).ConfigureAwait(false);
            duration += compile.DurationMilliseconds;
            transcript.Add(compile.Output);
            if (!compile.Success)
                return new(false, "cpp-gcc", "test", $"GCC could not compile {Path.GetFileName(test)}.", compile.ExitCode, duration, string.Join(Environment.NewLine, transcript), []);
            var run = await AliBoundedProcessRunner.RunAsync(executable, project.ProjectDirectory, [], TimeSpan.FromMinutes(5), cancellationToken, NativeEnvironment(null)).ConfigureAwait(false);
            duration += run.DurationMilliseconds;
            transcript.Add(run.Output);
            artifacts.Add(executable);
            if (!run.Success)
                return new(false, "cpp-gcc", "test", $"GNU native test {Path.GetFileName(test)} failed.", run.ExitCode, duration, string.Join(Environment.NewLine, transcript), artifacts);
        }
        return new(true, "cpp-gcc", "test", $"{tests.Length} GNU native test executable(s) compiled and passed.", 0, duration, string.Join(Environment.NewLine, transcript), artifacts);
    }

    private async Task<AliLanguageOperationResult> RunAsync(
        AliResolvedLanguageProject project,
        string? configuration,
        CancellationToken cancellationToken)
    {
        var build = await BuildAsync(project, configuration, cancellationToken).ConfigureAwait(false);
        var executable = build.Artifacts.FirstOrDefault(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (!build.Success || executable is null)
            return build with { Operation = "run", Summary = "GNU native execution could not start because no executable was produced." };
        var run = await AliBoundedProcessRunner.RunAsync(executable, project.ProjectDirectory, [], TimeSpan.FromMinutes(2), cancellationToken, NativeEnvironment(null)).ConfigureAwait(false);
        return From(run, "run", run.Success ? "The GCC-built application executed successfully." : "The GCC-built application failed or timed out.", build.Artifacts);
    }

    private static Task<BoundedProcessResult> RunGxxAsync(
        string root,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var executable = Require(AliDeveloperToolPaths.Gxx, "MSYS2 UCRT64 G++");
        var environment = NativeEnvironment(null);
        var managedRoot = ResolveAliManagedMsys2Root(executable);
        return managedRoot is null
            ? AliBoundedProcessRunner.RunAsync(
                executable,
                root,
                arguments,
                timeout,
                cancellationToken,
                environment)
            : AliBoundedProcessRunner.RunAsync(
                executable,
                root,
                arguments,
                timeout,
                cancellationToken,
                environment,
                managedRoot);
    }

    private static string? ResolveAliManagedMsys2Root(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var managedRoot = Path.GetFullPath(Path.Combine(
            AliDeveloperToolPaths.ToolchainRoot,
            "msys64"));
        var selected = Path.GetFullPath(executable);
        return string.Equals(managedRoot, selected, StringComparison.OrdinalIgnoreCase)
               || selected.StartsWith(
                   managedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase)
            ? managedRoot
            : null;
    }

    private string? ResolveCMake() => locator.Resolve("cmake.exe")
        ?? AliDeveloperToolPaths.DiscoverVisualStudio().Select(item => item.CMake).FirstOrDefault(File.Exists);

    private string? ResolveNinja() => locator.Resolve("ninja.exe")
        ?? AliDeveloperToolPaths.DiscoverVisualStudio().Select(item => item.Ninja).FirstOrDefault(File.Exists);

    private static IReadOnlyDictionary<string, string> NativeEnvironment(string? ninja)
    {
        var paths = new[]
        {
            Path.GetDirectoryName(AliDeveloperToolPaths.Gxx),
            Path.GetDirectoryName(ninja),
            Environment.GetEnvironmentVariable("PATH")
        }.Where(path => !string.IsNullOrWhiteSpace(path));
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = string.Join(Path.PathSeparator, paths),
            ["MSYSTEM"] = "UCRT64"
        };
    }

    private static string Require(string path, string name) => File.Exists(path)
        ? path
        : throw new InvalidOperationException($"{name} is not installed. Run tools/RestoreCodingToolchains.ps1.");

    private static string? Existing(string path) => File.Exists(path) ? path : null;
    private static string ReadVersion(string executable)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            if (process is null) return "unknown";
            var line = process.StandardOutput.ReadLine() ?? "unknown";
            process.WaitForExit(5_000);
            return line.Trim();
        }
        catch { return "unknown"; }
    }

    private static bool HasFunctionalCMakeProject(string root)
    {
        var file = Path.Combine(root, "CMakeLists.txt");
        if (!File.Exists(file)) return false;
        try
        {
            var text = File.ReadAllText(file);
            return text.Contains("add_executable", StringComparison.OrdinalIgnoreCase)
                || text.Contains("add_library", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static IReadOnlyList<string> SourceFiles(string root) => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
        .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".c" or ".cc" or ".cpp" or ".cxx")
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

    private static AliLanguageOperationResult From(BoundedProcessResult result, string operation, string summary, IReadOnlyList<string> artifacts) =>
        new(result.Success, "cpp-gcc", operation, summary, result.ExitCode, result.DurationMilliseconds, result.Output, artifacts);

    private static AliLanguageOperationResult Missing(string operation, string summary) =>
        new(false, "cpp-gcc", operation, summary, null, 0, string.Empty, []);
}
