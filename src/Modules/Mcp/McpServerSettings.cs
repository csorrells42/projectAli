using System.Text.Json;

namespace Ali.Modules.Mcp;

public sealed class McpServerSettings
{
    public bool Enabled { get; init; }

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 8771;

    public string Path { get; init; } = "/mcp";

    public bool Stateless { get; init; }

    public bool RequireAuthentication { get; init; } = true;

    public string AuthenticationEnvironmentVariable { get; init; } = "ALI_MCP_SERVER_TOKEN";

    public IReadOnlyList<McpServerToolPolicy> Tools { get; init; } = McpServerToolCatalog.CreateDefaultPolicies();

    public string Endpoint => $"http://{Host}:{Port}{NormalizePath(Path)}";

    public McpServerSettings Normalize() => new()
    {
        Enabled = Enabled,
        Host = "127.0.0.1",
        Port = Port is >= 1024 and <= 65535 ? Port : 8771,
        Path = NormalizePath(Path),
        Stateless = Stateless,
        RequireAuthentication = RequireAuthentication,
        AuthenticationEnvironmentVariable = string.IsNullOrWhiteSpace(AuthenticationEnvironmentVariable)
            ? "ALI_MCP_SERVER_TOKEN"
            : AuthenticationEnvironmentVariable.Trim(),
        Tools = McpServerToolCatalog.NormalizePolicies(Tools)
    };

    public static string NormalizePath(string? path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? "/mcp" : path.Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }
}

public sealed class McpServerToolPolicy
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public bool WritesLocalData { get; init; }

    public bool UsesNetwork { get; init; }

    public bool ReadsPrivateData { get; init; }
}

public static class McpServerSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string GetSettingsPath(string dataRoot) =>
        System.IO.Path.Combine(dataRoot, "MCP", "mcp-server.json");

    public static McpServerSettings LoadOrDefault(string dataRoot)
    {
        var path = GetSettingsPath(dataRoot);
        if (!File.Exists(path))
        {
            return new McpServerSettings().Normalize();
        }

        try
        {
            using var stream = File.OpenRead(path);
            return (JsonSerializer.Deserialize<McpServerSettings>(stream, JsonOptions)
                ?? new McpServerSettings()).Normalize();
        }
        catch (JsonException)
        {
            return new McpServerSettings().Normalize();
        }
        catch (IOException)
        {
            return new McpServerSettings().Normalize();
        }
    }

    public static McpServerSettings Save(string dataRoot, McpServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        var path = GetSettingsPath(dataRoot);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        using (var stream = File.Create(temporaryPath))
        {
            JsonSerializer.Serialize(stream, normalized, JsonOptions);
        }

        File.Move(temporaryPath, path, overwrite: true);
        return normalized;
    }
}
