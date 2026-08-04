using Microsoft.Agents.AI;

#pragma warning disable MAAI001 // Agent Framework file-store API is intentionally adopted behind this module boundary.

namespace Ali.Modules.WorkstationFiles;

public sealed record AliWorkstationFileMount(string Name, string RootPath);

internal sealed record AliResolvedWorkstationPath(
    string MountName,
    string MountRoot,
    string RelativePath,
    string PhysicalPath);

/// <summary>
/// Presents several explicitly approved Windows folders as one virtual Agent Framework
/// file store. Every path begins with a mount name; absolute paths, traversal, and
/// reparse-point escapes are rejected by this layer and the framework store beneath it.
/// </summary>
public sealed class AliWorkstationFileStore : AgentFileStore
{
    private readonly IReadOnlyDictionary<string, MountedStore> _mounts;
    private readonly string _trashRoot;

    public AliWorkstationFileStore(
        IEnumerable<AliWorkstationFileMount> mounts,
        string trashRoot)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        ArgumentException.ThrowIfNullOrWhiteSpace(trashRoot);
        _trashRoot = Path.GetFullPath(trashRoot);
        Directory.CreateDirectory(_trashRoot);

        var configured = new Dictionary<string, MountedStore>(StringComparer.OrdinalIgnoreCase);
        foreach (var mount in mounts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mount.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(mount.RootPath);
            var name = NormalizeMountName(mount.Name);
            if (!configured.TryAdd(name, new MountedStore(name, mount.RootPath)))
            {
                throw new ArgumentException($"Duplicate workstation file mount '{name}'.", nameof(mounts));
            }
        }

        if (configured.Count == 0)
        {
            throw new ArgumentException("At least one workstation file mount is required.", nameof(mounts));
        }

        _mounts = configured;
    }

    public IReadOnlyList<AliWorkstationFileMount> Mounts => _mounts.Values
        .Select(mount => new AliWorkstationFileMount(mount.Name, mount.RootPath))
        .OrderBy(mount => mount.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public string TrashRoot => _trashRoot;

    internal AliResolvedWorkstationPath ResolvePhysicalFilePath(string path)
    {
        var resolved = ResolveFile(path);
        return new AliResolvedWorkstationPath(
            resolved.Mount.Name,
            resolved.Mount.RootPath,
            resolved.RelativePath,
            resolved.Mount.ResolvePhysicalPath(resolved.RelativePath));
    }

    internal AliResolvedWorkstationPath ResolvePhysicalDirectoryPath(string path)
    {
        var resolved = ResolveDirectory(path);
        return new AliResolvedWorkstationPath(
            resolved.Mount.Name,
            resolved.Mount.RootPath,
            resolved.RelativePath,
            resolved.Mount.ResolvePhysicalPath(resolved.RelativePath));
    }

    internal AliResolvedWorkstationPath ResolveExistingItemPath(string path)
    {
        var resolved = ResolveExistingBareItemOrPath(path);
        return ResolvedWorkstationPath(resolved);
    }

    internal AliResolvedWorkstationPath ResolveItemDestinationPath(
        string path,
        string sourceMountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMountName);
        if (!_mounts.TryGetValue(sourceMountName, out var sourceMount))
        {
            throw new ArgumentException(
                "The source workstation mount is not registered.",
                nameof(sourceMountName));
        }

        return ResolvedWorkstationPath(ResolveDestinationFile(path, sourceMount));
    }

    internal string ResolveExactTrashPath(
        AliResolvedWorkstationPath source,
        string transactionId)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (transactionId is null
            || transactionId.Length != 32
            || transactionId.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "A lowercase 128-bit transaction identity is required.",
                nameof(transactionId));
        }

        return Path.GetFullPath(Path.Combine(
            _trashRoot,
            "Transactions",
            transactionId,
            source.MountName,
            source.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    internal Task<bool> DeleteToExactTrashAsync(
        string path,
        string expectedSourcePath,
        string exactTrashPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = ResolveExistingBareItemOrPath(path);
        if (string.IsNullOrWhiteSpace(resolved.RelativePath))
        {
            throw new ArgumentException(
                "An item name is required beneath the workstation mount.",
                nameof(path));
        }

        var source = resolved.Mount.ResolvePhysicalPath(resolved.RelativePath);
        if (!Path.GetFullPath(source).Equals(
                Path.GetFullPath(expectedSourcePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The exact delete grant does not authorize the resolved source path.");
        }

        var expectedTrashRoot = Path.Combine(_trashRoot, "Transactions")
            + Path.DirectorySeparatorChar;
        var trash = Path.GetFullPath(exactTrashPath);
        if (!trash.StartsWith(expectedTrashRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The exact delete destination escaped Ali's recoverable trash.");
        }

        var isFile = File.Exists(source);
        var isDirectory = Directory.Exists(source);
        if (!isFile && !isDirectory)
        {
            return Task.FromResult(false);
        }
        if (File.Exists(trash) || Directory.Exists(trash))
        {
            throw new IOException(
                "The exact recoverable-trash destination already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
        if (isFile)
        {
            File.Move(source, trash, overwrite: false);
        }
        else
        {
            Directory.Move(source, trash);
        }
        return Task.FromResult(true);
    }

    internal bool TryNormalizeApprovedAbsolutePath(
        string path,
        bool allowMountRoot,
        out string virtualPath)
    {
        virtualPath = path;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
        {
            return false;
        }

        try
        {
            var resolved = ResolveApprovedAbsolutePath(path.Trim(), allowMountRoot);
            virtualPath = string.IsNullOrWhiteSpace(resolved.RelativePath)
                ? resolved.Mount.Name
                : $"{resolved.Mount.Name}/{resolved.RelativePath}";
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public override Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveFile(path);
        return resolved.Mount.Store.WriteAsync(resolved.RelativePath, content, cancellationToken);
    }

    public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveFile(path);
        return resolved.Mount.Store.ReadAsync(resolved.RelativePath, cancellationToken);
    }

    internal Task MoveItemAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ResolveExistingBareItemOrPath(sourcePath);
        var destination = ResolveDestinationFile(destinationPath, source.Mount);
        var sourcePhysicalPath = source.Mount.ResolvePhysicalPath(source.RelativePath);
        var destinationPhysicalPath = destination.Mount.ResolvePhysicalPath(destination.RelativePath);
        var sourceIsFile = File.Exists(sourcePhysicalPath);
        var sourceIsDirectory = Directory.Exists(sourcePhysicalPath);
        if (!sourceIsFile && !sourceIsDirectory)
        {
            throw new FileNotFoundException("The source file or folder does not exist.", sourcePhysicalPath);
        }

        if (File.Exists(destinationPhysicalPath) || Directory.Exists(destinationPhysicalPath))
        {
            throw new IOException("The destination already exists. Ali will not overwrite it during a move or rename.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPhysicalPath)!);
        if (sourceIsFile)
        {
            File.Move(sourcePhysicalPath, destinationPhysicalPath, overwrite: false);
        }
        else
        {
            Directory.Move(sourcePhysicalPath, destinationPhysicalPath);
        }
        return Task.CompletedTask;
    }

    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveExistingBareItemOrPath(path);
        if (string.IsNullOrWhiteSpace(resolved.RelativePath))
        {
            throw new ArgumentException("An item name is required beneath the workstation mount.", nameof(path));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var source = resolved.Mount.ResolvePhysicalPath(resolved.RelativePath);
        var isFile = File.Exists(source);
        var isDirectory = Directory.Exists(source);
        if (!isFile && !isDirectory)
        {
            return Task.FromResult(false);
        }

        var trashDirectory = Path.Combine(
            _trashRoot,
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"),
            resolved.Mount.Name);
        var destination = Path.Combine(
            trashDirectory,
            resolved.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (isFile)
        {
            File.Move(source, destination);
        }
        else
        {
            Directory.Move(source, destination);
        }
        return Task.FromResult(true);
    }

    public override async Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return _mounts.Values
                .OrderBy(mount => mount.Name, StringComparer.OrdinalIgnoreCase)
                .Select(mount => new FileStoreEntry(mount.Name, FileStoreEntry.Directory))
                .ToArray();
        }

        var resolved = ResolveDirectory(directory);
        return await resolved.Mount.Store
            .ListChildrenAsync(resolved.RelativePath, cancellationToken)
            .ConfigureAwait(false);
    }

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveFile(path);
        return resolved.Mount.Store.FileExistsAsync(resolved.RelativePath, cancellationToken);
    }

    public override async Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var resolved = ResolveDirectory(directory);
            return await resolved.Mount.Store
                .SearchAsync(resolved.RelativePath, regexPattern, globPattern, recursive, cancellationToken)
                .ConfigureAwait(false);
        }

        var all = new List<FileSearchResult>();
        foreach (var mount in _mounts.Values.OrderBy(mount => mount.Name, StringComparer.OrdinalIgnoreCase))
        {
            var results = await mount.Store
                .SearchAsync(string.Empty, regexPattern, globPattern, recursive, cancellationToken)
                .ConfigureAwait(false);
            all.AddRange(results.Select(result => new FileSearchResult
            {
                FileName = $"{mount.Name}/{result.FileName}",
                Snippet = result.Snippet,
                MatchingLines = result.MatchingLines
            }));
        }

        return all;
    }

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveDirectory(path);
        return resolved.Mount.Store.CreateDirectoryAsync(resolved.RelativePath, cancellationToken);
    }

    private ResolvedPath ResolveFile(string path)
    {
        var resolved = ResolveExistingBareFileOrPath(path);
        if (string.IsNullOrWhiteSpace(resolved.RelativePath))
        {
            throw new ArgumentException("A file path must include a name beneath an approved mount.", nameof(path));
        }

        return resolved;
    }

    private ResolvedPath ResolveExistingBareFileOrPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.Trim();
        if (Path.IsPathFullyQualified(trimmed)
            || trimmed.Contains('/')
            || trimmed.Contains('\\')
            || _mounts.ContainsKey(trimmed))
        {
            return Resolve(trimmed, allowMountRoot: false);
        }

        var matches = _mounts.Values
            .Select(mount => new ResolvedPath(mount, trimmed))
            .Where(candidate => File.Exists(candidate.Mount.ResolvePhysicalPath(candidate.RelativePath)))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            > 1 => throw new ArgumentException(
                $"The bare file name '{trimmed}' exists in more than one approved root ({string.Join(", ", matches.Select(match => match.Mount.Name))}). Use a virtual root to disambiguate it.",
                nameof(path)),
            _ => Resolve(trimmed, allowMountRoot: false)
        };
    }

    private ResolvedPath ResolveExistingBareItemOrPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.Trim();
        if (Path.IsPathFullyQualified(trimmed)
            || trimmed.Contains('/')
            || trimmed.Contains('\\')
            || _mounts.ContainsKey(trimmed))
        {
            return Resolve(trimmed, allowMountRoot: false);
        }

        var matches = _mounts.Values
            .Select(mount => new ResolvedPath(mount, trimmed))
            .Where(candidate =>
            {
                var physical = candidate.Mount.ResolvePhysicalPath(candidate.RelativePath);
                return File.Exists(physical) || Directory.Exists(physical);
            })
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            > 1 => throw new ArgumentException(
                $"The bare item name '{trimmed}' exists in more than one approved root ({string.Join(", ", matches.Select(match => match.Mount.Name))}). Use a virtual root to disambiguate it.",
                nameof(path)),
            _ => Resolve(trimmed, allowMountRoot: false)
        };
    }

    private ResolvedPath ResolveDestinationFile(string path, MountedStore sourceMount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.Trim();
        if (!Path.IsPathFullyQualified(trimmed)
            && !trimmed.Contains('/')
            && !trimmed.Contains('\\')
            && !_mounts.ContainsKey(trimmed))
        {
            return new ResolvedPath(sourceMount, trimmed);
        }

        return Resolve(trimmed, allowMountRoot: false);
    }

    private static AliResolvedWorkstationPath ResolvedWorkstationPath(ResolvedPath resolved) =>
        new(
            resolved.Mount.Name,
            resolved.Mount.RootPath,
            resolved.RelativePath,
            resolved.Mount.ResolvePhysicalPath(resolved.RelativePath));

    private ResolvedPath ResolveDirectory(string path) => Resolve(path, allowMountRoot: true);

    private ResolvedPath Resolve(string path, bool allowMountRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathFullyQualified(path))
        {
            return ResolveApprovedAbsolutePath(path, allowMountRoot);
        }

        var normalized = path.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or ".." || segment.Contains(':', StringComparison.Ordinal)))
        {
            throw new ArgumentException("File paths must stay beneath an approved workstation mount.", nameof(path));
        }

        if (!_mounts.TryGetValue(segments[0], out var mount))
        {
            var exampleMount = _mounts.ContainsKey("Workspace")
                ? "Workspace"
                : _mounts.Keys.Order(StringComparer.Ordinal).First();
            throw new ArgumentException(
                $"Unknown workstation mount '{segments[0]}'. Retry with a virtual path beginning with one of: "
                + $"{string.Join(", ", _mounts.Keys)}. For example, use {exampleMount}/touch.txt; "
                + "do not ask the user for an absolute path.",
                nameof(path));
        }

        var relativePath = string.Join('/', segments.Skip(1));
        if (!allowMountRoot && relativePath.Length == 0)
        {
            throw new ArgumentException("A file name is required beneath the workstation mount.", nameof(path));
        }

        return new ResolvedPath(mount, relativePath);
    }

    private ResolvedPath ResolveApprovedAbsolutePath(string path, bool allowMountRoot)
    {
        string candidate;
        try
        {
            candidate = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The file path was not valid.", nameof(path), ex);
        }

        foreach (var mount in _mounts.Values.OrderByDescending(item => item.RootPath.Length))
        {
            var root = Path.TrimEndingDirectorySeparator(mount.RootPath);
            var beneathRoot = candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!beneathRoot)
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(root, candidate).Replace('\\', '/');
            if (relativePath == ".")
            {
                relativePath = string.Empty;
            }

            if (!allowMountRoot && relativePath.Length == 0)
            {
                throw new ArgumentException("A file name is required beneath the workstation mount.", nameof(path));
            }

            return new ResolvedPath(mount, relativePath);
        }

        throw new ArgumentException(
            "Absolute paths are accepted only when they resolve inside an approved workstation folder. "
            + $"Retry with a virtual path beginning with one of: {string.Join(", ", _mounts.Keys)}.",
            nameof(path));
    }

    private static string NormalizeMountName(string name)
    {
        var normalized = name.Trim().Replace(' ', '-');
        if (normalized is "." or ".."
            || normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid workstation file mount name '{name}'.", nameof(name));
        }

        return normalized;
    }

    private sealed class MountedStore
    {
        public MountedStore(string name, string rootPath)
        {
            Name = name;
            RootPath = Path.GetFullPath(rootPath);
            Directory.CreateDirectory(RootPath);
            Store = new FileSystemAgentFileStore(RootPath);
        }

        public string Name { get; }

        public string RootPath { get; }

        public FileSystemAgentFileStore Store { get; }

        public string ResolvePhysicalPath(string relativePath)
        {
            var root = Path.EndsInDirectorySeparator(RootPath)
                ? RootPath
                : RootPath + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(
                Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The resolved file path escaped its approved mount.", nameof(relativePath));
            }

            return candidate;
        }
    }

    private sealed record ResolvedPath(MountedStore Mount, string RelativePath);
}
