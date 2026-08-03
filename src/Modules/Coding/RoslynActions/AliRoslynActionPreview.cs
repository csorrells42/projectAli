namespace Ali.Modules.Coding.RoslynActions;

public sealed record AliRoslynActionPreview(
    bool Success,
    string? HandleId,
    string ActionIdentitySha256,
    string? Title,
    string? ChangeSetId,
    string? ChangeSetManifestDigest,
    string? CanonicalSolutionFingerprintSha256,
    string? StagedSolutionFingerprintSha256,
    IReadOnlyList<string> ChangedRelativePaths,
    bool DiagnosticsHaveNoRegressions,
    int DiagnosticRegressions,
    string Summary,
    string? FailureCode)
{
    internal static AliRoslynActionPreview Failed(
        string actionIdentitySha256,
        string failureCode,
        string summary) =>
        new(false, null, actionIdentitySha256, null, null, null, null, null, [], false, 0, summary, failureCode);
}
