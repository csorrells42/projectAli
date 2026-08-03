using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Orchestration.State;

/// <summary>
/// Exact, current-user-protected lookup from an admitted user/conversation pair to the one
/// durable turn which is waiting for that user's next explicit message. This is deliberately
/// an index, not a discovery scan: a resume never guesses a turn identity.
/// </summary>
internal sealed class DurablePausedTurnCatalog
{
    private const int CurrentVersion = 1;
    private const int MaximumIdentityComponentCharacters = 2048;
    internal const int MaximumProtectedEntryBytes = 64 * 1024;
    private const string DirectoryName = "paused-turns";
    private const string AssociatedDataDomain = "Ali.Orchestration.PausedTurnCatalog\0v1\0";
    private const string LookupDomain = "Ali.Orchestration.PausedTurnLookup\0v1\0";
    private readonly string _directory;
    private readonly WindowsCurrentUserEvidenceProtector _protector;

    internal DurablePausedTurnCatalog(string rootDirectory, string assistantProfileBinding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantProfileBinding);
        var root = Path.GetFullPath(rootDirectory);
        _directory = Path.Combine(root, DirectoryName);
        _protector = new WindowsCurrentUserEvidenceProtector(root, assistantProfileBinding);
    }

    internal async Task RecordAsync(
        TurnIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateComponent(identity.UserId, nameof(identity.UserId));
        ValidateComponent(identity.ConversationId, nameof(identity.ConversationId));
        ValidateComponent(identity.AssistantMessageId, nameof(identity.AssistantMessageId));
        EnsureStorageDirectory();

        using var keys = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        var lookupDigest = CalculateLookupDigest(
            keys,
            identity.UserId,
            identity.ConversationId);
        var path = EntryPath(lookupDigest);
        await using var lease = await AcquireLeaseAsync(lookupDigest, cancellationToken)
            .ConfigureAwait(false);
        var existing = await ReadExactAsync(
                keys,
                lookupDigest,
                identity.UserId,
                identity.ConversationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null && existing != identity)
        {
            throw new InvalidOperationException(
                "Another exact durable turn is already active for this user and conversation.");
        }

        var entry = new PausedTurnCatalogEntry(
            CurrentVersion,
            lookupDigest,
            identity.UserId,
            identity.ConversationId,
            identity.AssistantMessageId,
            identity.StorageKey,
            DateTimeOffset.UtcNow);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(entry);
        byte[]? protectedBytes = null;
        try
        {
            if (plaintext.Length > MaximumProtectedEntryBytes / 2)
            {
                throw new InvalidDataException("The paused-turn catalog entry exceeds its bounded size.");
            }

            protectedBytes = keys.Protect(plaintext, AssociatedData(lookupDigest));
            if (protectedBytes.Length > MaximumProtectedEntryBytes)
            {
                throw new InvalidDataException("The protected paused-turn catalog entry exceeds its bounded size.");
            }

            await AtomicReplaceAsync(path, protectedBytes).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    internal async Task<TurnIdentity?> FindAsync(
        string userId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateComponent(userId, nameof(userId));
        ValidateComponent(conversationId, nameof(conversationId));
        if (!WindowsOrchestrationFileBoundary.RegularDirectoryExists(
                _directory,
                "The paused-turn catalog directory is not a regular local directory."))
        {
            return null;
        }

        using var keys = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        var lookupDigest = CalculateLookupDigest(keys, userId, conversationId);
        await using var lease = await AcquireLeaseAsync(lookupDigest, cancellationToken)
            .ConfigureAwait(false);
        return await ReadExactAsync(
                keys,
                lookupDigest,
                expectedUserId: userId,
                expectedConversationId: conversationId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task RemoveExactAsync(
        TurnIdentity expectedIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ValidateComponent(expectedIdentity.UserId, nameof(expectedIdentity.UserId));
        ValidateComponent(expectedIdentity.ConversationId, nameof(expectedIdentity.ConversationId));
        if (!WindowsOrchestrationFileBoundary.RegularDirectoryExists(
                _directory,
                "The paused-turn catalog directory is not a regular local directory."))
        {
            return;
        }

        using var keys = await _protector.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        var lookupDigest = CalculateLookupDigest(
            keys,
            expectedIdentity.UserId,
            expectedIdentity.ConversationId);
        await using var lease = await AcquireLeaseAsync(lookupDigest, cancellationToken)
            .ConfigureAwait(false);
        var current = await ReadExactAsync(
                keys,
                lookupDigest,
                expectedIdentity.UserId,
                expectedIdentity.ConversationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (current is null || current != expectedIdentity)
        {
            return;
        }

        _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
            EntryPath(lookupDigest),
            "The protected paused-turn catalog entry is not a regular local file.");
    }

    private async Task<TurnIdentity?> ReadExactAsync(
        EvidenceKeySession keys,
        string lookupDigest,
        string expectedUserId,
        string expectedConversationId,
        CancellationToken cancellationToken)
    {
        var path = EntryPath(lookupDigest);
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            _directory,
            "The paused-turn catalog directory is not a regular local directory.");
        var protectedBytes = await WindowsBoundedFileReader.TryReadExactlyAsync(
            path,
            minimumLength: 1,
            MaximumProtectedEntryBytes,
            "The protected paused-turn catalog entry is not a regular local file.",
            "The protected paused-turn catalog entry has an invalid size.",
            "The protected paused-turn catalog entry changed while it was being read.",
            cancellationToken).ConfigureAwait(false);
        if (protectedBytes is null)
        {
            return null;
        }

        byte[]? plaintext = null;
        try
        {
            try
            {
                plaintext = keys.Unprotect(protectedBytes, AssociatedData(lookupDigest));
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException(
                    "The paused-turn catalog entry failed current-user authentication.",
                    ex);
            }

            var entry = JsonSerializer.Deserialize<PausedTurnCatalogEntry>(plaintext)
                ?? throw new InvalidDataException("The paused-turn catalog entry is empty.");
            ValidateEntry(entry, lookupDigest, expectedUserId, expectedConversationId);
            var identity = new TurnIdentity(
                entry.UserId,
                entry.ConversationId,
                entry.AssistantMessageId);
            if (!string.Equals(identity.StorageKey, entry.TurnStorageKey, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The paused-turn catalog identity does not match its durable storage binding.");
            }

            return identity;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The paused-turn catalog entry is malformed.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private async Task<FileStream> AcquireLeaseAsync(
        string lookupDigest,
        CancellationToken cancellationToken)
    {
        EnsureStorageDirectory();
        var path = Path.Combine(_directory, lookupDigest + ".lock");
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
                    "The paused-turn catalog lease is not a regular local file.");
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task AtomicReplaceAsync(string finalPath, byte[] contents)
    {
        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("The paused-turn catalog path has no directory.");
        var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.paused.tmp");
        try
        {
            WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                directory,
                "The paused-turn catalog directory is not a regular local directory.");
            await using (var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             writeThrough: true,
                             "The paused-turn catalog temporary entry is not a regular local file."))
            {
                await stream.WriteAsync(contents, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            WindowsOrchestrationFileBoundary.MoveRegularFile(
                temporaryPath,
                finalPath,
                replaceExisting: true,
                "The protected paused-turn catalog entry is not a regular local file.");
        }
        finally
        {
            _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                temporaryPath,
                "The paused-turn catalog temporary entry is not a regular local file.");
        }
    }

    private void EnsureStorageDirectory() =>
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            _directory,
            "The paused-turn catalog directory is not a regular local directory.");

    private static bool IsSharingViolation(IOException exception)
    {
        var error = exception.HResult & 0xFFFF;
        return error is 32 or 33
               || exception.InnerException is System.ComponentModel.Win32Exception
               {
                   NativeErrorCode: 32 or 33
               };
    }

    private string EntryPath(string lookupDigest)
    {
        TurnStateIntegrity.RequireDigest(lookupDigest, nameof(lookupDigest));
        return Path.Combine(_directory, lookupDigest + ".paused");
    }

    private static string CalculateLookupDigest(
        EvidenceKeySession keys,
        string userId,
        string conversationId) =>
        keys.HmacHex(
            EvidenceKeyPurpose.Identifier,
            Encoding.UTF8.GetBytes(
                LookupDomain
                + userId.Length + ":" + userId
                + "|" + conversationId.Length + ":" + conversationId));

    private static byte[] AssociatedData(string lookupDigest) =>
        Encoding.UTF8.GetBytes(AssociatedDataDomain + lookupDigest);

    private static void ValidateEntry(
        PausedTurnCatalogEntry entry,
        string lookupDigest,
        string expectedUserId,
        string expectedConversationId)
    {
        if (entry.Version != CurrentVersion
            || !string.Equals(entry.LookupDigest, lookupDigest, StringComparison.Ordinal)
            || !string.Equals(entry.UserId, expectedUserId, StringComparison.Ordinal)
            || !string.Equals(entry.ConversationId, expectedConversationId, StringComparison.Ordinal)
            || entry.SavedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("The paused-turn catalog entry crossed its exact lookup binding.");
        }

        ValidateComponent(entry.UserId, nameof(entry.UserId));
        ValidateComponent(entry.ConversationId, nameof(entry.ConversationId));
        ValidateComponent(entry.AssistantMessageId, nameof(entry.AssistantMessageId));
        TurnStateIntegrity.RequireDigest(entry.TurnStorageKey, nameof(entry.TurnStorageKey));
    }

    private static void ValidateComponent(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumIdentityComponentCharacters)
        {
            throw new ArgumentException(
                "A paused-turn identity component is empty or exceeds its bounded size.",
                parameterName);
        }
    }

    private sealed record PausedTurnCatalogEntry(
        int Version,
        string LookupDigest,
        string UserId,
        string ConversationId,
        string AssistantMessageId,
        string TurnStorageKey,
        DateTimeOffset SavedAtUtc);
}
