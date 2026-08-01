using System.Xml;
using System.Xml.Linq;
using Ali.Modules.Mcp;

namespace Ali.McpHost;

internal sealed record AliMcpHostConfiguration(
    string Path,
    string DataRoot,
    string? WorkspaceRoot,
    McpServerSettings ServerSettings)
{
    public static AliMcpHostConfiguration LoadOrCreate(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            WriteDefault(path);
        }

        var document = LoadDocument(path);
        var root = document.Root
            ?? throw new InvalidDataException($"The MCP configuration '{path}' has no root element.");
        if (!root.Name.LocalName.Equals("AliMcpHost", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The MCP configuration root must be <AliMcpHost>, not <{root.Name.LocalName}>.");
        }

        var dataRootText = root.Element("DataRoot")?.Value.Trim();
        var dataRoot = ResolvePath(
            string.IsNullOrWhiteSpace(dataRootText)
                ? "%LOCALAPPDATA%\\AliFiles"
                : dataRootText,
            System.IO.Path.GetDirectoryName(path)!);
        var workspaceRootText = root.Element("WorkspaceRoot")?.Value.Trim();
        var workspaceRoot = string.IsNullOrWhiteSpace(workspaceRootText)
            ? null
            : ResolvePath(workspaceRootText, System.IO.Path.GetDirectoryName(path)!);

        var toolsElement = root.Element("Tools");
        var defaultEnabled = ParseBoolean(
            toolsElement?.Attribute("DefaultEnabled")?.Value,
            true,
            "Tools/@DefaultEnabled");
        var configuredTools = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var toolElement in toolsElement?.Elements("Tool") ?? [])
        {
            var name = toolElement.Attribute("Name")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidDataException("Every <Tool> entry must have a non-empty Name attribute.");
            }

            if (!configuredTools.TryAdd(
                    name,
                    ParseBoolean(toolElement.Attribute("Enabled")?.Value, true, $"Tool '{name}'/@Enabled")))
            {
                throw new InvalidDataException($"Tool '{name}' appears more than once in '{path}'.");
            }
        }

        var defaults = McpServerToolCatalog.CreateDefaultPolicies();
        var knownNames = defaults.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownNames = configuredTools.Keys.Where(name => !knownNames.Contains(name)).ToArray();
        if (unknownNames.Length > 0)
        {
            throw new InvalidDataException(
                $"Unknown MCP tool name(s) in '{path}': {string.Join(", ", unknownNames)}");
        }

        var policies = defaults.Select(tool => new McpServerToolPolicy
        {
            Name = tool.Name,
            Description = tool.Description,
            Enabled = configuredTools.GetValueOrDefault(tool.Name, defaultEnabled),
            WritesLocalData = tool.WritesLocalData,
            UsesNetwork = tool.UsesNetwork,
            ReadsPrivateData = tool.ReadsPrivateData
        }).ToArray();

        return new AliMcpHostConfiguration(
            path,
            dataRoot,
            workspaceRoot,
            new McpServerSettings
            {
                Enabled = true,
                Tools = policies
            });
    }

    private static XDocument LoadDocument(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var reader = XmlReader.Create(path, settings);
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }

    private static void WriteDefault(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tools = McpServerToolCatalog.CreateDefaultPolicies()
            .Select(tool => new XElement("Tool",
                new XAttribute("Name", tool.Name),
                new XAttribute("Enabled", true)));
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("AliMcpHost",
                new XComment("Open Interpreter starts this process through MCP standard I/O."),
                new XElement("DataRoot", "%LOCALAPPDATA%\\AliFiles"),
                new XComment("Set WorkspaceRoot to the exact Open Interpreter workspace. Leave it empty to use Ali's private workspace."),
                new XElement("WorkspaceRoot"),
                new XElement("Tools",
                    new XAttribute("DefaultEnabled", true),
                    new XComment("Set Enabled to false to hide a tool. New Ali tools follow DefaultEnabled."),
                    tools)));
        var temporaryPath = path + ".tmp";
        document.Save(temporaryPath);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static bool ParseBoolean(string? value, bool defaultValue, string location)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidDataException($"{location} must be true or false.");
    }

    private static string ResolvePath(string value, string configurationDirectory)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value);
        return System.IO.Path.GetFullPath(
            System.IO.Path.IsPathRooted(expanded)
                ? expanded
                : System.IO.Path.Combine(configurationDirectory, expanded));
    }
}
