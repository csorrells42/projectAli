using System.Text.Json;

namespace Ali.Modules.Storage;

public sealed record AliWorkspaceFolderSettings(string WorkspaceRoot);

public static class AliWorkspaceFolderSettingsStore
{
    private const string FileName = "workspace-settings.json";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string GetPath(string settingsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsRoot);
        return Path.Combine(Path.GetFullPath(settingsRoot), FileName);
    }

    public static string? Load(string settingsRoot)
    {
        try
        {
            var path = GetPath(settingsRoot);
            if (!File.Exists(path))
            {
                return null;
            }

            var settings = JsonSerializer.Deserialize<AliWorkspaceFolderSettings>(
                File.ReadAllText(path),
                JsonOptions);
            if (string.IsNullOrWhiteSpace(settings?.WorkspaceRoot))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(settings.WorkspaceRoot.Trim().Trim('"')));
            var driveRoot = Path.GetPathRoot(fullPath);
            return !string.IsNullOrWhiteSpace(driveRoot) && Directory.Exists(driveRoot)
                ? fullPath
                : null;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or JsonException)
        {
            return null;
        }
    }

    public static void Save(string settingsRoot, string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var fullPath = Path.GetFullPath(workspaceRoot.Trim().Trim('"'));
        Directory.CreateDirectory(fullPath);
        Directory.CreateDirectory(settingsRoot);

        var path = GetPath(settingsRoot);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new AliWorkspaceFolderSettings(fullPath), JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
