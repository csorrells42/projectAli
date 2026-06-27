using System.IO.Compression;
using System.Text.Json;

namespace Ali.Infrastructure.Storage;

public sealed record UserDataBackupManifest(
    int Version,
    DateTimeOffset CreatedAt,
    string Application,
    string DataRootName,
    string ProfileRootName,
    IReadOnlyList<string> ExcludedPaths);

public sealed record UserDataBackupResult(
    string BackupPath,
    int FileCount,
    long TotalBytes,
    DateTimeOffset CreatedAt);

public sealed record UserDataRestoreResult(
    string BackupPath,
    int FileCount,
    DateTimeOffset BackupCreatedAt,
    string RestoredProfileDataRoot);

public sealed class UserDataBackupService(string dataRoot, string profileDataRoot)
{
    public const int ManifestVersion = 1;
    public const string ManifestEntryName = "ali-backup-manifest.json";
    private const string DataEntryRoot = "data";
    private const string ProfileEntryRoot = "profile";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] ExcludedDirectoryNames =
    [
        "SessionAudio",
        "SessionImages",
        "Backups",
        "RestoreStaging"
    ];

    private readonly string _dataRoot = Path.GetFullPath(dataRoot);
    private readonly string _profileDataRoot = Path.GetFullPath(profileDataRoot);

    public UserDataBackupResult CreateBackup(string backupPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var fullBackupPath = Path.GetFullPath(backupPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullBackupPath)!);
        if (File.Exists(fullBackupPath))
        {
            File.Delete(fullBackupPath);
        }

        var createdAt = DateTimeOffset.Now;
        var manifest = new UserDataBackupManifest(
            ManifestVersion,
            createdAt,
            "Ali",
            Path.GetFileName(_dataRoot),
            Path.GetFileName(_profileDataRoot),
            ExcludedDirectoryNames.Select(name => $"{name}/").ToArray());

        var fileCount = 0;
        long totalBytes = 0;
        using (var archive = ZipFile.Open(fullBackupPath, ZipArchiveMode.Create))
        {
            var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            using (var stream = manifestEntry.Open())
            {
                JsonSerializer.Serialize(stream, manifest, JsonOptions);
            }

            AddRoot(archive, _dataRoot, DataEntryRoot, fullBackupPath, ref fileCount, ref totalBytes);
            AddRoot(archive, _profileDataRoot, ProfileEntryRoot, fullBackupPath, ref fileCount, ref totalBytes);
        }

        return new UserDataBackupResult(fullBackupPath, fileCount, totalBytes, createdAt);
    }

    public UserDataBackupManifest InspectBackup(string backupPath)
    {
        using var archive = ZipFile.OpenRead(Path.GetFullPath(backupPath));
        return ReadManifest(archive);
    }

    public UserDataRestoreResult RestoreBackup(string backupPath)
    {
        var fullBackupPath = Path.GetFullPath(backupPath);
        var stagingRoot = Path.Combine(Path.GetTempPath(), "AliRestore-" + Guid.NewGuid().ToString("N"));
        try
        {
            UserDataBackupManifest manifest;
            using (var archive = ZipFile.OpenRead(fullBackupPath))
            {
                manifest = ReadManifest(archive);
                if (manifest.Version != ManifestVersion)
                {
                    throw new InvalidOperationException($"Unsupported Ali backup version: {manifest.Version}.");
                }

                ExtractRoot(archive, DataEntryRoot, Path.Combine(stagingRoot, DataEntryRoot));
                ExtractRoot(archive, ProfileEntryRoot, Path.Combine(stagingRoot, ProfileEntryRoot));
            }

            ReplaceRootContents(Path.Combine(stagingRoot, DataEntryRoot), _dataRoot);
            var profileParent = Directory.GetParent(_profileDataRoot)?.FullName
                                ?? throw new InvalidOperationException("Profile data root has no parent folder.");
            var fullProfileParent = Path.GetFullPath(profileParent);
            var restoredProfileRoot = Path.GetFullPath(Path.Combine(fullProfileParent, manifest.ProfileRootName));
            var profileParentPrefix = fullProfileParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                      + Path.DirectorySeparatorChar;
            if (!restoredProfileRoot.StartsWith(profileParentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Backup profile path is unsafe.");
            }

            ReplaceRootContents(Path.Combine(stagingRoot, ProfileEntryRoot), restoredProfileRoot);

            using var verifyArchive = ZipFile.OpenRead(fullBackupPath);
            var verifiedManifest = ReadManifest(verifyArchive);
            var restoredFileCount = verifyArchive.Entries.Count(entry =>
                entry.FullName.StartsWith(DataEntryRoot + "/", StringComparison.Ordinal)
                || entry.FullName.StartsWith(ProfileEntryRoot + "/", StringComparison.Ordinal));
            return new UserDataRestoreResult(fullBackupPath, restoredFileCount, verifiedManifest.CreatedAt, restoredProfileRoot);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private static void AddRoot(
        ZipArchive archive,
        string root,
        string entryRoot,
        string backupPath,
        ref int fileCount,
        ref long totalBytes)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var fullFilePath = Path.GetFullPath(filePath);
            if (fullFilePath.Equals(backupPath, StringComparison.OrdinalIgnoreCase)
                || IsExcluded(root, fullFilePath))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(root, fullFilePath);
            var entryName = ToZipPath(Path.Combine(entryRoot, relativePath));
            archive.CreateEntryFromFile(fullFilePath, entryName, CompressionLevel.Optimal);
            fileCount++;
            totalBytes += new FileInfo(fullFilePath).Length;
        }
    }

    private static bool IsExcluded(string root, string fullPath)
    {
        var relativePath = Path.GetRelativePath(root, fullPath);
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part => ExcludedDirectoryNames.Any(excluded =>
            excluded.Equals(part, StringComparison.OrdinalIgnoreCase)));
    }

    private static void ExtractRoot(ZipArchive archive, string entryRoot, string targetRoot)
    {
        var normalizedPrefix = entryRoot + "/";
        var fullTargetRoot = Path.GetFullPath(targetRoot);
        Directory.CreateDirectory(fullTargetRoot);

        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith(normalizedPrefix, StringComparison.Ordinal)))
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            var relative = entry.FullName[normalizedPrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var targetPath = Path.GetFullPath(Path.Combine(fullTargetRoot, relative));
            var targetRootPrefix = fullTargetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                   + Path.DirectorySeparatorChar;
            if (!targetPath.StartsWith(targetRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Backup contains an unsafe path: {entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static void ReplaceRootContents(string sourceRoot, string targetRoot)
    {
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(targetRoot);

        foreach (var filePath in Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories))
        {
            if (!IsExcluded(targetRoot, filePath))
            {
                File.Delete(filePath);
            }
        }

        DeleteEmptyDirectories(targetRoot);

        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var targetPath = Path.Combine(targetRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
        }
    }

    private static UserDataBackupManifest ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidOperationException("This is not an Ali backup: manifest is missing.");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<UserDataBackupManifest>(stream, JsonOptions)
               ?? throw new InvalidOperationException("This is not an Ali backup: manifest is unreadable.");
    }

    private static void DeleteEmptyDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (IsExcluded(root, directory) || Directory.EnumerateFileSystemEntries(directory).Any())
            {
                continue;
            }

            Directory.Delete(directory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Staging cleanup is best-effort; a failed cleanup should not hide restore outcome.
        }
    }

    private static string ToZipPath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
}
