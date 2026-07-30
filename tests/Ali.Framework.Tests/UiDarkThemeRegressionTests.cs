using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Ali.Framework.Tests;

public sealed class UiDarkThemeRegressionTests
{
    private static readonly string[] RequiredControlTypes =
    [
        "TextBox",
        "ComboBox",
        "ComboBoxItem",
        "DataGrid",
        "DataGridRow",
        "DataGridCell",
        "DataGridColumnHeader"
    ];

    private static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Ali.sln")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not locate the Project Ali repository root.");
        }
    }

    [Fact]
    public void ApplicationTheme_ProvidesDarkFallbacksForEveryInputAndTableControl()
    {
        var appDocument = XDocument.Load(Path.Combine(RepositoryRoot, "src", "App.xaml"), LoadOptions.SetLineInfo);
        var implicitStyles = appDocument
            .Descendants()
            .Where(element => element.Name.LocalName == "Style" && element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) is null)
            .ToDictionary(element => NormalizeTargetType((string?)element.Attribute("TargetType")), StringComparer.Ordinal);

        foreach (var controlType in RequiredControlTypes)
        {
            Assert.True(implicitStyles.TryGetValue(controlType, out var style),
                $"App.xaml must define an implicit dark style for {controlType}.");
            AssertStylePropertyIsDark(style!, "Background", appDocument, "src/App.xaml");
            AssertStyleHasProperty(style!, "Foreground", "src/App.xaml");
        }
    }

    [Fact]
    public void EveryLocalInputOrTableStyleAndOverride_PreservesTheDarkTheme()
    {
        var xamlRoot = Path.Combine(RepositoryRoot, "src");
        foreach (var path in Directory.EnumerateFiles(xamlRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            var displayPath = Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/');

            foreach (var style in document.Descendants().Where(element => element.Name.LocalName == "Style"))
            {
                var targetType = NormalizeTargetType((string?)style.Attribute("TargetType"));
                if (!RequiredControlTypes.Contains(targetType, StringComparer.Ordinal)
                    || style.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) is not null)
                {
                    continue;
                }

                AssertStylePropertyIsDark(style, "Background", document, displayPath);
                AssertStyleHasProperty(style, "Foreground", displayPath);
            }

            foreach (var control in document.Descendants()
                         .Where(element => RequiredControlTypes.Contains(element.Name.LocalName, StringComparer.Ordinal)))
            {
                var styleValue = (string?)control.Attribute("Style");
                Assert.False(string.Equals(styleValue, "{x:Null}", StringComparison.OrdinalIgnoreCase),
                    $"{displayPath}:{Line(control)} disables the required theme for {control.Name.LocalName}.");

                var background = (string?)control.Attribute("Background");
                if (!string.IsNullOrWhiteSpace(background))
                {
                    AssertDarkBackground(background, document, displayPath, Line(control), control.Name.LocalName);
                }
            }
        }
    }

    [Fact]
    public void SettingsScrollBar_IsExplicitlyDarkThemed()
    {
        const string relativePath = "src/UI/SettingsWindow.xaml";
        var document = XDocument.Load(Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)), LoadOptions.SetLineInfo);
        var style = document.Descendants()
            .Single(element => element.Name.LocalName == "Style"
                               && NormalizeTargetType((string?)element.Attribute("TargetType")) == "ScrollBar"
                               && element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) is null);

        AssertStylePropertyIsDark(style, "Background", document, relativePath);
        AssertStyleHasProperty(style, "Foreground", relativePath);

        var permissionsTab = document.Descendants()
            .Single(element => element.Name.LocalName == "TabItem"
                               && string.Equals((string?)element.Attribute("Header"), "Permissions", StringComparison.Ordinal));
        var pageScroller = permissionsTab.Elements().Single(element => element.Name.LocalName == "ScrollViewer");
        Assert.Equal("Auto", (string?)pageScroller.Attribute("VerticalScrollBarVisibility"));
    }

    [Fact]
    public void UnverifiedMaintenanceActions_AreNotExposedAnywhereInTheUi()
    {
        var sourceRoot = Path.Combine(RepositoryRoot, "src");
        var xaml = string.Join('\n', Directory.EnumerateFiles(sourceRoot, "*.xaml", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        Assert.DoesNotContain("Run Health Check", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Repair Ali Install", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Maintenance Plan", xaml, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(sourceRoot, "UI", "MaintenanceDashboardWindow.xaml")));
    }

    private static void AssertStylePropertyIsDark(XElement style, string property, XDocument document, string path)
    {
        var setter = style.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Setter"
                                       && string.Equals((string?)element.Attribute("Property"), property, StringComparison.Ordinal));
        Assert.True(setter is not null,
            $"{path}:{Line(style)} implicit {NormalizeTargetType((string?)style.Attribute("TargetType"))} style must set {property}.");
        AssertDarkBackground((string?)setter!.Attribute("Value"), document, path, Line(setter),
            NormalizeTargetType((string?)style.Attribute("TargetType")));
    }

    private static void AssertStyleHasProperty(XElement style, string property, string path)
    {
        Assert.Contains(style.Elements(), element => element.Name.LocalName == "Setter"
                                                     && string.Equals((string?)element.Attribute("Property"), property, StringComparison.Ordinal));
    }

    private static void AssertDarkBackground(string? value, XDocument document, string path, int line, string controlType)
    {
        Assert.False(string.IsNullOrWhiteSpace(value), $"{path}:{line} {controlType} background is empty.");
        if (string.Equals(value, "Transparent", StringComparison.OrdinalIgnoreCase)
            || value!.StartsWith("{Binding", StringComparison.Ordinal)
            || value.StartsWith("{TemplateBinding", StringComparison.Ordinal))
        {
            return;
        }

        if (TryReadStaticResourceKey(value, out var resourceKey))
        {
            var brush = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "SolidColorBrush"
                                           && string.Equals(
                                               (string?)element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")),
                                               resourceKey,
                                               StringComparison.Ordinal));
            if (brush is null)
            {
                var appDocument = XDocument.Load(Path.Combine(RepositoryRoot, "src", "App.xaml"));
                brush = appDocument.Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == "SolidColorBrush"
                                               && string.Equals(
                                                   (string?)element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")),
                                                   resourceKey,
                                                   StringComparison.Ordinal));
            }

            Assert.True(brush is not null, $"{path}:{line} cannot resolve background resource {resourceKey}.");
            value = (string?)brush!.Attribute("Color");
        }

        Assert.True(TryParseRgb(value!, out var red, out var green, out var blue),
            $"{path}:{line} {controlType} uses an unverifiable background '{value}'.");
        var relativeLuminance = RelativeLuminance(red, green, blue);
        Assert.True(relativeLuminance <= 0.18,
            $"{path}:{line} {controlType} background '{value}' is too light for Ali's dark theme (luminance {relativeLuminance:F3}).");
    }

    private static string NormalizeTargetType(string? targetType)
    {
        if (string.IsNullOrWhiteSpace(targetType))
        {
            return string.Empty;
        }

        var value = targetType.Trim();
        if (value.StartsWith("{x:Type ", StringComparison.Ordinal) && value.EndsWith('}'))
        {
            value = value[8..^1].Trim();
        }

        var separator = value.LastIndexOf(':');
        return separator >= 0 ? value[(separator + 1)..] : value;
    }

    private static bool TryReadStaticResourceKey(string value, out string key)
    {
        const string prefix = "{StaticResource ";
        if (value.StartsWith(prefix, StringComparison.Ordinal) && value.EndsWith('}'))
        {
            key = value[prefix.Length..^1].Trim();
            return true;
        }

        key = string.Empty;
        return false;
    }

    private static bool TryParseRgb(string value, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        if (!value.StartsWith('#'))
        {
            return false;
        }

        var hex = value[1..];
        if (hex.Length == 8)
        {
            hex = hex[2..];
        }
        else if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(character => new string(character, 2)));
        }

        return hex.Length == 6
               && byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out red)
               && byte.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out green)
               && byte.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out blue);
    }

    private static double RelativeLuminance(byte red, byte green, byte blue)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(red) + 0.7152 * Linear(green) + 0.0722 * Linear(blue);
    }

    private static int Line(XElement element) =>
        element is IXmlLineInfo lineInfo && lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0;
}
