using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Coordinator;
using Ali.Modules.Runtime;
using System.Globalization;

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
            ["-d", distro, "--exec", "bash", "-lc", "OPENHANDS_SUPPRESS_BANNER=1 PYTHONWARNINGS=ignore::DeprecationWarning \"$HOME/.local/bin/openhands\" --version"],
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
        var launcherPath = Path.Combine(
            AppContext.BaseDirectory,
            "Modules",
            "Coding",
            "Agents",
            "Tools",
            "ali_openhands_launcher.py");
        if (!File.Exists(launcherPath))
        {
            return new ExternalCodingAgentPassResult(Name, false, -1, 0,
                "Ali's OpenHands launcher is missing from the Release folder.", string.Empty);
        }

        var linuxLauncherPath = await ResolveLinuxPathAsync(distro, launcherPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(linuxLauncherPath))
        {
            return new ExternalCodingAgentPassResult(Name, false, -1, 0,
                "OpenHands could not map Ali's launcher into WSL.", string.Empty);
        }

        var model = runtime.Model.StartsWith("openai/", StringComparison.OrdinalIgnoreCase)
            ? runtime.Model
            : $"openai/{runtime.Model}";
        var baseUrl = await ResolveWslRuntimeEndpointAsync(distro, runtime.Endpoint, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new ExternalCodingAgentPassResult(Name, false, -1, 0,
                "OpenHands is installed, but WSL cannot reach Ali's local model endpoint. Enable WSL mirrored networking with tools/SetupOpenHands.ps1, then retry.", string.Empty);
        }

        var reasoningEffort = NormalizeReasoningEffort(runtime.ReasoningEffort);
        var environment = new List<string>
        {
            "ALI_OPENHANDS_API_KEY=local-ali-runtime",
            $"ALI_OPENHANDS_MODEL={model}",
            $"ALI_OPENHANDS_BASE_URL={baseUrl}",
            $"ALI_OPENHANDS_CONTEXT_TOKENS={runtime.ContextTokens.ToString(CultureInfo.InvariantCulture)}",
            $"ALI_OPENHANDS_MAX_OUTPUT_TOKENS={runtime.OutputTokenLimit.ToString(CultureInfo.InvariantCulture)}",
            $"ALI_OPENHANDS_TEMPERATURE={runtime.Temperature.ToString(CultureInfo.InvariantCulture)}",
            $"ALI_OPENHANDS_REASONING_EFFORT={reasoningEffort}",
            "OPENHANDS_SUPPRESS_BANNER=1",
            "PYTHONWARNINGS=ignore::DeprecationWarning",
            "NO_COLOR=1"
        };
        if (runtime.TopP.HasValue)
        {
            environment.Add($"ALI_OPENHANDS_TOP_P={runtime.TopP.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        using var taskFile = await ExternalCodingAgentTemporaryFile
            .CreateAsync(objective, ".txt", cancellationToken)
            .ConfigureAwait(false);
        var linuxTaskFile = await ResolveLinuxPathAsync(distro, taskFile.Path, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(linuxTaskFile))
        {
            return new ExternalCodingAgentPassResult(Name, false, -1, 0,
                "OpenHands could not map Ali's task into WSL.", string.Empty);
        }

        var arguments = new List<string>
        {
            "-d", distro,
            "--cd", linuxPath,
            "--exec", "/usr/bin/env"
        };
        arguments.AddRange(environment);
        arguments.AddRange(
        [
            "bash",
            "-lc",
            "exec \"$HOME/.local/share/ali-openhands-tools/openhands/bin/python\" \"$@\"",
            "ali-openhands-python",
            linuxLauncherPath,
            "--headless",
            "--json",
            "--always-approve",
            "--exit-without-confirmation",
            "--file", linuxTaskFile
        ]);
        var result = await processRunner.RunAsync(
            WslExecutable,
            Environment.SystemDirectory,
            arguments,
            cancellationToken).ConfigureAwait(false);
        var completed = result.Success
            && result.Output.Contains("\"kind\": \"FinishObservation\"", StringComparison.Ordinal);
        return new ExternalCodingAgentPassResult(
            Name,
            completed,
            result.ExitCode,
            result.DurationMilliseconds,
            completed
                ? "OpenHands completed its autonomous implementation pass."
                : "OpenHands exited without a finish event, so Ali will not treat the pass as complete.",
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

        if (await CanWslReachEndpointAsync(distro, endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false))
        {
            return endpoint.ToString().TrimEnd('/');
        }

        var route = await processRunner.RunAsync(
            WslExecutable,
            Environment.SystemDirectory,
            ["-d", distro, "--exec", "ip", "route", "show", "default"],
            cancellationToken).ConfigureAwait(false);
        if (!route.Success || string.IsNullOrWhiteSpace(route.Output))
        {
            return string.Empty;
        }

        var fields = route.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var via = Array.FindIndex(fields, field => field.Equals("via", StringComparison.OrdinalIgnoreCase));
        if (via < 0 || via + 1 >= fields.Length)
        {
            return string.Empty;
        }

        var builder = new UriBuilder(endpoint) { Host = fields[via + 1] };
        return await CanWslReachEndpointAsync(distro, builder.Host, builder.Port, cancellationToken).ConfigureAwait(false)
            ? builder.Uri.ToString().TrimEnd('/')
            : string.Empty;
    }

    private async Task<bool> CanWslReachEndpointAsync(
        string distro,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        const string probe = "import socket,sys; connection=socket.create_connection((sys.argv[1],int(sys.argv[2])),2); connection.close()";
        var result = await processRunner.RunAsync(
            WslExecutable,
            Environment.SystemDirectory,
            ["-d", distro, "--exec", "python3", "-c", probe, host, port.ToString(CultureInfo.InvariantCulture)],
            cancellationToken).ConfigureAwait(false);
        return result.Success;
    }

    private ExternalCodingAgentProviderStatus NotReady(string summary) =>
        new(Name, false, "not installed", summary, WslExecutable);

    private static string NormalizeReasoningEffort(string? effort) =>
        effort?.Trim().ToLowerInvariant() switch
        {
            "medium" => "medium",
            "high" => "high",
            _ => "low"
        };
}
