using System.Security.Cryptography;
using Ali.Modules.Orchestration.Contracts;

namespace Ali.Modules.Orchestration.Evidence;

internal sealed record EvidencePayloadBinding(
    string AssistantProfileBinding,
    string TurnStorageKey,
    string EvidenceId,
    string CapabilityGroupDigest,
    string ToolNameDigest,
    string ProviderIdDigest,
    string MetadataDigest);

internal sealed record ProtectedEvidencePayloadReference(
    string Reference,
    string Digest);

internal sealed class ProtectedEvidencePayloadStore
{
    internal const int MaximumPlaintextBytes = 8 * 1024 * 1024;
    private const int MaximumEnvelopeBytes = MaximumPlaintextBytes + 64;
    private readonly string _rootDirectory;
    private readonly string _assistantProfileBinding;

    public ProtectedEvidencePayloadStore(
        string rootDirectory,
        string assistantProfileBinding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantProfileBinding);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _assistantProfileBinding = assistantProfileBinding;
    }

    public async Task<ProtectedEvidencePayloadReference> WriteAsync(
        string turnStorageKey,
        string evidenceId,
        string capabilityGroupDigest,
        string toolNameDigest,
        string providerIdDigest,
        string metadataDigest,
        ReadOnlyMemory<byte> plaintext,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        RequireStorageKey(turnStorageKey);
        if (plaintext.Length > MaximumPlaintextBytes)
        {
            throw new InvalidDataException(
                $"Protected orchestration evidence cannot exceed {MaximumPlaintextBytes} bytes.");
        }
        var binding = CreateBinding(
            turnStorageKey,
            evidenceId,
            capabilityGroupDigest,
            toolNameDigest,
            providerIdDigest,
            metadataDigest);
        var associatedData = CanonicalEvidenceJson.SerializeToUtf8Bytes(binding);
        var envelope = keys.Protect(plaintext.Span, associatedData);
        try
        {
            var digest = Convert.ToHexString(SHA256.HashData(envelope)).ToLowerInvariant();
            var directory = GetPayloadDirectory(turnStorageKey);
            WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
                directory,
                "The protected evidence payload directory is not a regular local directory.");
            var finalPath = Path.Combine(directory, digest + ".evidence");
            var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.payload.tmp");
            try
            {
                await using (var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 writeThrough: true,
                                 "The protected evidence temporary payload is not a regular local file."))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await stream.WriteAsync(envelope, CancellationToken.None).ConfigureAwait(false);
                    await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                WindowsOrchestrationFileBoundary.MoveRegularFile(
                    temporaryPath,
                    finalPath,
                    replaceExisting: false,
                    "The protected evidence payload is not a regular local file.");
            }
            finally
            {
                _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                    temporaryPath,
                    "The protected evidence temporary payload is not a regular local file.");
            }

            return new ProtectedEvidencePayloadReference(digest, digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(associatedData);
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    public async Task ValidateAsync(
        string turnStorageKey,
        ProtectedEvidencePayloadReference reference,
        CancellationToken cancellationToken)
    {
        RequireStorageKey(turnStorageKey);
        var path = GetValidatedPayloadPath(turnStorageKey, reference.Reference);
        var envelope = await ReadBoundedEnvelopeAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            var digest = Convert.ToHexString(SHA256.HashData(envelope)).ToLowerInvariant();
            if (!FixedTimeHexEquals(digest, reference.Digest))
            {
                throw new InvalidDataException(
                    $"Protected orchestration evidence '{reference.Reference}' failed digest validation.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    public async Task<byte[]> ReadAsync(
        string turnStorageKey,
        EvidenceRecord record,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        RequireStorageKey(turnStorageKey);
        var path = GetValidatedPayloadPath(turnStorageKey, record.ProtectedPayloadReference);
        var envelope = await ReadBoundedEnvelopeAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            var digest = Convert.ToHexString(SHA256.HashData(envelope)).ToLowerInvariant();
            if (!FixedTimeHexEquals(digest, record.ProtectedPayloadDigest))
            {
                throw new InvalidDataException(
                    $"Protected orchestration evidence '{record.ProtectedPayloadReference}' failed digest validation.");
            }

            var binding = CreateBinding(
                turnStorageKey,
                record.EvidenceId,
                record.CapabilityGroupDigest,
                record.ToolNameDigest,
                record.ProviderIdDigest,
                record.MetadataDigest);
            var associatedData = CanonicalEvidenceJson.SerializeToUtf8Bytes(binding);
            try
            {
                return keys.Unprotect(envelope, associatedData);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException(
                    $"Protected orchestration evidence '{record.ProtectedPayloadReference}' failed authentication.",
                    ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(associatedData);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private EvidencePayloadBinding CreateBinding(
        string turnStorageKey,
        string evidenceId,
        string capabilityGroupDigest,
        string toolNameDigest,
        string providerIdDigest,
        string metadataDigest) =>
        new(
            _assistantProfileBinding,
            turnStorageKey,
            evidenceId,
            capabilityGroupDigest,
            toolNameDigest,
            providerIdDigest,
            metadataDigest);

    private static async Task<byte[]> ReadBoundedEnvelopeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidDataException(
                "The protected evidence payload path has no directory.");
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            directory,
            "The protected evidence payload directory is not a regular local directory.");
        return await WindowsBoundedFileReader.TryReadExactlyAsync(
            WindowsOrchestrationFileBoundary.ToExtendedLengthWin32Path(path),
            minimumLength: 1,
            MaximumEnvelopeBytes,
            "The protected evidence payload is not a regular local file.",
            $"A protected orchestration evidence envelope must be between 1 and {MaximumEnvelopeBytes} bytes.",
            "The protected orchestration evidence envelope changed while it was being read.",
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("A referenced protected orchestration evidence payload is missing.");
    }

    private string GetPayloadDirectory(string turnStorageKey) =>
        Path.Combine(_rootDirectory, "turns", turnStorageKey, "payloads");

    private string GetValidatedPayloadPath(string turnStorageKey, string reference)
    {
        if (reference.Length != 64 || reference.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
        {
            throw new InvalidDataException("A protected evidence payload reference is invalid.");
        }

        return Path.Combine(GetPayloadDirectory(turnStorageKey), reference + ".evidence");
    }

    private static void RequireStorageKey(string turnStorageKey)
    {
        if (turnStorageKey is null
            || turnStorageKey.Length != 64
            || turnStorageKey.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
        {
            throw new ArgumentException("The evidence turn storage key is invalid.", nameof(turnStorageKey));
        }
    }

    private static bool FixedTimeHexEquals(string left, string right)
    {
        byte[] leftBytes;
        byte[] rightBytes;
        try
        {
            leftBytes = Convert.FromHexString(left);
            rightBytes = Convert.FromHexString(right);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            return leftBytes.Length == rightBytes.Length
                   && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }
}
