using System.Security.Cryptography;
using System.Text;

namespace Ali.Modules.Runtime;

/// <summary>
/// Stores the optional remote OpenAI-compatible credential under the current Windows user.
/// Runtime settings contain only the environment-variable name; plaintext secrets never enter
/// runtime-settings.json, capability profiles, dispatch identities, or health logs.
/// </summary>
internal sealed class RuntimeCredentialStore(string dataRoot)
{
    internal const string DefaultApiKeyEnvironmentVariable = "ALI_REMOTE_OPENAI_API_KEY";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
        "Ali.Runtime.RemoteOpenAiCredential.v1");
    private readonly string _path = Path.Combine(
        Path.GetFullPath(dataRoot ?? throw new ArgumentNullException(nameof(dataRoot))),
        "runtime-credentials.dpapi");

    internal string? ResolveApiKey(string? environmentVariable)
    {
        var variable = string.IsNullOrWhiteSpace(environmentVariable)
            ? DefaultApiKeyEnvironmentVariable
            : environmentVariable.Trim();
        var fromEnvironment = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(fromEnvironment)
            ? fromEnvironment.Trim()
            : LoadApiKey();
    }

    internal string? LoadApiKey()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = File.ReadAllBytes(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            byte[] plaintext;
            try
            {
                plaintext = ProtectedData.Unprotect(
                    protectedBytes,
                    Entropy,
                    DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(plaintext);
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

    internal void SaveApiKey(string? apiKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            return;
        }

        var plaintext = Encoding.UTF8.GetBytes(apiKey.Trim());
        byte[] protectedBytes = [];
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            protectedBytes = ProtectedData.Protect(
                plaintext,
                Entropy,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
