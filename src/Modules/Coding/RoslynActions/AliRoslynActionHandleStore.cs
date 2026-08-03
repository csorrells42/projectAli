using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coding.Changesets;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Coding.RoslynActions;

/// <summary>
/// Current-user-protected, revisioned storage for executable Roslyn action identities.
/// </summary>
internal sealed class AliRoslynActionHandleStore
{
    private const int ProtectedEnvelopeFormat = 2;
    internal const int MaximumHandleBytes = 1024 * 1024;
    internal const int MaximumDiagnosticIds = 256;
    internal const int MaximumRetainedHandleFiles = 1024;
    private const int MaximumRetentionScanFiles = MaximumRetainedHandleFiles * 2;
    internal static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly string _root;
    private readonly string _profileBinding;
    private readonly int _maximumRetainedHandleFiles;

    private static readonly HashSet<string> SafeFailedRetentionCodes = new(StringComparer.Ordinal)
    {
        "cancelled-before-publication",
        "canonical-workspace-warning",
        "manifest-binding-mismatch",
        "publication-not-started",
        "publication-rolled-back",
        "stale-canonical-fingerprint",
        "stale-canonical-input-manifest",
        "verification-input-binding-invalid"
    };

    internal AliRoslynActionHandleStore(
        string root,
        string profileBinding,
        int maximumRetainedHandleFiles = MaximumRetainedHandleFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileBinding);
        if (maximumRetainedHandleFiles is <= 0 or > MaximumRetainedHandleFiles)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedHandleFiles));
        }
        _root = Path.GetFullPath(root);
        _profileBinding = profileBinding;
        _maximumRetainedHandleFiles = maximumRetainedHandleFiles;
    }

    internal async Task<AliRoslynActionHandle> CreateAsync(
        AliRoslynActionHandle handle,
        string previewedStagedSolutionFingerprint,
        CancellationToken cancellationToken)
    {
        Validate(handle);
        ValidateDigest(
            previewedStagedSolutionFingerprint,
            nameof(previewedStagedSolutionFingerprint));
        if (handle.State != AliRoslynActionHandleState.Previewed || handle.Revision != 1)
        {
            throw new InvalidDataException("A new Roslyn action handle must begin at Previewed revision 1.");
        }

        EnsureRoot();
        await using var lease = await AcquireWriterLeaseAsync(cancellationToken).ConfigureAwait(false);
        var path = PathFor(handle.Id);
        if (File.Exists(path))
        {
            throw new InvalidOperationException("The Roslyn action handle already exists.");
        }

        await PruneForCreateAsync(cancellationToken).ConfigureAwait(false);
        await WriteProtectedAsync(
                path,
                new ProtectedHandleEnvelope(
                    ProtectedEnvelopeFormat,
                    handle,
                    previewedStagedSolutionFingerprint),
                replaceExisting: false)
            .ConfigureAwait(false);
        return Clone(handle);
    }

    internal async Task<AliRoslynActionHandle> LoadAsync(
        string id,
        CancellationToken cancellationToken) =>
        (await LoadEnvelopeAsync(id, cancellationToken).ConfigureAwait(false)).Handle;

    internal async Task<string> LoadPreviewedStagedSolutionFingerprintAsync(
        string id,
        CancellationToken cancellationToken) =>
        (await LoadEnvelopeAsync(id, cancellationToken).ConfigureAwait(false))
            .PreviewedStagedSolutionFingerprint;

    private async Task<ProtectedHandleEnvelope> LoadEnvelopeAsync(
        string id,
        CancellationToken cancellationToken)
    {
        ValidateId(id);
        EnsureRoot();
        var protectedBytes = await WindowsBoundedFileReader.TryReadExactlyAsync(
                WindowsOrchestrationFileBoundary.ToExtendedLengthWin32Path(PathFor(id)),
                minimumLength: 1,
                MaximumHandleBytes,
                "The Roslyn action handle is not a regular local file.",
                "The Roslyn action handle has an invalid size.",
                "The Roslyn action handle changed while it was being read.",
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new FileNotFoundException("The Roslyn action handle does not exist.");
        try
        {
            var plaintext = Unprotect(id, protectedBytes);
            try
            {
                var envelope = JsonSerializer.Deserialize<ProtectedHandleEnvelope>(plaintext, JsonOptions)
                    ?? throw new InvalidDataException("The Roslyn action handle payload is empty.");
                ValidateEnvelope(envelope);
                if (!string.Equals(envelope.Handle.Id, id, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The protected Roslyn action handle identity does not match its file.");
                }

                return envelope with { Handle = Clone(envelope.Handle) };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    /// <summary>
    /// Resolves the sole protected handle bound to a durable source changeset. Recovery uses this
    /// bounded scan after an interruption because the prepared broker identity is the changeset
    /// ID, not the protected action-handle ID. Duplicate bindings fail closed.
    /// </summary>
    internal async Task<AliRoslynActionHandle?> FindByChangeSetIdAsync(
        string changeSetId,
        CancellationToken cancellationToken)
    {
        ValidateId(changeSetId);
        EnsureRoot();
        const string suffix = ".handle.protected";
        var files = Directory.EnumerateFiles(_root, "*" + suffix, SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Take(_maximumRetainedHandleFiles + 1)
            .ToArray();
        if (files.Length > _maximumRetainedHandleFiles)
        {
            throw new InvalidDataException(
                $"The Roslyn action handle store exceeds its {_maximumRetainedHandleFiles}-artifact recovery bound.");
        }

        AliRoslynActionHandle? match = null;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            if (!fileName.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var id = fileName[..^suffix.Length];
            ValidateId(id);
            var candidate = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(candidate.ChangeSetId, changeSetId, StringComparison.Ordinal))
            {
                continue;
            }

            if (match is not null)
            {
                throw new InvalidDataException(
                    "More than one protected Roslyn action handle binds the same changeset.");
            }

            match = candidate;
        }

        return match;
    }

    /// <summary>
    /// Captures the protected artifact itself for target-version binding without decrypting or
    /// projecting any handle content into the planner state.
    /// </summary>
    internal string CaptureProtectedArtifactDigest(string id)
    {
        ValidateId(id);
        EnsureRoot();
        return HashProtectedArtifact(PathFor(id));
    }

    /// <summary>
    /// Binds preview progress to the bounded durable handle set. Only file names and protected
    /// bytes participate in the digest; no source path or requested value is exposed.
    /// </summary>
    internal string CaptureStoreRevisionDigest()
    {
        EnsureRoot();
        var files = Directory.EnumerateFiles(_root, "*.handle.protected", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Take(_maximumRetainedHandleFiles + 1)
            .ToArray();
        if (files.Length > _maximumRetainedHandleFiles)
        {
            throw new InvalidDataException(
                $"The Roslyn action handle store exceeds its {_maximumRetainedHandleFiles}-artifact revision bound.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashComponent(hash, "ali-roslyn-handle-store-v1");
        foreach (var file in files)
        {
            AppendHashComponent(hash, Path.GetFileName(file));
            AppendHashComponent(hash, HashProtectedArtifact(file));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal Task<AliRoslynActionHandle> RecordVerificationAsync(
        string id,
        int expectedRevision,
        AliRoslynPreverificationReceipt receipt,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            id,
            expectedRevision,
            current =>
            {
                if (current.State != AliRoslynActionHandleState.Previewed)
                {
                    throw new InvalidOperationException("Only a previewed Roslyn action can be verified.");
                }

                ValidateReceipt(current, receipt);
                if (!receipt.Success)
                {
                    throw new InvalidOperationException("A failed preverification receipt cannot authorize publication.");
                }

                return current with
                {
                    State = AliRoslynActionHandleState.Verified,
                    Revision = checked(current.Revision + 1),
                    Verification = receipt with { }
                };
            },
            cancellationToken);

    internal Task<AliRoslynActionHandle> BeginApplyAsync(
        string id,
        int expectedRevision,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            id,
            expectedRevision,
            current =>
            {
                if (current.State != AliRoslynActionHandleState.Verified
                    || current.Verification?.Success != true)
                {
                    throw new InvalidOperationException("Only a successfully verified Roslyn action can be applied.");
                }

                if (current.ExpiresAtUtc <= DateTimeOffset.UtcNow
                    || current.Verification.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    return current with
                    {
                        State = AliRoslynActionHandleState.Expired,
                        Revision = checked(current.Revision + 1),
                        FailureCode = "verification-expired"
                    };
                }

                return current with
                {
                    State = AliRoslynActionHandleState.Applying,
                    Revision = checked(current.Revision + 1)
                };
            },
            cancellationToken);

    internal Task<AliRoslynActionHandle> CommitAppliedAsync(
        string id,
        int expectedRevision,
        string publicationTransactionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationTransactionId);
        return TransitionAsync(
            id,
            expectedRevision,
            current => current.State == AliRoslynActionHandleState.Applying
                ? current with
                {
                    State = AliRoslynActionHandleState.Applied,
                    Revision = checked(current.Revision + 1),
                    PublicationTransactionId = publicationTransactionId,
                    FailureCode = null
                }
                : throw new InvalidOperationException("Only an applying Roslyn action can be committed."),
            cancellationToken);
    }

    internal Task<AliRoslynActionHandle> MarkFailedAsync(
        string id,
        int expectedRevision,
        string failureCode,
        CancellationToken cancellationToken)
    {
        ValidateBounded(failureCode, 128, nameof(failureCode));
        return TransitionAsync(
            id,
            expectedRevision,
            current => current.State is AliRoslynActionHandleState.Applying
                or AliRoslynActionHandleState.Verified
                ? current with
                {
                    State = AliRoslynActionHandleState.Failed,
                    Revision = checked(current.Revision + 1),
                    FailureCode = failureCode
                }
                : throw new InvalidOperationException("The Roslyn action is not in a fail-able state."),
            cancellationToken);
    }

    internal async Task<AliRoslynActionHandle> TransitionAsync(
        string id,
        int expectedRevision,
        Func<AliRoslynActionHandle, AliRoslynActionHandle> transition,
        CancellationToken cancellationToken)
    {
        ValidateId(id);
        if (expectedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        ArgumentNullException.ThrowIfNull(transition);
        EnsureRoot();
        await using var lease = await AcquireWriterLeaseAsync(cancellationToken).ConfigureAwait(false);
        var envelope = await LoadEnvelopeAsync(id, cancellationToken).ConfigureAwait(false);
        var current = envelope.Handle;
        if (current.Revision != expectedRevision)
        {
            throw new InvalidOperationException("The Roslyn action handle changed concurrently.");
        }

        var updated = transition(current) ?? throw new InvalidDataException("The handle transition returned no state.");
        Validate(updated);
        if (!string.Equals(updated.Id, current.Id, StringComparison.Ordinal)
            || updated.Revision != checked(current.Revision + 1))
        {
            throw new InvalidDataException("The Roslyn action transition changed immutable identity or revision incorrectly.");
        }

        await WriteProtectedAsync(
                PathFor(id),
                envelope with { Handle = updated },
                replaceExisting: true)
            .ConfigureAwait(false);
        return Clone(updated);
    }

    private async Task PruneForCreateAsync(CancellationToken cancellationToken)
    {
        const string suffix = ".handle.protected";
        var files = Directory.EnumerateFiles(_root, "*" + suffix, SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Take(MaximumRetentionScanFiles + 1)
            .ToArray();
        if (files.Length > MaximumRetentionScanFiles)
        {
            throw new InvalidDataException(
                $"The Roslyn action handle store exceeds its {MaximumRetentionScanFiles}-artifact retention scan bound.");
        }
        if (files.Length < _maximumRetainedHandleFiles)
        {
            return;
        }

        // Authenticate every bounded artifact before deleting any of them. A corrupt, redirected,
        // or unauthenticated artifact leaves the complete store untouched and blocks retention.
        var authenticated = new List<(string Path, ProtectedHandleEnvelope Envelope)>(files.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            if (!fileName.EndsWith(suffix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A Roslyn action retention candidate has an invalid file name.");
            }

            var id = fileName[..^suffix.Length];
            ValidateId(id);
            authenticated.Add((file, await LoadEnvelopeAsync(id, cancellationToken).ConfigureAwait(false)));
        }

        var required = checked(files.Length - (_maximumRetainedHandleFiles - 1));
        var removable = authenticated
            .Where(candidate => IsSafeToForget(candidate.Envelope.Handle, DateTimeOffset.UtcNow))
            .OrderBy(candidate => candidate.Envelope.Handle.ExpiresAtUtc)
            .ThenBy(candidate => candidate.Envelope.Handle.CreatedAtUtc)
            .ThenBy(candidate => candidate.Envelope.Handle.Id, StringComparer.Ordinal)
            .Take(required)
            .ToArray();
        if (removable.Length != required)
        {
            throw new InvalidDataException(
                "The Roslyn action handle store is full of nonterminal or recovery-relevant artifacts.");
        }

        foreach (var candidate in removable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                    candidate.Path,
                    "The Roslyn action retention candidate is not a regular local file."))
            {
                throw new InvalidDataException(
                    "The Roslyn action retention candidate disappeared before its no-follow deletion was proved.");
            }
        }
    }

    private static bool IsSafeToForget(AliRoslynActionHandle handle, DateTimeOffset now)
    {
        if (handle.ExpiresAtUtc > now)
        {
            return false;
        }

        return handle.State switch
        {
            AliRoslynActionHandleState.Applied => true,
            AliRoslynActionHandleState.Expired => handle.PublicationTransactionId is null,
            AliRoslynActionHandleState.Failed => handle.PublicationTransactionId is null
                && handle.FailureCode is not null
                && SafeFailedRetentionCodes.Contains(handle.FailureCode),
            _ => false
        };
    }

    private async Task<FileStream> AcquireWriterLeaseAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, ".handles.writer.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return WindowsOrchestrationFileBoundary.OpenRegularFile(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    writeThrough: true,
                    "The Roslyn action handle writer lease is not a regular local file.");
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteProtectedAsync(
        string finalPath,
        ProtectedHandleEnvelope envelope,
        bool replaceExisting)
    {
        ValidateEnvelope(envelope);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (plaintext.Length is <= 0 or > MaximumHandleBytes / 2)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("The Roslyn action handle payload is unbounded.");
        }

        var protectedBytes = Protect(envelope.Handle.Id, plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        try
        {
            if (protectedBytes.Length > MaximumHandleBytes)
            {
                throw new InvalidDataException("The protected Roslyn action handle is unbounded.");
            }

            var temporaryPath = Path.Combine(_root, $".{envelope.Handle.Id}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 writeThrough: true,
                                 "The Roslyn action handle temporary file is not a regular local file."))
                {
                    await stream.WriteAsync(protectedBytes, CancellationToken.None).ConfigureAwait(false);
                    await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                WindowsOrchestrationFileBoundary.MoveRegularFile(
                    temporaryPath,
                    finalPath,
                    replaceExisting,
                    "The Roslyn action handle is not a regular local file.");
            }
            finally
            {
                _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                    temporaryPath,
                    "The Roslyn action handle temporary file is not a regular local file.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private byte[] Protect(string id, byte[] plaintext)
    {
        var entropy = Entropy(id);
        try
        {
            return ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private byte[] Unprotect(string id, byte[] protectedBytes)
    {
        var entropy = Entropy(id);
        try
        {
            return ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("The Roslyn action handle failed its current-user integrity check.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private byte[] Entropy(string id) => SHA256.HashData(Encoding.UTF8.GetBytes(
        "Ali.Coding.RoslynActionHandle\0" + _profileBinding + "\0" + id));

    private void EnsureRoot() => WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
        _root,
        "The Roslyn action handle root is not a regular local directory.");

    private string PathFor(string id) => Path.Combine(_root, id + ".handle.protected");

    private static string HashProtectedArtifact(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The Roslyn action handle does not exist.");
        }

        using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            "The Roslyn action handle is not a regular local file.");
        if (stream.Length is <= 0 or > MaximumHandleBytes)
        {
            throw new InvalidDataException("The Roslyn action handle has an invalid size.");
        }

        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void AppendHashComponent(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void Validate(AliRoslynActionHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ValidateId(handle.Id);
        ValidateDigest(handle.ActionIdentitySha256, nameof(handle.ActionIdentitySha256));
        ValidateBounded(handle.ProviderIdentity, 512, nameof(handle.ProviderIdentity));
        ValidateBounded(handle.ProviderVersion, 128, nameof(handle.ProviderVersion));
        ValidateOptionalBounded(handle.EquivalenceKey, 512, nameof(handle.EquivalenceKey));
        ValidateBounded(handle.Title, 512, nameof(handle.Title));
        if (handle.DiagnosticIds is null || handle.DiagnosticIds.Length > MaximumDiagnosticIds
            || handle.DiagnosticIds.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 128))
        {
            throw new InvalidDataException("The Roslyn action diagnostic identity list is invalid.");
        }

        ValidateBounded(handle.TargetPath, 32_768, nameof(handle.TargetPath));
        ValidateBounded(handle.SourceRoot, 32_768, nameof(handle.SourceRoot));
        ValidateBounded(handle.ProjectIdentity, 512, nameof(handle.ProjectIdentity));
        ValidateBounded(handle.DocumentIdentity, 512, nameof(handle.DocumentIdentity));
        ValidateBounded(handle.DocumentPath, 32_768, nameof(handle.DocumentPath));
        if (handle.SpanStart < 0 || handle.SpanLength < 0)
        {
            throw new InvalidDataException("The Roslyn action source span is invalid.");
        }

        ValidateOptionalBounded(handle.RequestedValue, 4_096, nameof(handle.RequestedValue));
        ValidateDigest(handle.CanonicalSolutionFingerprint, nameof(handle.CanonicalSolutionFingerprint));
        ValidateId(handle.ChangeSetId);
        ValidateDigest(handle.ChangeSetManifestDigest, nameof(handle.ChangeSetManifestDigest));
        ValidateDocumentChanges(handle.DocumentChanges);
        if (handle.CreatedAtUtc == default
            || handle.ExpiresAtUtc <= handle.CreatedAtUtc
            || handle.ExpiresAtUtc - handle.CreatedAtUtc > MaximumLifetime
            || !Enum.IsDefined(handle.State)
            || handle.Revision <= 0)
        {
            throw new InvalidDataException("The Roslyn action handle lifecycle is invalid.");
        }

        if (handle.Verification is not null)
        {
            ValidateReceipt(handle, handle.Verification);
        }

        if (handle.State == AliRoslynActionHandleState.Applied)
        {
            ValidateBounded(handle.PublicationTransactionId, 256, nameof(handle.PublicationTransactionId));
        }

        if (handle.FailureCode is not null)
        {
            ValidateBounded(handle.FailureCode, 128, nameof(handle.FailureCode));
        }
    }

    private static void ValidateEnvelope(ProtectedHandleEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Format != ProtectedEnvelopeFormat)
        {
            throw new InvalidDataException("The protected Roslyn action handle format is unsupported.");
        }

        Validate(envelope.Handle);
        ValidateDigest(
            envelope.PreviewedStagedSolutionFingerprint,
            nameof(envelope.PreviewedStagedSolutionFingerprint));
        if (envelope.Handle.Verification is not null
            && !string.Equals(
                envelope.Handle.Verification.StagedSolutionFingerprint,
                envelope.PreviewedStagedSolutionFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Roslyn verification receipt does not match the authenticated previewed staged fingerprint.");
        }
    }

    private static void ValidateReceipt(
        AliRoslynActionHandle handle,
        AliRoslynPreverificationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateId(receipt.Id);
        if (!string.Equals(receipt.ChangeSetId, handle.ChangeSetId, StringComparison.Ordinal)
            || !string.Equals(
                receipt.ChangeSetManifestDigest,
                handle.ChangeSetManifestDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                receipt.CanonicalSolutionFingerprint,
                handle.CanonicalSolutionFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Roslyn verification receipt does not bind this preview handle.");
        }

        ValidateDigest(receipt.ChangeSetManifestDigest, nameof(receipt.ChangeSetManifestDigest));
        ValidateDigest(receipt.CanonicalSolutionFingerprint, nameof(receipt.CanonicalSolutionFingerprint));
        ValidateDigest(receipt.StagedSolutionFingerprint, nameof(receipt.StagedSolutionFingerprint));
        ValidateDigest(receipt.BaselineDiagnosticsDigest, nameof(receipt.BaselineDiagnosticsDigest));
        ValidateDigest(receipt.StagedDiagnosticsDigest, nameof(receipt.StagedDiagnosticsDigest));
        ValidateId(receipt.MaterializationReceiptId);
        ValidateDigest(receipt.InputBindingDigest, nameof(receipt.InputBindingDigest));
        ValidateDigest(receipt.InputManifestPolicyDigest, nameof(receipt.InputManifestPolicyDigest));
        ValidateDigest(receipt.CanonicalInputManifestDigest, nameof(receipt.CanonicalInputManifestDigest));
        ValidateDigest(receipt.StagedInputManifestDigest, nameof(receipt.StagedInputManifestDigest));
        ValidateDigest(receipt.VerificationDigest, nameof(receipt.VerificationDigest));
        if (receipt.TestsRun < 0
            || receipt.CreatedAtUtc == default
            || receipt.ExpiresAtUtc <= receipt.CreatedAtUtc
            || receipt.ExpiresAtUtc > handle.ExpiresAtUtc)
        {
            throw new InvalidDataException("The Roslyn verification receipt lifecycle is invalid.");
        }
    }

    private static void ValidateDocumentChanges(IReadOnlyList<AliRoslynDocumentChange>? changes)
    {
        if (changes is null)
        {
            return;
        }
        if (changes.Count is <= 0 or > AliSourceChangeSetStore.MaximumOperations)
        {
            throw new InvalidDataException("The protected Roslyn document delta is empty or unbounded.");
        }
        foreach (var change in changes)
        {
            if (change is null
                || !Enum.IsDefined(change.Kind)
                || !Enum.IsDefined(change.DocumentKind)
                || !Enum.IsDefined(change.SourceCodeKind))
            {
                throw new InvalidDataException("A protected Roslyn document delta kind is invalid.");
            }
            ValidateRelativePath(change.ProjectRelativePath, nameof(change.ProjectRelativePath));
            if (change.SourceRelativePath is not null)
            {
                ValidateRelativePath(change.SourceRelativePath, nameof(change.SourceRelativePath));
            }
            if (change.DestinationRelativePath is not null)
            {
                ValidateRelativePath(change.DestinationRelativePath, nameof(change.DestinationRelativePath));
            }
            ValidateDocumentMetadata(change.CanonicalName, change.CanonicalFolders, allowMissing: change.Kind == AliRoslynDocumentChangeKind.Add);
            ValidateDocumentMetadata(change.StagedName, change.StagedFolders, allowMissing: change.Kind == AliRoslynDocumentChangeKind.Delete);
            if (change.SourceOperationSequences is null
                || change.SourceOperationSequences.Length is < 1 or > 2
                || change.SourceOperationSequences.Any(sequence => sequence < 0)
                || change.SourceOperationSequences.Distinct().Count() != change.SourceOperationSequences.Length)
            {
                throw new InvalidDataException("A protected Roslyn document delta operation binding is invalid.");
            }
        }
    }

    private static void ValidateDocumentMetadata(
        string? name,
        IReadOnlyList<string>? folders,
        bool allowMissing)
    {
        if ((!allowMissing && string.IsNullOrWhiteSpace(name))
            || name is { Length: > 1_024 }
            || folders is null
            || folders.Count > 64
            || folders.Any(folder => string.IsNullOrWhiteSpace(folder) || folder.Length > 1_024))
        {
            throw new InvalidDataException("Protected Roslyn document metadata is invalid or unbounded.");
        }
    }

    private static void ValidateRelativePath(string value, string name)
    {
        var normalized = AliSourceChangeSetStore.NormalizeRelativePath(value);
        if (!string.Equals(normalized, value.Replace('\\', '/'), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{name} is not a canonical relative source path.");
        }
    }

    private static AliRoslynActionHandle Clone(AliRoslynActionHandle handle) => handle with
    {
        DiagnosticIds = handle.DiagnosticIds.ToArray(),
        Verification = handle.Verification is null ? null : handle.Verification with { },
        DocumentChanges = handle.DocumentChanges?.Select(change => change with
        {
            CanonicalFolders = change.CanonicalFolders.ToArray(),
            StagedFolders = change.StagedFolders.ToArray(),
            SourceOperationSequences = change.SourceOperationSequences.ToArray()
        }).ToArray()
    };

    private static void ValidateId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length is < 16 or > 128
            || value.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new InvalidDataException("The Roslyn action artifact ID is invalid.");
        }
    }

    private static void ValidateDigest(string value, string name)
    {
        if (value?.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidDataException($"{name} must be a SHA-256 digest.");
        }
    }

    private static void ValidateBounded(string? value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
        {
            throw new InvalidDataException($"{name} is missing or unbounded.");
        }
    }

    private static void ValidateOptionalBounded(string? value, int maximum, string name)
    {
        if (value is null || value.Length > maximum)
        {
            throw new InvalidDataException($"{name} is null or unbounded.");
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var error = exception.HResult & 0xFFFF;
        return error is 32 or 33
               || exception.InnerException is Win32Exception { NativeErrorCode: 32 or 33 };
    }

    private sealed record ProtectedHandleEnvelope(
        int Format,
        AliRoslynActionHandle Handle,
        string PreviewedStagedSolutionFingerprint);
}
