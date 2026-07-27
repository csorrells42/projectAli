namespace Ali.Modules.Storage;

public static class AliDataFolderSelectionStore
{
    private const string PointerFileName = "AliDataFolder.txt";

    public static string PointerFilePath =>
        Path.Combine(AppContext.BaseDirectory, PointerFileName);

    public static string? Load()
    {
        try
        {
            if (!File.Exists(PointerFilePath))
            {
                return null;
            }

            var configured = File.ReadLines(PointerFilePath)
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?
                .Trim()
                .Trim('"');
            if (string.IsNullOrWhiteSpace(configured))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configured));
            var root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root)
                ? fullPath
                : null;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            return null;
        }
    }

    public static void Save(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var fullPath = Path.GetFullPath(folder.Trim().Trim('"'));
        Directory.CreateDirectory(fullPath);
        File.WriteAllText(PointerFilePath, fullPath + Environment.NewLine);
    }
}
