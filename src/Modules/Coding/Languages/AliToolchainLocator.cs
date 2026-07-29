namespace Ali.Modules.Coding.Languages;

internal sealed class AliToolchainLocator
{
    public string? Resolve(
        string executableName,
        string? environmentVariable = null,
        string? projectDirectory = null,
        params string[] projectRelativeCandidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        var configured = string.IsNullOrWhiteSpace(environmentVariable)
            ? null
            : Environment.GetEnvironmentVariable(environmentVariable);
        if (IsExecutable(configured))
        {
            return Path.GetFullPath(configured!);
        }

        if (!string.IsNullOrWhiteSpace(projectDirectory))
        {
            foreach (var relative in projectRelativeCandidates)
            {
                var candidate = Path.GetFullPath(Path.Combine(projectDirectory, relative));
                if (IsExecutable(candidate))
                {
                    return candidate;
                }
            }
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "dependencies", "coding", executableName);
        if (IsExecutable(bundled))
        {
            return bundled;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executableName);
            if (IsExecutable(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(Path.GetExtension(executableName)))
            {
                foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
                {
                    candidate = Path.Combine(directory, executableName + extension);
                    if (IsExecutable(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }
        }

        return null;
    }

    private static bool IsExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path);
}
