using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Coordinator;
using Ali.Modules.Runtime;

namespace Ali.Modules.Coding.Agents;

internal sealed class OpenHandsCodingAgentProvider(
    Func<AgentOrchestrationSettings> orchestrationSettings,
    Func<OpenAiCompatibleRuntimeOptions> runtimeSettings,
    IExternalCodingAgentProcessRunner processRunner) : IExternalCodingAgentProvider
{
    private static readonly string WslExecutable = Path.Combine(Environment.SystemDirectory, "wsl.exe");

    public string Name => "OpenHands";

    public async Task<ExternalCodingAgentProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(WslExecutable))
        {
            return NotReady("Windows Subsystem for Linux is unavailable. OpenHands officially requires WSL on Windows.");
        }

        var distro = orchestrationSettings().Normalize().OpenHandsWslDistribution;
        var result = await processRunner.RunAsync(
            WslExecutable,
            Environment.SystemDirectory,
            ["-d", distro, "--exec", "openhands", "--version"],
            cancellationToken).ConfigureAwait(false);
        return new ExternalCodingAgentProviderStatus(
            Name,
            result.Success,
            result.Success ? result.Output.Trim() : "not installed",
            result.Success
                ? $"OpenHands headless CLI is ready in WSL distribution '{distro}'."
                : $"OpenHands is not ready in WSL distribution '{distro}': {result.Output}",
            $"{WslExecutable} -d {distro}");
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

        var distro = orchestrationSettings().Normalize().OpenHandsWslDistribution;
        var linuxPath = await ResolveLinuxPathAsync(distro, projectDirectory, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(linuxPath))
        {
            return new ExternalCodingAgentPassResult(Name, false, -1, 0,
                "OpenHands could not map the approved Windows project into WSL.", string.Empty);
        }

        var runtime = runtimeSettings();
        var model = runtime.Model.StartsWith("openai/", StringComparison.OrdinalIgnoreCase)
            ? runtime.Model
            : $"openai/{runtime.Model}";
        var baseUrl = await ResolveWslRuntimeEndpointAsync(distro, runtime.Endpoint, cancellationToken).ConfigureAwait(false);
        var result = await processRunner.RunAsync(
            WslExecutable,
            Environment.SystemDirectory,
            [
                "-d", distro,
                "--cd", linuxPath,
                "--exec", "/usr/bin/env",
                "LLM_API_KEY=local-ali-runtime",
                $"LLM_MODEL={model}",
                $"LLM_BASE_URL={baseUrl}",
                "openhands",
                "--headless",
                "--json",
                "--always-approve",
                "--override-with-envs",
                "--exit-without-confirmation",
                "--task", objective
            ],
            cancellationToken).ConfigureAwait(false);
        return new ExternalCodingAgentPassResult(
            Name,
            result.Success,
            result.ExitCode,
            result.DurationMilliseconds,
            result.Success ? "OpenHands completed its autonomous implementation pass." : "OpenHands exited without completing its implementation pass.",
            result.Output);
    }

    private async Task<string> ResolveLinuxPathAsync(
        string distro,
        string windowsPath,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            WslExecutable,
            Environment.SystemDirectory,
            ["-d", distro, "--exec", "wslpath", "-a", "-u", windowsPath],
            cancellationToken).ConfigureAwait(false);
        return result.Success ? result.Output.Trim() : string.Empty;
    }

    private async Task<string> ResolveWslRuntimeEndpointAsync(
        string distro,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        if (endpoint.Host is not ("127.0.0.1" or "localhost" or "::1"))
        {
            return endpoint.ToString().TrimEnd('/');
        }

        var gateway = await processRunner.RunAsync(
            WslExecutable,
            Environment.SystemDirectory,
            ["-d", distro, "--exec", "awk", "/^nameserver[[:space:]]+/{print $2; exit}", "/etc/resolv.conf"],
            cancellationToken).ConfigureAwait(false);
        if (!gateway.Success || string.IsNullOrWhiteSpace(gateway.Output))
        {
            return endpoint.ToString().TrimEnd('/');
        }

        var builder = new UriBuilder(endpoint) { Host = gateway.Output.Trim() };
        return builder.Uri.ToString().TrimEnd('/');
    }

    private ExternalCodingAgentProviderStatus NotReady(string summary) =>
        new(Name, false, "not installed", summary, WslExecutable);
}
