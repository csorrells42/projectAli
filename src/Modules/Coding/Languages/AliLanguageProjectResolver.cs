using Ali.Modules.WorkstationFiles;

namespace Ali.Modules.Coding.Languages;

internal sealed record AliResolvedLanguageProject(
    string VirtualPath,
    string PhysicalPath,
    string MountRoot,
    string ProjectDirectory,
    string ManifestName,
    AliProgrammingLanguage Language);

internal sealed class AliLanguageProjectResolver(AliWorkstationFileAccess fileAccess)
{
    private static readonly Dictionary<string, AliProgrammingLanguage> ManifestLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pyproject.toml"] = AliProgrammingLanguage.Python,
        ["requirements.txt"] = AliProgrammingLanguage.Python,
        ["setup.py"] = AliProgrammingLanguage.Python,
        ["setup.cfg"] = AliProgrammingLanguage.Python,
        ["package.json"] = AliProgrammingLanguage.JavaScript,
        ["tsconfig.json"] = AliProgrammingLanguage.TypeScript,
        ["jsconfig.json"] = AliProgrammingLanguage.JavaScript,
        ["pom.xml"] = AliProgrammingLanguage.Java,
        ["build.gradle"] = AliProgrammingLanguage.Java,
        ["build.gradle.kts"] = AliProgrammingLanguage.Java,
        ["settings.gradle"] = AliProgrammingLanguage.Java,
        ["settings.gradle.kts"] = AliProgrammingLanguage.Java,
        ["cmakelists.txt"] = AliProgrammingLanguage.Cpp,
        ["conanfile.py"] = AliProgrammingLanguage.Cpp,
        ["conanfile.txt"] = AliProgrammingLanguage.Cpp,
        ["vcpkg.json"] = AliProgrammingLanguage.Cpp,
        ["sketch.yaml"] = AliProgrammingLanguage.Arduino
    };

    public AliResolvedLanguageProject Resolve(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var resolved = fileAccess.ResolvePhysicalFilePath(targetPath);
        var physicalTarget = resolved.PhysicalPath;
        if (Directory.Exists(physicalTarget))
        {
            AliCodingProjectResolver.RejectReparsePoints(resolved.MountRoot, physicalTarget);
            physicalTarget = ResolveDirectoryManifest(physicalTarget);
        }

        if (!File.Exists(physicalTarget))
        {
            throw new FileNotFoundException("The requested coding target does not exist.", physicalTarget);
        }

        AliCodingProjectResolver.RejectReparsePoints(resolved.MountRoot, physicalTarget);
        var language = DetectLanguage(physicalTarget);
        if (language == AliProgrammingLanguage.Unknown)
        {
            throw new ArgumentException(
                "The target must be a recognized project manifest, solution, or source document.",
                nameof(targetPath));
        }

        var projectDirectory = Path.GetDirectoryName(physicalTarget)!;
        var manifest = FindContainingManifest(resolved.MountRoot, projectDirectory, language);
        if (manifest is not null)
        {
            projectDirectory = Path.GetDirectoryName(manifest)!;
        }
        else
        {
            manifest = physicalTarget;
        }

        return new AliResolvedLanguageProject(
            targetPath,
            physicalTarget,
            resolved.MountRoot,
            projectDirectory,
            Path.GetFileName(manifest),
            language);
    }

    private static string ResolveDirectoryManifest(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
        var priorityGroups = new[]
        {
            files.Where(path => Path.GetExtension(path) is ".sln" or ".slnx"),
            files.Where(path => Path.GetExtension(path) is ".csproj" or ".vcxproj"),
            files.Where(path => ManifestLanguages.ContainsKey(Path.GetFileName(path))),
            files.Where(path => Path.GetExtension(path).Equals(".ino", StringComparison.OrdinalIgnoreCase))
        };

        foreach (var group in priorityGroups)
        {
            var candidates = group
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (candidates.Length == 1)
            {
                return candidates[0];
            }
            if (candidates.Length > 1)
            {
                throw new ArgumentException(
                    $"The coding folder contains multiple project targets ({string.Join(", ", candidates.Select(Path.GetFileName))}). Supply the desired manifest path.",
                    nameof(directory));
            }
        }

        throw new FileNotFoundException(
            "The coding folder does not contain a recognized project manifest.",
            directory);
    }

    public string ResolveDocument(AliResolvedLanguageProject project, string documentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        var resolved = fileAccess.ResolvePhysicalFilePath(documentPath);
        var fullPath = Path.GetFullPath(resolved.PhysicalPath);
        var root = Path.TrimEndingDirectorySeparator(project.ProjectDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The requested source document is outside the approved project folder.");
        }
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The requested source document does not exist.", fullPath);
        }

        AliCodingProjectResolver.RejectReparsePoints(project.MountRoot, fullPath);
        return fullPath;
    }

    internal static AliProgrammingLanguage DetectLanguage(string path)
    {
        var fileName = Path.GetFileName(path);
        if (ManifestLanguages.TryGetValue(fileName, out var manifestLanguage))
        {
            return manifestLanguage;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".cs" or ".csproj" or ".sln" or ".slnx" => AliProgrammingLanguage.CSharp,
            ".py" or ".pyi" => AliProgrammingLanguage.Python,
            ".js" or ".jsx" or ".mjs" or ".cjs" => AliProgrammingLanguage.JavaScript,
            ".ts" or ".tsx" or ".mts" or ".cts" => AliProgrammingLanguage.TypeScript,
            ".html" or ".htm" => AliProgrammingLanguage.Html,
            ".css" or ".scss" or ".sass" or ".less" => AliProgrammingLanguage.Css,
            ".java" => AliProgrammingLanguage.Java,
            ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hh" or ".hpp" or ".hxx" or ".ixx" or ".cppm" or ".vcxproj" => AliProgrammingLanguage.Cpp,
            ".ino" => AliProgrammingLanguage.Arduino,
            _ => AliProgrammingLanguage.Unknown
        };
    }

    private static string? FindContainingManifest(string mountRoot, string startDirectory, AliProgrammingLanguage language)
    {
        var candidates = ManifestLanguages
            .Where(pair => IsCompatible(language, pair.Value))
            .Select(pair => pair.Key)
            .Concat(language == AliProgrammingLanguage.CSharp ? ["*.csproj", "*.sln", "*.slnx"] : [])
            .ToArray();
        var mount = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mountRoot));
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            foreach (var candidate in candidates)
            {
                var match = candidate.Contains('*')
                    ? Directory.EnumerateFiles(current.FullName, candidate, SearchOption.TopDirectoryOnly).FirstOrDefault()
                    : Path.Combine(current.FullName, candidate);
                if (match is not null && File.Exists(match))
                {
                    return match;
                }
            }

            if (Path.TrimEndingDirectorySeparator(current.FullName).Equals(mount, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = current.Parent;
        }

        return null;
    }

    private static bool IsCompatible(AliProgrammingLanguage requested, AliProgrammingLanguage manifest) =>
        requested == manifest
        || requested is AliProgrammingLanguage.Html or AliProgrammingLanguage.Css or AliProgrammingLanguage.TypeScript
            && manifest is AliProgrammingLanguage.JavaScript or AliProgrammingLanguage.TypeScript;
}
