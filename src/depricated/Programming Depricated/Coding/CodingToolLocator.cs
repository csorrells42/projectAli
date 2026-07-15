namespace Ali.Infrastructure.Coding;

public static class CodingToolLocator
{
    public static string? FindNotepadPlusPlus(string? configuredPath = null) =>
        ResolveConfiguredExecutable(configuredPath, "notepad++.exe")
        ?? FindFirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Notepad++", "notepad++.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Notepad++", "notepad++.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Notepad++", "notepad++.exe"))
        ?? FindOnPath("notepad++.exe");

    public static string? FindVisualStudio(string? configuredPath = null)
    {
        var configured = ResolveConfiguredExecutable(
            configuredPath,
            "devenv.exe",
            Path.Combine("Common7", "IDE", "devenv.exe"));
        if (configured is not null)
        {
            return configured;
        }

        var vsWhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio",
            "Installer",
            "vswhere.exe");

        if (File.Exists(vsWhere))
        {
            var installPath = TryRunVsWhere(vsWhere);
            if (!string.IsNullOrWhiteSpace(installPath))
            {
                var devenv = Path.Combine(installPath, "Common7", "IDE", "devenv.exe");
                if (File.Exists(devenv))
                {
                    return devenv;
                }
            }
        }

        return FindFirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2022", "Community", "Common7", "IDE", "devenv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2022", "Professional", "Common7", "IDE", "devenv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio", "2022", "Enterprise", "Common7", "IDE", "devenv.exe"))
        ?? FindOnPath("devenv.exe");
    }

    private static string? FindFirstExisting(params string[] paths) =>
        paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));

    private static string? ResolveConfiguredExecutable(
        string? configuredPath,
        string executableName,
        params string[] relativeExecutablePaths)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(configuredPath.Trim().Trim('"'));
            if (File.Exists(fullPath)
                && Path.GetFileName(fullPath).Equals(executableName, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }

            if (!Directory.Exists(fullPath))
            {
                return null;
            }

            var direct = Path.Combine(fullPath, executableName);
            if (File.Exists(direct))
            {
                return direct;
            }

            foreach (var relativePath in relativeExecutablePaths)
            {
                var candidate = Path.Combine(fullPath, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? FindOnPath(string executable)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH segments.
            }
        }

        return null;
    }

    private static string? TryRunVsWhere(string vsWhere)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = vsWhere,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("-latest");
            process.StartInfo.ArgumentList.Add("-products");
            process.StartInfo.ArgumentList.Add("*");
            process.StartInfo.ArgumentList.Add("-requires");
            process.StartInfo.ArgumentList.Add("Microsoft.Component.MSBuild");
            process.StartInfo.ArgumentList.Add("-property");
            process.StartInfo.ArgumentList.Add("installationPath");

            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(3000) || process.ExitCode != 0)
            {
                return null;
            }

            return output;
        }
        catch
        {
            return null;
        }
    }
}
