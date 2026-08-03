using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Orchestration.State;

/// <summary>
/// An authenticated reference to exact user input. The referenced plaintext is never written
/// to the turn journal; it is encrypted for the current Windows user in the turn input store.
/// </summary>
public sealed record ProtectedTurnInputReference(
    string ContentDigest,
    string PayloadReference,
    string BindingDigest,
    int Utf8Length)
{
    internal void Validate(string purpose, string correlationKey)
    {
        ValidateStoredShape();
        TurnStateIntegrity.RequireBoundedValue(purpose, 64, nameof(purpose));
        TurnStateIntegrity.RequireBoundedValue(correlationKey, 256, nameof(correlationKey));
        var expectedBinding = TurnStateIntegrity.Digest(purpose + "\0" + correlationKey);
        if (!string.Equals(expectedBinding, BindingDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The protected turn input is not bound to this transition identity.");
        }
    }

    internal void ValidateStoredShape()
    {
        TurnStateIntegrity.RequireDigest(ContentDigest, nameof(ContentDigest));
        TurnStateIntegrity.RequireDigest(PayloadReference, nameof(PayloadReference));
        TurnStateIntegrity.RequireDigest(BindingDigest, nameof(BindingDigest));
        if (Utf8Length <= 0 || Utf8Length > ProtectedTurnInputStore.MaximumPlaintextBytes)
        {
            throw new InvalidDataException("The protected turn input length is invalid.");
        }
    }
}

internal static class TurnInputPurposes
{
    public const string OriginalRequest = "original-request";
    public const string Steering = "steering";
    public const string AcceptedConversationEntry = "accepted-conversation-entry";
    public const string AcceptedCall = "accepted-call";
    public const string InterimPublicationText = "interim-publication-text";
    public const string FinalPublicationAnswer = "final-publication-answer";

    public static string AcceptedConversationBinding(
        string sourceMessageId,
        long sequence,
        AcceptedConversationRole role,
        string contentDigest) =>
        TurnStateIntegrity.Digest(
            "accepted-conversation-entry-v2\0"
            + sourceMessageId
            + "\0"
            + sequence.ToString(CultureInfo.InvariantCulture)
            + "\0"
            + ((int)role).ToString(CultureInfo.InvariantCulture)
            + "\0"
            + contentDigest);

    public static string AcceptedCallBinding(
        string callId,
        string workItemId,
        string toolName,
        string? capabilityId,
        string registryRevisionDigest,
        string argumentsDigest,
        string actionFingerprint,
        AcceptedCallExecutionClass executionClass,
        string? reconcilerId) =>
        TurnStateIntegrity.Digest(
            "accepted-call-v1\0"
            + callId
            + "\0"
            + workItemId
            + "\0"
            + toolName
            + "\0"
            + (capabilityId ?? string.Empty)
            + "\0"
            + registryRevisionDigest
            + "\0"
            + argumentsDigest
            + "\0"
            + actionFingerprint
            + "\0"
            + ((int)executionClass).ToString(CultureInfo.InvariantCulture)
            + "\0"
            + (reconcilerId ?? string.Empty));

    public static string InterimPublicationBinding(
        string publicationId,
        InterimPublicationKind kind,
        InterimPublicationReason reason,
        string subjectId,
        string textDigest) =>
        TurnStateIntegrity.Digest(
            "interim-publication-text-v2\0"
            + publicationId
            + "\0"
            + ((int)kind).ToString(CultureInfo.InvariantCulture)
            + "\0"
            + ((int)reason).ToString(CultureInfo.InvariantCulture)
            + "\0"
            + subjectId
            + "\0"
            + textDigest);

    public static string FinalPublicationBinding(
        string publicationId,
        string assistantMessageId,
        string answerDigest) =>
        TurnStateIntegrity.Digest(
            "final-publication-answer-v1\0"
            + publicationId
            + "\0"
            + assistantMessageId
            + "\0"
            + answerDigest);
}

internal sealed record ProtectedTurnInputBinding(
    string TurnStorageKey,
    string Purpose,
    string BindingDigest,
    string ContentDigest,
    int Utf8Length);

/// <summary>
/// Stores exact user input in independently authenticated AES-GCM envelopes. The encryption
/// key is itself protected by Windows DPAPI for the current user. Stable HMAC references make
/// an interrupted retry idempotent without exposing plaintext or a raw content hash as a path.
/// </summary>
internal sealed class ProtectedTurnInputStore
{
    internal const int MaximumPlaintextBytes = 16 * 1024 * 1024;
    private const int EnvelopeOverheadBytes = 40;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly string _rootDirectory;

    public ProtectedTurnInputStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public async Task<ProtectedTurnInputReference> StoreAsync(
        TurnIdentity identity,
        string purpose,
        string correlationKey,
        string plaintext,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        TurnStateIntegrity.RequireBoundedValue(purpose, 64, nameof(purpose));
        TurnStateIntegrity.RequireBoundedValue(correlationKey, 256, nameof(correlationKey));
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            throw new ArgumentException("Exact turn input must not be empty.", nameof(plaintext));
        }

        ArgumentNullException.ThrowIfNull(keys);
        var byteCount = StrictUtf8.GetByteCount(plaintext);
        if (byteCount <= 0 || byteCount > MaximumPlaintextBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(plaintext),
                $"Exact turn input must be at most {MaximumPlaintextBytes} UTF-8 bytes.");
        }

        var plaintextBytes = new byte[byteCount];
        StrictUtf8.GetBytes(plaintext, plaintextBytes);
        try
        {
            var contentDigest = TurnStateIntegrity.Digest(plaintextBytes);
            var bindingDigest = TurnStateIntegrity.Digest(purpose + "\0" + correlationKey);
            var binding = new ProtectedTurnInputBinding(
                identity.StorageKey,
                purpose,
                bindingDigest,
                contentDigest,
                plaintextBytes.Length);
            var associatedData = CanonicalEvidenceJson.SerializeToUtf8Bytes(binding);
            try
            {
                var payloadReference = keys.HmacHex(
                    EvidenceKeyPurpose.Identifier,
                    associatedData);
                var reference = new ProtectedTurnInputReference(
                    contentDigest,
                    payloadReference,
                    bindingDigest,
                    plaintextBytes.Length);
                var path = GetValidatedPayloadPath(identity.StorageKey, payloadReference);
                if (File.Exists(path))
                {
                    await RequireExistingPayloadMatchesAsync(
                        path,
                        reference,
                        associatedData,
                        plaintextBytes,
                        keys,
                        cancellationToken).ConfigureAwait(false);
                    return reference;
                }

                var envelope = keys.Protect(plaintextBytes, associatedData);
                try
                {
                    var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException("The protected input path has no directory.");
                    WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
                        directory,
                        "The protected turn-input directory is not a regular local directory.");
                    var temporaryPath = Path.Combine(
                        directory,
                        $".{Guid.NewGuid():N}.turn-input.tmp");
                    try
                    {
                        await using (var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                                         temporaryPath,
                                         FileMode.CreateNew,
                                         FileAccess.Write,
                                         FileShare.None,
                                         writeThrough: true,
                                         "The protected turn-input temporary file is not a regular local file."))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await stream.WriteAsync(envelope, CancellationToken.None)
                                .ConfigureAwait(false);
                            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                            stream.Flush(flushToDisk: true);
                        }

                        try
                        {
                            WindowsOrchestrationFileBoundary.MoveRegularFile(
                                temporaryPath,
                                path,
                                replaceExisting: false,
                                "The protected turn input is not a regular local file.");
                        }
                        catch (IOException) when (File.Exists(path))
                        {
                            await RequireExistingPayloadMatchesAsync(
                                path,
                                reference,
                                associatedData,
                                plaintextBytes,
                                keys,
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                            temporaryPath,
                            "The protected turn-input temporary file is not a regular local file.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(envelope);
                }

                return reference;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(associatedData);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    public async Task<string> OpenAsync(
        TurnIdentity identity,
        string purpose,
        ProtectedTurnInputReference reference,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        TurnStateIntegrity.RequireBoundedValue(purpose, 64, nameof(purpose));
        ArgumentNullException.ThrowIfNull(reference);
        reference.ValidateStoredShape();
        ArgumentNullException.ThrowIfNull(keys);

        var binding = new ProtectedTurnInputBinding(
            identity.StorageKey,
            purpose,
            reference.BindingDigest,
            reference.ContentDigest,
            reference.Utf8Length);
        var associatedData = CanonicalEvidenceJson.SerializeToUtf8Bytes(binding);
        try
        {
            var expectedReference = keys.HmacHex(EvidenceKeyPurpose.Identifier, associatedData);
            if (!FixedTimeHexEquals(expectedReference, reference.PayloadReference))
            {
                throw new InvalidDataException(
                    "The protected turn input reference failed authentication.");
            }

            var path = GetValidatedPayloadPath(identity.StorageKey, reference.PayloadReference);
            var plaintext = await OpenPayloadAsync(
                path,
                associatedData,
                keys,
                cancellationToken).ConfigureAwait(false);
            try
            {
                if (plaintext.Length != reference.Utf8Length
                    || !FixedTimeHexEquals(
                        TurnStateIntegrity.Digest(plaintext),
                        reference.ContentDigest))
                {
                    throw new InvalidDataException(
                        "The protected turn input does not match its committed digest and length.");
                }

                try
                {
                    return StrictUtf8.GetString(plaintext);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new InvalidDataException(
                        "The protected turn input is not valid UTF-8.",
                        ex);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private async Task RequireExistingPayloadMatchesAsync(
        string path,
        ProtectedTurnInputReference reference,
        byte[] associatedData,
        byte[] expectedPlaintext,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        var plaintext = await OpenPayloadAsync(
            path,
            associatedData,
            keys,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (plaintext.Length != reference.Utf8Length
                || !CryptographicOperations.FixedTimeEquals(plaintext, expectedPlaintext)
                || !FixedTimeHexEquals(
                    TurnStateIntegrity.Digest(plaintext),
                    reference.ContentDigest))
            {
                throw new InvalidDataException(
                    "An existing protected turn input does not match the requested content.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static async Task<byte[]> OpenPayloadAsync(
        string path,
        byte[] associatedData,
        EvidenceKeySession keys,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidDataException("The protected turn-input path has no directory.");
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            directory,
            "The protected turn-input directory is not a regular local directory.");
        var envelope = await WindowsBoundedFileReader.TryReadExactlyAsync(
            WindowsOrchestrationFileBoundary.ToExtendedLengthWin32Path(path),
            EnvelopeOverheadBytes + 1,
            MaximumPlaintextBytes + EnvelopeOverheadBytes,
            "A protected turn input is not a regular local file.",
            "A protected turn input has an invalid length.",
            "A protected turn input changed while it was being read.",
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("A referenced protected turn input is missing.");
        try
        {
            try
            {
                return keys.Unprotect(envelope, associatedData);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException(
                    "A protected turn input failed authentication.",
                    ex);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private string GetValidatedPayloadPath(string turnStorageKey, string reference)
    {
        TurnStateIntegrity.RequireDigest(turnStorageKey, nameof(turnStorageKey));
        TurnStateIntegrity.RequireDigest(reference, nameof(reference));
        return Path.Combine(
            _rootDirectory,
            "turns",
            turnStorageKey,
            "inputs",
            reference + ".turn-input");
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
