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
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = "New MCP server";

    public bool Enabled { get; set; }

    public string Transport { get; set; } = McpTransportKinds.Http;

    public string Endpoint { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public List<string> Arguments { get; set; } = [];

    public string WorkingDirectory { get; set; } = string.Empty;

    public bool InheritEnvironmentVariables { get; set; }

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

    public string SchemaFingerprint { get; set; } = string.Empty;
}

public enum McpSettingsLoadStatus
{
    Loaded,
    MissingDefaults,
    FailedClosed
}

public sealed record McpClientSettingsLoadResult(
    McpSettingsLoadStatus Status,
    McpClientSettings Settings,
    string? Error,
    string BoundaryRevision)
{
    public bool CanPersist => Status != McpSettingsLoadStatus.FailedClosed;
}

public static class McpClientSettingsStore
{
    internal const long MaximumSettingsFileBytes = 1024 * 1024;
    internal const int MaximumServerCount = 32;
    internal const int MaximumToolsPerServer = 128;
    internal const int MaximumServerIdCharacters = 128;
    internal const int MaximumServerNameCharacters = 120;
    internal const int MaximumToolNameCharacters = 256;
    internal const int MaximumToolDescriptionCharacters = 1600;
    internal const int MaximumSchemaFingerprintCharacters = 128;
    internal const int MaximumConnectionFieldCharacters = 4096;
    internal const int MaximumArgumentCount = 64;
    internal const int MaximumArgumentCharacters = 4096;
    internal const int MaximumEnvironmentBindingCount = 64;
    internal const int MaximumEnvironmentNameCharacters = 256;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string GetSettingsPath(string dataRoot) =>
        Path.Combine(dataRoot, "MCP", "mcp-clients.json");

    public static McpClientSettingsLoadResult Load(string dataRoot)
    {
        var path = GetSettingsPath(dataRoot);
        string boundaryRevision;
        try
        {
            boundaryRevision = McpClientSettingsBoundaryRevision.Capture(path);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or System.Security.SecurityException)
        {
            return FailedClosed(ex.GetType().Name, $"unavailable:{ex.GetType().Name}");
        }
        if (!File.Exists(path))
        {
            return new McpClientSettingsLoadResult(
                McpSettingsLoadStatus.MissingDefaults,
                new McpClientSettings(),
                null,
                boundaryRevision);
        }

        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaximumSettingsFileBytes)
            {
                return FailedClosed("SizeLimitExceeded", boundaryRevision);
            }

            var settings = JsonSerializer.Deserialize<McpClientSettings>(stream, JsonOptions)
                ?? new McpClientSettings();
            if (!TryValidateRawDocument(settings, out var rawError))
            {
                return FailedClosed(rawError, boundaryRevision);
            }
            settings = Normalize(settings);
            if (!TryValidateStorageBounds(settings, out var validationError))
            {
                return FailedClosed(validationError, boundaryRevision);
            }

            var currentBoundaryRevision = McpClientSettingsBoundaryRevision.Capture(path);
            if (!string.Equals(
                    boundaryRevision,
                    currentBoundaryRevision,
                    StringComparison.Ordinal))
            {
                return FailedClosed("ChangedWhileLoading", currentBoundaryRevision);
            }

            return new McpClientSettingsLoadResult(
                McpSettingsLoadStatus.Loaded,
                settings,
                null,
                currentBoundaryRevision);
        }
        catch (Exception ex) when (ex is JsonException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or System.Security.SecurityException)
        {
            return FailedClosed(ex.GetType().Name, boundaryRevision);
        }
    }

    public static McpClientSettings LoadOrDefault(string dataRoot) => Load(dataRoot).Settings;

    public static McpClientSettings Save(
        string dataRoot,
        McpClientSettings settings,
        string? expectedBoundaryRevision = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var path = GetSettingsPath(dataRoot);
        EnsureExpectedBoundary(path, expectedBoundaryRevision);
        if (!TryValidateRawDocument(settings, out var rawError))
        {
            throw new InvalidOperationException(
                $"MCP client settings are invalid ({rawError}); Ali did not save or substitute values.");
        }
        var normalized = Normalize(settings);
        AssignPersistentServerIds(normalized);
        if (!TryValidateStorageBounds(normalized, out var validationError))
        {
            throw new InvalidOperationException(
                $"MCP client settings exceed Ali's bounded storage limits ({validationError}).");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        try
        {
            using (var stream = File.Create(temporaryPath))
            {
                JsonSerializer.Serialize(stream, normalized, JsonOptions);
                if (stream.Length > MaximumSettingsFileBytes)
                {
                    throw new InvalidOperationException(
                        $"MCP client settings exceed Ali's {MaximumSettingsFileBytes}-byte storage limit.");
                }
            }

            EnsureExpectedBoundary(path, expectedBoundaryRevision);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return normalized;
    }

    public static McpClientSettings Normalize(McpClientSettings settings)
    {
        settings.Servers ??= [];
        foreach (var server in settings.Servers)
        {
            server.Id = server.Id?.Trim() ?? string.Empty;
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
                .Select(group =>
                {
                    var tool = group.First();
                    tool.Name = tool.Name.Trim();
                    tool.Description ??= string.Empty;
                    tool.SchemaFingerprint = tool.SchemaFingerprint?.Trim() ?? string.Empty;
                    return tool;
                })
                .ToList();
        }

        return settings;
    }

    private static bool TryValidateStorageBounds(
        McpClientSettings settings,
        out string error)
    {
        if (settings.Servers.Count > MaximumServerCount)
        {
            error = "ServerCountLimitExceeded";
            return false;
        }

        foreach (var server in settings.Servers)
        {
            if (!TryValidateProfileStorageBounds(server, out error))
            {
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    internal static bool TryValidateProfileStorageBounds(
        McpServerProfile? server,
        out string error)
    {
        if (server is null
            || server.Id is null
            || server.Name is null
            || server.Transport is null
            || server.Endpoint is null
            || server.Command is null
            || server.WorkingDirectory is null
            || server.AuthenticationHeaderName is null
            || server.AuthenticationPrefix is null
            || server.AuthenticationEnvironmentVariable is null
            || server.Arguments is null
            || server.EnvironmentVariables is null
            || server.Tools is null
            || server.Id.Length > MaximumServerIdCharacters
            || server.Name.Length > MaximumServerNameCharacters
            || server.Endpoint.Length > MaximumConnectionFieldCharacters
            || server.Command.Length > MaximumConnectionFieldCharacters
            || server.WorkingDirectory.Length > MaximumConnectionFieldCharacters
            || server.AuthenticationHeaderName.Length > MaximumEnvironmentNameCharacters
            || server.AuthenticationPrefix.Length > MaximumEnvironmentNameCharacters
            || server.AuthenticationEnvironmentVariable.Length > MaximumEnvironmentNameCharacters
            || server.Arguments.Count > MaximumArgumentCount
            || server.Arguments.Any(argument =>
                string.IsNullOrWhiteSpace(argument)
                || argument.Length > MaximumArgumentCharacters)
            || server.EnvironmentVariables.Count > MaximumEnvironmentBindingCount
            || server.EnvironmentVariables.Any(binding =>
                binding is null
                || string.IsNullOrWhiteSpace(binding.Name)
                || string.IsNullOrWhiteSpace(binding.SourceEnvironmentVariable)
                || binding.Name.Length > MaximumEnvironmentNameCharacters
                || binding.SourceEnvironmentVariable.Length > MaximumEnvironmentNameCharacters)
            || server.Tools.Count > MaximumToolsPerServer
            || server.Tools.Any(tool =>
                tool is null
                || string.IsNullOrWhiteSpace(tool.Name)
                || tool.Description is null
                || tool.SchemaFingerprint is null
                || tool.Name.Length > MaximumToolNameCharacters
                || tool.Description.Length > MaximumToolDescriptionCharacters
                || tool.SchemaFingerprint.Length > MaximumSchemaFingerprintCharacters))
        {
            error = "ServerDeclarationLimitExceeded";
            return false;
        }

        var destinationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (server.EnvironmentVariables.Any(binding =>
                !destinationNames.Add(binding.Name.Trim())))
        {
            error = "DuplicateEnvironmentDestination";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateRawDocument(
        McpClientSettings settings,
        out string error)
    {
        if (settings.Servers is null || settings.Servers.Count > MaximumServerCount)
        {
            error = "InvalidServerCollection";
            return false;
        }

        foreach (var server in settings.Servers)
        {
            if (server is null
                || string.IsNullOrWhiteSpace(server.Name)
                || server.Id is null
                || server.Name.Length > MaximumServerNameCharacters
                || server.Id.Length > MaximumServerIdCharacters
                || !McpTransportKinds.All.Any(transport =>
                    string.Equals(transport, server.Transport, StringComparison.OrdinalIgnoreCase))
                || server.ConnectionTimeoutSeconds is < 1 or > 300
                || server.Arguments is null
                || server.Arguments.Any(string.IsNullOrWhiteSpace)
                || server.Arguments.Count > MaximumArgumentCount
                || server.Arguments.Any(argument => argument.Length > MaximumArgumentCharacters)
                || server.EnvironmentVariables is null
                || server.EnvironmentVariables.Count > MaximumEnvironmentBindingCount
                || server.EnvironmentVariables.Any(binding =>
                    binding is null
                    || string.IsNullOrWhiteSpace(binding.Name)
                    || string.IsNullOrWhiteSpace(binding.SourceEnvironmentVariable)
                    || binding.Name.Length > MaximumEnvironmentNameCharacters
                    || binding.SourceEnvironmentVariable.Length > MaximumEnvironmentNameCharacters)
                || server.Endpoint is null
                || server.Endpoint.Length > MaximumConnectionFieldCharacters
                || server.Command is null
                || server.Command.Length > MaximumConnectionFieldCharacters
                || server.WorkingDirectory is null
                || server.WorkingDirectory.Length > MaximumConnectionFieldCharacters
                || server.AuthenticationHeaderName is null
                || server.AuthenticationHeaderName.Length > MaximumEnvironmentNameCharacters
                || server.AuthenticationPrefix is null
                || server.AuthenticationPrefix.Length > MaximumEnvironmentNameCharacters
                || server.AuthenticationEnvironmentVariable is null
                || server.AuthenticationEnvironmentVariable.Length > MaximumEnvironmentNameCharacters
                || server.Tools is null
                || server.Tools.Count > MaximumToolsPerServer)
            {
                error = "InvalidServerDeclaration";
                return false;
            }

            var toolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var environmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (server.EnvironmentVariables.Any(binding =>
                    !environmentNames.Add(binding.Name.Trim())))
            {
                error = "DuplicateEnvironmentDestination";
                return false;
            }
            foreach (var tool in server.Tools)
            {
                if (tool is null
                    || string.IsNullOrWhiteSpace(tool.Name)
                    || tool.Description is null
                    || tool.SchemaFingerprint is null
                    || !toolNames.Add(tool.Name.Trim())
                    || tool.Name.Length > MaximumToolNameCharacters
                    || tool.Description.Length > MaximumToolDescriptionCharacters
                    || tool.SchemaFingerprint.Length > MaximumSchemaFingerprintCharacters)
                {
                    error = "InvalidToolDeclaration";
                    return false;
                }
            }
        }

        error = string.Empty;
        return true;
    }

    private static McpClientSettingsLoadResult FailedClosed(
        string reason,
        string boundaryRevision) =>
        new(
            McpSettingsLoadStatus.FailedClosed,
            new McpClientSettings(),
            $"mcp-clients.json failed safely ({reason}). Ali did not overwrite it; fix or replace the file, then Reload.",
            boundaryRevision);

    private static void EnsureExpectedBoundary(
        string path,
        string? expectedBoundaryRevision)
    {
        if (string.IsNullOrWhiteSpace(expectedBoundaryRevision))
        {
            return;
        }

        var current = McpClientSettingsBoundaryRevision.Capture(path);
        if (!string.Equals(current, expectedBoundaryRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "mcp-clients.json changed after Reload. Ali preserved both the newer file and your unsaved draft; Reload before saving again.");
        }
    }

    private static void AssignPersistentServerIds(McpClientSettings settings)
    {
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in settings.Servers)
        {
            var candidate = server.Id;
            if (!string.IsNullOrWhiteSpace(candidate) && usedIds.Add(candidate))
            {
                continue;
            }

            do
            {
                candidate = Guid.NewGuid().ToString("N");
            }
            while (!usedIds.Add(candidate));
            server.Id = candidate;
        }
    }
}
