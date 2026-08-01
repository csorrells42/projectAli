using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ali.Modules.Orchestration.Evidence;

internal enum EvidenceKeyPurpose
{
    TurnBinding,
    TurnStorage,
    Identifier,
    Arguments,
    NormalizedTarget,
    NormalizedResult,
    Result,
    PermissionReceipt,
    NoEffect,
    RecordMac,
    JournalHead
}

internal sealed class WindowsCurrentUserEvidenceProtector
{
    private const string KeyFileName = "evidence.key.protected";
    private const string KeyLockFileName = ".evidence-key.writer.lock";
    private const int MasterKeyLength = 32;
    private readonly string _rootDirectory;
    private readonly byte[] _entropy;

    public WindowsCurrentUserEvidenceProtector(string rootDirectory, string assistantProfileBinding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantProfileBinding);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _entropy = SHA256.HashData(Encoding.UTF8.GetBytes(
            "Ali.Orchestration.Evidence.CurrentUser\0" + assistantProfileBinding));
    }

    public async Task<EvidenceKeySession> OpenSessionAsync(
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_rootDirectory);
        await using var lease = await AcquireKeyLeaseAsync(cancellationToken).ConfigureAwait(false);
        var keyPath = Path.Combine(_rootDirectory, KeyFileName);
        byte[] masterKey;
        if (File.Exists(keyPath))
        {
            var wrapped = await File.ReadAllBytesAsync(keyPath, cancellationToken).ConfigureAwait(false);
            try
            {
                masterKey = ProtectedData.Unprotect(
                    wrapped,
                    _entropy,
                    DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException(
                    "The protected orchestration evidence key cannot be opened by the current Windows user.",
                    ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrapped);
            }

            if (masterKey.Length != MasterKeyLength)
            {
                CryptographicOperations.ZeroMemory(masterKey);
                throw new InvalidDataException("The protected orchestration evidence key has an invalid length.");
            }
        }
        else
        {
            var turnsDirectory = Path.Combine(_rootDirectory, "turns");
            if (Directory.Exists(turnsDirectory)
                && Directory.EnumerateFileSystemEntries(turnsDirectory).Any())
            {
                throw new InvalidDataException(
                    "The protected orchestration evidence key is missing while durable evidence still exists.");
            }

            masterKey = RandomNumberGenerator.GetBytes(MasterKeyLength);
            var wrapped = ProtectedData.Protect(
                masterKey,
                _entropy,
                DataProtectionScope.CurrentUser);
            try
            {
                await WriteNewKeyAsync(keyPath, wrapped).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrapped);
            }
        }

        try
        {
            return new EvidenceKeySession(masterKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private async Task<FileStream> AcquireKeyLeaseAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_rootDirectory, KeyLockFileName);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteNewKeyAsync(string finalPath, byte[] wrapped)
    {
        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("The protected evidence key path has no directory.");
        var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.key.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(wrapped, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, finalPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var error = exception.HResult & 0xFFFF;
        return error is 32 or 33;
    }
}

internal sealed class EvidenceKeySession : IDisposable
{
    private static readonly byte[] EnvelopeMagic = [0x41, 0x4C, 0x49, 0x45, 0x56, 0x49, 0x44, 0x00];
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private readonly byte[] _encryptionKey;
    private readonly Dictionary<EvidenceKeyPurpose, byte[]> _hmacKeys;
    private bool _disposed;

    public EvidenceKeySession(ReadOnlySpan<byte> masterKey)
    {
        _encryptionKey = DeriveKey(masterKey, "payload-encryption");
        _hmacKeys = [];
        foreach (var purpose in Enum.GetValues<EvidenceKeyPurpose>())
        {
            _hmacKeys.Add(
                purpose,
                DeriveKey(masterKey, "hmac-" + purpose.ToString()));
        }
    }

    public string HmacHex(EvidenceKeyPurpose purpose, ReadOnlySpan<byte> value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var hash = HMACSHA256.HashData(_hmacKeys[purpose], value);
        try
        {
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public bool VerifyHmac(
        EvidenceKeyPurpose purpose,
        ReadOnlySpan<byte> value,
        string expectedHex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualHex = HmacHex(purpose, value);
        var actual = Convert.FromHexString(actualHex);
        try
        {
            return expected.Length == actual.Length
                   && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var tag = new byte[TagLength];
        var ciphertext = new byte[plaintext.Length];
        try
        {
            using var aes = new AesGcm(_encryptionKey, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            var envelope = new byte[
                EnvelopeMagic.Length + NonceLength + TagLength + sizeof(int) + ciphertext.Length];
            var offset = 0;
            EnvelopeMagic.CopyTo(envelope, offset);
            offset += EnvelopeMagic.Length;
            nonce.CopyTo(envelope, offset);
            offset += NonceLength;
            tag.CopyTo(envelope, offset);
            offset += TagLength;
            BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(offset, sizeof(int)), ciphertext.Length);
            offset += sizeof(int);
            ciphertext.CopyTo(envelope, offset);
            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public byte[] Unprotect(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> associatedData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var headerLength = EnvelopeMagic.Length + NonceLength + TagLength + sizeof(int);
        if (envelope.Length < headerLength
            || !envelope[..EnvelopeMagic.Length].SequenceEqual(EnvelopeMagic))
        {
            throw new InvalidDataException("The protected evidence payload header is invalid.");
        }

        var offset = EnvelopeMagic.Length;
        var nonce = envelope.Slice(offset, NonceLength);
        offset += NonceLength;
        var tag = envelope.Slice(offset, TagLength);
        offset += TagLength;
        var ciphertextLength = BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        if (ciphertextLength < 0 || ciphertextLength != envelope.Length - offset)
        {
            throw new InvalidDataException("The protected evidence payload length is invalid.");
        }

        var plaintext = new byte[ciphertextLength];
        try
        {
            using var aes = new AesGcm(_encryptionKey, TagLength);
            aes.Decrypt(nonce, envelope[offset..], tag, plaintext, associatedData);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_encryptionKey);
        foreach (var key in _hmacKeys.Values)
        {
            CryptographicOperations.ZeroMemory(key);
        }
        _hmacKeys.Clear();
    }

    private static byte[] DeriveKey(ReadOnlySpan<byte> masterKey, string purpose)
    {
        var masterCopy = masterKey.ToArray();
        try
        {
            return HMACSHA256.HashData(
                masterCopy,
                Encoding.UTF8.GetBytes("Ali.Orchestration.Evidence.KeyDerivation\0" + purpose));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterCopy);
        }
    }
}
