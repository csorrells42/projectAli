using System.Security.Cryptography;
using System.Text;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Work;

namespace Ali.Modules.Coding.SourceControl;

internal sealed record AliGitRepositoryLayout(
    string MountRoot,
    string RepositoryRoot,
    string WorktreeGitDirectory,
    string CommonGitDirectory)
{
    private const int MaximumPointerBytes = 4096;

    internal static AliGitRepositoryLayout Resolve(AliResolvedCodingTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var mountRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.MountRoot));
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.RootDirectory));
        while (IsWithin(mountRoot, current))
        {
            WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                current,
                "A Git repository path contains a reparse point or non-directory entry.");
            var marker = Path.Combine(current, ".git");
            if (TryGetAttributes(marker, out var markerAttributes))
            {
                if ((markerAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException(
                        "The Git metadata marker is a reparse point or device entry.");
                }
                if ((markerAttributes & FileAttributes.Directory) != 0)
                {
                    WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                        marker,
                        "The Git metadata directory is not a regular local directory.");
                    return Create(mountRoot, current, marker);
                }
                var pointer = ReadSmallRegularText(
                    marker,
                    "The Git worktree pointer is not a regular local file.",
                    mountRoot);
                const string prefix = "gitdir:";
                if (!pointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The Git worktree pointer does not contain an exact gitdir entry.");
                }
                var value = pointer[prefix.Length..].Trim();
                if (string.IsNullOrWhiteSpace(value)
                    || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
                {
                    throw new InvalidDataException("The Git worktree pointer is invalid.");
                }
                var worktreeGitDirectory = Path.GetFullPath(
                    Path.IsPathRooted(value)
                        ? value
                        : Path.Combine(current, value));
                if (!IsWithin(mountRoot, worktreeGitDirectory))
                {
                    throw new InvalidDataException(
                        "The Git worktree metadata pointer leaves the approved mount.");
                }
                WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                    worktreeGitDirectory,
                    "The Git worktree metadata target is not a regular local directory.");
                return Create(mountRoot, current, worktreeGitDirectory);
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || PathsEqual(parent, current))
            {
                break;
            }
            current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        }

        throw new DirectoryNotFoundException(
            "No regular Git repository contains the approved coding target.");
    }

    private static AliGitRepositoryLayout Create(
        string mountRoot,
        string repositoryRoot,
        string worktreeGitDirectory)
    {
        var commonDirectory = worktreeGitDirectory;
        var commonPointer = Path.Combine(worktreeGitDirectory, "commondir");
        if (TryGetAttributes(commonPointer, out var commonAttributes))
        {
            if ((commonAttributes & (FileAttributes.Directory
                                     | FileAttributes.ReparsePoint
                                     | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException(
                    "The Git common-directory pointer is not a regular local file.");
            }
            var value = ReadSmallRegularText(
                    commonPointer,
                    "The Git common-directory pointer is not a regular local file.",
                    mountRoot)
                .Trim();
            if (string.IsNullOrWhiteSpace(value)
                || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                throw new InvalidDataException(
                    "The Git common-directory pointer is invalid.");
            }
            commonDirectory = Path.GetFullPath(
                Path.IsPathRooted(value)
                    ? value
                    : Path.Combine(worktreeGitDirectory, value));
        }

        if (!IsWithin(mountRoot, worktreeGitDirectory)
            || !IsWithin(mountRoot, commonDirectory))
        {
            throw new InvalidDataException(
                "The Git metadata layout leaves the approved mount.");
        }

        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            commonDirectory,
            "The Git common metadata target is not a regular local directory.");
        return new AliGitRepositoryLayout(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(mountRoot)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreeGitDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(commonDirectory)));
    }

    private static string ReadSmallRegularText(
        string path,
        string invalidMessage,
        string allowedHardLinkRoot)
    {
        using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            invalidMessage);
        var fileIdentity = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
            stream,
            path,
            invalidMessage,
            allowedHardLinkRoot);
        if (stream.Length < 1 || stream.Length > MaximumPointerBytes)
        {
            throw new InvalidDataException(invalidMessage);
        }
        var length = checked((int)stream.Length);
        var bytes = new byte[length];
        try
        {
            stream.ReadExactly(bytes);
            if (stream.Length != length)
            {
                throw new InvalidDataException(
                    "A Git metadata pointer changed while it was captured.");
            }
            var repeatedIdentity = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
                stream,
                path,
                invalidMessage,
                allowedHardLinkRoot);
            if (!string.Equals(
                    fileIdentity.CanonicalIdentity,
                    repeatedIdentity.CanonicalIdentity,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A Git metadata pointer changed file identity while it was captured.");
            }
            return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        if (PathsEqual(root, candidate))
        {
            return true;
        }
        var prefix = root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(
            prefix,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}

/// <summary>
/// Captures the exact local inputs visible to the fixed Git operations. Repository content and
/// metadata are bounded and opened without following reparse points. A repository too large to
/// prove within those bounds is rejected instead of receiving a weaker identity.
/// </summary>
internal static class AliGitRepositoryStateCapture
{
    private const int MaximumWorktreeFiles = 25_000;
    private const int MaximumMetadataFiles = 50_000;
    private const long MaximumWorktreeBytes = 1024L * 1024 * 1024;
    private const long MaximumMetadataBytes = 512L * 1024 * 1024;
    private const long MaximumFileBytes = 256L * 1024 * 1024;

    internal static TargetStateSnapshot Capture(
        AliGitRepositoryLayout repository,
        AliGitEffectiveInputBinding effectiveInputs)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(effectiveInputs);
        var headPath = Path.Combine(repository.WorktreeGitDirectory, "HEAD");
        var indexPath = Path.Combine(repository.WorktreeGitDirectory, "index");
        var refsPath = Path.Combine(repository.CommonGitDirectory, "refs");
        var reftablePath = Path.Combine(repository.CommonGitDirectory, "reftable");
        var hooksPath = Path.Combine(repository.CommonGitDirectory, "hooks");
        var headMaterial = OptionalFile(headPath, repository.MountRoot);
        var refsMaterial = JoinDigests(
            TreeOrAbsent(
                refsPath,
                MaximumMetadataFiles,
                MaximumMetadataBytes,
                repository.MountRoot),
            OptionalFile(
                Path.Combine(repository.CommonGitDirectory, "packed-refs"),
                repository.MountRoot),
            TreeOrAbsent(
                reftablePath,
                MaximumMetadataFiles,
                MaximumMetadataBytes,
                repository.MountRoot));
        var layoutMaterial = HashText(string.Join(
            "\0",
            NormalizePath(repository.MountRoot),
            NormalizePath(repository.RepositoryRoot),
            NormalizePath(repository.WorktreeGitDirectory),
            NormalizePath(repository.CommonGitDirectory)));
        var versions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["git-repository-layout-v2"] = layoutMaterial,
            ["git-head-v1"] = HashText(headMaterial + "\0" + refsMaterial),
            ["git-branch-v1"] = HashText(headMaterial),
            ["git-refs-v1"] = refsMaterial,
            ["git-index-v1"] = OptionalFile(indexPath, repository.MountRoot),
            ["git-worktree-v1"] = HashTree(
                repository.RepositoryRoot,
                Path.Combine(repository.RepositoryRoot, ".git"),
                MaximumWorktreeFiles,
                MaximumWorktreeBytes,
                repository.MountRoot),
            ["git-config-v2"] = effectiveInputs.ConfigurationDigest,
            ["git-helpers-v1"] = effectiveInputs.HelperDigest,
            ["git-remote-v1"] = effectiveInputs.RemoteDigest,
            ["git-hooks-v1"] = TreeOrAbsent(
                hooksPath,
                MaximumMetadataFiles,
                MaximumMetadataBytes,
                repository.MountRoot)
        };
        return new TargetStateSnapshot(
            versions,
            new Dictionary<string, string>(versions, StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static string TreeOrAbsent(
        string path,
        int maximumFiles,
        long maximumBytes,
        string allowedHardLinkRoot)
    {
        if (!TryGetAttributes(path, out var attributes))
        {
            return HashText("absent-directory");
        }
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                "A Git metadata directory path is occupied by a non-directory entry.");
        }
        return HashTree(
            path,
            excludedPath: null,
            maximumFiles,
            maximumBytes,
            allowedHardLinkRoot);
    }

    private static string HashTree(
        string root,
        string? excludedPath,
        int maximumFiles,
        long maximumBytes,
        string allowedHardLinkRoot)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonicalExcluded = excludedPath is null
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(excludedPath));
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            canonicalRoot,
            "A Git state root contains a reparse point or non-directory entry.");
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pending = new Stack<string>();
        pending.Push(canonicalRoot);
        var fileCount = 0;
        var directoryCount = 0;
        long byteCount = 0;

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                directory,
                "A Git state directory changed into a reparse point or non-directory entry.");
            directoryCount = checked(directoryCount + 1);
            if (directoryCount > maximumFiles)
            {
                throw new InvalidDataException(
                    "The Git state exceeds its fixed directory-count bound.");
            }
            var relativeDirectory = Path.GetRelativePath(canonicalRoot, directory)
                .Replace('\\', '/');
            Append(aggregate, "d\0" + relativeDirectory);
            var children = Directory.EnumerateFileSystemEntries(directory)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var childDirectories = new List<string>();
            foreach (var child in children)
            {
                var fullChild = Path.GetFullPath(child);
                if (canonicalExcluded is not null && PathsEqual(fullChild, canonicalExcluded))
                {
                    continue;
                }
                var attributes = File.GetAttributes(fullChild);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException(
                        "The Git state contains a reparse point or device entry.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    childDirectories.Add(fullChild);
                    continue;
                }

                fileCount = checked(fileCount + 1);
                if (fileCount > maximumFiles)
                {
                    throw new InvalidDataException(
                        "The Git state exceeds its fixed file-count bound.");
                }
                var file = HashRegularFile(fullChild, allowedHardLinkRoot);
                byteCount = checked(byteCount + file.Length);
                if (byteCount > maximumBytes)
                {
                    throw new InvalidDataException(
                        "The Git state exceeds its fixed aggregate-size bound.");
                }
                var relative = Path.GetRelativePath(canonicalRoot, fullChild)
                    .Replace('\\', '/');
                Append(aggregate, string.Join(
                    "\0",
                    "f",
                    relative,
                    file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    file.Digest));
            }

            for (var index = childDirectories.Count - 1; index >= 0; index--)
            {
                pending.Push(childDirectories[index]);
            }
        }

        Append(aggregate, fileCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(aggregate, directoryCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(aggregate, byteCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var hash = aggregate.GetHashAndReset();
        try
        {
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static string OptionalFile(string path, string allowedHardLinkRoot)
    {
        if (!TryGetAttributes(path, out var attributes))
        {
            return HashText("absent-file");
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException(
                "A Git metadata file path is occupied by a directory.");
        }
        var file = HashRegularFile(path, allowedHardLinkRoot);
        return HashText(string.Join(
            "\0",
            "file",
            file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            file.Digest));
    }

    private static (long Length, string Digest) HashRegularFile(
        string path,
        string allowedHardLinkRoot)
    {
        using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            "A Git state file is not a regular local file.");
        var fileIdentity = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
            stream,
            path,
            "A Git state file does not have an authorized stable file identity.",
            allowedHardLinkRoot);
        var length = stream.Length;
        if (length < 0 || length > MaximumFileBytes)
        {
            throw new InvalidDataException(
                "A Git state file exceeds its fixed size bound.");
        }
        var digest = SHA256.HashData(stream);
        try
        {
            if (stream.Position != length || stream.Length != length)
            {
                throw new InvalidDataException(
                    "A Git state file changed while its exact digest was captured.");
            }
            var repeatedIdentity = WindowsOrchestrationFileBoundary.CaptureRegularFileIdentity(
                stream,
                path,
                "A Git state file does not have an authorized stable file identity.",
                allowedHardLinkRoot);
            if (!string.Equals(
                    fileIdentity.CanonicalIdentity,
                    repeatedIdentity.CanonicalIdentity,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A Git state file changed identity while its exact digest was captured.");
            }
            return (length, HashText(string.Join(
                "\0",
                fileIdentity.CanonicalIdentity,
                Convert.ToHexString(digest).ToLowerInvariant())));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static string JoinDigests(params string[] values) =>
        HashText(string.Join("\0", values));

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            hash.AppendData(bytes);
            hash.AppendData([0]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string NormalizePath(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
