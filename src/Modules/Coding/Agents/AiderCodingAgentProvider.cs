using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Runtime;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ali.Modules.Coding.Agents;

internal sealed class AiderCodingAgentProvider(
    string installRoot,
    Func<OpenAiCompatibleRuntimeOptions> runtimeSettings,
    IExternalCodingAgentProcessRunner processRunner) : IExternalCodingAgentProvider
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
            result.Success ? "Aider's scripted architect mode is ready." : $"Aider failed its version check: {result.Output}",
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

        var result = await processRunner.RunAsync(
            _python,
            projectDirectory,
            arguments,
            cancellationToken,
            Environment()).ConfigureAwait(false);
        var completed = result.Success
            && !result.Output.Contains("failed due to:", StringComparison.OrdinalIgnoreCase)
            && !result.Output.Contains("Traceback (most recent call last):", StringComparison.Ordinal);
        return new ExternalCodingAgentPassResult(
            Name,
            completed,
            result.ExitCode,
            result.DurationMilliseconds,
            completed
                ? "Aider completed its architect/edit pass."
                : "Aider reported an execution failure, so Ali will not treat the pass as complete.",
            result.Output);
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
