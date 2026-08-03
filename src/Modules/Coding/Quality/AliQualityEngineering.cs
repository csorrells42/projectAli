using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Coding.Quality;

public sealed record QualityFinding(string RuleId, string Severity, string Message, string? File, int? Line);
public sealed record QualityScanResult(bool Success, string Summary, IReadOnlyList<QualityFinding> Findings, string SarifPath, bool EditorConfigPresent);

internal sealed partial class AliQualityEngineering(AliCodingProjectResolver resolver, AliRoslynCodingTools roslyn)
{
    private const int MaximumEntries = 16_000;
    private const int MaximumFiles = 12_000;
    private const long MaximumAggregateBytes = 512L * 1024 * 1024;
    private const long MaximumScannedFileBytes = 2_000_000;
    private static readonly FrozenSet<string> ExcludedDirectoryNames = new[]
    {
        ".git",
        ".vs",
        ".idea",
        ".ali",
        "bin",
        "obj",
        "node_modules",
        "artifacts",
        "release",
        "TestResults"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> ScannedExtensions = new[]
    {
        ".cs",
        ".json",
        ".config",
        ".xml",
        ".props",
        ".targets"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyPattern();
    [GeneratedRegex("AKIA[0-9A-Z]{16}", RegexOptions.CultureInvariant)]
    private static partial Regex AwsKeyPattern();

    public async Task<QualityScanResult> ScanAsync(string projectPath, CancellationToken cancellationToken)
    {
        var project = resolver.ResolveExistingProject(projectPath);
        var analysis = await roslyn.AnalyzeCompilerOnlyAsync(projectPath, cancellationToken)
            .ConfigureAwait(false);
        var findings = analysis.Diagnostics.Select(diagnostic => new QualityFinding(
            diagnostic.Id, diagnostic.Severity, diagnostic.Message, diagnostic.File, diagnostic.Line)).ToList();
        var sourceInputs = CaptureSourceInputs(project.ProjectDirectory);
        foreach (var file in sourceInputs.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lineNumber = 0;
            using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                writeThrough: false,
                "A quality-scan input is not a regular local file.");
            var capturedLength = stream.Length;
            if (capturedLength < 0 || capturedLength > MaximumScannedFileBytes)
            {
                throw new InvalidDataException(
                    "A quality-scan input changed outside its bounded scan contract.");
            }
            using var reader = new StreamReader(
                stream,
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                if (PrivateKeyPattern().IsMatch(line)) findings.Add(new QualityFinding("ALI-SECRET-PRIVATE-KEY", "error", "A private-key marker is present in source.", file, lineNumber));
                if (AwsKeyPattern().IsMatch(line)) findings.Add(new QualityFinding("ALI-SECRET-AWS-KEY", "error", "A possible AWS access key is present in source.", file, lineNumber));
            }
            if (stream.Length != capturedLength)
            {
                throw new InvalidDataException(
                    "A quality-scan input changed while it was being read.");
            }
        }

        var resultDirectory = Path.Combine(project.ProjectDirectory, ".ali", "quality");
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            resultDirectory,
            "The quality-results path is not a regular local directory.");
        var sarifPath = Path.Combine(resultDirectory, $"quality-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.sarif");
        var sarif = new
        {
            version = "2.1.0",
            schema = "https://json.schemastore.org/sarif-2.1.0.json",
            runs = new[] { new { tool = new { driver = new { name = "Ali Roslyn Quality", informationUri = "https://github.com/dotnet/roslyn" } }, results = findings.Select(finding => new { ruleId = finding.RuleId, level = NormalizeLevel(finding.Severity), message = new { text = finding.Message }, locations = finding.File is null ? null : new[] { new { physicalLocation = new { artifactLocation = new { uri = finding.File }, region = finding.Line is null ? null : new { startLine = finding.Line } } } } }).ToArray() } }
        };
        var sarifBytes = JsonSerializer.SerializeToUtf8Bytes(
            sarif,
            new JsonSerializerOptions { WriteIndented = true });
        await using (var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                         sarifPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         writeThrough: true,
                         "The quality result is not a regular local file."))
        {
            await stream.WriteAsync(sarifBytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        var errors = findings.Count(finding => finding.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
        return new QualityScanResult(errors == 0, errors == 0 ? $"Quality scan completed with {findings.Count} non-error finding(s)." : $"Quality scan found {errors} error-level finding(s).",
            findings, sarifPath, sourceInputs.EditorConfigPresent);
    }

    internal static QualitySourceInputs CaptureSourceInputs(string root)
    {
        var canonicalRoot = Path.GetFullPath(root);
        var pending = new Stack<string>();
        pending.Push(canonicalRoot);
        var files = new List<string>();
        var entryCount = 0;
        var fileCount = 0;
        long aggregateBytes = 0;
        var editorConfigPresent = false;

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var attributes = File.GetAttributes(directory);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                || (attributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidDataException(
                    "The quality-scan input tree contains a reparse point or non-directory entry.");
            }

            var childDirectories = new List<string>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory)
                         .OrderBy(path => path, PathComparer))
            {
                entryCount++;
                if (entryCount > MaximumEntries)
                {
                    throw new InvalidDataException(
                        $"The quality-scan input tree exceeds its {MaximumEntries}-entry bound.");
                }

                attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException(
                        "The quality-scan input tree contains a reparse point or device entry.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!ExcludedDirectoryNames.Contains(Path.GetFileName(entry)))
                    {
                        childDirectories.Add(entry);
                    }
                    continue;
                }

                fileCount++;
                if (fileCount > MaximumFiles)
                {
                    throw new InvalidDataException(
                        $"The quality-scan input tree exceeds its {MaximumFiles}-file bound.");
                }

                var length = new FileInfo(entry).Length;
                if (length < 0)
                {
                    throw new InvalidDataException("A quality-scan input has an invalid length.");
                }
                aggregateBytes = checked(aggregateBytes + length);
                if (aggregateBytes > MaximumAggregateBytes)
                {
                    throw new InvalidDataException(
                        "The quality-scan input tree exceeds its aggregate byte bound.");
                }

                if (Path.GetFileName(entry).Equals(".editorconfig", StringComparison.OrdinalIgnoreCase))
                {
                    editorConfigPresent = true;
                }
                if (length <= MaximumScannedFileBytes
                    && ScannedExtensions.Contains(Path.GetExtension(entry)))
                {
                    files.Add(entry);
                }
            }

            for (var index = childDirectories.Count - 1; index >= 0; index--)
            {
                pending.Push(childDirectories[index]);
            }
        }

        return new QualitySourceInputs(
            files.AsReadOnly(),
            editorConfigPresent);
    }

    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string NormalizeLevel(string severity) => severity.ToLowerInvariant() switch { "error" => "error", "warning" => "warning", _ => "note" };

    internal sealed record QualitySourceInputs(
        IReadOnlyList<string> Files,
        bool EditorConfigPresent);
}
