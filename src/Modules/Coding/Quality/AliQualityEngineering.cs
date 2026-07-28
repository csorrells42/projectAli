using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ali.Modules.Coding.Quality;

public sealed record QualityFinding(string RuleId, string Severity, string Message, string? File, int? Line);
public sealed record QualityScanResult(bool Success, string Summary, IReadOnlyList<QualityFinding> Findings, string SarifPath, bool EditorConfigPresent);

internal sealed partial class AliQualityEngineering(AliCodingProjectResolver resolver, AliRoslynCodingTools roslyn)
{
    [GeneratedRegex("-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyPattern();
    [GeneratedRegex("AKIA[0-9A-Z]{16}", RegexOptions.CultureInvariant)]
    private static partial Regex AwsKeyPattern();

    public async Task<QualityScanResult> ScanAsync(string projectPath, CancellationToken cancellationToken)
    {
        var project = resolver.ResolveExistingProject(projectPath);
        var analysis = await roslyn.AnalyzeAsync(projectPath, cancellationToken).ConfigureAwait(false);
        var findings = analysis.Diagnostics.Select(diagnostic => new QualityFinding(
            diagnostic.Id, diagnostic.Severity, diagnostic.Message, diagnostic.File, diagnostic.Line)).ToList();
        foreach (var file in Directory.EnumerateFiles(project.ProjectDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !IsOutput(path) && new[] { ".cs", ".json", ".config", ".xml", ".props", ".targets" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(file).Length > 2_000_000) continue;
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (PrivateKeyPattern().IsMatch(line)) findings.Add(new QualityFinding("ALI-SECRET-PRIVATE-KEY", "error", "A private-key marker is present in source.", file, lineNumber));
                if (AwsKeyPattern().IsMatch(line)) findings.Add(new QualityFinding("ALI-SECRET-AWS-KEY", "error", "A possible AWS access key is present in source.", file, lineNumber));
            }
        }

        var resultDirectory = Path.Combine(project.ProjectDirectory, ".ali", "quality");
        Directory.CreateDirectory(resultDirectory);
        var sarifPath = Path.Combine(resultDirectory, $"quality-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.sarif");
        var sarif = new
        {
            version = "2.1.0",
            schema = "https://json.schemastore.org/sarif-2.1.0.json",
            runs = new[] { new { tool = new { driver = new { name = "Ali Roslyn Quality", informationUri = "https://github.com/dotnet/roslyn" } }, results = findings.Select(finding => new { ruleId = finding.RuleId, level = NormalizeLevel(finding.Severity), message = new { text = finding.Message }, locations = finding.File is null ? null : new[] { new { physicalLocation = new { artifactLocation = new { uri = finding.File }, region = finding.Line is null ? null : new { startLine = finding.Line } } } } }).ToArray() } }
        };
        await File.WriteAllTextAsync(sarifPath, JsonSerializer.Serialize(sarif, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        var errors = findings.Count(finding => finding.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
        return new QualityScanResult(errors == 0, errors == 0 ? $"Quality scan completed with {findings.Count} non-error finding(s)." : $"Quality scan found {errors} error-level finding(s).",
            findings, sarifPath, FindEditorConfig(project.ProjectDirectory));
    }

    private static bool FindEditorConfig(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, ".editorconfig"))) return true;
            current = current.Parent;
        }
        return false;
    }

    private static bool IsOutput(string path) => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeLevel(string severity) => severity.ToLowerInvariant() switch { "error" => "error", "warning" => "warning", _ => "note" };
}
