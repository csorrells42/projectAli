using System.Text.Json;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Coding.Languages;

namespace Ali.Modules.Coding.Web;

internal sealed class AliWebLanguageProvider : IAliLanguageProvider
{
    private static readonly TimeSpan OperationLimit = TimeSpan.FromMinutes(10);
    private readonly AliToolchainLocator _locator;

    public AliWebLanguageProvider(AliToolchainLocator locator) => _locator = locator;

    public string Id => "web-node";
    public string DisplayName => "Unified Web / Node.js";
    public IReadOnlySet<AliProgrammingLanguage> Languages { get; } = new HashSet<AliProgrammingLanguage>
        { AliProgrammingLanguage.Html, AliProgrammingLanguage.Css, AliProgrammingLanguage.JavaScript, AliProgrammingLanguage.TypeScript };
    public AliLanguageCapability Capabilities => AliLanguageCapability.Inspect | AliLanguageCapability.Index
        | AliLanguageCapability.Analyze | AliLanguageCapability.Format | AliLanguageCapability.Build
        | AliLanguageCapability.Test | AliLanguageCapability.Run | AliLanguageCapability.Debug
        | AliLanguageCapability.Dependencies | AliLanguageCapability.Architecture | AliLanguageCapability.Coverage
        | AliLanguageCapability.Profile;

    public bool CanHandle(AliResolvedLanguageProject project) => Languages.Contains(project.Language);

    public AliLanguageCapability GetAvailableCapabilities(AliResolvedLanguageProject? project = null)
    {
        var tools = InspectToolchains(project);
        var available = AliLanguageCapability.Inspect | AliLanguageCapability.Index
            | AliLanguageCapability.Analyze | AliLanguageCapability.Dependencies
            | AliLanguageCapability.Architecture;
        if (tools.First(item => item.Name == "node").Available)
        {
            available |= AliLanguageCapability.Build | AliLanguageCapability.Test
                | AliLanguageCapability.Run | AliLanguageCapability.Profile;
        }
        if (tools.First(item => item.Name == "prettier").Available) available |= AliLanguageCapability.Format;
        if (tools.First(item => item.Name == "vscode-js-debug").Available) available |= AliLanguageCapability.Debug;
        return available;
    }

    public IReadOnlyList<AliLanguageToolchainStatus> InspectToolchains(AliResolvedLanguageProject? project = null)
    {
        var node = ResolveNode();
        var root = project?.ProjectDirectory;
        return
        [
            new("node", node is not null, node, "Portable JavaScript runtime, syntax checks, test runner, and inspector"),
            ScriptStatus("typescript", root, Path.Combine("node_modules", "typescript", "bin", "tsc"), "TypeScript analysis and Language Server Protocol"),
            ScriptStatus("eslint", root, Path.Combine("node_modules", "eslint", "bin", "eslint.js"), "JavaScript and TypeScript linting"),
            ScriptStatus("prettier", root, Path.Combine("node_modules", "prettier", "bin", "prettier.cjs"), "Unified web formatting"),
            ScriptStatus("vscode-js-debug", root, Path.Combine("node_modules", "@vscode", "js-debug", "src", "dapDebugServer.js"), "Debug Adapter Protocol")
        ];
    }

    public async Task<AliLanguageOperationResult> AnalyzeAsync(AliResolvedLanguageProject project, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var report = await AliWebProjectAnalyzer.AnalyzeAsync(project.ProjectDirectory, cancellationToken).ConfigureAwait(false);
        var output = JsonSerializer.Serialize(report);
        var node = ResolveNode();
        var tsc = LocalScript(project.ProjectDirectory, Path.Combine("node_modules", "typescript", "bin", "tsc"));
        if (node is not null && tsc is not null)
        {
            var typed = await AliBoundedProcessRunner.RunAsync(node, project.ProjectDirectory,
                [tsc, "--noEmit", "--pretty", "false"], OperationLimit, cancellationToken).ConfigureAwait(false);
            output = Join(output, "TypeScript", typed.Output);
            started.Stop();
            return new(typed.Success && report.Diagnostics.All(item => item.Severity != "error"), Id, "analyze",
                typed.Success ? "Web structure and TypeScript analysis completed." : "TypeScript analysis reported errors.",
                typed.ExitCode, started.ElapsedMilliseconds, output, []);
        }

        started.Stop();
        var success = report.Diagnostics.All(item => item.Severity != "error");
        return new(success, Id, "analyze",
            success ? "Bounded HTML, JSON, CSS, JavaScript, and TypeScript structure checks completed." : "Web structure checks reported errors.",
            success ? 0 : 1, started.ElapsedMilliseconds, output, []);
    }

    public async Task<AliLanguageOperationResult> FormatAsync(AliResolvedLanguageProject project, CancellationToken cancellationToken)
    {
        var node = ResolveNode();
        var prettier = LocalScript(project.ProjectDirectory, Path.Combine("node_modules", "prettier", "bin", "prettier.cjs"))
            ?? LocalScript(project.ProjectDirectory, Path.Combine("node_modules", "prettier", "bin-prettier.js"));
        if (node is null || prettier is null) return Missing("format", "This project does not contain Prettier; no files were changed.");
        var result = await AliBoundedProcessRunner.RunAsync(node, project.ProjectDirectory,
            [prettier, "--write", ".", "--ignore-unknown"], OperationLimit, cancellationToken).ConfigureAwait(false);
        return From(result, "format", result.Success ? "Prettier formatted the web workspace." : "Prettier reported a formatting failure.");
    }

    public async Task<AliLanguageOperationResult> BuildAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken)
    {
        var node = RequireNode();
        if (HasScript(project.ProjectDirectory, "build")) return await RunNpmAsync(node, project.ProjectDirectory, "build", "build", cancellationToken).ConfigureAwait(false);
        var tsc = LocalScript(project.ProjectDirectory, Path.Combine("node_modules", "typescript", "bin", "tsc"));
        if (tsc is not null)
        {
            var result = await AliBoundedProcessRunner.RunAsync(node, project.ProjectDirectory, [tsc, "--pretty", "false"], OperationLimit, cancellationToken).ConfigureAwait(false);
            return From(result, "build", result.Success ? "TypeScript build completed." : "TypeScript build failed.");
        }
        var check = await CheckJavaScriptAsync(node, project.ProjectDirectory, cancellationToken).ConfigureAwait(false);
        return From(check, "build", check.Success ? "Every JavaScript module passed Node syntax validation." : "Node found a JavaScript syntax failure.");
    }

    public async Task<AliLanguageOperationResult> TestAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken)
    {
        var node = RequireNode();
        if (HasScript(project.ProjectDirectory, "test")) return await RunNpmAsync(node, project.ProjectDirectory, "test", "test", cancellationToken).ConfigureAwait(false);
        var result = await AliBoundedProcessRunner.RunAsync(node, project.ProjectDirectory, ["--test"], OperationLimit, cancellationToken).ConfigureAwait(false);
        return From(result, "test", result.Success ? "Node's native web tests passed." : "Node's native web tests failed.");
    }

    public async Task<AliLanguageOperationResult> RunAsync(AliResolvedLanguageProject project, string? configuration, CancellationToken cancellationToken)
    {
        var node = RequireNode();
        if (project.PhysicalPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || project.PhysicalPath.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)
            || project.PhysicalPath.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase))
        {
            var direct = await AliBoundedProcessRunner.RunAsync(node, project.ProjectDirectory,
                [project.PhysicalPath], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            return From(direct, "run", direct.Success ? "Node executed the selected JavaScript entry point." : "Node execution failed or timed out.");
        }
        if (HasScript(project.ProjectDirectory, "start"))
        {
            return await RunNpmAsync(node, project.ProjectDirectory, "start", "run", cancellationToken).ConfigureAwait(false);
        }
        return Missing("run", "Select a JavaScript source file or define a package.json start script before running this web project.");
    }

    private string? ResolveNode()
    {
        var configured = Environment.GetEnvironmentVariable("ALI_NODE_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        var bundled = Path.Combine(AppContext.BaseDirectory, "dependencies", "coding", "node", "node.exe");
        if (File.Exists(bundled)) return bundled;
        var staged = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "runtime-assets", "win-x64", "dependencies", "coding", "node", "node.exe"));
        return File.Exists(staged) ? staged : _locator.Resolve(OperatingSystem.IsWindows() ? "node.exe" : "node");
    }

    private string RequireNode() => ResolveNode() ?? throw new InvalidOperationException("Ali's verified Node.js runtime is unavailable.");

    private static AliLanguageToolchainStatus ScriptStatus(string name, string? root, string relative, string purpose)
    {
        var script = root is null ? null : LocalScript(root, relative);
        return new(name, script is not null, script, purpose);
    }

    private static string? LocalScript(string root, string relative)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        var rooted = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rooted, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate) ? candidate : null;
    }

    private static bool HasScript(string root, string name)
    {
        var manifest = Path.Combine(root, "package.json");
        if (!File.Exists(manifest)) return false;
        try
        {
            using var stream = AliCodingExecutionAssetFingerprint.OpenRegularFileNoFollow(
                manifest,
                "The web package manifest is not a regular local file.");
            if (stream.Length > 4L * 1024 * 1024)
            {
                throw new InvalidDataException(
                    "The web package manifest exceeds the fixed 4 MiB bound.");
            }
            using var json = JsonDocument.Parse(stream);
            return json.RootElement.TryGetProperty("scripts", out var scripts)
                && scripts.TryGetProperty(name, out var script)
                && !string.IsNullOrWhiteSpace(script.GetString());
        }
        catch (JsonException) { return false; }
    }

    private static async Task<BoundedProcessResult> CheckJavaScriptAsync(string node, string root, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".js" or ".mjs" or ".cjs")
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Contains("node_modules", StringComparer.OrdinalIgnoreCase))
            .Take(2_000).ToArray();
        var output = new List<string>();
        long duration = 0;
        foreach (var file in files)
        {
            var result = await AliBoundedProcessRunner.RunAsync(node, root, ["--check", file], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            duration += result.DurationMilliseconds;
            if (!string.IsNullOrWhiteSpace(result.Output)) output.Add(result.Output);
            if (!result.Success) return new(false, result.ExitCode, string.Join(Environment.NewLine, output), duration, result.TimedOut);
        }
        return new(true, 0, string.Join(Environment.NewLine, output), duration, false);
    }

    private static async Task<AliLanguageOperationResult> RunNpmAsync(string node, string root, string script, string operation, CancellationToken cancellationToken)
    {
        var npm = Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(npm)) return new(false, "web-node", operation, "The verified Node distribution is missing npm-cli.js.", null, 0, "", []);
        var result = await AliBoundedProcessRunner.RunAsync(node, root, [npm, "run", script, "--"], OperationLimit, cancellationToken).ConfigureAwait(false);
        return From(result, operation, result.Success ? $"npm {script} completed." : $"npm {script} failed.");
    }

    private static AliLanguageOperationResult From(BoundedProcessResult result, string operation, string summary) =>
        new(result.Success, "web-node", operation, summary, result.ExitCode, result.DurationMilliseconds, result.Output, []);
    private static AliLanguageOperationResult Missing(string operation, string summary) => new(false, "web-node", operation, summary, null, 0, "", []);
    private static string Join(string first, string label, string second) => string.IsNullOrWhiteSpace(second) ? first : $"{first}{Environment.NewLine}{label}:{Environment.NewLine}{second}";
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
