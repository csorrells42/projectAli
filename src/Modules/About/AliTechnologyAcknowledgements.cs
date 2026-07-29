using System.Text;
using System.Text.Json;

namespace Ali.Modules.About;

public sealed record AliTechnologyAcknowledgement(
    string Category,
    string Name,
    string Version,
    string Contribution,
    string License,
    string Source);

public sealed record AliTechnologyAcknowledgementReport(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AliTechnologyAcknowledgement> Items,
    string FormattedText);

/// <summary>
/// Builds the About-page inventory from the artifacts Ali actually ships. New modules,
/// NuGet dependencies, runtime assets, and coding toolchains therefore appear without
/// maintaining a second hand-written dependency list.
/// </summary>
public static class AliTechnologyAcknowledgements
{
    public static AliTechnologyAcknowledgementReport Load(string? applicationRoot = null)
    {
        var root = Path.GetFullPath(applicationRoot ?? AppContext.BaseDirectory);
        var items = new List<AliTechnologyAcknowledgement>();
        AddAliModules(items);
        AddDotNetDependencies(root, items);
        AddManifest(root, "runtime-assets.json", "Runtime, model, and developer asset", items);
        AddManifest(root, "THIRD-PARTY-RUNTIME-ASSETS.json", "Runtime, model, and developer asset", items);
        AddManifest(root, "coding-toolchains.json", "Coding toolchain", items);
        AddEditorIntegrations(root, items);

        var distinct = items
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => $"{item.Category}\u001f{item.Name}\u001f{item.Version}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(DateTimeOffset.Now, distinct, Format(distinct));
    }

    private static void AddAliModules(List<AliTechnologyAcknowledgement> items)
    {
        foreach (var module in AliModuleCatalog.Default)
            items.Add(new("Ali capability module", module.DisplayName, "current", module.Purpose, "Project Ali", module.Id));
    }

    private static void AddDotNetDependencies(string root, List<AliTechnologyAcknowledgement> items)
    {
        var depsPath = Directory.EnumerateFiles(root, "Ali.deps.json", SearchOption.TopDirectoryOnly).FirstOrDefault()
            ?? Directory.EnumerateFiles(root, "*.deps.json", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (depsPath is null) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries)) return;
            foreach (var library in libraries.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("type", out var type)
                    || !string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase)) continue;
                var slash = library.Name.LastIndexOf('/');
                var name = slash > 0 ? library.Name[..slash] : library.Name;
                var version = slash > 0 ? library.Name[(slash + 1)..] : string.Empty;
                items.Add(new(".NET and native library", name, version,
                    "A library in Ali's resolved application dependency graph. Thank you to its maintainers and contributors.",
                    "See included third-party notices and upstream package metadata", $"https://www.nuget.org/packages/{Uri.EscapeDataString(name)}"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private static void AddManifest(
        string root,
        string fileName,
        string category,
        List<AliTechnologyAcknowledgement> items)
    {
        var path = Path.Combine(root, fileName);
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var assets = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement
                : document.RootElement.TryGetProperty("assets", out var nested) && nested.ValueKind == JsonValueKind.Array
                    ? nested
                    : default;
            if (assets.ValueKind != JsonValueKind.Array) return;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "id");
                if (string.IsNullOrWhiteSpace(name)) name = Text(asset, "name");
                var version = Text(asset, "version");
                var license = Text(asset, "license");
                var source = Text(asset, "source");
                if (string.IsNullOrWhiteSpace(source)) source = Text(asset, "url");
                items.Add(new(category, name, version,
                    "A pinned component Ali uses for local capability. Thank you to its maintainers, researchers, and contributors.",
                    license, source));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.ToString().Trim() : string.Empty;

    private static void AddEditorIntegrations(string root, List<AliTechnologyAcknowledgement> items)
    {
        var path = Path.Combine(root, "editor-integrations.json");
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("notepadPlusPlus", out var notepad)
                || !notepad.TryGetProperty("plugins", out var plugins)
                || plugins.ValueKind != JsonValueKind.Array) return;
            foreach (var plugin in plugins.EnumerateArray())
            {
                items.Add(new(
                    "Editor integration",
                    Text(plugin, "displayName"),
                    Text(plugin, "fallbackVersion"),
                    Text(plugin, "purpose"),
                    "See upstream package metadata",
                    Text(plugin, "fallbackUrl")));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private static string Format(IReadOnlyList<AliTechnologyAcknowledgement> items)
    {
        var text = new StringBuilder();
        text.AppendLine("Project Ali stands on the work of an extraordinary open-source and engineering community.");
        text.AppendLine("Thank you to every maintainer, researcher, standards author, tester, documenter, and contributor represented below.");
        text.AppendLine();
        foreach (var category in items.GroupBy(item => item.Category))
        {
            text.AppendLine(category.Key.ToUpperInvariant());
            text.AppendLine(new string('-', category.Key.Length));
            foreach (var item in category)
            {
                text.Append("• ").Append(item.Name);
                if (!string.IsNullOrWhiteSpace(item.Version)) text.Append("  ").Append(item.Version);
                text.AppendLine();
                text.Append("  ").AppendLine(item.Contribution);
                if (!string.IsNullOrWhiteSpace(item.License)) text.Append("  License: ").AppendLine(item.License);
                if (!string.IsNullOrWhiteSpace(item.Source)) text.Append("  Source: ").AppendLine(item.Source);
            }
            text.AppendLine();
        }
        text.AppendLine("The complete legal inventory is distributed with Ali in THIRD-PARTY-NOTICES.md and THIRD-PARTY-RUNTIME-ASSETS.json.");
        return text.ToString().TrimEnd();
    }
}
