using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace Ali.Modules.Coding.Verification;

public sealed record ApplicationVerificationResult(bool Success, string Summary, string ProjectPath, string ApplicationKind, int? ExitCode, int? ProcessId, string Output, string? ScreenshotPath, bool HealthCheckPassed, long DurationMilliseconds);

internal sealed partial class AliApplicationVerification(AliCodingProjectResolver resolver)
{
    public async Task<ApplicationVerificationResult> SmokeTestAsync(string projectPath, string? configuration, string? healthUrl, CancellationToken cancellationToken)
    {
        var project = resolver.ResolveExistingProject(projectPath);
        var normalized = NormalizeConfiguration(configuration);
        var artifact = AliRoslynCodingTools.FindBuiltArtifact(project.PhysicalPath, normalized)
            ?? throw new FileNotFoundException("Build the project before application verification.");
        AliCodingProjectResolver.RejectReparsePoints(project.MountRoot, artifact);
        var kind = DetectKind(project.PhysicalPath);
        var health = ValidateHealthUrl(healthUrl);
        var started = Stopwatch.StartNew();
        var info = CreateStartInfo(artifact);
        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) output.AppendLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) output.AppendLine(args.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var processId = process.Id;
        string? screenshot = null;
        var healthPassed = false;
        var success = false;
        int? exitCode = null;
        try
        {
            if (health is not null)
            {
                using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                while (!process.HasExited)
                {
                    try { healthPassed = (await client.GetAsync(health, cancellationToken).ConfigureAwait(false)).IsSuccessStatusCode; }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                    if (healthPassed) break;
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                success = healthPassed;
            }
            else if (kind == "desktop")
            {
                while (!process.HasExited)
                {
                    process.Refresh();
                    if (process.MainWindowHandle != IntPtr.Zero) break;
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                process.Refresh();
                success = !process.HasExited && process.MainWindowHandle != IntPtr.Zero;
                if (success) screenshot = CaptureWindow(project.ProjectDirectory, process.MainWindowHandle);
            }
            else
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (process.HasExited) { exitCode = process.ExitCode; success = exitCode == 0; }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.CloseMainWindow(); } catch { }
                await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            }
        }
        started.Stop();
        return new ApplicationVerificationResult(success,
            success ? $"{kind} application smoke verification passed." : $"{kind} application smoke verification failed.",
            projectPath, kind, exitCode, processId, output.ToString().Trim(), screenshot, healthPassed, started.ElapsedMilliseconds);
    }

    private static Uri? ValidateHealthUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !uri.IsLoopback)
            throw new ArgumentException("Health URL must be an absolute loopback HTTP or HTTPS URL.", nameof(value));
        return uri;
    }

    private static string DetectKind(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        if (document.Descendants().Any(element => element.Name.LocalName is "UseWPF" or "UseWindowsForms" && element.Value.Equals("true", StringComparison.OrdinalIgnoreCase))) return "desktop";
        var sdk = (string?)document.Root?.Attribute("Sdk") ?? "";
        if (sdk.Contains("Web", StringComparison.OrdinalIgnoreCase)) return "web";
        return "console-or-service";
    }

    private static string CaptureWindow(string projectDirectory, IntPtr window)
    {
        if (!GetWindowRect(window, out var rect)) throw new InvalidOperationException("Could not read the verified window bounds.");
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height));
        var directory = Path.Combine(projectDirectory, ".ali", "verification");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"window-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private static ProcessStartInfo CreateStartInfo(string artifact)
    {
        var dll = Path.GetExtension(artifact).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(dll ? ResolveDotNet() : artifact)
        {
            WorkingDirectory = Path.GetDirectoryName(artifact)!, UseShellExecute = false, CreateNoWindow = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (dll) info.ArgumentList.Add(artifact);
        return info;
    }
    private static string ResolveDotNet() => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path && File.Exists(path) ? path : "dotnet";
    private static string NormalizeConfiguration(string? value) => string.IsNullOrWhiteSpace(value) ? "Release" : value.Trim() switch
    {
        var text when text.Equals("Debug", StringComparison.OrdinalIgnoreCase) => "Debug", var text when text.Equals("Release", StringComparison.OrdinalIgnoreCase) => "Release",
        _ => throw new ArgumentException("Configuration must be Debug or Release.", nameof(value))
    };

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr handle, out WindowRect rect);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left; public int Top; public int Right; public int Bottom; }
}
