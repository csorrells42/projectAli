using Ali.Modules.WorkstationFiles;

namespace Ali.Modules.Coding;

internal sealed record AliResolvedCodingProject(
    string VirtualPath,
    string PhysicalPath,
    string MountRoot,
    string ProjectDirectory);

internal sealed class AliCodingProjectResolver(AliWorkstationFileAccess fileAccess)
{
    public AliResolvedCodingProject ResolveExistingProject(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var resolved = fileAccess.ResolvePhysicalFilePath(projectPath);
        if (!Path.GetExtension(resolved.PhysicalPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The coding tools accept only an approved .csproj path.", nameof(projectPath));
        }

        if (!File.Exists(resolved.PhysicalPath))
        {
            throw new FileNotFoundException("The requested .csproj file does not exist.", resolved.PhysicalPath);
        }

        RejectReparsePoints(resolved.MountRoot, resolved.PhysicalPath);
        return new AliResolvedCodingProject(
            projectPath,
            resolved.PhysicalPath,
            resolved.MountRoot,
            Path.GetDirectoryName(resolved.PhysicalPath)!);
    }

    public string ResolveDocument(AliResolvedCodingProject project, string documentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        var resolved = fileAccess.ResolvePhysicalFilePath(documentPath);
        var fullPath = Path.GetFullPath(resolved.PhysicalPath);
        var projectRoot = Path.TrimEndingDirectorySeparator(project.ProjectDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The requested document is outside the approved project folder.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The requested project document does not exist.", fullPath);
        }

        RejectReparsePoints(project.MountRoot, fullPath);
        return fullPath;
    }

    public static void RejectReparsePoints(string mountRoot, string physicalPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mountRoot));
        var current = File.Exists(physicalPath)
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
