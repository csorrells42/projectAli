using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Ali.Modules.Coding.RoslynActions;

internal sealed record AliRoslynDiagnosticIdentity(
    string StableKey,
    string ProjectIdentity,
    string Id,
    string Severity,
    string Message,
    string? FilePath,
    int? SpanStart,
    int? SpanLength);

internal sealed record AliRoslynDiagnosticSet(
    string Sha256,
    IReadOnlyList<AliRoslynDiagnosticIdentity> Diagnostics);

internal sealed record AliRoslynDiagnosticComparison(
    bool Equivalent,
    bool NoRegressions,
    IReadOnlyList<AliRoslynDiagnosticIdentity> Added,
    IReadOnlyList<AliRoslynDiagnosticIdentity> Removed,
    bool DifferencesTruncated);

/// <summary>Captures and compares exact compiler/analyzer diagnostic multisets.</summary>
internal sealed class AliRoslynChangeSetVerifier
{
    internal const int MaximumReportedDifferences = 100;

    public async Task<AliRoslynDiagnosticSet> CaptureAsync(
        Solution solution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var diagnostics = new List<AliRoslynDiagnosticIdentity>();
        foreach (var project in solution.Projects.OrderBy(ProjectIdentity, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Roslyn could not create a compilation for project '{project.Name}'.");
            ImmutableArray<Diagnostic> projectDiagnostics;
            var analyzers = project.AnalyzerReferences
                .SelectMany(reference => reference.GetAnalyzers(project.Language))
                .Distinct()
                .ToImmutableArray();
            if (analyzers.IsDefaultOrEmpty)
            {
                projectDiagnostics = compilation.GetDiagnostics(cancellationToken);
            }
            else
            {
                var withAnalyzers = new CompilationWithAnalyzers(
                    compilation,
                    analyzers,
                    project.AnalyzerOptions);
                projectDiagnostics = await withAnalyzers.GetAllDiagnosticsAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            diagnostics.AddRange(projectDiagnostics.Select(diagnostic => CreateIdentity(project, diagnostic)));
        }

        diagnostics.Sort((left, right) => StringComparer.Ordinal.Compare(left.StableKey, right.StableKey));
        var canonical = string.Join("\n", diagnostics.Select(diagnostic => diagnostic.StableKey));
        return new(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
            diagnostics);
    }

    public AliRoslynDiagnosticComparison Compare(
        AliRoslynDiagnosticSet baseline,
        AliRoslynDiagnosticSet candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.Equals(baseline.Sha256, candidate.Sha256, StringComparison.Ordinal)
            && baseline.Diagnostics.Count == candidate.Diagnostics.Count
            && baseline.Diagnostics.Select(item => item.StableKey)
                .SequenceEqual(candidate.Diagnostics.Select(item => item.StableKey), StringComparer.Ordinal))
        {
            return new(true, true, [], [], false);
        }

        var baselineCounts = CountByKey(baseline.Diagnostics);
        var candidateCounts = CountByKey(candidate.Diagnostics);
        var added = ExpandDifference(candidate.Diagnostics, candidateCounts, baselineCounts);
        var removed = ExpandDifference(baseline.Diagnostics, baselineCounts, candidateCounts);
        var truncated = added.Count > MaximumReportedDifferences || removed.Count > MaximumReportedDifferences;
        return new(
            false,
            added.Count == 0,
            added.Take(MaximumReportedDifferences).ToArray(),
            removed.Take(MaximumReportedDifferences).ToArray(),
            truncated);
    }

    public async Task<AliRoslynDiagnosticComparison> CompareAsync(
        Solution baseline,
        Solution candidate,
        CancellationToken cancellationToken)
    {
        var baselineSet = await CaptureAsync(baseline, cancellationToken).ConfigureAwait(false);
        var candidateSet = await CaptureAsync(candidate, cancellationToken).ConfigureAwait(false);
        return Compare(baselineSet, candidateSet);
    }

    private static IReadOnlyDictionary<string, int> CountByKey(
        IReadOnlyList<AliRoslynDiagnosticIdentity> diagnostics) =>
        diagnostics
            .GroupBy(diagnostic => diagnostic.StableKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static IReadOnlyList<AliRoslynDiagnosticIdentity> ExpandDifference(
        IReadOnlyList<AliRoslynDiagnosticIdentity> source,
        IReadOnlyDictionary<string, int> sourceCounts,
        IReadOnlyDictionary<string, int> otherCounts)
    {
        var remaining = sourceCounts.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        foreach (var item in otherCounts)
        {
            if (remaining.TryGetValue(item.Key, out var count))
            {
                remaining[item.Key] = Math.Max(0, count - item.Value);
            }
        }

        var result = new List<AliRoslynDiagnosticIdentity>();
        foreach (var diagnostic in source)
        {
            if (remaining.TryGetValue(diagnostic.StableKey, out var count) && count > 0)
            {
                result.Add(diagnostic);
                remaining[diagnostic.StableKey] = count - 1;
            }
        }

        return result;
    }

    private static AliRoslynDiagnosticIdentity CreateIdentity(Project project, Diagnostic diagnostic)
    {
        var location = diagnostic.Location;
        string? filePath = null;
        int? spanStart = null;
        int? spanLength = null;
        if (location.IsInSource)
        {
            var lineSpan = location.GetLineSpan();
            filePath = NormalizePath(lineSpan.Path);
            spanStart = location.SourceSpan.Start;
            spanLength = location.SourceSpan.Length;
        }

        var projectIdentity = ProjectIdentity(project);
        var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
        var additional = diagnostic.AdditionalLocations
            .Select(LocationIdentity)
            .Order(StringComparer.Ordinal);
        var properties = diagnostic.Properties
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key + "=" + item.Value);
        var stableKey = string.Join(
            "\u001f",
            projectIdentity,
            diagnostic.Id,
            diagnostic.Severity.ToString(),
            diagnostic.WarningLevel.ToString(CultureInfo.InvariantCulture),
            diagnostic.IsSuppressed.ToString(CultureInfo.InvariantCulture),
            diagnostic.Descriptor.Category,
            message,
            location.Kind.ToString(),
            filePath ?? string.Empty,
            spanStart?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            spanLength?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            string.Join("\u001e", additional),
            string.Join("\u001e", properties));
        return new(
            stableKey,
            projectIdentity,
            diagnostic.Id,
            diagnostic.Severity.ToString(),
            message,
            filePath,
            spanStart,
            spanLength);
    }

    private static string LocationIdentity(Location location)
    {
        if (!location.IsInSource)
        {
            return location.Kind + "|" + location;
        }

        var lineSpan = location.GetLineSpan();
        return string.Join(
            "|",
            location.Kind,
            NormalizePath(lineSpan.Path),
            location.SourceSpan.Start.ToString(CultureInfo.InvariantCulture),
            location.SourceSpan.Length.ToString(CultureInfo.InvariantCulture));
    }

    private static string ProjectIdentity(Project project) =>
        NormalizePath(project.FilePath) + "|" + project.Name + "|" + project.AssemblyName + "|" + project.Language;

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : path;
        normalized = normalized.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }
}
