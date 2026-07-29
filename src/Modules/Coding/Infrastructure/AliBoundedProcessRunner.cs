using System.Diagnostics;
using System.Text;

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
        IReadOnlyDictionary<string, string>? environment)
    {
        var started = Stopwatch.StartNew();
        var startInfo = new ProcessStartInfo(executable)
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
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) output.AppendLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) output.AppendLine(args.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            started.Stop();
            return new BoundedProcessResult(process.ExitCode == 0, process.ExitCode, Compact(output.ToString()), started.ElapsedMilliseconds, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            started.Stop();
            return new BoundedProcessResult(false, -1, Compact(output.ToString()), started.ElapsedMilliseconds, true);
        }
    }

    private static string Compact(string value)
    {
        const int maximum = 30_000;
        var normalized = value.ReplaceLineEndings(Environment.NewLine).Trim();
        return normalized.Length <= maximum ? normalized : normalized[^maximum..];
    }
}
