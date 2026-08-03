using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Ali.Modules.Coding.Architecture;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Coding.Release;

public sealed record ReleaseFileEvidence(string RelativePath, long Size, string Sha256);
public sealed record DotNetReleaseResult(bool Success, string Summary, string ProjectPath, string PublishDirectory, IReadOnlyList<ReleaseFileEvidence> Files, string ManifestPath, string Output);
public sealed record EngineeringReportResult(bool Success, string Summary, string ReportPath);

internal sealed class AliReleaseEngineering(AliCodingProjectResolver resolver, AliArchitectureEngineering architecture)
{
    private const int MaximumReleaseFiles = 50_000;
    private const long MaximumReleaseFileBytes = 1024L * 1024 * 1024;
    private const long MaximumReleaseBytes = 4L * 1024 * 1024 * 1024;

    public async Task<DotNetReleaseResult> PublishAsync(string projectPath, string? runtimeIdentifier, bool selfContained, CancellationToken cancellationToken)
    {
        var project = resolver.ResolveExistingProject(projectPath);
        var runtime = NormalizeRuntime(runtimeIdentifier);
        var publishDirectory = Path.Combine(project.ProjectDirectory, ".ali", "release", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            publishDirectory,
            "The release output path is not a regular local directory.");
        var arguments = new List<string> { "publish", project.PhysicalPath, "--configuration", "Release", "--runtime", runtime, "--self-contained", selfContained ? "true" : "false", "--output", publishDirectory, "--nologo" };
        var execution = await AliBoundedProcessRunner.RunAsync(ResolveDotNet(), project.ProjectDirectory, arguments, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        var files = Directory.Exists(publishDirectory)
            ? CaptureReleaseFiles(publishDirectory)
            : [];
        var manifestPath = Path.Combine(publishDirectory, "ALI-RELEASE-MANIFEST.json");
        if (execution.Success)
        {
            var manifest = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    project = projectPath,
                    runtime,
                    selfContained,
                    createdUtc = DateTimeOffset.UtcNow,
                    files
                },
                new JsonSerializerOptions { WriteIndented = true });
            await using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                manifestPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                writeThrough: true,
                "The release manifest is not a regular local file.");
            await stream.WriteAsync(manifest, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        return new DotNetReleaseResult(execution.Success,
            execution.Success ? $"Published {files.Length} file(s) and wrote a checksum manifest." : "The bounded dotnet publish operation failed.",
            projectPath, publishDirectory, files, manifestPath, execution.Output);
    }

    public async Task<EngineeringReportResult> GenerateArchitectureReportAsync(string targetPath, CancellationToken cancellationToken)
    {
        var target = resolver.ResolveExistingTarget(targetPath);
        var graph = await architecture.InspectAsync(targetPath, cancellationToken).ConfigureAwait(false);
        var reportDirectory = Path.Combine(target.RootDirectory, ".ali", "reports");
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            reportDirectory,
            "The architecture-report path is not a regular local directory.");
        var reportPath = Path.Combine(reportDirectory, $"architecture-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.md");
        var markdown = new StringBuilder().AppendLine("# Architecture Report").AppendLine()
            .AppendLine($"Generated: {DateTimeOffset.Now:O}").AppendLine()
            .AppendLine(graph.Summary).AppendLine().AppendLine("## Project dependencies").AppendLine();
        foreach (var edge in graph.ProjectEdges) markdown.AppendLine($"- `{edge.From}` -> `{edge.To}`");
        markdown.AppendLine().AppendLine("## Semantic call graph sample").AppendLine();
        foreach (var edge in graph.CallEdges.Take(250)) markdown.AppendLine($"- `{edge.Caller}` -> `{edge.Callee}` ({Path.GetFileName(edge.File)}:{edge.Line})");
        markdown.AppendLine().AppendLine("## Project cycles").AppendLine();
        if (graph.ProjectCycles.Count == 0) markdown.AppendLine("No project-reference cycles detected.");
        else foreach (var cycle in graph.ProjectCycles) markdown.AppendLine($"- {string.Join(" -> ", cycle.Select(name => $"`{name}`"))}");
        await File.WriteAllTextAsync(reportPath, markdown.ToString(), cancellationToken).ConfigureAwait(false);
        return new EngineeringReportResult(true, "Generated a source-backed architecture report.", reportPath);
    }

    private static string NormalizeRuntime(string? value) => string.IsNullOrWhiteSpace(value) ? "win-x64" : value.Trim() switch
    {
        "win-x64" => "win-x64", "win-arm64" => "win-arm64", _ => throw new ArgumentException("Runtime must be win-x64 or win-arm64.", nameof(value))
    };

    private static ReleaseFileEvidence[] CaptureReleaseFiles(string root)
    {
        var canonicalRoot = Path.GetFullPath(root);
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            canonicalRoot,
            "The release output root is not a regular local directory.");
        var pending = new Stack<string>();
        pending.Push(canonicalRoot);
        var files = new List<ReleaseFileEvidence>();
        long aggregateBytes = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                directory,
                "The release output contains a non-regular directory.");
            var children = new List<string>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory)
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException(
                        "The release output contains a reparse point or device entry.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    children.Add(entry);
                    continue;
                }
                if (files.Count >= MaximumReleaseFiles)
                {
                    throw new InvalidDataException(
                        "The release output exceeds its fixed file-count bound.");
                }

                using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                    entry,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    writeThrough: false,
                    "A release output is not a regular local file.");
                var length = stream.Length;
                if (length < 0 || length > MaximumReleaseFileBytes)
                {
                    throw new InvalidDataException(
                        "A release output exceeds its fixed file-size bound.");
                }
                aggregateBytes = checked(aggregateBytes + length);
                if (aggregateBytes > MaximumReleaseBytes)
                {
                    throw new InvalidDataException(
                        "The release output exceeds its fixed aggregate byte bound.");
                }
                var hash = SHA256.HashData(stream);
                try
                {
                    if (stream.Position != length || stream.Length != length)
                    {
                        throw new IOException(
                            "A release output changed while its exact checksum was captured.");
                    }
                    files.Add(new ReleaseFileEvidence(
                        Path.GetRelativePath(canonicalRoot, entry),
                        length,
                        Convert.ToHexString(hash).ToLowerInvariant()));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(hash);
                }
            }
            for (var index = children.Count - 1; index >= 0; index--)
            {
                pending.Push(children[index]);
            }
        }
        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static string ResolveDotNet()
    {
        if (AliExactProcessExecutionContext.Current is { } exact)
        {
            return exact.RequireStableDotNetHost();
        }
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured)
            ? AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
                configured,
                "The .NET release host").PhysicalPath
            : AliCodingExecutionAssetFingerprint.ResolveRequiredExecutable(
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }
}
