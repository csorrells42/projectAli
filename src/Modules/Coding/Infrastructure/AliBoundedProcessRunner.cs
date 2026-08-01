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
        IReadOnlyDictionary<string, string>? environment,
        Action<string, bool>? outputLine = null)
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
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            started.Stop();
            string captured;
            lock (outputSync)
            {
                captured = output.ToString();
            }
            return new BoundedProcessResult(process.ExitCode == 0, process.ExitCode, Compact(captured), started.ElapsedMilliseconds, false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    private static string Compact(string value)
    {
        const int maximum = 30_000;
        var normalized = value.ReplaceLineEndings(Environment.NewLine).Trim();
        return normalized.Length <= maximum ? normalized : normalized[^maximum..];
    }
}
