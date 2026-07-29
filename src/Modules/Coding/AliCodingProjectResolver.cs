using Ali.Modules.WorkstationFiles;

namespace Ali.Modules.Coding;

internal sealed record AliResolvedCodingProject(
    string VirtualPath,
    string PhysicalPath,
    string MountRoot,
    string ProjectDirectory);

internal sealed record AliResolvedCodingTarget(
    string VirtualPath,
    string PhysicalPath,
    string MountRoot,
    string RootDirectory,
    bool IsSolution);

internal sealed class AliCodingProjectResolver(AliWorkstationFileAccess fileAccess)
{
    public AliResolvedCodingProject ResolveExistingProject(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var resolved = fileAccess.ResolvePhysicalFilePath(projectPath);
        var physicalTarget = resolved.PhysicalPath;
        if (Directory.Exists(physicalTarget))
        {
            RejectReparsePoints(resolved.MountRoot, physicalTarget);
            physicalTarget = ResolveSingleProject(physicalTarget);
        }

        if (!Path.GetExtension(physicalTarget).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The coding tools accept only an approved .csproj path.", nameof(projectPath));
        }

        if (!File.Exists(physicalTarget))
        {
            throw new FileNotFoundException("The requested .csproj file does not exist.", physicalTarget);
        }

        RejectReparsePoints(resolved.MountRoot, physicalTarget);
        return new AliResolvedCodingProject(
            projectPath,
            physicalTarget,
            resolved.MountRoot,
            Path.GetDirectoryName(physicalTarget)!);
    }

    public AliResolvedCodingTarget ResolveExistingTarget(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var resolved = fileAccess.ResolvePhysicalFilePath(targetPath);
        var physicalTarget = resolved.PhysicalPath;
        if (Directory.Exists(physicalTarget))
        {
            RejectReparsePoints(resolved.MountRoot, physicalTarget);
            physicalTarget = ResolveSingleSolutionOrProject(physicalTarget);
        }

        var extension = Path.GetExtension(physicalTarget);
        var isSolution = extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
        if (!isSolution && !extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Roslyn accepts an approved .csproj, .sln, or .slnx path.", nameof(targetPath));
        }

        if (!File.Exists(physicalTarget))
        {
            throw new FileNotFoundException("The requested Roslyn target does not exist.", physicalTarget);
        }

        RejectReparsePoints(resolved.MountRoot, physicalTarget);
        return new AliResolvedCodingTarget(
            targetPath,
            physicalTarget,
            resolved.MountRoot,
            Path.GetDirectoryName(physicalTarget)!,
            isSolution);
    }

    private static string ResolveSingleProject(string directory)
    {
        var projects = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return projects.Length switch
        {
            1 => projects[0],
            0 => throw new FileNotFoundException("The coding folder does not contain a .csproj file.", directory),
            _ => throw AmbiguousTarget(directory, projects)
        };
    }

    private static string ResolveSingleSolutionOrProject(string directory)
    {
        var solutions = Directory.EnumerateFiles(directory, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(directory, "*.slnx", SearchOption.TopDirectoryOnly))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (solutions.Length == 1) return solutions[0];
        if (solutions.Length > 1) throw AmbiguousTarget(directory, solutions);
        return ResolveSingleProject(directory);
    }

    private static ArgumentException AmbiguousTarget(string directory, IReadOnlyList<string> targets) =>
        new(
            $"The coding folder contains multiple .NET targets ({string.Join(", ", targets.Select(Path.GetFileName))}). Supply the desired project or solution path.",
            nameof(directory));

    public string ResolveDocument(AliResolvedCodingProject project, string documentPath)
        => ResolveDocument(project.MountRoot, project.ProjectDirectory, documentPath);

    public string ResolveDocument(AliResolvedCodingTarget target, string documentPath)
        => ResolveDocument(target.MountRoot, target.RootDirectory, documentPath);

    private string ResolveDocument(string mountRoot, string targetRoot, string documentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        var resolved = fileAccess.ResolvePhysicalFilePath(documentPath);
        var fullPath = Path.GetFullPath(resolved.PhysicalPath);
        var projectRoot = Path.TrimEndingDirectorySeparator(targetRoot) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The requested document is outside the approved project folder.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The requested project document does not exist.", fullPath);
        }

        RejectReparsePoints(mountRoot, fullPath);
        return fullPath;
    }

    public static void RejectReparsePoints(string mountRoot, string physicalPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mountRoot));
        var current = Directory.Exists(physicalPath)
            ? new DirectoryInfo(physicalPath)
            : File.Exists(physicalPath)
                ? new FileInfo(physicalPath).Directory
                : new DirectoryInfo(Path.GetDirectoryName(physicalPath)!);
        while (current is not null && !current.FullName.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Coding projects reached through a reparse point are not accessible by Ali.");
            }

            current = current.Parent;
        }

        if (current is null)
        {
            throw new InvalidOperationException("The coding project escaped its approved workstation mount.");
        }

        if (File.Exists(physicalPath) && (File.GetAttributes(physicalPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("A reparse-point project file is not accessible by Ali.");
        }
    }
}
