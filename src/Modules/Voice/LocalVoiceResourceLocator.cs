namespace Ali.Modules.Voice;

public static class LocalVoiceResourceLocator
{
    private const string VoiceRootEnvironmentVariable = "ALI_VOICE_ROOT";

    public static string? FindVoiceRoot(string appBaseDirectory, string? searchRoot = null)
    {
        foreach (var candidate in EnumerateVoiceRootCandidates(appBaseDirectory, searchRoot))
        {
            if (IsVoiceResourceRoot(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    public static string? FindPiperExecutable(string appBaseDirectory, string? searchRoot = null)
    {
        var voiceRoot = FindVoiceRoot(appBaseDirectory, searchRoot);
        var candidate = voiceRoot is null ? null : Path.Combine(voiceRoot, "python-venv", "Scripts", "piper.exe");
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    public static string? FindPythonExecutable(string appBaseDirectory, string? searchRoot = null)
    {
        var voiceRoot = FindVoiceRoot(appBaseDirectory, searchRoot);
        var candidate = voiceRoot is null ? null : Path.Combine(voiceRoot, "python-venv", "Scripts", "python.exe");
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    public static string? FindPiperVoiceDirectory(string appBaseDirectory, string? searchRoot = null)
    {
        foreach (var voiceRoot in EnumerateVoiceRootCandidates(appBaseDirectory, searchRoot))
        {
            var candidate = Path.Combine(voiceRoot, "piper");
            if (IsPiperVoiceDirectory(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    public static string? FindWhisperPythonExecutable(string appBaseDirectory, string? searchRoot = null)
    {
        return FindPythonExecutable(appBaseDirectory, searchRoot);
    }

    public static string? FindWhisperModelRoot(string appBaseDirectory, string? searchRoot = null)
    {
        var voiceRoot = FindVoiceRoot(appBaseDirectory, searchRoot);
        var candidate = voiceRoot is null ? null : Path.Combine(voiceRoot, "whisper");
        return Directory.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    public static string? FindWhisperScript(string appBaseDirectory, string? searchRoot = null)
    {
        var voiceRoot = FindVoiceRoot(appBaseDirectory, searchRoot);
        var installedCandidate = voiceRoot is null ? null : Path.Combine(voiceRoot, "local_whisper_stt.py");
        if (File.Exists(installedCandidate))
        {
            return Path.GetFullPath(installedCandidate);
        }

        var repoRoot = TryGetRepositoryRootFromVoiceRoot(voiceRoot);
        var candidate = repoRoot is null ? null : Path.Combine(repoRoot, "src", "Modules", "Voice", "Tools", "local_whisper_stt.py");
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    public static string? FindKittenPythonExecutable(string appBaseDirectory, string? searchRoot = null)
    {
        return FindPythonExecutable(appBaseDirectory, searchRoot);
    }

    public static string? FindKittenModelRoot(string appBaseDirectory, string? searchRoot = null)
    {
        var voiceRoot = FindVoiceRoot(appBaseDirectory, searchRoot);
        var candidate = voiceRoot is null ? null : Path.Combine(voiceRoot, "kitten");
        return Directory.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    public static string? FindKittenScript(string appBaseDirectory, string? searchRoot = null)
    {
        var voiceRoot = FindVoiceRoot(appBaseDirectory, searchRoot);
        var installedCandidate = voiceRoot is null ? null : Path.Combine(voiceRoot, "local_kitten_tts.py");
        if (File.Exists(installedCandidate))
        {
            return Path.GetFullPath(installedCandidate);
        }

        var repoRoot = TryGetRepositoryRootFromVoiceRoot(voiceRoot);
        var candidate = repoRoot is null ? null : Path.Combine(repoRoot, "src", "Modules", "Voice", "Tools", "local_kitten_tts.py");
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    public static string? ResolvePath(string appBaseDirectory, string? value, string? searchRoot = null)
    {
        var trimmed = NullIfWhiteSpace(value);
        if (trimmed is null)
        {
            return null;
        }

        try
        {
            var direct = Path.GetFullPath(Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.Combine(appBaseDirectory, trimmed));
            if (LocalPathExists(direct))
            {
                return direct;
            }

            return ResolveFromVoiceRoot(appBaseDirectory, trimmed, searchRoot) ?? direct;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
    }

    public static string? ToPortablePath(string appBaseDirectory, string? value, string? searchRoot = null)
    {
        var fullPath = ResolvePath(appBaseDirectory, value, searchRoot);
        if (fullPath is null)
        {
            return null;
        }

        try
        {
            var normalizedBase = EnsureTrailingSeparator(Path.GetFullPath(appBaseDirectory));
            var normalizedPath = Path.GetFullPath(fullPath);
            if (!normalizedPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedPath;
            }

            var relativePath = Path.GetRelativePath(normalizedBase, normalizedPath);
            return string.IsNullOrWhiteSpace(relativePath) ? normalizedPath : relativePath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return fullPath;
        }
    }

    private static IEnumerable<string> EnumerateVoiceRootCandidates(string appBaseDirectory, string? searchRoot)
    {
        var configuredVoiceRoot = NullIfWhiteSpace(Environment.GetEnvironmentVariable(VoiceRootEnvironmentVariable));
        if (configuredVoiceRoot is not null)
        {
            yield return configuredVoiceRoot;
        }

        yield return Path.Combine(appBaseDirectory, "lib", "voice");

        var directory = new DirectoryInfo(appBaseDirectory);
        while (directory is not null)
        {
            yield return Path.Combine(directory.FullName, "lib", "voice");
            directory = directory.Parent;
        }

        var configuredSearchRoot = NullIfWhiteSpace(searchRoot);
        if (configuredSearchRoot is not null)
        {
            foreach (var candidate in EnumerateVoiceRootsUnder(configuredSearchRoot, maxDepth: 5))
            {
                yield return candidate;
            }
        }

        var currentDirectory = Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            yield return Path.Combine(currentDirectory, "lib", "voice");
        }

        foreach (var codexRoot in EnumerateCodexRoots())
        {
            foreach (var candidate in EnumerateVoiceRootsUnder(codexRoot, maxDepth: 5))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateCodexRoots()
    {
        var roots = new List<string>();
        AddIfPresent(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Codex"));

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            AddIfPresent(roots, Path.Combine(userProfile, "Documents", "Codex"));
            AddIfPresent(roots, Path.Combine(userProfile, "OneDrive", "Documents", "Codex"));
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddIfPresent(List<string> paths, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            paths.Add(path);
        }
    }

    private static IEnumerable<string> EnumerateVoiceRootsUnder(string root, int maxDepth)
    {
        if (maxDepth < 0 || !Directory.Exists(root))
        {
            yield break;
        }

        var direct = Path.Combine(root, "lib", "voice");
        if (Directory.Exists(direct))
        {
            yield return direct;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(root)
                .Where(directory => !ShouldSkipDirectory(Path.GetFileName(directory)))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            foreach (var candidate in EnumerateVoiceRootsUnder(child, maxDepth - 1))
            {
                yield return candidate;
            }
        }
    }

    private static string? ResolveFromVoiceRoot(string appBaseDirectory, string value, string? searchRoot)
    {
        var normalized = value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var voiceRoot = FindVoiceRoot(appBaseDirectory, searchRoot);
        if (voiceRoot is null)
        {
            return null;
        }

        var voiceMarker = $"lib{Path.DirectorySeparatorChar}voice{Path.DirectorySeparatorChar}";
        var voiceMarkerIndex = normalized.IndexOf(voiceMarker, StringComparison.OrdinalIgnoreCase);
        if (voiceMarkerIndex >= 0)
        {
            var afterVoiceRoot = normalized[(voiceMarkerIndex + voiceMarker.Length)..];
            var candidate = Path.Combine(voiceRoot, afterVoiceRoot);
            if (LocalPathExists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var repoRoot = TryGetRepositoryRootFromVoiceRoot(voiceRoot);
        if (repoRoot is null)
        {
            return null;
        }

        var moduleToolsMarker = $"src{Path.DirectorySeparatorChar}Modules{Path.DirectorySeparatorChar}Voice{Path.DirectorySeparatorChar}Tools{Path.DirectorySeparatorChar}";
        var moduleToolsMarkerIndex = normalized.IndexOf(moduleToolsMarker, StringComparison.OrdinalIgnoreCase);
        if (moduleToolsMarkerIndex >= 0)
        {
            var afterRepoRoot = normalized[moduleToolsMarkerIndex..];
            var candidate = Path.Combine(repoRoot, afterRepoRoot);
            if (LocalPathExists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var moduleVoiceToolsMarker = $"tools{Path.DirectorySeparatorChar}Ali.Modules{Path.DirectorySeparatorChar}Voice{Path.DirectorySeparatorChar}";
        var moduleVoiceToolsMarkerIndex = normalized.IndexOf(moduleVoiceToolsMarker, StringComparison.OrdinalIgnoreCase);
        if (moduleVoiceToolsMarkerIndex >= 0)
        {
            var afterLegacyTools = normalized[(moduleVoiceToolsMarkerIndex + moduleVoiceToolsMarker.Length)..];
            var candidate = Path.Combine(repoRoot, "src", "Modules", "Voice", "Tools", afterLegacyTools);
            if (LocalPathExists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var legacyToolsMarker = $"tools{Path.DirectorySeparatorChar}voice{Path.DirectorySeparatorChar}";
        var legacyToolsMarkerIndex = normalized.IndexOf(legacyToolsMarker, StringComparison.OrdinalIgnoreCase);
        if (legacyToolsMarkerIndex >= 0)
        {
            var afterLegacyTools = normalized[(legacyToolsMarkerIndex + legacyToolsMarker.Length)..];
            var candidate = Path.Combine(repoRoot, "src", "Modules", "Voice", "Tools", afterLegacyTools);
            if (LocalPathExists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    private static string? TryGetRepositoryRootFromVoiceRoot(string? voiceRoot)
    {
        if (string.IsNullOrWhiteSpace(voiceRoot))
        {
            return null;
        }

        var voiceDirectory = new DirectoryInfo(voiceRoot);
        return voiceDirectory.Parent?.Parent?.FullName;
    }

    private static bool IsVoiceResourceRoot(string candidate)
    {
        try
        {
            if (!Directory.Exists(candidate))
            {
                return false;
            }

            var piperExecutable = Path.Combine(candidate, "python-venv", "Scripts", "piper.exe");
            var piperVoiceDirectory = Path.Combine(candidate, "piper");
            var whisperDirectory = Path.Combine(candidate, "whisper");
            var kittenDirectory = Path.Combine(candidate, "kitten");
            var whisperScript = Path.Combine(candidate, "local_whisper_stt.py");
            var kittenScript = Path.Combine(candidate, "local_kitten_tts.py");
            return File.Exists(piperExecutable)
                || HasPiperVoices(piperVoiceDirectory)
                || Directory.Exists(whisperDirectory)
                || Directory.Exists(kittenDirectory)
                || File.Exists(whisperScript)
                || File.Exists(kittenScript);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool IsPiperVoiceDirectory(string candidate)
    {
        try
        {
            return Directory.Exists(candidate)
                && Directory.EnumerateFiles(candidate, "en_US-*.onnx").Any();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool HasPiperVoices(string candidate)
    {
        try
        {
            return Directory.Exists(candidate)
                && Directory.EnumerateFiles(candidate, "en_US-*.onnx").Any();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool LocalPathExists(string? path) =>
        !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));

    private static bool ShouldSkipDirectory(string? name) =>
        name is null
        || name.Equals(".git", StringComparison.OrdinalIgnoreCase)
        || name.Equals(".agents", StringComparison.OrdinalIgnoreCase)
        || name.Equals(".codex", StringComparison.OrdinalIgnoreCase)
        || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
        || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
        || name.Equals("python-venv", StringComparison.OrdinalIgnoreCase)
        || name.Equals("SessionAudio", StringComparison.OrdinalIgnoreCase)
        || name.Equals("SessionSpeech", StringComparison.OrdinalIgnoreCase);

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}


