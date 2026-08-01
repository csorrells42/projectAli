using System.Text.Json;
using Ali.Modules.Integrations;

namespace Ali.Framework.Tests;

public sealed class EditorIntegrationTests
{
    private static readonly string RepositoryRoot = TestRepository.Root;

    [Fact]
    public void ManifestDefinesChecksumPinnedNotepadPlusPlusToolkit()
    {
        var path = Path.Combine(RepositoryRoot, "editor-integrations.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var notepad = document.RootElement.GetProperty("notepadPlusPlus");
        Assert.StartsWith("https://raw.githubusercontent.com/notepad-plus-plus/nppPluginList/", notepad.GetProperty("catalogUrl").GetString());
        var plugins = notepad.GetProperty("plugins").EnumerateArray().ToArray();
        Assert.Equal(8, plugins.Length);
        Assert.All(plugins, plugin =>
        {
            Assert.Equal(64, plugin.GetProperty("fallbackSha256").GetString()!.Length);
            Assert.StartsWith("https://github.com/", plugin.GetProperty("fallbackUrl").GetString());
        });
    }

    [Fact]
    public void IntegrationManagerLoadsManifestAndReturnsEditorStatus()
    {
        var report = AliEditorIntegrationManager.Inspect(RepositoryRoot);
        Assert.Equal(8, report.DesiredNotepadPlusPlusPlugins);
        Assert.Contains("Notepad++", report.Details, StringComparison.Ordinal);
        Assert.Contains("Visual Studio", report.Details, StringComparison.Ordinal);
        Assert.Contains("SHA-256", report.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerIsSyntacticallyValidAndProtectsOpenEditingSessions()
    {
        var path = Path.Combine(RepositoryRoot, "tools", "ConfigureEditorIntegrations.ps1");
        var script = File.ReadAllText(path);
        Assert.Contains("Get-Process -Name 'notepad++'", script, StringComparison.Ordinal);
        Assert.Contains("Backup-NotepadPlusPlusConfig", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", script, StringComparison.Ordinal);
        Assert.Contains("official Notepad++ catalog", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsExposeVersionIndependentEditorControls()
    {
        var xaml = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "UI", "SettingsWindow.xaml"));
        Assert.Contains("SettingsIntegrationsTab", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsInstallNotepadPlusPlusToolkitButton", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsRefreshEditorIntegrationsButton", xaml, StringComparison.Ordinal);
        Assert.Contains("InstallNotepadPlusPlusToolkitCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenEditorIntegrationGuideCommand", xaml, StringComparison.Ordinal);
    }
}
