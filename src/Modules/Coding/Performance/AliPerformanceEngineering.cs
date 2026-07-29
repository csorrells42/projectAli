using System.Diagnostics;
using System.Text.Json;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;

namespace Ali.Modules.Coding.Performance;

public sealed record PerformanceSample(int Iteration, bool Success, int ExitCode, double WallMilliseconds, long PeakWorkingSetBytes, double CpuMilliseconds);
public sealed record PerformanceRunResult(bool Success, string Summary, string ProjectPath, string ArtifactPath, IReadOnlyList<PerformanceSample> Samples, double MedianWallMilliseconds, long MaximumWorkingSetBytes, string EvidencePath);
public sealed record PerformanceComparisonResult(bool Success, string Summary, double BaselineMedianMilliseconds, double CandidateMedianMilliseconds, double PercentChange, long BaselinePeakBytes, long CandidatePeakBytes);
public sealed record PerformanceTraceResult(bool Success, string Summary, int ProcessId, int DurationSeconds, string TracePath, long TraceSizeBytes);

internal sealed class AliPerformanceEngineering(AliCodingProjectResolver resolver)
{
    public async Task<PerformanceRunResult> MeasureAsync(string projectPath, string? configuration, int iterations, CancellationToken cancellationToken)
    {
        var project = resolver.ResolveExistingProject(projectPath);
        var normalized = NormalizeConfiguration(configuration);
        var artifact = AliRoslynCodingTools.FindBuiltArtifact(project.PhysicalPath, normalized)
            ?? throw new FileNotFoundException("Build the project before measuring performance.");
        AliCodingProjectResolver.RejectReparsePoints(project.MountRoot, artifact);
        var boundedIterations = Math.Clamp(iterations, 1, 20);
        var samples = new List<PerformanceSample>();
        for (var iteration = 1; iteration <= boundedIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var process = new Process { StartInfo = CreateStartInfo(artifact) };
            var started = Stopwatch.StartNew();
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var timedOut = false;
            long peakWorkingSet = 0;
            double cpuMilliseconds = 0;
            try
            {
                while (!process.HasExited)
                {
                    process.Refresh();
                    peakWorkingSet = Math.Max(peakWorkingSet, TryRead(() => process.PeakWorkingSet64));
                    cpuMilliseconds = Math.Max(cpuMilliseconds, TryRead(() => process.TotalProcessorTime.TotalMilliseconds));
                    await Task.Delay(10, timeout.Token).ConfigureAwait(false);
                }
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); } catch { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            started.Stop();
            samples.Add(new PerformanceSample(iteration, !timedOut && process.ExitCode == 0, timedOut ? -1 : process.ExitCode,
                started.Elapsed.TotalMilliseconds, peakWorkingSet, cpuMilliseconds));
        }
        var ordered = samples.Select(sample => sample.WallMilliseconds).Order().ToArray();
        var median = ordered[ordered.Length / 2];
        var evidenceDirectory = Path.Combine(project.ProjectDirectory, ".ali", "performance");
        Directory.CreateDirectory(evidenceDirectory);
        var fileName = $"performance-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json";
        var physicalEvidencePath = Path.Combine(evidenceDirectory, fileName);
        var virtualEvidencePath = Path.Combine(Path.GetDirectoryName(project.VirtualPath)!, ".ali", "performance", fileName).Replace('\\', '/');
        var result = new PerformanceRunResult(samples.All(sample => sample.Success), $"Completed {samples.Count} bounded process measurement(s).",
            projectPath, artifact, samples, median, samples.Max(sample => sample.PeakWorkingSetBytes), virtualEvidencePath);
        await File.WriteAllTextAsync(physicalEvidencePath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<PerformanceComparisonResult> CompareAsync(string projectPath, string baselineEvidencePath, string candidateEvidencePath, CancellationToken cancellationToken)
    {
        var project = resolver.ResolveExistingProject(projectPath);
        var baselinePath = resolver.ResolveDocument(project, baselineEvidencePath);
        var candidatePath = resolver.ResolveDocument(project, candidateEvidencePath);
        var baseline = JsonSerializer.Deserialize<PerformanceRunResult>(await File.ReadAllTextAsync(baselinePath, cancellationToken).ConfigureAwait(false))
            ?? throw new InvalidDataException("Baseline performance evidence is invalid.");
        var candidate = JsonSerializer.Deserialize<PerformanceRunResult>(await File.ReadAllTextAsync(candidatePath, cancellationToken).ConfigureAwait(false))
            ?? throw new InvalidDataException("Candidate performance evidence is invalid.");
        var percent = baseline.MedianWallMilliseconds == 0 ? 0 : (candidate.MedianWallMilliseconds - baseline.MedianWallMilliseconds) / baseline.MedianWallMilliseconds * 100;
        return new PerformanceComparisonResult(percent <= 0,
            percent <= 0 ? $"Candidate median improved by {Math.Abs(percent):F1}%." : $"Candidate median regressed by {percent:F1}%.",
            baseline.MedianWallMilliseconds, candidate.MedianWallMilliseconds, percent, baseline.MaximumWorkingSetBytes, candidate.MaximumWorkingSetBytes);
    }

    public async Task<PerformanceTraceResult> CaptureTraceAsync(string projectPath, int processId, int durationSeconds, CancellationToken cancellationToken)
    {
        var project = resolver.ResolveExistingProject(projectPath);
        using var process = Process.GetProcessById(processId);
        var processPath = process.MainModule?.FileName ?? throw new InvalidOperationException("The target process path is unavailable.");
        var projectRoot = Path.TrimEndingDirectorySeparator(project.ProjectDirectory) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(processPath).StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The process is not running from the approved project folder.");
        var boundedDuration = Math.Clamp(durationSeconds, 1, 60);
        var directory = Path.Combine(project.ProjectDirectory, ".ali", "performance");
        Directory.CreateDirectory(directory);
        var tracePath = Path.Combine(directory, $"trace-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.nettrace");
        var client = new DiagnosticsClient(processId);
        var providers = new[]
        {
            new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, long.MaxValue),
            new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational)
        };
        using var session = client.StartEventPipeSession(providers, requestRundown: true);
        await using var file = new FileStream(tracePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var copying = session.EventStream.CopyToAsync(file, cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(boundedDuration), cancellationToken).ConfigureAwait(false);
        session.Stop();
        await copying.ConfigureAwait(false);
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new PerformanceTraceResult(true, "Captured a bounded managed EventPipe CPU/allocation trace.", processId, boundedDuration, tracePath, file.Length);
    }

    private static ProcessStartInfo CreateStartInfo(string artifact)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.GetExtension(artifact).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? ResolveDotNet() : artifact,
            WorkingDirectory = Path.GetDirectoryName(artifact)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetExtension(artifact).Equals(".dll", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(artifact);
        return info;
    }
    private static string ResolveDotNet() => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path && File.Exists(path) ? path : "dotnet";
    private static T TryRead<T>(Func<T> read) where T : struct { try { return read(); } catch (InvalidOperationException) { return default; } }
    private static string NormalizeConfiguration(string? value) => string.IsNullOrWhiteSpace(value) ? "Release" : value.Trim() switch
    {
        var text when text.Equals("Debug", StringComparison.OrdinalIgnoreCase) => "Debug",
        var text when text.Equals("Release", StringComparison.OrdinalIgnoreCase) => "Release",
        _ => throw new ArgumentException("Configuration must be Debug or Release.", nameof(value))
    };
}
