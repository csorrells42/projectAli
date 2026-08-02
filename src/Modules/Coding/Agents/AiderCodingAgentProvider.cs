using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Runtime;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ali.Modules.Coding.Agents;

internal sealed class AiderCodingAgentProvider(
    string installRoot,
    Func<OpenAiCompatibleRuntimeOptions> runtimeSettings,
    IExternalCodingAgentProcessRunner processRunner,
    Action<ExternalCodingAgentProgress>? progress = null) : IExternalCodingAgentProvider
{
    private readonly string _python = Path.Combine(installRoot, "runtime", "python", "python.exe");
    private readonly string _packages = Path.Combine(installRoot, "runtime", "aider-packages");
    private readonly string _launcher = Path.Combine(
        installRoot,
        "Modules",
        "Coding",
        "Agents",
        "Tools",
        "ali_aider_launcher.py");

    public string Name => "Aider";

    public async Task<ExternalCodingAgentProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_python))
        {
            return NotReady("Ali's portable Python runtime is missing.");
        }
        if (!Directory.Exists(Path.Combine(_packages, "aider")))
        {
            return NotReady("Aider packages are not provisioned. Restore Ali's runtime assets to install the pinned Aider package group.");
        }
        if (!File.Exists(_launcher))
        {
            return NotReady("Ali's Aider launcher is missing from the Release folder.");
        }

        var result = await processRunner.RunAsync(
            _python,
            installRoot,
            [_launcher, "--version"],
            cancellationToken,
            Environment()).ConfigureAwait(false);
        return new ExternalCodingAgentProviderStatus(
            Name,
            result.Success,
            result.Success ? result.Output.Trim() : "unknown",
            result.Success ? "Aider's autonomous architect, edit, lint and build/test repair loop is ready." : $"Aider failed its version check: {result.Output}",
            _python);
    }

    public async Task<ExternalCodingAgentPassResult> ExecuteAsync(
        string projectDirectory,
        string objective,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.Ready)
        {
            return new ExternalCodingAgentPassResult(Name, false, -1, 0, status.Summary, string.Empty);
        }

        var runtime = runtimeSettings();
        var baseUrl = runtime.Endpoint.ToString().TrimEnd('/');
        var model = runtime.Model.StartsWith("openai/", StringComparison.OrdinalIgnoreCase)
            ? runtime.Model
            : $"openai/{runtime.Model}";
        var maximumInputTokens = Math.Max(1, runtime.ContextTokens - runtime.OutputTokenLimit);
        var reasoningEffort = NormalizeReasoningEffort(runtime.ReasoningEffort);
        using var taskFile = await ExternalCodingAgentTemporaryFile
            .CreateAsync(objective, ".txt", cancellationToken)
            .ConfigureAwait(false);
        using var metadataFile = await ExternalCodingAgentTemporaryFile
            .CreateAsync(BuildModelMetadata(model, maximumInputTokens, runtime.OutputTokenLimit, runtime.ContextTokens), ".json", cancellationToken)
            .ConfigureAwait(false);
        using var modelSettingsFile = await ExternalCodingAgentTemporaryFile
            .CreateAsync(BuildModelSettings(model, runtime, reasoningEffort), ".yml", cancellationToken)
            .ConfigureAwait(false);
        var arguments = new List<string>
        {
            _launcher,
            "--model", model,
            "--model-metadata-file", metadataFile.Path,
            "--model-settings-file", modelSettingsFile.Path,
            "--openai-api-base", baseUrl,
            "--openai-api-key", "local-ali-runtime",
            "--architect",
            "--auto-accept-architect",
            "--yes-always",
            "--auto-lint",
            "--no-auto-commits",
            "--no-dirty-commits",
            "--no-gitignore",
            "--no-stream",
            "--no-pretty",
            "--no-show-model-warnings",
            "--analytics-disable",
            "--no-check-update",
            "--encoding", "utf-8",
            "--message-file", taskFile.Path
        };
        var verificationCommand = ResolveAutomaticVerificationCommand(projectDirectory);
        if (!string.IsNullOrWhiteSpace(verificationCommand))
        {
            arguments.Add("--test-cmd");
            arguments.Add(verificationCommand);
            arguments.Add("--auto-test");
        }
        arguments.AddRange(ResolveInitialProjectFiles(projectDirectory));

        progress?.Invoke(new ExternalCodingAgentProgress(
            Name,
            ExternalCodingAgentProgressKind.Started,
            "Aider accepted the coding job",
            "Aider is inspecting the repository and performing its bounded approved collaboration pass."));
        var result = await processRunner.RunAsync(
            _python,
            projectDirectory,
            arguments,
            cancellationToken,
            Environment(),
            (line, isError) => ReportProgressLine(line, isError, progress)).ConfigureAwait(false);
        var completed = result.Success
            && !result.Output.Contains("failed due to:", StringComparison.OrdinalIgnoreCase)
            && !result.Output.Contains("Traceback (most recent call last):", StringComparison.Ordinal);
        return new ExternalCodingAgentPassResult(
            Name,
            completed,
            result.ExitCode,
            result.DurationMilliseconds,
            completed
                ? "Aider completed its native architect, edit, lint, build/test and repair loop."
                : "Aider's autonomous implementation loop reported an execution failure, so Ali will not treat the work as complete.",
            result.Output);
    }

    private static void ReportProgressLine(
        string line,
        bool isError,
        Action<ExternalCodingAgentProgress>? progress)
    {
        if (progress is null || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var text = line.Trim();
        if (text.StartsWith("Applied edit to ", StringComparison.OrdinalIgnoreCase))
        {
            progress(new ExternalCodingAgentProgress(
                "Aider",
                ExternalCodingAgentProgressKind.Working,
                "Aider updated a project file",
                text));
        }
        else if (text.Contains("Running", StringComparison.OrdinalIgnoreCase)
                 && text.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            progress(new ExternalCodingAgentProgress(
                "Aider",
                ExternalCodingAgentProgressKind.Working,
                "Aider is verifying the project",
                text));
        }
        else if (text.Contains("failed", StringComparison.OrdinalIgnoreCase) || isError)
        {
            progress(new ExternalCodingAgentProgress(
                "Aider",
                ExternalCodingAgentProgressKind.Warning,
                "Aider is repairing a failed step",
                text.Length <= 240 ? text : $"{text[..240]}…"));
        }
    }

    private static string? ResolveAutomaticVerificationCommand(string projectDirectory)
    {
        var candidates = Directory
            .EnumerateFiles(projectDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path) is ".sln" or ".slnx" or ".csproj")
            .OrderBy(path => Path.GetExtension(path) is ".sln" or ".slnx" ? 0 : 1)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var target = Path.GetFileName(candidates[0]).Replace("\"", "\"\"");
        return $"dotnet build \"{target}\" --configuration Release --nologo";
    }

    private static IReadOnlyList<string> ResolveInitialProjectFiles(string projectDirectory)
    {
        if (Directory.Exists(Path.Combine(projectDirectory, ".git")))
        {
            return [];
        }

        var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".csproj", ".sln", ".slnx", ".xaml",
            ".py", ".pyi", ".toml",
            ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts",
            ".html", ".htm", ".css", ".scss", ".sass", ".less", ".json",
            ".java", ".gradle", ".xml",
            ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".ixx", ".cppm",
            ".ino", ".yaml", ".yml"
        };
        return Directory
            .EnumerateFiles(projectDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => supportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => Path.GetExtension(path) is ".sln" or ".slnx" or ".csproj" ? 0 : 1)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Take(32)
            .ToArray();
    }

    private Dictionary<string, string> Environment() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ALI_AIDER_PACKAGES"] = _packages,
        ["PYTHONNOUSERSITE"] = "1",
        ["PYTHONUTF8"] = "1",
        ["NO_COLOR"] = "1"
    };

    private static string BuildModelMetadata(
        string model,
        int maximumInputTokens,
        int maximumOutputTokens,
        int contextTokens) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [model] = new Dictionary<string, object>
            {
                ["max_tokens"] = contextTokens,
                ["max_input_tokens"] = maximumInputTokens,
                ["max_output_tokens"] = maximumOutputTokens,
                ["input_cost_per_token"] = 0,
                ["output_cost_per_token"] = 0,
                ["litellm_provider"] = "openai",
                ["mode"] = "chat"
            }
        });

    private static string BuildModelSettings(
        string model,
        OpenAiCompatibleRuntimeOptions runtime,
        string reasoningEffort)
    {
        var settings = new StringBuilder()
            .AppendLine($"- name: {model}")
            .AppendLine("  edit_format: diff")
            .AppendLine("  editor_edit_format: whole")
            .AppendLine("  use_repo_map: true")
            .AppendLine($"  use_temperature: {runtime.Temperature.ToString(CultureInfo.InvariantCulture)}")
            .AppendLine("  extra_params:")
            .AppendLine($"    max_tokens: {runtime.OutputTokenLimit.ToString(CultureInfo.InvariantCulture)}");
        if (runtime.TopP.HasValue)
        {
            settings.AppendLine($"    top_p: {runtime.TopP.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        settings
            .AppendLine("    extra_body:")
            .AppendLine("      chat_template_kwargs:")
            .AppendLine($"        reasoning_effort: {reasoningEffort}");
        return settings.ToString();
    }

    private static string NormalizeReasoningEffort(string? effort) =>
        effort?.Trim().ToLowerInvariant() switch
        {
            "medium" => "medium",
            "high" => "high",
            _ => "low"
        };

    private ExternalCodingAgentProviderStatus NotReady(string summary) =>
        new(Name, false, "not installed", summary, _python);
}
