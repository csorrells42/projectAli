using System.Text;
using System.Text.RegularExpressions;
using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Coding.Languages;

namespace Ali.Modules.Coding.Java;

internal sealed partial class AliJavaLanguageProvider : IAliLanguageProvider
{
    private static readonly TimeSpan CompileLimit = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TestLimit = TimeSpan.FromMinutes(5);
    private readonly AliToolchainLocator _locator;

    public AliJavaLanguageProvider(AliToolchainLocator locator) => _locator = locator;
    public string Id => "java-temurin";
    public string DisplayName => "Java / Eclipse Temurin JDK";
    public IReadOnlySet<AliProgrammingLanguage> Languages { get; } = new HashSet<AliProgrammingLanguage> { AliProgrammingLanguage.Java };
    public AliLanguageCapability Capabilities => AliLanguageCapability.Inspect | AliLanguageCapability.Index
        | AliLanguageCapability.Analyze | AliLanguageCapability.Format | AliLanguageCapability.Build
        | AliLanguageCapability.Test | AliLanguageCapability.Run | AliLanguageCapability.Debug
        | AliLanguageCapability.Dependencies | AliLanguageCapability.Architecture
        | AliLanguageCapability.Coverage | AliLanguageCapability.Profile;

    public bool CanHandle(AliResolvedLanguageProject project) => project.Language == AliProgrammingLanguage.Java;

    public AliLanguageCapability GetAvailableCapabilities(AliResolvedLanguageProject? project = null)
    {
        var tools = InspectToolchains(project);
        var available = AliLanguageCapability.Inspect | AliLanguageCapability.Index
            | AliLanguageCapability.Dependencies | AliLanguageCapability.Architecture;
        if (tools.First(item => item.Name == "javac").Available)
        {
            available |= AliLanguageCapability.Analyze | AliLanguageCapability.Build | AliLanguageCapability.Test;
        }
        if (tools.First(item => item.Name == "java").Available) available |= AliLanguageCapability.Run;
        if (tools.First(item => item.Name == "jdb").Available) available |= AliLanguageCapability.Debug;
        if (tools.First(item => item.Name == "jfr").Available) available |= AliLanguageCapability.Profile;
        return available;
    }

    public IReadOnlyList<AliLanguageToolchainStatus> InspectToolchains(AliResolvedLanguageProject? project = null)
    {
        var home = ResolveJavaHome();
        var root = project?.ProjectDirectory;
        return
        [
            Tool("java", home, "java", "Runtime and assertions"),
            Tool("javac", home, "javac", "Compiler and static diagnostics"),
            Tool("jdb", home, "jdb", "Command-line debugger"),
            Tool("jcmd", home, "jcmd", "Live process diagnostics"),
            Tool("jfr", home, "jfr", "Java Flight Recorder performance analysis"),
            ProjectTool("Maven", root, "pom.xml", "Dependency, build, and test lifecycle when Maven is installed"),
            ProjectTool("Gradle", root, "build.gradle", "Dependency, build, and test lifecycle when Gradle is installed"),
            ProjectTool("Eclipse JDT LS", root, Path.Combine(".ali", "tools", "jdtls"), "Language Server Protocol")
        ];
    }

    public async Task<AliLanguageOperationResult> AnalyzeAsync(AliResolvedLanguageProject project, CancellationToken cancellationToken)
    {
        // Use one uniquely named leaf directly under Temp. A shared parent can inherit
        // an administrator-only ACL when another elevated toolchain pass creates it.
        var temporary = Path.Combine(Path.GetTempPath(), "AliJavaAnalysis_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var result = await CompileAsync(project.ProjectDirectory, temporary, cancellationToken).ConfigureAwait(false);
            return From(result, "analyze", result.Success
                ? "javac completed Java type, syntax, and lint analysis."
                : "javac reported Java analysis diagnostics.", []);
        }
        finally { try { Directory.Delete(temporary, recursive: true); } catch { } }
    }

    public Task<AliLanguageOperationResult> FormatAsync(AliResolvedLanguageProject project, CancellationToken cancellationToken)
    {
        var formatter = Path.Combine(project.ProjectDirectory, ".ali", "tools", "google-java-format.jar");
        if (!File.Exists(formatter)) return Task.FromResult(Missing("format", "google-java-format is not installed in this project; no files were changed."));
        return FormatCoreAsync(project, formatter, cancellationToken);
    }

    private async Task<AliLanguageOperationResult> FormatCoreAsync(AliResolvedLanguageProject project, string formatter, CancellationToken cancellationToken)
    {
        var java = RequireTool("java");
        var files = SourceFiles(project.ProjectDirectory);
        var result = await AliBoundedProcessRunner.RunAsync(java, project.ProjectDirectory,
            ["-jar", formatter, "--replace", .. files], CompileLimit, cancellationToken).ConfigureAwait(false);
        return From(result, "format", result.Success ? "google-java-format formatted the Java project." : "Java formatting failed.", []);
    }

    public async Task<AliLanguageOperationResult> BuildAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken)
    {
        var output = Path.Combine(project.ProjectDirectory, ".ali", "build", "java");
        Directory.CreateDirectory(output);
        var result = await CompileAsync(project.ProjectDirectory, output, cancellationToken).ConfigureAwait(false);
        return From(result, "build", result.Success ? "javac built the Java project." : "javac failed to build the Java project.", [output]);
    }

    public async Task<AliLanguageOperationResult> TestAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken)
    {
        var build = await BuildAsync(project, configuration, cancellationToken).ConfigureAwait(false);
        if (!build.Success) return build with { Operation = "test", Summary = "Java tests could not run because compilation failed." };
        var output = build.Artifacts[0];
        var candidates = SourceFiles(project.ProjectDirectory)
            .Where(file => Path.GetFileNameWithoutExtension(file).EndsWith("Test", StringComparison.Ordinal))
            .Select(file => (File: file, Class: QualifiedClassName(file)))
            .Where(item => HasMain(item.File)).Take(100).ToArray();
        if (candidates.Length == 0) return Missing("test", "No JUnit runner or executable *Test.java class was found; Ali will not claim tests ran.");

        var java = RequireTool("java");
        var combined = new StringBuilder();
        long duration = 0;
        foreach (var candidate in candidates)
        {
            var result = await AliBoundedProcessRunner.RunAsync(java, project.ProjectDirectory,
                ["-ea", "-cp", output, candidate.Class], TestLimit, cancellationToken).ConfigureAwait(false);
            duration += result.DurationMilliseconds;
            combined.AppendLine($"{candidate.Class}: {result.Output}");
            if (!result.Success) return new(false, Id, "test", $"Java test {candidate.Class} failed.", result.ExitCode, duration, combined.ToString().Trim(), []);
        }
        return new(true, Id, "test", $"{candidates.Length} executable Java test class(es) passed with assertions enabled.", 0, duration, combined.ToString().Trim(), []);
    }

    public async Task<AliLanguageOperationResult> RunAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken)
    {
        var build = await BuildAsync(project, configuration, cancellationToken).ConfigureAwait(false);
        if (!build.Success) return build with { Operation = "run", Summary = "Java execution could not start because compilation failed." };
        var entry = SourceFiles(project.ProjectDirectory).FirstOrDefault(HasMain);
        if (entry is null) return Missing("run", "No Java class containing static void main was found.");
        var result = await AliBoundedProcessRunner.RunAsync(
            RequireTool("java"),
            project.ProjectDirectory,
            ["-cp", build.Artifacts[0], QualifiedClassName(entry)],
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        return From(result, "run", result.Success ? $"Java executed {QualifiedClassName(entry)} successfully." : "Java execution failed or timed out.", build.Artifacts);
    }

    private async Task<BoundedProcessResult> CompileAsync(string root, string output, CancellationToken cancellationToken)
    {
        var sources = SourceFiles(root);
        if (sources.Count == 0) return new(false, 2, "No Java source files were found.", 0, false);
        // javac rejects a UTF-8 BOM even though many Windows editors emit one.
        // Compile normalized generated copies so analysis/build never rewrites user source.
        var sourceStage = Path.Combine(output, ".ali-sources");
        if (Directory.Exists(sourceStage)) Directory.Delete(sourceStage, recursive: true);
        Directory.CreateDirectory(sourceStage);
        var normalizedSources = new List<string>(sources.Count);
        foreach (var source in sources)
        {
            var relative = Path.GetRelativePath(root, source);
            var destination = Path.Combine(sourceStage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var text = await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(destination, text.TrimStart('\uFEFF'), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var package = PackageRegex().Match(text);
            if (package.Success)
            {
                // Some constrained Windows child-process tokens may write files but may not
                // create a new package directory. Create it in Ali's audited parent process.
                Directory.CreateDirectory(Path.Combine(output, package.Groups[1].Value.Replace('.', Path.DirectorySeparatorChar)));
            }
            normalizedSources.Add(destination);
        }
        var argumentFile = Path.Combine(output, "ali-javac.args");
        Directory.CreateDirectory(output);
        var lines = new List<string> { "-encoding", "UTF-8", "-Xlint:all", "-proc:none", "-d", Quote(output) };
        lines.AddRange(normalizedSources.Select(Quote));
        await File.WriteAllLinesAsync(argumentFile, lines, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return await AliBoundedProcessRunner.RunAsync(RequireTool("javac"), root, [$"@{argumentFile}"], CompileLimit, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> SourceFiles(string root) => Directory.EnumerateFiles(root, "*.java", SearchOption.AllDirectories)
        .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is ".git" or ".gradle" or "build" or "target" or ".ali"))
        .Take(10_000).ToArray();

    private string? ResolveJavaHome()
    {
        var configured = Environment.GetEnvironmentVariable("ALI_JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return Path.GetFullPath(configured);
        var bundled = Path.Combine(AppContext.BaseDirectory, "dependencies", "coding", "java");
        if (File.Exists(Path.Combine(bundled, "bin", "javac.exe"))) return bundled;
        var staged = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "runtime-assets", "win-x64", "dependencies", "coding", "java"));
        return File.Exists(Path.Combine(staged, "bin", "javac.exe")) ? staged : null;
    }

    private string RequireTool(string name)
    {
        var home = ResolveJavaHome();
        var bundled = home is null ? null : Path.Combine(home, "bin", name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        return bundled is not null && File.Exists(bundled) ? bundled
            : _locator.Resolve(name + (OperatingSystem.IsWindows() ? ".exe" : ""))
              ?? throw new InvalidOperationException($"Ali's verified Java tool '{name}' is unavailable.");
    }

    private static AliLanguageToolchainStatus Tool(string name, string? home, string executable, string purpose)
    {
        var path = home is null ? null : Path.Combine(home, "bin", executable + (OperatingSystem.IsWindows() ? ".exe" : ""));
        return new(name, path is not null && File.Exists(path), path is not null && File.Exists(path) ? path : null, purpose);
    }
    private static AliLanguageToolchainStatus ProjectTool(string name, string? root, string marker, string purpose)
    {
        var path = root is null ? null : Path.Combine(root, marker);
        return new(name, path is not null && (File.Exists(path) || Directory.Exists(path)), path, purpose);
    }
    private static string Quote(string path) => '"' + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';
    private static bool HasMain(string file) => File.ReadAllText(file).Contains("static void main", StringComparison.Ordinal);
    private static string QualifiedClassName(string file)
    {
        var source = File.ReadAllText(file);
        var package = PackageRegex().Match(source);
        var name = Path.GetFileNameWithoutExtension(file);
        return package.Success ? package.Groups[1].Value + "." + name : name;
    }

    private AliLanguageOperationResult From(BoundedProcessResult result, string operation, string summary, IReadOnlyList<string> artifacts) =>
        new(result.Success, Id, operation, summary, result.ExitCode, result.DurationMilliseconds, result.Output, artifacts);
    private AliLanguageOperationResult Missing(string operation, string summary) => new(false, Id, operation, summary, null, 0, "", []);
    [GeneratedRegex(@"\bpackage\s+([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)\s*;")]
    private static partial Regex PackageRegex();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
