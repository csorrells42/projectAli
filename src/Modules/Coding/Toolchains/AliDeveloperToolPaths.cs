using System.Text.Json;

namespace Ali.Modules.Coding.Toolchains;

internal sealed record AliVisualStudioInstancePaths(
    string DisplayName,
    string Version,
    string InstallationPath,
    string Devenv,
    string DevenvConsole,
    string MsBuild,
    string VsDevCmd,
    string? CMake,
    string? Ninja,
    string? TestConsole,
    string? ClangFormat,
    string? ClangTidy,
    string? ClangD,
    string? OpenDebugAd7,
    string? MsvcCompiler);

internal static class AliDeveloperToolPaths
{
    public static string ToolchainRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ali", "Toolchains");

    public static string ArduinoCli => EnvironmentPathOrDefault(
        "ALI_ARDUINO_CLI",
        Path.Combine(ToolchainRoot, "arduino-cli", "arduino-cli.exe"));

    public static string ArduinoIde => EnvironmentPathOrDefault(
        "ALI_ARDUINO_IDE",
        Path.Combine(ToolchainRoot, "arduino-ide", "Arduino IDE.exe"));

    public static string ArduinoConfiguration => EnvironmentPathOrDefault(
        "ALI_ARDUINO_CONFIG",
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Arduino15", "arduino-cli.yaml"));

    public static string Gcc => EnvironmentPathOrDefault(
        "ALI_GCC_PATH",
        Path.Combine(ToolchainRoot, "msys64", "ucrt64", "bin", "gcc.exe"));

    public static string Gxx => EnvironmentPathOrDefault(
        "ALI_GXX_PATH",
        Path.Combine(ToolchainRoot, "msys64", "ucrt64", "bin", "g++.exe"));

    public static string Gdb => EnvironmentPathOrDefault(
        "ALI_GDB_PATH",
        Path.Combine(ToolchainRoot, "msys64", "ucrt64", "bin", "gdb.exe"));

    public static string MingwMake => EnvironmentPathOrDefault(
        "ALI_MAKE_PATH",
        Path.Combine(ToolchainRoot, "msys64", "ucrt64", "bin", "mingw32-make.exe"));

    public static string Ssh => ResolveWindowsCommand("ssh.exe")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");

    public static string Scp => ResolveWindowsCommand("scp.exe")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "scp.exe");

    public static IReadOnlyList<AliVisualStudioInstancePaths> DiscoverVisualStudio()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return [];

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(vswhere)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                Arguments = "-all -products * -format json -utf8"
            });
            if (process is null) return [];
            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            if (process.ExitCode != 0) return [];
            using var document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateArray()
                .Select(ParseVisualStudioInstance)
                .Where(instance => instance is not null)
                .Cast<AliVisualStudioInstancePaths>()
                .OrderByDescending(instance => instance.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static AliVisualStudioInstancePaths? ParseVisualStudioInstance(JsonElement element)
    {
        var root = element.TryGetProperty("installationPath", out var pathElement)
            ? pathElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        var displayName = element.TryGetProperty("displayName", out var displayElement)
            ? displayElement.GetString() ?? "Visual Studio"
            : "Visual Studio";
        var version = element.TryGetProperty("installationVersion", out var versionElement)
            ? versionElement.GetString() ?? "unknown"
            : "unknown";
        var ide = Path.Combine(root, "Common7", "IDE");
        var vc = Path.Combine(root, "VC", "Tools", "MSVC");
        var msvcCompiler = Directory.Exists(vc)
            ? Directory.EnumerateFiles(vc, "cl.exe", SearchOption.AllDirectories)
                .Where(file => file.Contains(
                    $"{Path.DirectorySeparatorChar}Hostx64{Path.DirectorySeparatorChar}x64{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
            : null;
        return new(
            displayName,
            version,
            root,
            Path.Combine(ide, "devenv.exe"),
            Path.Combine(ide, "devenv.com"),
            Path.Combine(root, "MSBuild", "Current", "Bin", "MSBuild.exe"),
            Path.Combine(root, "Common7", "Tools", "VsDevCmd.bat"),
            Existing(Path.Combine(ide, "CommonExtensions", "Microsoft", "CMake", "CMake", "bin", "cmake.exe")),
            Existing(Path.Combine(ide, "CommonExtensions", "Microsoft", "CMake", "Ninja", "ninja.exe")),
            Existing(Path.Combine(ide, "CommonExtensions", "Microsoft", "TestWindow", "vstest.console.exe")),
            FindFirst(root, "clang-format.exe"),
            FindFirst(root, "clang-tidy.exe"),
            FindFirst(root, "clangd.exe"),
            FindFirst(root, "OpenDebugAD7.exe"),
            msvcCompiler);
    }

    private static string EnvironmentPathOrDefault(string name, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured)
            ? Path.GetFullPath(configured)
            : fallback;
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private static string? FindFirst(string root, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                .OrderBy(path => path.Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveWindowsCommand(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, executable))
            .FirstOrDefault(File.Exists);
    }
}
