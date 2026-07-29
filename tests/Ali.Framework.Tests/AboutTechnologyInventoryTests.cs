using Ali.Modules.About;

namespace Ali.Framework.Tests;

public sealed class AboutTechnologyInventoryTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void InventoryIsGeneratedFromTheActualBuildGraphAndManifests()
    {
        var report = AliTechnologyAcknowledgements.Load(AppContext.BaseDirectory);

        Assert.True(report.Items.Count >= 40, $"Only {report.Items.Count} technologies were discovered.");
        Assert.Contains(report.Items, item => item.Category == "Ali capability module" && item.Name == "Internet");
        Assert.Contains(report.Items, item => item.Category == ".NET and native library" && item.Name == "Microsoft.Extensions.AI");
        Assert.Contains(report.Items, item => item.Name.Contains("TreeSitter", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Thank you", report.FormattedText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AboutTabExposesRadarAndRefreshableAcknowledgements()
    {
        var xaml = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "UI", "SettingsWindow.xaml"));
        Assert.Contains("SettingsAboutTab", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsSoftwareEngineeringRadarButton", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsTechnologyAcknowledgementsText", xaml, StringComparison.Ordinal);
        Assert.Contains("SoftwareEngineeringRadarCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("RefreshTechnologyAcknowledgementsCommand", xaml, StringComparison.Ordinal);
    }
}
