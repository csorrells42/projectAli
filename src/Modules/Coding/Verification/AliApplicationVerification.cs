using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Coding.Verification;

public sealed record ApplicationVerificationResult(bool Success, string Summary, string ProjectPath, string ApplicationKind, int? ExitCode, int? ProcessId, string Output, string? ScreenshotPath, bool HealthCheckPassed, long DurationMilliseconds);

internal sealed partial class AliApplicationVerification
{
    private readonly AliCodingProjectResolver _resolver;
    private readonly Action? _beforeProcessStart;

    internal AliApplicationVerification(AliCodingProjectResolver resolver)
        : this(resolver, beforeProcessStart: null)
    {
    }

    internal AliApplicationVerification(
        AliCodingProjectResolver resolver,
        Action? beforeProcessStart)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _beforeProcessStart = beforeProcessStart;
    }

    public async Task<ApplicationVerificationResult> SmokeTestAsync(string projectPath, string? configuration, string? healthUrl, CancellationToken cancellationToken)
    {
        var project = _resolver.ResolveExistingProject(projectPath);
        var normalized = NormalizeConfiguration(configuration);
        var exactProcess = AliExactProcessExecutionContext.Current;
        var artifact = ResolveApplicationArtifactForLaunch(
            project,
            normalized,
            exactProcess);
        var kind = DetectKind(project.PhysicalPath);
        var health = ValidateHealthUrl(healthUrl);
        var principal = exactProcess?.ApplicationArtifact
            ?? AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
                artifact,
                "The selected application verification artifact");
        var closure = exactProcess?.ApplicationLaunchClosure
            ?? AliApplicationLaunchClosure.Capture(principal);
        using var launchLease = AliApplicationLaunchLease.Acquire(principal, closure);
        AliBoundExecutionFile? host = null;
        if (Path.GetExtension(artifact).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            host = exactProcess is null
                ? AliExactDotNetHost.CaptureCurrent()
                : exactProcess.DotNetHost
                  ?? throw new InvalidOperationException(
                      "The exact application launch did not bind its .NET host executable.");
        }
        using var hostLease = host is null
            ? null
            : AliExecutionFileLease.Acquire(host, "The exact application .NET host executable");
        var started = Stopwatch.StartNew();
        var info = CreateStartInfo(artifact, host?.PhysicalPath);
        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) output.AppendLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) output.AppendLine(args.Data); };
        var processStarted = false;
        int? processId = null;
        string? screenshot = null;
        var healthPassed = false;
        var success = false;
        int? exitCode = null;
        try
        {
            RevalidateLaunchInputs(exactProcess, artifact);
            launchLease.RequireStable();
            hostLease?.RequireStable();
            _beforeProcessStart?.Invoke();
            launchLease.RequireStable();
            hostLease?.RequireStable();
            RevalidateLaunchInputs(exactProcess, artifact);
            process.Start();
            processStarted = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (hostLease is not null)
            {
                hostLease.RequireStartedProcessImage(process);
            }
            else
            {
                launchLease.RequireStartedPrincipalProcessImage(process);
            }
            processId = process.Id;
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
            if (processStarted && !process.HasExited)
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

    internal static string ResolveApplicationArtifactForLaunch(
        AliResolvedCodingProject project,
        string configuration,
        AliExactProcessExecutionBinding? exactProcess)
    {
        ArgumentNullException.ThrowIfNull(project);
        var artifact = exactProcess is null
            ? AliRoslynCodingTools.FindBuiltArtifact(project.PhysicalPath, configuration)
                ?? throw new FileNotFoundException(
                    "Build the project before application verification.")
            : exactProcess.RequireStableApplicationArtifact();
        AliCodingProjectResolver.RejectReparsePoints(project.MountRoot, artifact);
        if (exactProcess is not null)
        {
            _ = exactProcess.RequireStableApplicationLaunchClosure();
        }
        return artifact;
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
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            directory,
            "The application-verification path is not a regular local directory.");
        var path = Path.Combine(directory, $"window-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
        using (var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                   path,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   writeThrough: true,
                   "The application-verification image is not a regular local file."))
        {
            bitmap.Save(stream, ImageFormat.Png);
            stream.Flush(flushToDisk: true);
        }
        return path;
    }

    private static ProcessStartInfo CreateStartInfo(string artifact, string? dotNetHost)
    {
        var dll = Path.GetExtension(artifact).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        var executable = dll
            ? dotNetHost
              ?? throw new InvalidOperationException(
                  "The application launch requires an exact .NET host executable.")
            : artifact;
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(artifact)!, UseShellExecute = false, CreateNoWindow = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (dll) info.ArgumentList.Add(artifact);
        return info;
    }
    private static void RevalidateLaunchInputs(
        AliExactProcessExecutionBinding? exactProcess,
        string artifact)
    {
        if (exactProcess is null)
        {
            return;
        }
        var approvedArtifact = exactProcess.RequireStableApplicationArtifact();
        if (!string.Equals(
                Path.GetFullPath(approvedArtifact),
                Path.GetFullPath(artifact),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The application launch artifact no longer matches its exact authorization.");
        }
        if (Path.GetExtension(artifact).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            _ = exactProcess.RequireStableDotNetHost();
        }
        _ = exactProcess.RequireStableApplicationLaunchClosure();
    }
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
