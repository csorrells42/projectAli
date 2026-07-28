using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Ali.Modules.Coding;

/// <summary>
/// Tracks the initial SDK-template files for projects created during the current Ali
/// session. A successful build of an untouched template is not completion of the
/// user's requested application.
/// </summary>
internal sealed class AliCodingProjectTracker
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _scaffolds =
        new(StringComparer.OrdinalIgnoreCase);

    public void RecordScaffold(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var fullProjectPath = Path.GetFullPath(projectPath);
        _scaffolds[fullProjectPath] = CaptureProjectFiles(fullProjectPath);
    }

    public CodingProjectChangeStatus CheckImplementationChanges(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var fullProjectPath = Path.GetFullPath(projectPath);
        if (!_scaffolds.TryGetValue(fullProjectPath, out var baseline))
        {
            return new CodingProjectChangeStatus(true, false, "This existing project was not created from a template during the current session.");
        }

        var current = CaptureProjectFiles(fullProjectPath);
        var changed = current.Count != baseline.Count
            || current.Any(item => !baseline.TryGetValue(item.Key, out var hash)
                || !hash.Equals(item.Value, StringComparison.Ordinal));
        return changed
            ? new CodingProjectChangeStatus(true, true, "Requested source changes were detected after project creation.")
            : new CodingProjectChangeStatus(
                false,
                true,
                "The project is still the untouched SDK template. Write the requested application source files before building or running it.");
    }

    private static IReadOnlyDictionary<string, string> CaptureProjectFiles(string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        if (!Directory.Exists(projectDirectory))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(projectDirectory, path))
            .Where(path => IsTrackedExtension(Path.GetExtension(path)))
            .ToDictionary(
                path => Path.GetRelativePath(projectDirectory, path),
                ComputeHash,
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsBuildOutput(string projectDirectory, string path)
    {
        var relative = Path.GetRelativePath(projectDirectory, path);
        return relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrackedExtension(string extension) =>
        extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase);

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

internal sealed record CodingProjectChangeStatus(bool HasImplementationChanges, bool IsSessionScaffold, string Detail);
