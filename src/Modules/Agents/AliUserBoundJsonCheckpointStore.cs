using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Routes Agent Framework checkpoints into an opaque per-user directory and
/// authenticates the owner binding embedded in every stored checkpoint.
/// </summary>
internal sealed class AliUserBoundJsonCheckpointStore : ICheckpointStore<JsonElement>, IDisposable
{
    private readonly object _sync = new();
    private readonly AliWorkflowCheckpointOwnership _ownership;
    private readonly Dictionary<string, FileSystemJsonCheckpointStore> _stores = new(StringComparer.Ordinal);
    private readonly AsyncLocal<OwnerScope?> _ambientOwner = new();
    private readonly SemaphoreSlim _storeGate = new(1, 1);
    private bool _disposed;

    public AliUserBoundJsonCheckpointStore(AliWorkflowCheckpointOwnership ownership)
    {
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
    }

    public IDisposable EnterOwnerScope(AliWorkflowCheckpointOwner owner)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(owner);
        var previous = _ambientOwner.Value;
        var current = new OwnerScope(owner, previous);
        _ambientOwner.Value = current;
        return new ScopeLease(this, current);
    }

    public async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(
        string sessionId,
        CheckpointInfo? withParent = null)
    {
        var owner = RequireOwner();
        await _storeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return (await GetStore(owner)
                    .RetrieveIndexAsync(sessionId, withParent)
                    .ConfigureAwait(false))
                .ToArray();
        }
        finally
        {
            _storeGate.Release();
        }
    }

    public async ValueTask<CheckpointInfo> CreateCheckpointAsync(
        string sessionId,
        JsonElement value,
        CheckpointInfo? parent = null)
    {
        var owner = RequireOwner();
        var boundValue = _ownership.Bind(value, owner);
        await _storeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await GetStore(owner)
                .CreateCheckpointAsync(sessionId, boundValue, parent)
                .ConfigureAwait(false);
        }
        finally
        {
            _storeGate.Release();
        }
    }

    public async ValueTask<JsonElement> RetrieveCheckpointAsync(
        string sessionId,
        CheckpointInfo key)
    {
        var owner = RequireOwner();
        await _storeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var value = await GetStore(owner)
                .RetrieveCheckpointAsync(sessionId, key)
                .ConfigureAwait(false);
            if (!_ownership.IsOwnedBy(value, owner))
            {
                throw new InvalidDataException(
                    "The durable workflow checkpoint owner binding is missing or invalid.");
            }

            return value;
        }
        finally
        {
            _storeGate.Release();
        }
    }

    private AliWorkflowCheckpointOwner RequireOwner() =>
        _ambientOwner.Value?.Owner
        ?? throw new InvalidOperationException(
            "A resolved active-user checkpoint scope is required for durable workflow access.");

    private FileSystemJsonCheckpointStore GetStore(AliWorkflowCheckpointOwner owner)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stores.TryGetValue(owner.OwnerKey, out var store))
            {
                return store;
            }

            store = new FileSystemJsonCheckpointStore(
                Directory.CreateDirectory(_ownership.GetCheckpointDirectory(owner)));
            _stores.Add(owner.OwnerKey, store);
            return store;
        }
    }

    private void ExitScope(OwnerScope scope)
    {
        if (ReferenceEquals(_ambientOwner.Value, scope))
        {
            _ambientOwner.Value = scope.Previous;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var store in _stores.Values)
            {
                store.Dispose();
            }
            _stores.Clear();
        }

        _storeGate.Dispose();
    }

    private sealed record OwnerScope(
        AliWorkflowCheckpointOwner Owner,
        OwnerScope? Previous);

    private sealed class ScopeLease(
        AliUserBoundJsonCheckpointStore owner,
        OwnerScope scope) : IDisposable
    {
        private AliUserBoundJsonCheckpointStore? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ExitScope(scope);
    }
}

internal sealed record AliWorkflowCheckpointOwner(string OwnerKey);

/// <summary>
/// Creates and verifies relocation-resistant checkpoint owner bindings. The raw
/// stable user ID is never used as a directory name or persisted in a checkpoint.
/// </summary>
internal sealed class AliWorkflowCheckpointOwnership : IDisposable
{
    internal const string KeyFileName = "checkpoint-owner.key.protected";
    internal const string OwnerPropertyName = "aliCheckpointOwner";
    private const string UsersDirectoryName = "users";
    private const int MasterKeyLength = 32;
    private static readonly byte[] DpapiEntropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("Ali.AgentFramework.WorkflowCheckpoint.OwnerKey.v1"));

    private readonly string _rootPath;
    private readonly byte[] _masterKey;
    private bool _disposed;

    public AliWorkflowCheckpointOwnership(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
        _masterKey = LoadOrCreateMasterKey();
    }

    public AliWorkflowCheckpointOwner CreateOwner(string stableUserId) =>
        new(CreateOwnerKey(stableUserId));

    internal static string CreateOwnerKey(string stableUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableUserId);
        var normalized = stableUserId
            .Trim()
            .Normalize(NormalizationForm.FormKC)
            .ToUpperInvariant();
        var material = Encoding.UTF8.GetBytes(
            "Ali.AgentFramework.WorkflowCheckpoint.User.v1\0" + normalized);
        try
        {
            return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    public string GetCheckpointDirectory(AliWorkflowCheckpointOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        RequireOwnerKey(owner.OwnerKey);
        return Path.Combine(_rootPath, UsersDirectoryName, owner.OwnerKey);
    }

    public JsonElement Bind(JsonElement value, AliWorkflowCheckpointOwner owner)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(owner);
        RequireOwnerKey(owner.OwnerKey);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Agent Framework checkpoint JSON must be an object.");
        }
        if (value.TryGetProperty(OwnerPropertyName, out _))
        {
            throw new InvalidDataException(
                "Agent Framework checkpoint JSON already contains reserved owner metadata.");
        }

        var payload = SerializeWithoutOwner(value);
        try
        {
            var digest = HashHex(payload);
            var mac = ComputeMac(owner.OwnerKey, digest);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    property.WriteTo(writer);
                }
                writer.WritePropertyName(OwnerPropertyName);
                writer.WriteStartObject();
                writer.WriteNumber("version", 1);
                writer.WriteString("ownerKey", owner.OwnerKey);
                writer.WriteString("payloadDigest", digest);
                writer.WriteString("mac", mac);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(stream.ToArray());
            return document.RootElement.Clone();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public bool IsOwnedBy(JsonElement value, AliWorkflowCheckpointOwner expectedOwner)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(expectedOwner);
        RequireOwnerKey(expectedOwner.OwnerKey);
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(OwnerPropertyName, out var metadata)
            || metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var parsedVersion)
            || parsedVersion != 1
            || !TryReadText(metadata, "ownerKey", out var ownerKey)
            || !TryReadText(metadata, "payloadDigest", out var storedDigest)
            || !TryReadText(metadata, "mac", out var storedMac)
            || !string.Equals(ownerKey, expectedOwner.OwnerKey, StringComparison.Ordinal))
        {
            return false;
        }

        var payload = SerializeWithoutOwner(value);
        try
        {
            var actualDigest = HashHex(payload);
            if (!FixedTimeHexEquals(actualDigest, storedDigest))
            {
                return false;
            }

            var actualMac = ComputeMac(ownerKey, actualDigest);
            return FixedTimeHexEquals(actualMac, storedMac);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private byte[] LoadOrCreateMasterKey()
    {
        var path = Path.Combine(_rootPath, KeyFileName);
        if (File.Exists(path))
        {
            return ReadMasterKey(path);
        }

        var usersPath = Path.Combine(_rootPath, UsersDirectoryName);
        if (Directory.Exists(usersPath)
            && Directory.EnumerateFiles(usersPath, "*", SearchOption.AllDirectories).Any())
        {
            throw new InvalidDataException(
                "The protected workflow checkpoint owner key is missing while user-bound checkpoints still exist.");
        }

        var masterKey = RandomNumberGenerator.GetBytes(MasterKeyLength);
        var protectedKey = ProtectedData.Protect(
            masterKey,
            DpapiEntropy,
            DataProtectionScope.CurrentUser);
        var temporaryPath = Path.Combine(_rootPath, $".{Guid.NewGuid():N}.workflow-key.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(protectedKey);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, path, overwrite: false);
                return masterKey;
            }
            catch (IOException) when (File.Exists(path))
            {
                CryptographicOperations.ZeroMemory(masterKey);
                return ReadMasterKey(path);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedKey);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static byte[] ReadMasterKey(string path)
    {
        var protectedKey = File.ReadAllBytes(path);
        try
        {
            byte[] masterKey;
            try
            {
                masterKey = ProtectedData.Unprotect(
                    protectedKey,
                    DpapiEntropy,
                    DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException(
                    "The workflow checkpoint owner key cannot be opened by the current Windows user.",
                    ex);
            }

            if (masterKey.Length == MasterKeyLength)
            {
                return masterKey;
            }

            CryptographicOperations.ZeroMemory(masterKey);
            throw new InvalidDataException("The workflow checkpoint owner key has an invalid length.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedKey);
        }
    }

    private string ComputeMac(string ownerKey, string payloadDigest)
    {
        var material = Encoding.UTF8.GetBytes(
            $"Ali.AgentFramework.WorkflowCheckpoint.OwnerBinding.v1\0{ownerKey}\0{payloadDigest}");
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(_masterKey, material)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static byte[] SerializeWithoutOwner(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                if (!string.Equals(property.Name, OwnerPropertyName, StringComparison.Ordinal))
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static string HashHex(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static bool TryReadText(
        JsonElement parent,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }

    private static void RequireOwnerKey(string ownerKey)
    {
        if (ownerKey.Length != 64
            || ownerKey.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException("The workflow checkpoint owner key is invalid.");
        }
    }

    private static bool FixedTimeHexEquals(string expectedHex, string candidateHex)
    {
        byte[] expected;
        byte[] candidate;
        try
        {
            expected = Convert.FromHexString(expectedHex);
            candidate = Convert.FromHexString(candidateHex);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            return expected.Length == candidate.Length
                && CryptographicOperations.FixedTimeEquals(expected, candidate);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_masterKey);
    }
}
