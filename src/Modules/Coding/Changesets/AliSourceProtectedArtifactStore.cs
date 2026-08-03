using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Coding.Changesets;

internal sealed partial class AliSourceChangeSetStore
{
    internal async Task<byte[]> ReadContentAsync(
        AliSourceChangeSet changeSet,
        AliSourceChangeOperation operation,
        CancellationToken cancellationToken) =>
        await ReadProtectedPayloadAsync(
            changeSet,
            operation,
            isBackup: false,
            cancellationToken).ConfigureAwait(false);

    internal async Task<byte[]> ReadBackupAsync(
        AliSourceChangeSet changeSet,
        AliSourceChangeOperation operation,
        CancellationToken cancellationToken) =>
        await ReadProtectedPayloadAsync(
            changeSet,
            operation,
            isBackup: true,
            cancellationToken).ConfigureAwait(false);

    internal string GetTransactionDirectory(string changeSetId)
    {
        ValidateId(changeSetId);
        return Path.Combine(GetChangeSetDirectory(changeSetId), TransactionsDirectoryName);
    }

    internal string GetReceiptPath(string changeSetId) =>
        Path.Combine(GetTransactionDirectory(changeSetId), "receipt.json");

    internal string GetJournalPath(string changeSetId) =>
        Path.Combine(GetTransactionDirectory(changeSetId), "publication.journal");

    internal string GetRootLeasePath(string canonicalSourceRoot)
    {
        var rootBytes = Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalSourceRoot)).ToUpperInvariant());
        string digest;
        try
        {
            digest = Hash(rootBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootBytes);
        }

        var directory = Path.Combine(_root, "publication-leases");
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            directory,
            "The source publication lease root is not a regular local directory.");
        return Path.Combine(directory, digest + ".lock");
    }

    internal async Task<string> AuthenticateAsync(
        string purpose,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        using var session = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        return AuthenticateWithSession(session, purpose, payload.Span);
    }

    internal async Task<bool> VerifyAuthenticationAsync(
        string purpose,
        ReadOnlyMemory<byte> payload,
        string expectedTag,
        CancellationToken cancellationToken)
    {
        using var session = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        var domain = Encoding.UTF8.GetBytes("Ali.SourceChangeSet." + purpose + "\0");
        var authenticated = new byte[checked(domain.Length + payload.Length)];
        try
        {
            domain.CopyTo(authenticated, 0);
            payload.Span.CopyTo(authenticated.AsSpan(domain.Length));
            return session.VerifyHmac(EvidenceKeyPurpose.RecordMac, authenticated, expectedTag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(domain);
            CryptographicOperations.ZeroMemory(authenticated);
        }
    }

    internal async Task WriteAuthenticatedDocumentAsync<T>(
        string path,
        string purpose,
        T value,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        try
        {
            if (payload.Length is <= 0 || payload.Length > maximumBytes)
            {
                throw new InvalidDataException("A durable source transaction document is invalid or unbounded.");
            }
            var tag = await AuthenticateAsync(purpose, payload, cancellationToken).ConfigureAwait(false);
            var envelope = JsonSerializer.SerializeToUtf8Bytes(
                new AliAuthenticatedDocument(Convert.ToBase64String(payload), tag),
                JsonOptions);
            try
            {
                if (envelope.Length > maximumBytes)
                {
                    throw new InvalidDataException("A durable source transaction envelope is unbounded.");
                }
                await WriteFileDurablyAsync(path, envelope, replaceExisting: true).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelope);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    internal async Task<T?> ReadAuthenticatedDocumentAsync<T>(
        string path,
        string purpose,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }
        var envelopeBytes = await ReadBoundedStoreFileAsync(
            path,
            maximumBytes,
            "A durable source transaction document is invalid or unbounded.",
            cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = JsonSerializer.Deserialize<AliAuthenticatedDocument>(envelopeBytes, JsonOptions)
                ?? throw new InvalidDataException("A durable source transaction document could not be read.");
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(envelope.PayloadBase64);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("A durable source transaction payload is malformed.", ex);
            }
            try
            {
                if (payload.Length is <= 0 || payload.Length > maximumBytes)
                {
                    throw new InvalidDataException("A durable source transaction payload is invalid or unbounded.");
                }
                if (!await VerifyAuthenticationAsync(
                        purpose,
                        payload,
                        envelope.AuthenticationTag,
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("A durable source transaction document failed authentication.");
                }
                return JsonSerializer.Deserialize<T>(payload, JsonOptions)
                    ?? throw new InvalidDataException("A durable source transaction payload could not be read.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("A durable source transaction document is malformed.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelopeBytes);
        }
    }

    private async Task SaveAsync(
        AliSourceChangeSet changeSet,
        IReadOnlyList<PendingOperation> pending,
        byte[] unsignedBytes,
        CancellationToken cancellationToken)
    {
        var changeSetDirectory = GetChangeSetDirectory(changeSet.Id);
        var blobsDirectory = Path.Combine(changeSetDirectory, BlobsDirectoryName);
        var backupsDirectory = Path.Combine(changeSetDirectory, BackupsDirectoryName);
        var transactionDirectory = Path.Combine(changeSetDirectory, TransactionsDirectoryName);
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            blobsDirectory,
            "The source changeset blob directory is not a regular local directory.");
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            backupsDirectory,
            "The source changeset backup directory is not a regular local directory.");
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            transactionDirectory,
            "The source transaction directory is not a regular local directory.");

        using var session = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.PostBytes is not null)
            {
                await WriteProtectedPayloadAsync(
                    session,
                    changeSet,
                    item.Operation,
                    item.PostBytes,
                    isBackup: false).ConfigureAwait(false);
            }
            if (item.BackupBytes is not null)
            {
                await WriteProtectedPayloadAsync(
                    session,
                    changeSet,
                    item.Operation,
                    item.BackupBytes,
                    isBackup: true).ConfigureAwait(false);
            }
        }

        var authenticationTag = AuthenticateWithSession(session, "manifest", unsignedBytes);
        var envelope = JsonSerializer.SerializeToUtf8Bytes(
            new AliSourceManifestEnvelope(
                new AliSourceChangeSetUnsigned(
                    changeSet.Id,
                    changeSet.CanonicalSourceRoot,
                    changeSet.CreatedAtUtc,
                    changeSet.SourceRootIdentity,
                    changeSet.NamespaceSpines,
                    changeSet.Operations),
                authenticationTag),
            JsonOptions);
        try
        {
            if (envelope.Length is <= 0 or > MaximumManifestBytes)
            {
                throw new InvalidOperationException("The authenticated source manifest is invalid or unbounded.");
            }
            await WriteFileDurablyAsync(GetManifestPath(changeSet.Id), envelope, replaceExisting: false)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private async Task WriteProtectedPayloadAsync(
        EvidenceKeySession session,
        AliSourceChangeSet changeSet,
        AliSourceChangeOperation operation,
        byte[] plaintext,
        bool isBackup)
    {
        var expected = isBackup ? operation.SourcePreimage : operation.SourcePostimage;
        if (!expected.Exists
            || plaintext.LongLength != expected.Length
            || !string.Equals(Hash(plaintext), expected.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A staged source payload does not match its manifest image.");
        }
        var associatedData = CreatePayloadAssociatedData(changeSet, operation, isBackup, expected);
        var protectedPayload = session.Protect(plaintext, associatedData);
        try
        {
            await WriteFileDurablyAsync(
                GetPayloadPath(changeSet.Id, operation, isBackup),
                protectedPayload,
                replaceExisting: false).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(associatedData);
            CryptographicOperations.ZeroMemory(protectedPayload);
        }
    }

    private async Task<byte[]> ReadProtectedPayloadAsync(
        AliSourceChangeSet changeSet,
        AliSourceChangeOperation operation,
        bool isBackup,
        CancellationToken cancellationToken)
    {
        var expected = isBackup ? operation.SourcePreimage : operation.SourcePostimage;
        if (!expected.Exists)
        {
            throw new InvalidDataException("The manifest does not declare the requested protected source payload.");
        }
        var maximumEnvelopeBytes = checked(MaximumFileBytes + 128);
        var envelope = await ReadBoundedStoreFileAsync(
            GetPayloadPath(changeSet.Id, operation, isBackup),
            maximumEnvelopeBytes,
            "A protected source payload is missing, invalid, or unbounded.",
            cancellationToken).ConfigureAwait(false);
        var associatedData = CreatePayloadAssociatedData(changeSet, operation, isBackup, expected);
        try
        {
            using var session = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
            byte[] plaintext;
            try
            {
                plaintext = session.Unprotect(envelope, associatedData);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException("A protected source payload failed authentication.", ex);
            }
            if (plaintext.LongLength != expected.Length
                || !string.Equals(Hash(plaintext), expected.Sha256, StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new InvalidDataException("A protected source payload does not match its manifest hash.");
            }
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private static byte[] CreatePayloadAssociatedData(
        AliSourceChangeSet changeSet,
        AliSourceChangeOperation operation,
        bool isBackup,
        AliSourceFileImage image) =>
        Encoding.UTF8.GetBytes(string.Join(
            '\0',
            "Ali.SourceChangeSet.Payload.v1",
            changeSet.Id,
            changeSet.ManifestDigest,
            operation.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            isBackup ? "backup" : "postimage",
            image.Sha256,
            image.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private string GetChangeSetDirectory(string id)
    {
        ValidateId(id);
        return Path.Combine(_root, "changesets", id);
    }

    private string GetManifestPath(string id) => Path.Combine(GetChangeSetDirectory(id), ManifestFileName);

    private string GetPayloadPath(string id, AliSourceChangeOperation operation, bool isBackup) =>
        isBackup
            ? Path.Combine(GetChangeSetDirectory(id), BackupsDirectoryName, $"{operation.Sequence:D4}.protected")
            : Path.Combine(
                GetChangeSetDirectory(id),
                BlobsDirectoryName,
                $"{operation.Sequence:D4}-{operation.ContentBlobSha256
                    ?? throw new InvalidDataException("A content operation has no blob digest.")}.protected");

    private static string AuthenticateWithSession(
        EvidenceKeySession session,
        string purpose,
        ReadOnlySpan<byte> payload)
    {
        var domain = Encoding.UTF8.GetBytes("Ali.SourceChangeSet." + purpose + "\0");
        var authenticated = new byte[checked(domain.Length + payload.Length)];
        try
        {
            domain.CopyTo(authenticated, 0);
            payload.CopyTo(authenticated.AsSpan(domain.Length));
            return session.HmacHex(EvidenceKeyPurpose.RecordMac, authenticated);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(domain);
            CryptographicOperations.ZeroMemory(authenticated);
        }
    }

    private sealed record AliAuthenticatedDocument(string PayloadBase64, string AuthenticationTag);
}
