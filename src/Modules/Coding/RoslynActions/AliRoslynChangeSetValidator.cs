using Microsoft.CodeAnalysis;

namespace Ali.Modules.Coding.RoslynActions;

internal sealed record AliRoslynChangeSetValidationResult(
    bool Valid,
    bool CanonicalFingerprintMatches,
    AliRoslynSolutionFingerprintSnapshot CurrentCanonicalFingerprint,
    AliRoslynDiagnosticSet StagedDiagnostics,
    AliRoslynDiagnosticComparison DiagnosticComparison,
    IReadOnlyList<string> Errors);

/// <summary>
/// Binds a staged semantic solution to the exact canonical fingerprint and to
/// the baseline diagnostic multiset captured during preview.
/// </summary>
internal sealed class AliRoslynChangeSetValidator(
    AliRoslynSolutionFingerprint fingerprint,
    AliRoslynChangeSetVerifier verifier)
{
    public async Task<AliRoslynChangeSetValidationResult> ValidateAsync(
        AliRoslynSolutionFingerprintSnapshot expectedCanonicalFingerprint,
        AliRoslynDiagnosticSet baselineDiagnostics,
        Solution currentCanonicalSolution,
        Solution stagedSolution,
        IReadOnlyList<string> currentWorkspaceWarnings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedCanonicalFingerprint);
        ArgumentNullException.ThrowIfNull(baselineDiagnostics);
        ArgumentNullException.ThrowIfNull(currentCanonicalSolution);
        ArgumentNullException.ThrowIfNull(stagedSolution);
        ArgumentNullException.ThrowIfNull(currentWorkspaceWarnings);

        var errors = currentWorkspaceWarnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.Ordinal)
            .Select(warning => "Canonical MSBuildWorkspace warning: " + warning)
            .ToList();
        var currentFingerprint = await fingerprint.CaptureAsync(currentCanonicalSolution, cancellationToken)
            .ConfigureAwait(false);
        var fingerprintMatches = string.Equals(
            expectedCanonicalFingerprint.Sha256,
            currentFingerprint.Sha256,
            StringComparison.Ordinal);
        if (!fingerprintMatches)
        {
            errors.Add("The canonical solution's exact semantic fingerprint changed after preview.");
        }

        var stagedDiagnostics = await verifier.CaptureAsync(stagedSolution, cancellationToken).ConfigureAwait(false);
        var comparison = verifier.Compare(baselineDiagnostics, stagedDiagnostics);
        if (!comparison.NoRegressions)
        {
            errors.Add(
                $"The staged solution introduced or changed diagnostics ({comparison.Added.Count} added or changed, {comparison.Removed.Count} removed"
                + (comparison.DifferencesTruncated ? ", report truncated)." : ")."));
        }

        return new(
            errors.Count == 0,
            fingerprintMatches,
            currentFingerprint,
            stagedDiagnostics,
            comparison,
            errors);
    }
}
