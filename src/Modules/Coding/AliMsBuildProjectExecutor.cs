using System.Text;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;

namespace Ali.Modules.Coding;

internal sealed record MsBuildExecutionResult(
    bool Success,
    int ExitCode,
    string Output,
    string ToolsetPath,
    bool TimedOut = false);

/// <summary>
/// Executes restore/build through Microsoft's in-process MSBuild API. This class is
/// deliberately separated so its MSBuild types are not loaded until after Locator has
/// registered the installed SDK.
/// </summary>
internal static class AliMsBuildProjectExecutor
{
    private const int BuildTimeoutSeconds = 180;
    private static readonly SemaphoreSlim BuildLock = new(1, 1);

    public static async Task<MsBuildExecutionResult> BuildAsync(
        string projectPath,
        string configuration,
        CancellationToken cancellationToken)
        => await ExecuteAsync(projectPath, configuration, ["Restore", "Build"], cancellationToken).ConfigureAwait(false);

    public static async Task<MsBuildExecutionResult> ExecuteAsync(
        string targetPath,
        string configuration,
        IReadOnlyList<string> targets,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (targets.Count == 0 || targets.Any(target => target is not ("Restore" or "Build")))
        {
            throw new ArgumentException("Only the bounded Restore and Build MSBuild targets are permitted.", nameof(targets));
        }

        var toolsetPath = AliMsBuildRuntime.EnsureRegistered();
        await BuildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(BuildTimeoutSeconds));
            using var manager = new BuildManager("Ali Roslyn/MSBuild");
            using var cancelRegistration = timeout.Token.Register(manager.CancelAllSubmissions);
            var logger = new CapturingLogger();
            var globalProperties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Configuration"] = configuration,
                ["RestoreIgnoreFailedSources"] = "true"
            };
            var request = new BuildRequestData(
                targetPath,
                globalProperties,
                toolsVersion: null,
                targetsToBuild: targets.ToArray(),
                hostServices: null,
                flags: BuildRequestDataFlags.ClearCachesAfterBuild);
            var parameters = new BuildParameters(ProjectCollection.GlobalProjectCollection)
            {
                Loggers = [logger],
                EnableNodeReuse = false,
                MaxNodeCount = 1,
                ShutdownInProcNodeOnBuildFinish = true,
                DetailedSummary = false
            };

            try
            {
                var result = await Task.Run(
                        () => manager.Build(parameters, request),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                var success = result.OverallResult == BuildResultCode.Success;
                return new MsBuildExecutionResult(success, success ? 0 : 1, logger.Output, toolsetPath);
            }
            catch (BuildAbortedException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return new MsBuildExecutionResult(
                    false,
                    -1,
                    $"Build stopped after the {BuildTimeoutSeconds}-second safety timeout.{Environment.NewLine}{logger.Output}",
                    toolsetPath,
                    TimedOut: true);
            }
        }
        finally
        {
            BuildLock.Release();
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly StringBuilder _output = new();

        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Minimal;

        public string? Parameters { get; set; }

        public string Output => _output.ToString().Trim();

        public void Initialize(IEventSource eventSource)
        {
            eventSource.ErrorRaised += (_, args) => Append("error", args.File, args.LineNumber, args.Code, args.Message ?? "Unknown MSBuild error.");
            eventSource.WarningRaised += (_, args) => Append("warning", args.File, args.LineNumber, args.Code, args.Message ?? "Unknown MSBuild warning.");
            eventSource.MessageRaised += (_, args) =>
            {
                if (args.Importance == MessageImportance.High && !string.IsNullOrWhiteSpace(args.Message))
                {
                    _output.AppendLine(args.Message.Trim());
                }
            };
        }

        public void Shutdown()
        {
        }

        private void Append(string severity, string? file, int line, string? code, string message)
        {
            if (!string.IsNullOrWhiteSpace(file))
            {
                _output.Append(file);
                if (line > 0)
                {
                    _output.Append('(').Append(line).Append(')');
                }

                _output.Append(": ");
            }

            _output.Append(severity);
            if (!string.IsNullOrWhiteSpace(code))
            {
                _output.Append(' ').Append(code);
            }

            _output.Append(": ").AppendLine(message);
        }
    }
}
