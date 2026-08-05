using Ali.Modules.Storage;

namespace Ali.Framework.Tests;

public sealed class WorkspaceFolderSettingsStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsWorkspaceWithoutMovingSettingsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliWorkspaceSettingsTests", Guid.NewGuid().ToString("N"));
        var settingsRoot = Path.Combine(root, "Settings");
        var workspaceRoot = Path.Combine(root, "Projects");
        try
        {
            AliWorkspaceFolderSettingsStore.Save(settingsRoot, workspaceRoot);

            Assert.Equal(Path.GetFullPath(workspaceRoot), AliWorkspaceFolderSettingsStore.Load(settingsRoot));
            Assert.True(File.Exists(Path.Combine(settingsRoot, "workspace-settings.json")));
            Assert.False(File.Exists(Path.Combine(workspaceRoot, "workspace-settings.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Load_MissingSettingsReturnsNull()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliWorkspaceSettingsTests", Guid.NewGuid().ToString("N"));
        Assert.Null(AliWorkspaceFolderSettingsStore.Load(root));
    }
}
