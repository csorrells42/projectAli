using System.Text.Json;
using Ali.Modules.Internet;

namespace Ali.Framework.Tests;

public sealed class WebSourceBackendSettingsSecretTests
{
    [Fact]
    public void SaveEncryptsGeminiKeyAndLoadRestoresItForCurrentWindowsUser()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliInternetSecretTests", Guid.NewGuid().ToString("N"));
        try
        {
            const string secret = "test-secret-that-must-not-appear";
            WebSourceBackendSettingsStore.Save(root, new WebSourceBackendSettings { GeminiApiKey = secret });

            var path = WebSourceBackendSettingsStore.GetSettingsPath(root);
            var json = File.ReadAllText(path);
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
            Assert.Contains("dpapi:v1:", json, StringComparison.Ordinal);
            Assert.Equal(secret, WebSourceBackendSettingsStore.LoadOrDefault(root).GeminiApiKey);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadMigratesLegacyPlainTextGeminiKeyWithoutChangingItsValue()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliInternetSecretTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Sources"));
            const string secret = "legacy-plain-text-test-key";
            var path = WebSourceBackendSettingsStore.GetSettingsPath(root);
            File.WriteAllText(path, JsonSerializer.Serialize(new WebSourceBackendSettings { GeminiApiKey = secret }));

            var loaded = WebSourceBackendSettingsStore.LoadOrDefault(root);

            Assert.Equal(secret, loaded.GeminiApiKey);
            var migrated = File.ReadAllText(path);
            Assert.DoesNotContain(secret, migrated, StringComparison.Ordinal);
            Assert.Contains("dpapi:v1:", migrated, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
