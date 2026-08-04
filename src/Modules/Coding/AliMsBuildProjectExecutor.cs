using System.Diagnostics;
using System.Text;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coding.Infrastructure;

namespace Ali.Modules.Coding;

internal sealed record MsBuildExecutionResult(
    bool Success,
    int ExitCode,
    string Output,
    string ToolsetPath,
    bool TimedOut = false);

/// <summary>
/// Executes restore/build through the exact authorized dotnet host and Microsoft's SDK
/// MSBuild entry point. Restore and build are separate fixed commands so Build always
/// reevaluates the post-restore SDK graph before compiling.
/// </summary>
internal static class AliMsBuildProjectExecutor
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(20);
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
            var exactHost = AliCodingInvocationExecutionContext
                .ResolveDotNetHostBindingForExecution();
            var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(targetPath))
                ?? throw new InvalidDataException(
                    "The selected MSBuild target has no parent directory.");
            var output = new StringBuilder();
            foreach (var target in targets)
            {
                IReadOnlyList<string> arguments = target switch
                {
                    "Restore" =>
                    [
                        "restore",
                        targetPath,
                        "--ignore-failed-sources",
                        "--disable-build-servers",
                        "--nologo"
                    ],
                    "Build" =>
                    [
                        "build",
                        targetPath,
                        "--configuration",
                        configuration,
                        "--no-restore",
                        "--disable-build-servers",
                        "--nologo"
                    ],
                    _ => throw new UnreachableException()
                };
                var result = await AliBoundedProcessRunner.RunAsync(
                        exactHost,
                        workingDirectory,
                        arguments,
                        BuildTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result.Output))
                {
                    output.AppendLine(result.Output.Trim());
                }
                if (!result.Success)
                {
                    return new MsBuildExecutionResult(
                        false,
                        result.ExitCode,
                        output.ToString().Trim(),
                        toolsetPath,
                        result.TimedOut);
                }
            }

            return new MsBuildExecutionResult(
                true,
                0,
                output.ToString().Trim(),
                toolsetPath);
        }
        finally
        {
            BuildLock.Release();
        }
    }
}
