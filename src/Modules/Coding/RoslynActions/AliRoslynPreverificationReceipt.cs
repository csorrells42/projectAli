namespace Ali.Modules.Coding.RoslynActions;

/// <summary>
/// Exact staged verification evidence required before a handle may enter publication.
/// Human-readable build output remains in bounded evidence; this receipt contains identities.
/// </summary>
internal sealed record AliRoslynPreverificationReceipt(
    string Id,
    string ChangeSetId,
    string ChangeSetManifestDigest,
    string CanonicalSolutionFingerprint,
    string StagedSolutionFingerprint,
    string BaselineDiagnosticsDigest,
    string StagedDiagnosticsDigest,
    string MaterializationReceiptId,
    string InputBindingDigest,
    string InputManifestPolicyDigest,
    string CanonicalInputManifestDigest,
    string StagedInputManifestDigest,
    bool RoslynSucceeded,
    bool BuildSucceeded,
    int TestsRun,
    bool TestsSucceeded,
    string VerificationDigest,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    internal bool Success =>
        RoslynSucceeded
        && BuildSucceeded
        && TestsSucceeded
        && TestsRun >= 0;
}
