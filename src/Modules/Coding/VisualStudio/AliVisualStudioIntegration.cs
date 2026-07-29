using System.Diagnostics;
using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Coding.Toolchains;

namespace Ali.Modules.Coding.VisualStudio;

public sealed record AliVisualStudioInstance(
    string DisplayName,
    string Version,
    string InstallationPath,
    bool Ide,
    bool MsBuild,
    bool Msvc,
    bool CMake,
    bool Ninja,
    bool TestPlatform,
    bool ClangFormat,
    bool ClangTidy,
    bool ClangLanguageServer,
    bool NativeDebugAdapter,
    IReadOnlyList<string> IntegratedCapabilities);

public sealed record AliVisualStudioReport(
    bool Success,
    string Summary,
    IReadOnlyList<AliVisualStudioInstance> Instances);

public sealed record AliVisualStudioActionResult(
    bool Success,
    string Summary,
    int? ExitCode,
    long DurationMilliseconds,
    string Output,
    IReadOnlyList<string> Artifacts);

internal sealed class AliVisualStudioIntegration(AliLanguageProjectResolver resolver)
{
    public AliVisualStudioReport Inspect()
    {
        var instances = AliDeveloperToolPaths.DiscoverVisualStudio()
            .Select(instance => new AliVisualStudioInstance(
                instance.DisplayName,
                instance.Version,
                instance.InstallationPath,
                File.Exists(instance.Devenv),
                File.Exists(instance.MsBuild),
                File.Exists(instance.MsvcCompiler),
                File.Exists(instance.CMake),
                File.Exists(instance.Ninja),
                File.Exists(instance.TestConsole),
                File.Exists(instance.ClangFormat),
                File.Exists(instance.ClangTidy),
                File.Exists(instance.ClangD),
                File.Exists(instance.OpenDebugAd7),
                Capabilities(instance)))
            .ToArray();
        return new(
            instances.Length > 0,
            instances.Length == 0
                ? "No launchable Visual Studio instance was discovered. Ali's standalone Roslyn and .NET SDK tools remain available."
                : $"Discovered {instances.Length} Visual Studio instance(s). The report distinguishes installed features from Ali's protocol-level equivalents.",
            instances);
    }

    public async Task<AliVisualStudioActionResult> BuildAsync(
        string targetPath,
        string? configuration,
        string? platform,
        CancellationToken cancellationToken)
    {
        var project = resolver.Resolve(targetPath);
        var instance = RequireInstance(item => File.Exists(item.MsBuild), "MSBuild");
        var target = ResolveBuildTarget(project);
        var evidence = Path.Combine(project.ProjectDirectory, ".ali", "evidence", "visual-studio");
        Directory.CreateDirectory(evidence);
        var binlog = Path.Combine(evidence, "msbuild.binlog");
        var args = new List<string>
        {
            target,
            "/m",
            "/restore",
            "/nologo",
            "/verbosity:minimal",
            $"/p:Configuration={NormalizeConfiguration(configuration)}",
            $"/bl:{binlog}"
        };
        if (!string.IsNullOrWhiteSpace(platform)) args.Add($"/p:Platform={NormalizePlatform(platform)}");
        var result = await AliBoundedProcessRunner.RunAsync(
            instance.MsBuild,
            project.ProjectDirectory,
            args,
            TimeSpan.FromMinutes(20),
            cancellationToken).ConfigureAwait(false);
        return new(
            result.Success,
            result.Success
                ? "Visual Studio MSBuild completed the approved project and wrote a binary build log."
                : "Visual Studio MSBuild reported a project failure.",
            result.ExitCode,
            result.DurationMilliseconds,
            result.Output,
            File.Exists(binlog) ? [binlog] : []);
    }

    public AliVisualStudioActionResult Open(
        string targetPath,
        string? documentPath)
    {
        var project = resolver.Resolve(targetPath);
        var instance = RequireInstance(item => File.Exists(item.Devenv), "Visual Studio IDE");
        var itemToOpen = string.IsNullOrWhiteSpace(documentPath)
            ? ResolveBuildTarget(project)
            : resolver.ResolveDocument(project, documentPath);
        var startInfo = new ProcessStartInfo(instance.Devenv)
        {
            WorkingDirectory = project.ProjectDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(itemToOpen);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Visual Studio did not start.");
        return new(
            true,
            $"Opened {Path.GetFileName(itemToOpen)} in {instance.DisplayName}. The IDE owns the interactive editing and debugging session.",
            null,
            0,
            $"Visual Studio process {process.Id} started.",
            [itemToOpen]);
    }

    private static AliVisualStudioInstancePaths RequireInstance(
        Func<AliVisualStudioInstancePaths, bool> predicate,
        string feature) =>
        AliDeveloperToolPaths.DiscoverVisualStudio().FirstOrDefault(predicate)
        ?? throw new InvalidOperationException($"A Visual Studio instance with {feature} is not installed.");

    private static string ResolveBuildTarget(AliResolvedLanguageProject project)
    {
        var manifest = Path.Combine(project.ProjectDirectory, project.ManifestName);
        return File.Exists(manifest) ? manifest : project.PhysicalPath;
    }

    private static string NormalizeConfiguration(string? value) =>
        string.Equals(value, "Debug", StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";

    private static string NormalizePlatform(string value)
    {
        var normalized = value.Trim();
        return normalized.ToLowerInvariant() switch
        {
            "x64" => "x64",
            "x86" or "win32" => "Win32",
            "arm64" => "ARM64",
            "any cpu" or "anycpu" => "Any CPU",
            _ => throw new ArgumentException("Visual Studio platform must be x64, x86, ARM64, or Any CPU.", nameof(value))
        };
    }

    private static IReadOnlyList<string> Capabilities(AliVisualStudioInstancePaths instance)
    {
        var result = new List<string>
        {
            "Roslyn semantic diagnostics, IntelliSense, navigation, references, and refactoring through Ali",
            "CLR launch/attach debugging, breakpoints, stepping, locals, and evaluation through Ali",
            "Solution/project inspection, dependency engineering, tests, coverage, profiling, and release evidence through Ali"
        };
        if (File.Exists(instance.Devenv)) result.Add("Interactive Visual Studio solution, project, and document launch");
        if (File.Exists(instance.MsBuild)) result.Add("Visual Studio MSBuild with restore, parallel build, and binary logs");
        if (File.Exists(instance.MsvcCompiler)) result.Add("Microsoft Visual C++ x64 compiler, linker, Windows SDK, and PDB symbols");
        if (File.Exists(instance.CMake)) result.Add("Visual Studio CMake project configuration");
        if (File.Exists(instance.Ninja)) result.Add("Visual Studio Ninja native builds");
        if (File.Exists(instance.TestConsole)) result.Add("Visual Studio Test Platform discovery");
        if (File.Exists(instance.ClangFormat)) result.Add("Visual Studio LLVM C/C++ formatting");
        if (File.Exists(instance.ClangTidy)) result.Add("Visual Studio clang-tidy modernization diagnostics");
        if (File.Exists(instance.ClangD)) result.Add("Native clangd Language Server Protocol");
        if (File.Exists(instance.OpenDebugAd7)) result.Add("Native C/C++ Debug Adapter Protocol");
        return result;
    }
}
