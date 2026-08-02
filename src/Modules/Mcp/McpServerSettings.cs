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

public sealed record McpServerSettingsLoadResult(
    McpSettingsLoadStatus Status,
    McpServerSettings Settings,
    string? Error,
    string BoundaryRevision)
{
    public bool CanPersist => Status != McpSettingsLoadStatus.FailedClosed;
}

public static class McpServerSettingsStore
{
    internal const long MaximumSettingsFileBytes = 256 * 1024;
    internal const int MaximumPathCharacters = 2048;
    internal const int MaximumEnvironmentNameCharacters = 256;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string GetSettingsPath(string dataRoot) =>
        System.IO.Path.Combine(dataRoot, "MCP", "mcp-server.json");

    public static McpServerSettingsLoadResult Load(string dataRoot)
    {
        var path = GetSettingsPath(dataRoot);
        string boundaryRevision;
        try
        {
            boundaryRevision = McpClientSettingsBoundaryRevision.Capture(
                path,
                MaximumSettingsFileBytes);
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
            return new McpServerSettingsLoadResult(
                McpSettingsLoadStatus.MissingDefaults,
                new McpServerSettings().Normalize(),
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

            var settings = JsonSerializer.Deserialize<McpServerSettings>(stream, JsonOptions)
                ?? new McpServerSettings();
            if (!TryValidateRawDocument(settings, out var validationError))
            {
                return FailedClosed(validationError, boundaryRevision);
            }
            var currentBoundaryRevision = McpClientSettingsBoundaryRevision.Capture(
                path,
                MaximumSettingsFileBytes);
            if (!string.Equals(boundaryRevision, currentBoundaryRevision, StringComparison.Ordinal))
            {
                return FailedClosed("ChangedWhileLoading", currentBoundaryRevision);
            }
            return new McpServerSettingsLoadResult(
                McpSettingsLoadStatus.Loaded,
                settings.Normalize(),
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

    public static McpServerSettings LoadOrDefault(string dataRoot) => Load(dataRoot).Settings;

    public static McpServerSettings Save(
        string dataRoot,
        McpServerSettings settings,
        string? expectedBoundaryRevision = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TryValidateSaveDraft(settings, out var draftError))
        {
            throw new InvalidOperationException(
                $"MCP server settings are invalid ({draftError}); Ali did not save or substitute values.");
        }
        var normalized = settings.Normalize();
        if (normalized.Path.Length > MaximumPathCharacters
            || normalized.AuthenticationEnvironmentVariable.Length
                > MaximumEnvironmentNameCharacters)
        {
            throw new InvalidOperationException(
                "MCP server settings exceed Ali's bounded field limits.");
        }
        var path = GetSettingsPath(dataRoot);
        EnsureExpectedBoundary(path, expectedBoundaryRevision);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        try
        {
            using (var stream = File.Create(temporaryPath))
            {
                JsonSerializer.Serialize(stream, normalized, JsonOptions);
                if (stream.Length > MaximumSettingsFileBytes)
                {
                    throw new InvalidOperationException(
                        $"MCP server settings exceed Ali's {MaximumSettingsFileBytes}-byte storage limit.");
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

    private static McpServerSettingsLoadResult FailedClosed(
        string reason,
        string boundaryRevision) =>
        new(
            McpSettingsLoadStatus.FailedClosed,
            new McpServerSettings().Normalize(),
            $"mcp-server.json failed safely ({reason}). Ali did not overwrite it; fix or replace the file, then Reload.",
            boundaryRevision);

    private static void EnsureExpectedBoundary(
        string path,
        string? expectedBoundaryRevision)
    {
        if (string.IsNullOrWhiteSpace(expectedBoundaryRevision))
        {
            return;
        }

        var current = McpClientSettingsBoundaryRevision.Capture(
            path,
            MaximumSettingsFileBytes);
        if (!string.Equals(current, expectedBoundaryRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "mcp-server.json changed after Reload. Ali preserved both the newer file and your unsaved draft; Reload before saving again.");
        }
    }

    private static bool TryValidateRawDocument(
        McpServerSettings settings,
        out string error)
    {
        if (!string.Equals(settings.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || settings.Port is < 1024 or > 65535
            || string.IsNullOrWhiteSpace(settings.Path)
            || !settings.Path.StartsWith('/')
            || settings.Path.Length > MaximumPathCharacters
            || settings.AuthenticationEnvironmentVariable is null
            || settings.AuthenticationEnvironmentVariable.Length
                > MaximumEnvironmentNameCharacters
            || (settings.RequireAuthentication
                && string.IsNullOrWhiteSpace(settings.AuthenticationEnvironmentVariable))
            || settings.Tools is null)
        {
            error = "InvalidServerDeclaration";
            return false;
        }

        var defaults = McpServerToolCatalog.CreateDefaultPolicies()
            .ToDictionary(policy => policy.Name, StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in settings.Tools)
        {
            if (policy is null
                || string.IsNullOrWhiteSpace(policy.Name)
                || !names.Add(policy.Name.Trim())
                || !defaults.TryGetValue(policy.Name.Trim(), out var expected)
                || !string.Equals(policy.Description, expected.Description, StringComparison.Ordinal)
                || policy.WritesLocalData != expected.WritesLocalData
                || policy.UsesNetwork != expected.UsesNetwork
                || policy.ReadsPrivateData != expected.ReadsPrivateData)
            {
                error = "InvalidToolDeclaration";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateSaveDraft(
        McpServerSettings settings,
        out string error)
    {
        if (!string.Equals(settings.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || settings.Port is < 1024 or > 65535
            || string.IsNullOrWhiteSpace(settings.Path)
            || !settings.Path.StartsWith('/')
            || settings.Path.Length > MaximumPathCharacters
            || settings.AuthenticationEnvironmentVariable is null
            || settings.AuthenticationEnvironmentVariable.Length
                > MaximumEnvironmentNameCharacters
            || (settings.RequireAuthentication
                && string.IsNullOrWhiteSpace(settings.AuthenticationEnvironmentVariable))
            || settings.Tools is null)
        {
            error = "InvalidServerDeclaration";
            return false;
        }

        var knownNames = McpServerToolCatalog.CreateDefaultPolicies()
            .Select(policy => policy.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (settings.Tools.Any(policy =>
                policy is null
                || string.IsNullOrWhiteSpace(policy.Name)
                || !knownNames.Contains(policy.Name.Trim())
                || !names.Add(policy.Name.Trim())))
        {
            error = "InvalidToolDeclaration";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
