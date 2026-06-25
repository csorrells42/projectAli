namespace Ali.Infrastructure.Voice;

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
        var voiceRoot = FindVoiceRoot(appBaseDirectory, searchRoot);
        var candidate = voiceRoot is null ? null : Path.Combine(voiceRoot, "piper");
        return Directory.Exists(candidate) ? Path.GetFullPath(candidate) : null;
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
        var repoRoot = TryGetRepositoryRootFromVoiceRoot(voiceRoot);
        var candidate = repoRoot is null ? null : Path.Combine(repoRoot, "tools", "voice", "local_whisper_stt.py");
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

        var codexRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Codex");
        foreach (var candidate in EnumerateVoiceRootsUnder(codexRoot, maxDepth: 5))
        {
            yield return candidate;
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

        var toolsMarker = $"tools{Path.DirectorySeparatorChar}voice{Path.DirectorySeparatorChar}";
        var toolsMarkerIndex = normalized.IndexOf(toolsMarker, StringComparison.OrdinalIgnoreCase);
        if (toolsMarkerIndex >= 0)
        {
            var afterRepoRoot = normalized[toolsMarkerIndex..];
            var candidate = Path.Combine(repoRoot, afterRepoRoot);
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
            return File.Exists(piperExecutable)
                || Directory.EnumerateFiles(piperVoiceDirectory, "en_US-*.onnx").Any()
                || Directory.Exists(whisperDirectory);
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
