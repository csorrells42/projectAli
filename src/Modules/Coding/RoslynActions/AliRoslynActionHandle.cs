namespace Ali.Modules.Coding.RoslynActions;

internal enum AliRoslynActionHandleState
{
    Previewed,
    Verified,
    Applying,
    Applied,
    Failed,
    Expired
}

/// <summary>
/// Durable identity for one exact previewed provider action. Paths and requested values are
/// stored only inside the current-user-protected handle envelope.
/// </summary>
internal sealed record AliRoslynActionHandle(
    string Id,
    string ActionIdentitySha256,
    string ProviderIdentity,
    string ProviderVersion,
    string EquivalenceKey,
    string Title,
    string[] DiagnosticIds,
    string TargetPath,
    string SourceRoot,
    string ProjectIdentity,
    string DocumentIdentity,
    string DocumentPath,
    int SpanStart,
    int SpanLength,
    string RequestedValue,
    string CanonicalSolutionFingerprint,
    string ChangeSetId,
    string ChangeSetManifestDigest,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    AliRoslynActionHandleState State,
    int Revision,
    AliRoslynPreverificationReceipt? Verification = null,
    string? PublicationTransactionId = null,
    string? FailureCode = null,
    AliRoslynDocumentChange[]? DocumentChanges = null);
