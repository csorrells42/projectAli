using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ali.Modules.Internet;

/// <summary>
/// Owner-password gate for settings that can change Google API spend.
/// The password itself is never persisted. The guard is deliberately stored in
/// Ali's per-machine data root rather than the portable release folder.
/// </summary>
public sealed class GoogleBillingSettingsGuard
{
    internal const int MinimumPasswordLength = 8;
    internal const int Pbkdf2Iterations = 310_000;
    private const int SaltBytes = 32;
    private const int HashBytes = 32;
    private const string Purpose = "Ali.GoogleBillingSettings.v1\0";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object sync = new();
    private readonly string path;

    public GoogleBillingSettingsGuard(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        path = System.IO.Path.Combine(dataRoot, "Sources", "google_billing_guard.json");
    }

    public string Path => path;

    public bool IsConfigured
    {
        get
        {
            lock (sync)
            {
                return TryLoad() is not null;
            }
        }
    }

    public void SetPassword(string password)
    {
        ValidateNewPassword(password);
        lock (sync)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            var hash = Derive(password, salt, Pbkdf2Iterations);
            try
            {
                Save(new GuardRecord
                {
                    Version = 1,
                    Algorithm = "PBKDF2-SHA256",
                    Iterations = Pbkdf2Iterations,
                    Salt = Convert.ToBase64String(salt),
                    Hash = Convert.ToBase64String(hash),
                    UpdatedUtc = DateTimeOffset.UtcNow
                });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(hash);
            }
        }
    }

    public bool Verify(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        lock (sync)
        {
            var record = TryLoad();
            if (record is null) return false;
            byte[] salt;
            byte[] expected;
            try
            {
                salt = Convert.FromBase64String(record.Salt);
                expected = Convert.FromBase64String(record.Hash);
            }
            catch (FormatException)
            {
                return false;
            }

            var actual = Derive(password, salt, record.Iterations);
            try
            {
                return expected.Length == HashBytes
                    && actual.Length == expected.Length
                    && CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(expected);
                CryptographicOperations.ZeroMemory(actual);
            }
        }
    }

    public void ChangePassword(string currentPassword, string newPassword)
    {
        if (!Verify(currentPassword))
        {
            throw new UnauthorizedAccessException("The current owner password is incorrect.");
        }

        SetPassword(newPassword);
    }

    private GuardRecord? TryLoad()
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var value = JsonSerializer.Deserialize<GuardRecord>(stream, JsonOptions);
            return value is { Version: 1, Iterations: >= 100_000 }
                   && !string.IsNullOrWhiteSpace(value.Salt)
                   && !string.IsNullOrWhiteSpace(value.Hash)
                ? value
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Save(GuardRecord record)
    {
        var folder = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, record, JsonOptions);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
        var input = Encoding.UTF8.GetBytes(Purpose + password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                input,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                HashBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static void ValidateNewPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinimumPasswordLength)
        {
            throw new ArgumentException($"Use an owner password at least {MinimumPasswordLength} characters long.", nameof(password));
        }
    }

    private sealed class GuardRecord
    {
        public int Version { get; set; }

        public string Algorithm { get; set; } = string.Empty;

        public int Iterations { get; set; }

        public string Salt { get; set; } = string.Empty;

        public string Hash { get; set; } = string.Empty;

        public DateTimeOffset UpdatedUtc { get; set; }
    }
}
