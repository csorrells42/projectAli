using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Runtime;

namespace Ali.Modules.Coding.Agents;

internal sealed class AiderCodingAgentProvider(
    string installRoot,
    Func<OpenAiCompatibleRuntimeOptions> runtimeSettings,
    IExternalCodingAgentProcessRunner processRunner) : IExternalCodingAgentProvider
{
    private readonly string _python = Path.Combine(installRoot, "runtime", "python", "python.exe");
    private readonly string _packages = Path.Combine(installRoot, "runtime", "aider-packages");

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

        var result = await processRunner.RunAsync(
            _python,
            installRoot,
            ["-m", "aider", "--version"],
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
        var arguments = new List<string>
        {
            "-m", "aider",
            "--model", model,
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
            "--analytics-disable",
            "--no-check-update",
            "--encoding", "utf-8",
            "--message", objective
        };
        if (!string.IsNullOrWhiteSpace(runtime.ReasoningEffort))
        {
            arguments.InsertRange(arguments.Count - 2, ["--reasoning-effort", runtime.ReasoningEffort]);
        }

        var result = await processRunner.RunAsync(
            _python,
            projectDirectory,
            arguments,
            cancellationToken,
            Environment()).ConfigureAwait(false);
        return new ExternalCodingAgentPassResult(
            Name,
            result.Success,
            result.ExitCode,
            result.DurationMilliseconds,
            result.Success ? "Aider completed its architect/edit pass." : "Aider exited without completing its architect/edit pass.",
            result.Output);
    }

    private Dictionary<string, string> Environment() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PYTHONPATH"] = _packages,
        ["PYTHONNOUSERSITE"] = "1",
        ["PYTHONUTF8"] = "1",
        ["NO_COLOR"] = "1"
    };

    private ExternalCodingAgentProviderStatus NotReady(string summary) =>
        new(Name, false, "not installed", summary, _python);
}
