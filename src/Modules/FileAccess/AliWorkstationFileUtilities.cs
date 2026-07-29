using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Ali.Modules.WorkstationFiles;

public sealed record WorkstationFileOperationResult(
    bool Success,
    string SourcePath,
    string DestinationPath,
    string Message);

public sealed record WorkstationFileMetadataResult(
    bool Success,
    string Path,
    string Kind,
    long SizeBytes,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ModifiedAt,
    string? Sha256,
    int ChildCount,
    string Message);

public sealed record WorkstationArchiveEntry(string Path, long SizeBytes, bool IsDirectory);

public sealed record WorkstationArchiveResult(
    bool Success,
    string ArchivePath,
    string Format,
    string Message,
    IReadOnlyList<WorkstationArchiveEntry> Entries);

/// <summary>
/// Binary-safe workstation operations that complement Agent Framework's text
/// file provider. All paths are resolved through Ali's approved mounts, all
/// mutations refuse overwrite, and every operation is audited without content.
/// </summary>
public sealed class AliWorkstationFileUtilities(AliWorkstationFileAccess access)
{
    private const int MaximumArchiveEntries = 10_000;
    private const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024;

    public async Task<WorkstationFileOperationResult> CopyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        string? staging = null;
        try
        {
            var source = ResolveExistingInstance(sourcePath);
            EnsureSourceTreeSafe(source);
            var destination = ResolveDestination(destinationPath, source.IsDirectory);
            EnsureDestinationAbsent(destination.PhysicalPath);
            var parent = Path.GetDirectoryName(destination.PhysicalPath)!;
            Directory.CreateDirectory(parent);
            staging = Path.Combine(parent, $".ali-copy-{Guid.NewGuid():N}");
            cancellationToken.ThrowIfCancellationRequested();
            if (source.IsDirectory)
            {
                CopyDirectory(source.PhysicalPath, staging, cancellationToken);
                Directory.Move(staging, destination.PhysicalPath);
            }
            else
            {
                File.Copy(source.PhysicalPath, staging, overwrite: false);
                File.Move(staging, destination.PhysicalPath, overwrite: false);
            }
            staging = null;

            await AuditAsync("copy", $"{sourcePath} -> {destinationPath}", true, "completed", cancellationToken);
            return new(true, sourcePath, destinationPath, "The file or folder was copied successfully without overwriting anything.");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            DeleteStagingPath(staging);
            await AuditFailureAsync("copy", $"{sourcePath} -> {destinationPath}", ex);
            return new(false, sourcePath, destinationPath, ex.Message);
        }
    }

    public async Task<WorkstationFileOperationResult> CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = access.ResolvePhysicalDirectoryPath(path);
            var existed = Directory.Exists(resolved.PhysicalPath);
            Directory.CreateDirectory(resolved.PhysicalPath);
            await AuditAsync("create-directory", path, true, existed ? "already-existed" : "created", cancellationToken);
            return new(true, string.Empty, path, existed ? "The folder already existed." : "The folder was created successfully.");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            await AuditFailureAsync("create-directory", path, ex);
            return new(false, string.Empty, path, ex.Message);
        }
    }

    public async Task<WorkstationFileMetadataResult> GetMetadataAsync(
        string path,
        bool includeSha256,
        CancellationToken cancellationToken)
    {
        try
        {
            var item = ResolveExistingInstance(path);
            if (item.IsDirectory)
            {
                var info = new DirectoryInfo(item.PhysicalPath);
                var children = Directory.EnumerateFileSystemEntries(item.PhysicalPath).Count();
                await AuditAsync("metadata", path, true, $"directory-children:{children}", cancellationToken);
                return new(true, path, "directory", 0, info.CreationTimeUtc, info.LastWriteTimeUtc, null, children,
                    "Directory metadata was read. SHA-256 applies to files, not folders.");
            }

            var file = new FileInfo(item.PhysicalPath);
            string? hash = null;
            if (includeSha256)
            {
                await using var stream = new FileStream(item.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            }

            await AuditAsync("metadata", path, true, includeSha256 ? "file-with-sha256" : "file", cancellationToken);
            return new(true, path, "file", file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc, hash, 0,
                includeSha256 ? "File metadata and SHA-256 were read." : "File metadata was read.");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            await AuditFailureAsync("metadata", path, ex);
            return new(false, path, "unknown", 0, null, null, null, 0, ex.Message);
        }
    }

    public async Task<WorkstationArchiveResult> CreateArchiveAsync(
        string sourcePath,
        string destinationPath,
        string? format,
        CancellationToken cancellationToken)
    {
        string? staging = null;
        try
        {
            var source = ResolveExistingInstance(sourcePath);
            EnsureSourceTreeSafe(source);
            var normalizedFormat = NormalizeCreateFormat(format, destinationPath);
            destinationPath = EnsureArchiveExtension(destinationPath, normalizedFormat);
            var destination = access.ResolvePhysicalFilePath(destinationPath);
            EnsureDestinationAbsent(destination.PhysicalPath);
            var parent = Path.GetDirectoryName(destination.PhysicalPath)!;
            Directory.CreateDirectory(parent);
            var stagingParent = ResolveArchiveStagingParent(source, destination, parent);
            Directory.CreateDirectory(stagingParent);
            staging = Path.Combine(stagingParent, $".ali-archive-{Guid.NewGuid():N}{Path.GetExtension(destination.PhysicalPath)}");

            switch (normalizedFormat)
            {
                case "zip":
                    CreateZip(source, staging);
                    break;
                case "tar":
                    CreateTar(source, staging);
                    break;
                case "gzip":
                    await CreateGZipAsync(source, staging, cancellationToken);
                    break;
                case "tar.gz":
                    await CreateTarGZipAsync(source, staging, cancellationToken);
                    break;
                case "7z":
                    await Run7ZipAsync(["a", "-t7z", staging, source.PhysicalPath], cancellationToken);
                    break;
                default:
                    throw new NotSupportedException($"Archive format '{normalizedFormat}' is not supported.");
            }
            File.Move(staging, destination.PhysicalPath, overwrite: false);
            staging = null;

            await AuditAsync("archive-create", $"{sourcePath} -> {destinationPath}", true, normalizedFormat, cancellationToken);
            return new(true, destinationPath, normalizedFormat,
                $"Created the {normalizedFormat} archive without overwriting an existing file.", []);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            DeleteStagingPath(staging);
            await AuditFailureAsync("archive-create", $"{sourcePath} -> {destinationPath}", ex);
            return new(false, destinationPath, format ?? "auto", ex.Message, []);
        }
    }

    public async Task<WorkstationArchiveResult> ListArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var archive = access.ResolvePhysicalFilePath(archivePath);
            if (!File.Exists(archive.PhysicalPath))
            {
                throw new FileNotFoundException("The archive does not exist.", archive.PhysicalPath);
            }

            var format = DetectArchiveFormat(archive.PhysicalPath);
            var entries = format switch
            {
                "zip" => ListZip(archive.PhysicalPath),
                "tar" => ListTar(archive.PhysicalPath),
                "tar.gz" => await ListTarGZipAsync(archive.PhysicalPath, cancellationToken),
                "gzip" => [new WorkstationArchiveEntry(Path.GetFileNameWithoutExtension(archive.PhysicalPath), 0, false)],
                "7z" => await List7ZipAsync(archive.PhysicalPath, cancellationToken),
                _ => throw new NotSupportedException("The archive format is not supported.")
            };
            ValidateArchiveLimits(entries);
            await AuditAsync("archive-list", archivePath, true, $"entries:{entries.Count}", cancellationToken);
            return new(true, archivePath, format, $"Listed {entries.Count} archive entries.", entries);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            await AuditFailureAsync("archive-list", archivePath, ex);
            return new(false, archivePath, "unknown", ex.Message, []);
        }
    }

    public async Task<WorkstationArchiveResult> ExtractArchiveAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        string? staging = null;
        try
        {
            var archive = access.ResolvePhysicalFilePath(archivePath);
            if (!File.Exists(archive.PhysicalPath))
            {
                throw new FileNotFoundException("The archive does not exist.", archive.PhysicalPath);
            }

            var destination = access.ResolvePhysicalDirectoryPath(destinationDirectory);
            EnsureDestinationAbsent(destination.PhysicalPath);
            var parent = Path.GetDirectoryName(destination.PhysicalPath)!;
            Directory.CreateDirectory(parent);
            staging = Path.Combine(parent, $".ali-extract-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            var format = DetectArchiveFormat(archive.PhysicalPath);
            var entries = await ValidateAndExtractAsync(archive.PhysicalPath, staging, format, cancellationToken);
            EnsureExtractedTreeSafe(staging);
            Directory.Move(staging, destination.PhysicalPath);
            staging = null;
            await AuditAsync("archive-extract", $"{archivePath} -> {destinationDirectory}", true, format, cancellationToken);
            return new(true, archivePath, format,
                $"Extracted {entries.Count} entries into a new folder without overwriting existing files.", entries);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (staging is not null && Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            await AuditFailureAsync("archive-extract", $"{archivePath} -> {destinationDirectory}", ex);
            return new(false, archivePath, "unknown", ex.Message, []);
        }
    }

    private async Task<IReadOnlyList<WorkstationArchiveEntry>> ValidateAndExtractAsync(
        string archivePath,
        string staging,
        string format,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkstationArchiveEntry> entries;
        switch (format)
        {
            case "zip":
                entries = ListZip(archivePath);
                ValidateEntries(entries, staging);
                ZipFile.ExtractToDirectory(archivePath, staging, overwriteFiles: false);
                break;
            case "tar":
                entries = ListTar(archivePath);
                ValidateEntries(entries, staging);
                await ExtractTarAsync(archivePath, staging, cancellationToken);
                break;
            case "tar.gz":
                var temporaryTar = Path.Combine(Path.GetTempPath(), $"ali-{Guid.NewGuid():N}.tar");
                try
                {
                    await DecompressGZipAsync(archivePath, temporaryTar, cancellationToken);
                    entries = ListTar(temporaryTar);
                    ValidateEntries(entries, staging);
                    await ExtractTarAsync(temporaryTar, staging, cancellationToken);
                }
                finally
                {
                    File.Delete(temporaryTar);
                }
                break;
            case "gzip":
                entries = [new WorkstationArchiveEntry(Path.GetFileNameWithoutExtension(archivePath), 0, false)];
                ValidateEntries(entries, staging);
                await DecompressGZipAsync(archivePath, Path.Combine(staging, entries[0].Path), cancellationToken);
                break;
            case "7z":
                entries = await List7ZipAsync(archivePath, cancellationToken);
                ValidateEntries(entries, staging);
                await Run7ZipAsync(["x", archivePath, $"-o{staging}", "-aoa"], cancellationToken);
                break;
            default:
                throw new NotSupportedException("The archive format is not supported.");
        }

        return entries;
    }

    private ResolvedItem ResolveExistingInstance(string path)
    {
        var file = access.ResolvePhysicalFilePath(path);
        if (File.Exists(file.PhysicalPath))
        {
            return new(file.PhysicalPath, false);
        }

        var directory = access.ResolvePhysicalDirectoryPath(path);
        if (Directory.Exists(directory.PhysicalPath))
        {
            return new(directory.PhysicalPath, true);
        }

        throw new FileNotFoundException("The source file or folder does not exist.", file.PhysicalPath);
    }

    private AliResolvedWorkstationPath ResolveDestination(string path, bool directory) =>
        directory ? access.ResolvePhysicalDirectoryPath(path) : access.ResolvePhysicalFilePath(path);

    private static void EnsureDestinationAbsent(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("The destination already exists. Ali will not overwrite it.");
        }
    }

    private static void DeleteStagingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup must not hide the original operation failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup must not hide the original operation failure.
        }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Folder copies do not follow reparse points or junctions.");
        }
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Folder copies do not follow reparse points or junctions.");
            }
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void CreateZip(ResolvedItem source, string destination)
    {
        if (source.IsDirectory)
        {
            ZipFile.CreateFromDirectory(source.PhysicalPath, destination, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        else
        {
            using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(source.PhysicalPath, Path.GetFileName(source.PhysicalPath), CompressionLevel.Optimal);
        }
    }

    private static string ResolveArchiveStagingParent(
        ResolvedItem source,
        AliResolvedWorkstationPath destination,
        string destinationParent)
    {
        if (!source.IsDirectory || !IsWithinOrEqual(destinationParent, source.PhysicalPath))
        {
            return destinationParent;
        }

        var mountRoot = Path.TrimEndingDirectorySeparator(destination.MountRoot);
        if (!IsWithinOrEqual(mountRoot, source.PhysicalPath))
        {
            return mountRoot;
        }

        throw new InvalidOperationException(
            "Ali cannot place an archive inside the same approved mount root that is being archived. "
            + "Choose a destination outside the source folder.");
    }

    private static bool IsWithinOrEqual(string candidatePath, string directoryPath)
    {
        var candidate = Path.GetFullPath(candidatePath);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        return candidate.Equals(directory, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void CreateTar(ResolvedItem source, string destination)
    {
        if (source.IsDirectory)
        {
            TarFile.CreateFromDirectory(source.PhysicalPath, destination, includeBaseDirectory: false);
            return;
        }

        using var output = File.Create(destination);
        using var writer = new TarWriter(output, leaveOpen: false);
        writer.WriteEntry(source.PhysicalPath, Path.GetFileName(source.PhysicalPath));
    }

    private static async Task CreateGZipAsync(ResolvedItem source, string destination, CancellationToken cancellationToken)
    {
        if (source.IsDirectory)
        {
            throw new ArgumentException("Plain gzip compresses one file. Use tar.gz for a folder.");
        }
        await CompressGZipAsync(source.PhysicalPath, destination, cancellationToken);
    }

    private static async Task CreateTarGZipAsync(ResolvedItem source, string destination, CancellationToken cancellationToken)
    {
        var temporaryTar = Path.Combine(Path.GetTempPath(), $"ali-{Guid.NewGuid():N}.tar");
        try
        {
            CreateTar(source, temporaryTar);
            await CompressGZipAsync(temporaryTar, destination, cancellationToken);
        }
        finally
        {
            File.Delete(temporaryTar);
        }
    }

    private static async Task CompressGZipAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        await using var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: false);
        await input.CopyToAsync(gzip, cancellationToken);
    }

    private static async Task DecompressGZipAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        var buffer = new byte[131072];
        long expanded = 0;
        int read;
        while ((read = await gzip.ReadAsync(buffer, cancellationToken)) > 0)
        {
            expanded += read;
            if (expanded > MaximumExpandedBytes)
            {
                throw new InvalidDataException("The gzip payload exceeds Ali's safe extraction limit.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static IReadOnlyList<WorkstationArchiveEntry> ListZip(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(entry => new WorkstationArchiveEntry(
            entry.FullName,
            entry.Length,
            entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))).ToArray();
    }

    private static IReadOnlyList<WorkstationArchiveEntry> ListTar(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new TarReader(stream, leaveOpen: false);
        var entries = new List<WorkstationArchiveEntry>();
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            entries.Add(new(entry.Name, entry.Length, entry.EntryType is TarEntryType.Directory));
        }
        return entries;
    }

    private static async Task ExtractTarAsync(string archivePath, string destinationRoot, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        using var reader = new TarReader(stream, leaveOpen: false);
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = ResolveValidatedArchiveEntry(destinationRoot, entry.Name);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(destination);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                    {
                        if (entry.DataStream is not null)
                        {
                            await entry.DataStream.CopyToAsync(output, cancellationToken);
                        }
                    }
                    break;
                case TarEntryType.GlobalExtendedAttributes:
                case TarEntryType.ExtendedAttributes:
                    break;
                default:
                    throw new InvalidDataException(
                        $"The TAR entry '{entry.Name}' uses unsupported link or special type '{entry.EntryType}'.");
            }
        }
    }

    private static async Task<IReadOnlyList<WorkstationArchiveEntry>> ListTarGZipAsync(string path, CancellationToken cancellationToken)
    {
        var temporaryTar = Path.Combine(Path.GetTempPath(), $"ali-{Guid.NewGuid():N}.tar");
        try
        {
            await DecompressGZipAsync(path, temporaryTar, cancellationToken);
            return ListTar(temporaryTar);
        }
        finally
        {
            File.Delete(temporaryTar);
        }
    }

    private static void ValidateEntries(IReadOnlyList<WorkstationArchiveEntry> entries, string destinationRoot)
    {
        ValidateArchiveLimits(entries);
        var root = Path.EndsInDirectorySeparator(destinationRoot)
            ? destinationRoot
            : destinationRoot + Path.DirectorySeparatorChar;
        foreach (var entry in entries)
        {
            _ = ResolveValidatedArchiveEntry(destinationRoot, entry.Path, root);
        }
    }

    private static string ResolveValidatedArchiveEntry(string destinationRoot, string entryPath, string? normalizedRoot = null)
    {
        var name = entryPath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathFullyQualified(name))
        {
            throw new IOException("The archive contains an absolute path and was not extracted.");
        }
        var root = normalizedRoot ?? (Path.EndsInDirectorySeparator(destinationRoot)
            ? destinationRoot
            : destinationRoot + Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(destinationRoot, name));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The archive contains a path that escapes the destination folder and was not extracted.");
        }
        return candidate;
    }

    private static void ValidateArchiveLimits(IReadOnlyCollection<WorkstationArchiveEntry> entries)
    {
        if (entries.Count > MaximumArchiveEntries || entries.Sum(entry => Math.Max(0, entry.SizeBytes)) > MaximumExpandedBytes)
        {
            throw new IOException("The archive exceeds Ali's safe extraction limits.");
        }
    }

    private static void EnsureSourceTreeSafe(ResolvedItem source)
    {
        if ((File.GetAttributes(source.PhysicalPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("File operations do not follow reparse points, symbolic links, or junctions.");
        }

        if (source.IsDirectory
            && Directory.EnumerateFileSystemEntries(source.PhysicalPath, "*", SearchOption.AllDirectories)
                .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
        {
            throw new IOException("File operations do not follow reparse points, symbolic links, or junctions.");
        }
    }

    private static void EnsureExtractedTreeSafe(string root)
    {
        long total = 0;
        var count = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            count++;
            if (count > MaximumArchiveEntries)
            {
                throw new InvalidDataException("The extracted archive exceeds Ali's safe entry limit.");
            }
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The extracted archive contains a symbolic link or reparse point.");
            }
            if ((attributes & FileAttributes.Directory) == 0)
            {
                total += new FileInfo(path).Length;
                if (total > MaximumExpandedBytes)
                {
                    throw new InvalidDataException("The extracted archive exceeds Ali's safe size limit.");
                }
            }
        }
    }

    private static string NormalizeCreateFormat(string? format, string destinationPath)
    {
        if (!string.IsNullOrWhiteSpace(format) && !format.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeFormatName(format);
        }
        return DetectArchiveFormat(destinationPath, defaultToZip: true);
    }

    private static string NormalizeFormatName(string value) => value.Trim().ToLowerInvariant() switch
    {
        "zip" => "zip",
        "tar" => "tar",
        "gz" or "gzip" => "gzip",
        "tgz" or "tar.gz" => "tar.gz",
        "7z" or "7zip" => "7z",
        _ => throw new NotSupportedException($"Archive format '{value}' is not supported.")
    };

    private static string DetectArchiveFormat(string path, bool defaultToZip = false)
    {
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz")) return "tar.gz";
        if (lower.EndsWith(".zip")) return "zip";
        if (lower.EndsWith(".tar")) return "tar";
        if (lower.EndsWith(".gz")) return "gzip";
        if (lower.EndsWith(".7z")) return "7z";
        return defaultToZip ? "zip" : throw new NotSupportedException("Use a .zip, .tar, .tar.gz, .tgz, .gz, or .7z archive path.");
    }

    private static string EnsureArchiveExtension(string path, string format)
    {
        if (Path.HasExtension(path)) return path;
        return path + (format switch { "tar.gz" => ".tar.gz", "gzip" => ".gz", _ => "." + format });
    }

    private static async Task<IReadOnlyList<WorkstationArchiveEntry>> List7ZipAsync(string archivePath, CancellationToken cancellationToken)
    {
        var output = await Run7ZipAsync(["l", "-slt", archivePath], cancellationToken);
        var entries = new List<WorkstationArchiveEntry>();
        string? path = null;
        long size = 0;
        bool folder = false;
        var inEntries = false;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("----------", StringComparison.Ordinal)) { inEntries = true; continue; }
            if (!inEntries) continue;
            if (line.StartsWith("Path = ", StringComparison.Ordinal))
            {
                if (path is not null) entries.Add(new(path, size, folder));
                path = line[7..]; size = 0; folder = false;
            }
            else if (line.StartsWith("Size = ", StringComparison.Ordinal)) long.TryParse(line[7..], out size);
            else if (line.StartsWith("Folder = ", StringComparison.Ordinal)) folder = line[9..].Equals("+", StringComparison.Ordinal);
        }
        if (path is not null) entries.Add(new(path, size, folder));
        return entries;
    }

    private static async Task<string> Run7ZipAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var executable = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
        }.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException(
            "7-Zip is not installed. ZIP, TAR, TAR.GZ, and GZip remain available without it.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("7-Zip could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new IOException($"7-Zip failed with exit code {process.ExitCode}: {error}".Trim());
        return output;
    }

    private Task AuditAsync(string operation, string path, bool succeeded, string outcome, CancellationToken cancellationToken) =>
        access.Audit.AppendAsync(operation, path, succeeded, outcome, cancellationToken);

    private async Task AuditFailureAsync(string operation, string path, Exception exception)
    {
        try { await access.Audit.AppendAsync(operation, path, false, exception.GetType().Name, CancellationToken.None); }
        catch { }
    }

    private static bool IsExpected(Exception ex) => ex is ArgumentException or IOException or UnauthorizedAccessException
        or NotSupportedException or InvalidDataException or CryptographicException;

    private sealed record ResolvedItem(string PhysicalPath, bool IsDirectory);
}
