using System.Diagnostics;
using System.Text;
using Ali.Modules.Coding.Execution;

namespace Ali.Modules.Coding.Infrastructure;

internal sealed record BoundedProcessResult(bool Success, int ExitCode, string Output, long DurationMilliseconds, bool TimedOut);

/// <summary>Runs a fixed executable and caller-supplied ArgumentList without invoking a shell.</summary>
internal static class AliBoundedProcessRunner
{
    public static async Task<BoundedProcessResult> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        await RunAsync(executable, workingDirectory, arguments, timeout, cancellationToken, environment: null).ConfigureAwait(false);

    public static async Task<BoundedProcessResult> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment,
        Action<string, bool>? outputLine = null) =>
        await RunCoreAsync(
                executable,
                workingDirectory,
                arguments,
                timeout,
                cancellationToken,
                environment,
                outputLine,
                exactDotNetHost: null,
                beforeProcessStart: null)
            .ConfigureAwait(false);

    internal static async Task<BoundedProcessResult> RunAsync(
        AliBoundExecutionFile exactDotNetHost,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        await RunCoreAsync(
                exactDotNetHost?.PhysicalPath
                    ?? throw new ArgumentNullException(nameof(exactDotNetHost)),
                workingDirectory,
                arguments,
                timeout,
                cancellationToken,
                environment: null,
                outputLine: null,
                exactDotNetHost,
                beforeProcessStart: null)
            .ConfigureAwait(false);

    internal static async Task<BoundedProcessResult> RunAsync(
        AliBoundExecutionFile exactExecutable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action beforeProcessStart) =>
        await RunCoreAsync(
                exactExecutable?.PhysicalPath
                    ?? throw new ArgumentNullException(nameof(exactExecutable)),
                workingDirectory,
                arguments,
                timeout,
                cancellationToken,
                environment: null,
                outputLine: null,
                exactExecutable,
                beforeProcessStart ?? throw new ArgumentNullException(nameof(beforeProcessStart)))
            .ConfigureAwait(false);

    private static async Task<BoundedProcessResult> RunCoreAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment,
        Action<string, bool>? outputLine,
        AliBoundExecutionFile? exactDotNetHost,
        Action? beforeProcessStart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The process timeout must be greater than zero and no more than 24 hours.");
        }

        var resolvedExecutable = AliCodingInvocationExecutionContext.ValidateProcessLaunch(
            executable,
            arguments);
        using var executedAssetLeases =
            AliCodingInvocationExecutionContext.AcquireExecutedAssetLeases(arguments);
        var expectedExecutable = exactDotNetHost
            ?? AliCodingExecutionAssetFingerprint.CaptureRequiredExecutable(
                resolvedExecutable,
                "The validated bounded process executable");
        var allowedExecutableHardLinkRoot =
            AliCodingExecutionAssetFingerprint.ResolveAllowedWindowsExecutableHardLinkRoot(
                expectedExecutable.PhysicalPath);
        using var executableLease = AliExecutionFileLease.Acquire(
            expectedExecutable.PhysicalPath,
            "The exact bounded process executable",
            allowedExecutableHardLinkRoot,
            () =>
            {
                var current = AliCodingExecutionAssetFingerprint.CaptureRequiredExecutable(
                    expectedExecutable.PhysicalPath,
                    "The exact bounded process executable");
                if (current != expectedExecutable)
                {
                    throw new InvalidOperationException(
                        "The exact bounded process executable changed after authorization.");
                }
            });
        var workingDirectoryBinding = AliExecutionDirectoryBinding.Capture(
            workingDirectory,
            "The bounded process working directory spine");
        using var workingDirectoryLease = workingDirectoryBinding.Acquire(
            "The exact bounded process working directory spine");
        var started = Stopwatch.StartNew();
        var startInfo = new ProcessStartInfo(resolvedExecutable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (environment is not null)
        {
            foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;
        }
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var outputSync = new object();
        void Capture(string? line, bool isError)
        {
            if (line is null)
            {
                return;
            }

            lock (outputSync)
            {
                output.AppendLine(line);
            }
            outputLine?.Invoke(line, isError);
        }

        process.OutputDataReceived += (_, args) => Capture(args.Data, isError: false);
        process.ErrorDataReceived += (_, args) => Capture(args.Data, isError: true);
        var processStarted = false;
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            if (exactDotNetHost is not null)
            {
                var exactPath = AliExactDotNetHost.Revalidate(exactDotNetHost);
                if (!string.Equals(
                        exactPath,
                        resolvedExecutable,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The exact .NET host no longer matches the validated process executable.");
                }
                process.StartInfo.FileName = exactPath;
            }
            executableLease.RequireStable();
            workingDirectoryLease.RequireStable();
            executedAssetLeases.RequireStable();
            beforeProcessStart?.Invoke();
            executableLease.RequireStable();
            workingDirectoryLease.RequireStable();
            executedAssetLeases.RequireStable();
            var finalExecutable = AliCodingInvocationExecutionContext.ValidateProcessLaunch(
                process.StartInfo.FileName,
                arguments);
            executedAssetLeases.RequireStable();
            if (!string.Equals(
                    Path.GetFullPath(finalExecutable),
                    Path.GetFullPath(expectedExecutable.PhysicalPath),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The bounded process executable changed at the process-start boundary.");
            }
            process.StartInfo.FileName = expectedExecutable.PhysicalPath;
            process.Start();
            processStarted = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                executableLease.RequireStartedProcessImage(process);
            }
            catch (Exception exception) when (
                (exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception)
                && process.HasExited)
            {
                // A very short-lived child can retire before Windows returns its image path.
                // The executable and its directory spine stayed held no-write/no-delete
                // across Process.Start, so the exact held pathname remains authoritative.
                executableLease.RequireStable();
                workingDirectoryLease.RequireStable();
            }
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            started.Stop();
            return Result(
                process.ExitCode == 0,
                process.ExitCode,
                timedOut: false);
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process, processStarted);
            started.Stop();
            return Result(success: false, exitCode: -1, timedOut: true);
        }
        catch
        {
            KillProcessTree(process, processStarted);
            throw;
        }

        BoundedProcessResult Result(bool success, int exitCode, bool timedOut)
        {
            string captured;
            lock (outputSync)
            {
                captured = output.ToString();
            }
            return new(
                success,
                exitCode,
                Compact(captured),
                started.ElapsedMilliseconds,
                timedOut);
        }
    }

    private static void KillProcessTree(Process process, bool processStarted)
    {
        if (!processStarted)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5_000);
            }
        }
        catch
        {
            // Preserve the original timeout, cancellation, or execution failure. Callers receive
            // no false success and the process handle is still disposed by the owning using scope.
        }
    }

    private static string Compact(string value)
    {
        const int maximum = 30_000;
        var normalized = value.ReplaceLineEndings(Environment.NewLine).Trim();
        return normalized.Length <= maximum ? normalized : normalized[^maximum..];
    }
}
