using System.Security.Cryptography;
using Ali.Modules.Coding.Changesets;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Coding.RoslynActions;

/// <summary>
/// Owns one exact, empty verification directory and deletes only the tree paired
/// with its unguessable sibling ownership marker. Cleanup never follows reparse points.
/// </summary>
internal sealed class AliRoslynStagedDirectory : IDisposable
{
    private const string DirectoryPrefix = "Ali-Roslyn-Verify-";
    private const string MarkerSuffix = ".owner";
    private const int OwnershipTokenBytes = 32;

    private readonly string _markerPath;
    private readonly byte[] _ownershipToken;
    private int _disposed;

    private AliRoslynStagedDirectory(
        string path,
        string markerPath,
        byte[] ownershipToken)
    {
        Path = path;
        _markerPath = markerPath;
        _ownershipToken = ownershipToken;
    }

    internal string Path { get; }
    internal string MarkerPath => _markerPath;

    internal static AliRoslynStagedDirectory Create()
    {
        var tempRoot = System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
        var name = DirectoryPrefix + Guid.NewGuid().ToString("N");
        var path = System.IO.Path.Combine(tempRoot, name);
        var markerPath = path + MarkerSuffix;
        var token = RandomNumberGenerator.GetBytes(OwnershipTokenBytes);
        var createdDirectory = false;
        try
        {
            if (Directory.Exists(path) || File.Exists(markerPath))
            {
                throw new IOException("The exact Roslyn verification directory already exists.");
            }

            Directory.CreateDirectory(path);
            createdDirectory = true;
            WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                path,
                "The exact Roslyn verification directory is not a regular local directory.");
            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                throw new IOException("The exact Roslyn verification directory was not created empty.");
            }

            using (var marker = WindowsOrchestrationFileBoundary.OpenRegularFile(
                       markerPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       writeThrough: true,
                       "The exact Roslyn verification ownership marker is not a regular local file."))
            {
                marker.Write(token);
                marker.Flush(flushToDisk: true);
            }

            return new(path, markerPath, token);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(token);
            if (createdDirectory)
            {
                TryDeleteEmptyRegularDirectory(path);
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            TryDeleteOwnedTree();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(_ownershipToken);
        }
    }

    private void TryDeleteOwnedTree()
    {
        if (!HasExactOwnedShape()
            || !OwnershipMarkerMatches())
        {
            return;
        }

        try
        {
            if (Directory.Exists(Path))
            {
                WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                    Path,
                    "The exact Roslyn verification directory is not a regular local directory.");
                var (files, directories) = EnumerateRegularTree(Path);
                foreach (var file in files)
                {
                    WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                        file,
                        "A Roslyn verification artifact is not a regular local file.");
                }

                foreach (var directory in directories.OrderByDescending(
                             value => value.Count(character => character == System.IO.Path.DirectorySeparatorChar)))
                {
                    WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                        directory,
                        "A Roslyn verification directory is not a regular local directory.");
                    Directory.Delete(directory, recursive: false);
                }
            }

            WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                _markerPath,
                "The exact Roslyn verification ownership marker is not a regular local file.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The staged tree is non-authoritative. A failed exact cleanup is safer than
            // broadening the deletion target or following an unexpected filesystem entry.
        }
    }

    private bool HasExactOwnedShape()
    {
        try
        {
            var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Path));
            var temp = System.IO.Path.TrimEndingDirectorySeparator(
                System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            var name = System.IO.Path.GetFileName(root);
            var token = name.StartsWith(DirectoryPrefix, StringComparison.Ordinal)
                ? name[DirectoryPrefix.Length..]
                : string.Empty;
            return PathComparer.Equals(System.IO.Path.GetDirectoryName(root), temp)
                && token.Length == 32
                && token.All(char.IsAsciiHexDigit)
                && PathComparer.Equals(_markerPath, root + MarkerSuffix);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private bool OwnershipMarkerMatches()
    {
        try
        {
            using var marker = WindowsOrchestrationFileBoundary.OpenRegularFile(
                _markerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                writeThrough: false,
                "The exact Roslyn verification ownership marker is not a regular local file.");
            if (marker.Length != OwnershipTokenBytes)
            {
                return false;
            }

            Span<byte> observed = stackalloc byte[OwnershipTokenBytes];
            marker.ReadExactly(observed);
            return CryptographicOperations.FixedTimeEquals(observed, _ownershipToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static (IReadOnlyList<string> Files, IReadOnlyList<string> Directories) EnumerateRegularTree(
        string root)
    {
        var files = new List<string>();
        var directories = new List<string> { root };
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (files.Count + directories.Count
                    >= AliSourceTreeMaterializationPolicy.SafeDefault.MaximumEntries)
                {
                    throw new IOException("The exact Roslyn verification tree exceeded its cleanup bound.");
                }

                var full = System.IO.Path.GetFullPath(entry);
                if (!full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, PathComparison))
                {
                    throw new IOException("A Roslyn verification artifact escaped its exact temporary root.");
                }

                var attributes = File.GetAttributes(full);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new IOException("A Roslyn verification artifact is a reparse point or device.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(full);
                    pending.Push(full);
                }
                else
                {
                    files.Add(full);
                }
            }
        }

        return (files, directories);
    }

    private static void TryDeleteEmptyRegularDirectory(string path)
    {
        try
        {
            WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                path,
                "The exact Roslyn verification directory is not a regular local directory.");
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Never broaden cleanup after a failed exact create.
        }
    }

    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
