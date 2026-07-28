using System.Text.Json;

namespace Ali.Modules.Mcp;

public static class McpTransportKinds
{
    public const string Http = "HTTP";
    public const string Stdio = "Standard I/O";

    public static IReadOnlyList<string> All { get; } = [Http, Stdio];

    public static string Normalize(string? value) =>
        string.Equals(value, Stdio, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "stdio", StringComparison.OrdinalIgnoreCase)
            ? Stdio
            : Http;
}

public sealed class McpClientSettings
{
    public bool Enabled { get; set; }

    public List<McpServerProfile> Servers { get; set; } = [];
}

public sealed class McpServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New MCP server";

    public bool Enabled { get; set; }

    public string Transport { get; set; } = McpTransportKinds.Http;

    public string Endpoint { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public List<string> Arguments { get; set; } = [];

    public string WorkingDirectory { get; set; } = string.Empty;

    public bool InheritEnvironmentVariables { get; set; } = true;

    public List<McpEnvironmentVariableBinding> EnvironmentVariables { get; set; } = [];

    public string AuthenticationHeaderName { get; set; } = "Authorization";

    public string AuthenticationPrefix { get; set; } = "Bearer ";

    public string AuthenticationEnvironmentVariable { get; set; } = string.Empty;

    public int ConnectionTimeoutSeconds { get; set; } = 30;

    public List<McpToolPolicy> Tools { get; set; } = [];
}

public sealed class McpEnvironmentVariableBinding
{
    public string Name { get; set; } = string.Empty;

    public string SourceEnvironmentVariable { get; set; } = string.Empty;
}

public sealed class McpToolPolicy
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public bool RequiresApproval { get; set; } = true;

    public bool ReadOnlyHint { get; set; }

    public bool DestructiveHint { get; set; }
}

public static class McpClientSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string GetSettingsPath(string dataRoot) =>
        Path.Combine(dataRoot, "MCP", "mcp-clients.json");

    public static McpClientSettings LoadOrDefault(string dataRoot)
    {
        var path = GetSettingsPath(dataRoot);
        if (!File.Exists(path))
        {
            return new McpClientSettings();
        }

        try
        {
            using var stream = File.OpenRead(path);
            return Normalize(JsonSerializer.Deserialize<McpClientSettings>(stream, JsonOptions)
                ?? new McpClientSettings());
        }
        catch (JsonException)
        {
            return new McpClientSettings();
        }
        catch (IOException)
        {
            return new McpClientSettings();
        }
    }

    public static void Save(string dataRoot, McpClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var path = GetSettingsPath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        using (var stream = File.Create(temporaryPath))
        {
            JsonSerializer.Serialize(stream, Normalize(settings), JsonOptions);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    public static McpClientSettings Normalize(McpClientSettings settings)
    {
        settings.Servers ??= [];
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in settings.Servers)
        {
            server.Id = string.IsNullOrWhiteSpace(server.Id) || !usedIds.Add(server.Id.Trim())
                ? Guid.NewGuid().ToString("N")
                : server.Id.Trim();
            usedIds.Add(server.Id);
            server.Name = string.IsNullOrWhiteSpace(server.Name) ? "MCP server" : server.Name.Trim();
            server.Transport = McpTransportKinds.Normalize(server.Transport);
            server.Endpoint = server.Endpoint?.Trim() ?? string.Empty;
            server.Command = server.Command?.Trim() ?? string.Empty;
            server.WorkingDirectory = server.WorkingDirectory?.Trim() ?? string.Empty;
            server.AuthenticationHeaderName = string.IsNullOrWhiteSpace(server.AuthenticationHeaderName)
                ? "Authorization"
                : server.AuthenticationHeaderName.Trim();
            server.AuthenticationPrefix ??= string.Empty;
            server.AuthenticationEnvironmentVariable = server.AuthenticationEnvironmentVariable?.Trim() ?? string.Empty;
            server.ConnectionTimeoutSeconds = Math.Clamp(server.ConnectionTimeoutSeconds, 5, 300);
            server.Arguments = (server.Arguments ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList();
            server.EnvironmentVariables = (server.EnvironmentVariables ?? [])
                .Where(binding => !string.IsNullOrWhiteSpace(binding.Name)
                    && !string.IsNullOrWhiteSpace(binding.SourceEnvironmentVariable))
                .Select(binding => new McpEnvironmentVariableBinding
                {
                    Name = binding.Name.Trim(),
                    SourceEnvironmentVariable = binding.SourceEnvironmentVariable.Trim()
                })
                .ToList();
            server.Tools = (server.Tools ?? [])
                .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
                .GroupBy(tool => tool.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        return settings;
    }
}
