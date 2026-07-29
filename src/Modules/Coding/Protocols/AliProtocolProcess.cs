using System.Diagnostics;

namespace Ali.Modules.Coding.Protocols;

/// <summary>Starts a specifically resolved protocol adapter without invoking a shell.</summary>
internal sealed class AliProtocolProcess : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();

    private AliProtocolProcess(Process process)
    {
        Process = process;
        Transport = new AliContentLengthMessageTransport(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream,
            leaveOpen: true);
    }

    public Process Process { get; }
    public AliContentLengthMessageTransport Transport { get; }
    public CancellationToken Lifetime => _lifetime.Token;

    public static AliProtocolProcess Start(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
        {
            throw new FileNotFoundException("The resolved protocol adapter executable is missing.", executable);
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the protocol adapter.");
        return new AliProtocolProcess(process);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch
        {
        }

        await Transport.DisposeAsync().ConfigureAwait(false);
        Process.Dispose();
        _lifetime.Dispose();
    }
}
