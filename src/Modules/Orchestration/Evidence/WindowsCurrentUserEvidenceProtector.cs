using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
    JournalHead,
    WorkGraphRecord,
    TurnFactIndex,
    EvidenceExactIndex
}

internal sealed class WindowsCurrentUserEvidenceProtector
{
    private const string KeyFileName = "evidence.key.protected";
    private const string KeyLockFileName = ".evidence-key.writer.lock";
    private const int MasterKeyLength = 32;
    internal const int MaximumProtectedKeyBytes = 4 * 1024;
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
        // This boundary rejects reparse components present when the operation begins. An
        // actively racing process running as the same Windows user remains outside CP6's
        // at-rest tamper model and would require a directory-handle-relative API.
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            _rootDirectory,
            "The protected orchestration evidence root is not a regular local directory.");
        await using var lease = await AcquireKeyLeaseAsync(cancellationToken).ConfigureAwait(false);
        var keyPath = Path.Combine(_rootDirectory, KeyFileName);
        byte[] masterKey;
        var wrapped = await WindowsBoundedFileReader.TryReadExactlyAsync(
            WindowsOrchestrationFileBoundary.ToExtendedLengthWin32Path(keyPath),
            minimumLength: 1,
            MaximumProtectedKeyBytes,
            "The protected orchestration evidence key is not a regular local file.",
            "The protected orchestration evidence key has an invalid file length.",
            "The protected orchestration evidence key changed while it was being read.",
            cancellationToken).ConfigureAwait(false);
        if (wrapped is not null)
        {
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
            if (WindowsOrchestrationFileBoundary.RegularDirectoryExists(
                    turnsDirectory,
                    "The protected orchestration turns directory is not a regular local directory.")
                && Directory.EnumerateFileSystemEntries(turnsDirectory).Any())
            {
                throw new InvalidDataException(
                    "The protected orchestration evidence key is missing while durable evidence still exists.");
            }

            masterKey = RandomNumberGenerator.GetBytes(MasterKeyLength);
            var protectedMasterKey = ProtectedData.Protect(
                masterKey,
                _entropy,
                DataProtectionScope.CurrentUser);
            try
            {
                await WriteNewKeyAsync(keyPath, protectedMasterKey).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedMasterKey);
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
                return WindowsOrchestrationFileBoundary.OpenRegularFile(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    writeThrough: true,
                    "The protected orchestration evidence-key lease is not a regular local file.");
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteNewKeyAsync(string finalPath, byte[] wrapped)
    {
        if (wrapped.Length is <= 0 or > MaximumProtectedKeyBytes)
        {
            throw new InvalidDataException(
                "The generated protected orchestration evidence key has an invalid file length.");
        }

        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("The protected evidence key path has no directory.");
        var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.key.tmp");
        try
        {
            WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                directory,
                "The protected orchestration evidence root is not a regular local directory.");
            await using (var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             writeThrough: true,
                             "The protected orchestration evidence-key temporary file is not a regular local file."))
            {
                await stream.WriteAsync(wrapped, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            WindowsOrchestrationFileBoundary.MoveRegularFile(
                temporaryPath,
                finalPath,
                replaceExisting: false,
                "The protected orchestration evidence key is not a regular local file.");
        }
        finally
        {
            _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                temporaryPath,
                "The protected orchestration evidence-key temporary file is not a regular local file.");
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var error = exception.HResult & 0xFFFF;
        return error is 32 or 33
               || exception.InnerException is Win32Exception
               {
                   NativeErrorCode: 32 or 33
               };
    }
}

internal static class WindowsBoundedFileReader
{
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileTypeDisk = 0x0001;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    internal static Task<byte[]?> TryReadExactlyAsync(
        string path,
        int minimumLength,
        int maximumLength,
        string invalidTargetMessage,
        string invalidLengthMessage,
        string changedWhileReadingMessage,
        CancellationToken cancellationToken) =>
        TryReadExactlyAsync(
            path,
            minimumLength,
            maximumLength,
            invalidTargetMessage,
            invalidLengthMessage,
            changedWhileReadingMessage,
            cancellationToken,
            lengthValidatedObserver: null);

    internal static async Task<byte[]?> TryReadExactlyAsync(
        string path,
        int minimumLength,
        int maximumLength,
        string invalidTargetMessage,
        string invalidLengthMessage,
        string changedWhileReadingMessage,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask>? lengthValidatedObserver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (minimumLength <= 0 || maximumLength < minimumLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLength),
                "The bounded file length range is invalid.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var handle = CreateFileW(
            path,
            GenericRead,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagOverlapped | FileFlagSequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return null;
            }

            throw new InvalidDataException(
                invalidTargetMessage,
                new Win32Exception(error));
        }

        try
        {
            var attributes = File.GetAttributes(handle);
            if (GetFileType(handle) != FileTypeDisk
                || (attributes & (FileAttributes.Device
                                  | FileAttributes.Directory
                                  | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException(invalidTargetMessage);
            }

            var length = RandomAccess.GetLength(handle);
            if (length < minimumLength || length > maximumLength)
            {
                throw new InvalidDataException(invalidLengthMessage);
            }

            if (lengthValidatedObserver is not null)
            {
                await lengthValidatedObserver(cancellationToken).ConfigureAwait(false);
            }

            var content = new byte[checked((int)length)];
            try
            {
                await using var stream = new FileStream(
                    handle,
                    FileAccess.Read,
                    64 * 1024,
                    isAsync: true);
                try
                {
                    await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
                }
                catch (EndOfStreamException ex)
                {
                    throw new InvalidDataException(changedWhileReadingMessage, ex);
                }

                if (stream.Position != length
                    || RandomAccess.GetLength(stream.SafeFileHandle) != length)
                {
                    throw new InvalidDataException(changedWhileReadingMessage);
                }

                return content;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(content);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            throw new InvalidDataException(invalidTargetMessage, ex);
        }
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle fileHandle);
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
