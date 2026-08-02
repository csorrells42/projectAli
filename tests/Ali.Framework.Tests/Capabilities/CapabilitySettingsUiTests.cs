using System.Xml.Linq;

namespace Ali.Framework.Tests.Capabilities;

public sealed class CapabilitySettingsUiTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void CapabilitiesTab_GeneratesRowsAndPresetsFromTheViewModel()
    {
        var tab = LoadCapabilitiesTab();
        var rows = tab
            .Descendants(Presentation + "ItemsControl")
            .Single(element => Attribute(element, "ItemsSource") == "{Binding Rows}");

        Assert.Equal(
            "{Binding Presets}",
            tab.Descendants(Presentation + "ComboBox")
                .Single(element => AutomationId(element) == "SettingsCapabilitiesPreset")
                .Attribute("ItemsSource")?.Value);
        Assert.Equal(
            "{Binding SelectedPreset, Mode=TwoWay}",
            tab.Descendants(Presentation + "ComboBox")
                .Single(element => AutomationId(element) == "SettingsCapabilitiesPreset")
                .Attribute("SelectedItem")?.Value);
        Assert.Equal(
            "{Binding ApplyPresetCommand}",
            tab.Descendants(Presentation + "Button")
                .Single(element => AutomationId(element) == "SettingsCapabilitiesApplyPreset")
                .Attribute("Command")?.Value);

        var generatedToggle = Assert.Single(rows.Descendants(Presentation + "CheckBox"));
        Assert.Equal(
            "{Binding IsEnabled, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
            generatedToggle.Attribute("IsChecked")?.Value);
        Assert.Equal("{Binding IsEditable}", generatedToggle.Attribute("IsEnabled")?.Value);
        Assert.Contains("{Binding GroupId", AutomationId(generatedToggle), StringComparison.Ordinal);
        Assert.DoesNotContain(
            rows.Descendants(Presentation + "CheckBox"),
            checkBox => checkBox.Attribute("Content") is not null);
    }

    [Fact]
    public void CapabilitiesTab_WrapsUserTextAndDisablesHorizontalScrolling()
    {
        var tab = LoadCapabilitiesTab();
        var scrollViewer = Assert.Single(tab.Elements(Presentation + "ScrollViewer"));

        Assert.Equal("Disabled", Attribute(scrollViewer, "HorizontalScrollBarVisibility"));
        Assert.All(
            tab.Descendants(Presentation + "TextBlock"),
            textBlock => Assert.Equal("Wrap", Attribute(textBlock, "TextWrapping")));
        Assert.DoesNotContain("TextWrapping=\"NoWrap\"", tab.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("HorizontalScrollBarVisibility=\"Auto\"", tab.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilitiesTab_ShowsFilenameOnlyAndExplicitMutationStates()
    {
        var tab = LoadCapabilitiesTab();
        var markup = tab.ToString(SaveOptions.DisableFormatting);

        Assert.Contains("SettingsFileName", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsPath", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding IsDirty}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding RequiresReload}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding IsFailedClosed}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding NeedsInitialSave}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding StatusText}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding SaveCommand}", markup, StringComparison.Ordinal);
        Assert.Contains("{Binding ReloadCommand}", markup, StringComparison.Ordinal);
        Assert.Contains("Reset / reload", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilitiesTab_ProvidesStableAutomationTargets()
    {
        var tab = LoadCapabilitiesTab();
        var automationIds = tab
            .DescendantsAndSelf()
            .Select(AutomationId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        foreach (var expected in new[]
                 {
                     "SettingsCapabilitiesTab",
                     "SettingsCapabilitiesSummary",
                     "SettingsCapabilitiesStatus",
                     "SettingsCapabilitiesUnsavedDraft",
                     "SettingsCapabilitiesReloadRequired",
                     "SettingsCapabilitiesFailedClosed",
                     "SettingsCapabilitiesInitialSave",
                     "SettingsCapabilitiesPreset",
                     "SettingsCapabilitiesApplyPreset",
                     "SettingsCapabilitiesGroups",
                     "SettingsCapabilitiesResetReload",
                     "SettingsCapabilitiesSave"
                 })
        {
            Assert.Contains(expected, automationIds);
        }

        Assert.Equal(automationIds.Length, automationIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void MainWindowViewModel_WiresCapabilityOwnerAndReloadsWhenSettingsOpens()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src",
            "UI",
            "ViewModels",
            "MainWindowViewModel.cs"));

        Assert.Contains(
            "CapabilitySettings = new CapabilitySettingsViewModel(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("_services.CapabilitySettings,", source, StringComparison.Ordinal);
        Assert.Contains("_services.CapabilitySettingsPath,", source, StringComparison.Ordinal);
        Assert.Contains(
            "McpServerSettings.RefreshPublishedCapabilitiesIfRunningAsync);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("_services.ActiveUsers.Changed += OnActiveUserChanged;", source, StringComparison.Ordinal);
        Assert.Contains(
            ".RefreshPublishedCapabilitiesIfRunningAsync()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "public CapabilitySettingsViewModel CapabilitySettings { get; }",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CapabilitySettings.ReloadCommand.CanExecute(null)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CapabilitySettings.ReloadCommand.Execute(null)",
            source,
            StringComparison.Ordinal);
    }

    private static XElement LoadCapabilitiesTab()
    {
        var document = XDocument.Load(FindRepositoryFile("src", "UI", "SettingsWindow.xaml"));
        return document
            .Descendants(Presentation + "TabItem")
            .Single(element => AutomationId(element) == "SettingsCapabilitiesTab");
    }

    private static string? AutomationId(XElement element) =>
        Attribute(element, "AutomationProperties.AutomationId");

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

    private static string FindRepositoryFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{Path.Combine(segments)}'.");
    }
}
