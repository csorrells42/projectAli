using System.Text.Json;

namespace Ali.Framework.Tests;

public sealed class RuntimeAssetManifestTests
{
    private static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "runtime-assets.json")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not locate the Project Ali repository root.");
        }
    }

    [Fact]
    public void Manifest_PinsEveryDownloadAndDocumentsEveryLicense()
    {
        var path = Path.Combine(RepositoryRoot, "runtime-assets.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("win-x64", root.GetProperty("platform").GetString());

        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        Assert.NotEmpty(assets);
        Assert.All(assets, asset =>
        {
            Assert.False(string.IsNullOrWhiteSpace(asset.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(asset.GetProperty("version").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(asset.GetProperty("license").GetString()));
            Assert.True(Uri.TryCreate(asset.GetProperty("licenseUrl").GetString(), UriKind.Absolute, out _));
            Assert.True(Uri.TryCreate(asset.GetProperty("url").GetString(), UriKind.Absolute, out _));
            Assert.True(asset.GetProperty("size").GetInt64() > 0);
            Assert.Matches("^[0-9a-f]{64}$", asset.GetProperty("sha256").GetString()!);
        });
    }

    [Fact]
    public void Manifest_UsesOnlyEnglishRuntimeModelsAndOnePortablePython()
    {
        var path = Path.Combine(RepositoryRoot, "runtime-assets.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.Equal(["en"], root.GetProperty("policies").GetProperty("languages")
            .EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Single(root.GetProperty("assets").EnumerateArray(),
            asset => asset.GetProperty("id").GetString() == "python-embed");
        Assert.Contains("never copied", root.GetProperty("policies").GetProperty("python").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }
}
