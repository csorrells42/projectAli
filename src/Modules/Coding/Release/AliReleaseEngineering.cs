using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Ali.Modules.Coding.Architecture;
using Ali.Modules.Coding.Infrastructure;

namespace Ali.Modules.Coding.Release;

public sealed record ReleaseFileEvidence(string RelativePath, long Size, string Sha256);
public sealed record DotNetReleaseResult(bool Success, string Summary, string ProjectPath, string PublishDirectory, IReadOnlyList<ReleaseFileEvidence> Files, string ManifestPath, string Output);
public sealed record EngineeringReportResult(bool Success, string Summary, string ReportPath);

internal sealed class AliReleaseEngineering(AliCodingProjectResolver resolver, AliArchitectureEngineering architecture)
{
    public async Task<DotNetReleaseResult> PublishAsync(string projectPath, string? runtimeIdentifier, bool selfContained, CancellationToken cancellationToken)
    {
        var project = resolver.ResolveExistingProject(projectPath);
        var runtime = NormalizeRuntime(runtimeIdentifier);
        var publishDirectory = Path.Combine(project.ProjectDirectory, ".ali", "release", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(publishDirectory);
        var arguments = new List<string> { "publish", project.PhysicalPath, "--configuration", "Release", "--runtime", runtime, "--self-contained", selfContained ? "true" : "false", "--output", publishDirectory, "--nologo" };
        var execution = await AliBoundedProcessRunner.RunAsync(ResolveDotNet(), project.ProjectDirectory, arguments, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        var files = Directory.Exists(publishDirectory)
            ? Directory.EnumerateFiles(publishDirectory, "*", SearchOption.AllDirectories).Select(path => new ReleaseFileEvidence(
                Path.GetRelativePath(publishDirectory, path), new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant())).OrderBy(file => file.RelativePath).ToArray()
            : [];
        var manifestPath = Path.Combine(publishDirectory, "ALI-RELEASE-MANIFEST.json");
        if (execution.Success)
        {
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new { project = projectPath, runtime, selfContained, createdUtc = DateTimeOffset.UtcNow, files }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
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
        Directory.CreateDirectory(reportDirectory);
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
    private static string ResolveDotNet() => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path && File.Exists(path) ? path : "dotnet";
}
