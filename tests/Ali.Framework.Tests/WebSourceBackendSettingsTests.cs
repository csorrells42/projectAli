using System.Text.Json;
using Ali.Modules.Internet;

namespace Ali.Framework.Tests;

public sealed class WebSourceBackendSettingsTests
{
    [Fact]
    public void GeneratedConfigPersistsExplicitTavilyFirstProviderOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliInternetSettingsTests", Guid.NewGuid().ToString("N"));
        try
        {
            WebSourceBackendSettingsStore.WriteDefaultIfMissing(root);

            using var document = JsonDocument.Parse(
                File.ReadAllText(WebSourceBackendSettingsStore.GetSettingsPath(root)));
            var order = document.RootElement
                .GetProperty("currentSearchProviderOrder")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray();

            Assert.Equal(nameof(InternetSearchProvider.Tavily), order[0]);
            Assert.Equal(nameof(InternetSearchProvider.GoogleGroundedSearch), order[1]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
