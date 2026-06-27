using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace Ali.Infrastructure.Installation;

public static class AliDesktopInstallDiscovery
{
    public const string EmbeddedPayloadResourceName = "Ali.App.Installer.Payload.ali-payload.zip";
    public static readonly Uri OfficialOllamaInstallerUri = new("https://ollama.com/download/OllamaSetup.exe");

    private static readonly string[] VisualStudioExtensionRelativePaths =
    [
        Path.Combine("extras", "visualstudio", "Ali.App.VisualStudioExtension.vsix"),
        "Ali.App.VisualStudioExtension.vsix"
    ];

    private static readonly string[] VoiceResourceRelativePaths =
    [
        "Ali.VoicePack.zip",
        "Ali.VoicePatch.zip",
        "ali-voice-pack.zip",
        "ali-voice-patch.zip",
        "voice-pack.zip",
        "voice-patch.zip",
        Path.Combine("lib", "voice"),
        "voice"
    ];

    public static AliDesktopInstallOptions NormalizeOptions(AliDesktopInstallOptions options) =>
        options with
        {
            LocalAliRoot = string.IsNullOrWhiteSpace(options.LocalAliRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ali")
                : Path.GetFullPath(options.LocalAliRoot),
            RuntimeModel = string.IsNullOrWhiteSpace(options.RuntimeModel)
                ? "ali-deepseek-coder-v2:16b-low"
                : options.RuntimeModel.Trim(),
            VisionModel = string.IsNullOrWhiteSpace(options.VisionModel)
                ? "qwen3-vl:8b"
                : options.VisionModel.Trim(),
            OllamaInstallerPath = string.IsNullOrWhiteSpace(options.OllamaInstallerPath) ? null : Path.GetFullPath(options.OllamaInstallerPath.Trim()),
            OllamaInstallerUri = options.OllamaInstallerUri ?? OfficialOllamaInstallerUri,
            VsixPath = string.IsNullOrWhiteSpace(options.VsixPath) ? null : Path.GetFullPath(options.VsixPath.Trim()),
            VsixInstallerPath = string.IsNullOrWhiteSpace(options.VsixInstallerPath) ? null : Path.GetFullPath(options.VsixInstallerPath.Trim()),
            VoiceResourcesPath = string.IsNullOrWhiteSpace(options.VoiceResourcesPath) ? null : Path.GetFullPath(options.VoiceResourcesPath.Trim())
        };

    public static string? ResolvePayloadSource(string? payloadPath)
    {
        if (!string.IsNullOrWhiteSpace(payloadPath))
        {
            var fullPath = Path.GetFullPath(payloadPath.Trim());
            return Directory.Exists(fullPath) || File.Exists(fullPath) ? fullPath : null;
        }

        var sideBySideZip = Path.Combine(AppContext.BaseDirectory, "ali-payload.zip");
        if (File.Exists(sideBySideZip))
        {
            return sideBySideZip;
        }

        var sideBySideDirectory = Path.Combine(AppContext.BaseDirectory, "payload");
        if (Directory.Exists(sideBySideDirectory))
        {
            return sideBySideDirectory;
        }

        return HasEmbeddedPayload() ? EmbeddedPayloadResourceName : null;
    }

    public static bool HasUsablePayload(string? payloadSource)
    {
        if (string.IsNullOrWhiteSpace(payloadSource))
        {
            return false;
        }

        if (payloadSource.Equals(EmbeddedPayloadResourceName, StringComparison.Ordinal))
        {
            return true;
        }

        if (Directory.Exists(payloadSource))
        {
            return File.Exists(Path.Combine(payloadSource, "Ali.App.Wpf.exe"));
        }

        if (!File.Exists(payloadSource))
        {
            return false;
        }

        try
        {
            using var archive = ZipFile.OpenRead(payloadSource);
            return archive.Entries.Any(entry =>
                entry.FullName.Split('/', '\\').Last().Equals("Ali.App.Wpf.exe", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static string? ResolveOllamaExecutable(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var localAppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Ollama",
            "ollama.exe");
        if (File.Exists(localAppDataPath))
        {
            return localAppDataPath;
        }

        return Environment.GetEnvironmentVariable("PATH")
            ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.Combine(path, "ollama.exe"))
            .FirstOrDefault(File.Exists);
    }

    public static string? ResolveVsixPath(string? configuredPath, string? payloadRootOrSource, string targetDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullPath = Path.GetFullPath(configuredPath);
            return File.Exists(fullPath) ? fullPath : null;
        }

        foreach (var root in new[] { payloadRootOrSource, targetDirectory }.Where(root => Directory.Exists(root)))
        {
            foreach (var relativePath in VisualStudioExtensionRelativePaths)
            {
                var candidate = Path.Combine(root!, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(payloadRootOrSource)
            && File.Exists(payloadRootOrSource))
        {
            return ZipContainsVsix(payloadRootOrSource) ? $"{payloadRootOrSource}!extras/visualstudio/Ali.App.VisualStudioExtension.vsix" : null;
        }

        return HasEmbeddedPayload() ? $"{EmbeddedPayloadResourceName}!extras/visualstudio/Ali.App.VisualStudioExtension.vsix" : null;
    }

    public static string? ResolveVsixInstallerPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullPath = Path.GetFullPath(configuredPath);
            return File.Exists(fullPath) ? fullPath : null;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidatePaths = new[]
        {
            Path.Combine(programFiles, "Microsoft Visual Studio", "18", "Community", "Common7", "IDE", "VSIXInstaller.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "18", "Professional", "Common7", "IDE", "VSIXInstaller.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "18", "Enterprise", "Common7", "IDE", "VSIXInstaller.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Community", "Common7", "IDE", "VSIXInstaller.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Professional", "Common7", "IDE", "VSIXInstaller.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Enterprise", "Common7", "IDE", "VSIXInstaller.exe")
        };

        return candidatePaths.FirstOrDefault(File.Exists);
    }

    public static string? ResolveVoiceResourcesSource(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullPath = Path.GetFullPath(configuredPath.Trim());
            return Directory.Exists(fullPath) || File.Exists(fullPath) ? fullPath : null;
        }

        foreach (var root in EnumerateSidecarRoots())
        {
            foreach (var relativePath in VoiceResourceRelativePaths)
            {
                var candidate = Path.Combine(root, relativePath);
                if (Directory.Exists(candidate) || File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static int CountPiperVoices(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return 0;
        }

        if (Directory.Exists(source))
        {
            var voiceRoot = ResolveVoiceRootFromDirectory(source);
            return voiceRoot is null ? 0 : CountPiperVoicesInDirectory(voiceRoot);
        }

        if (!File.Exists(source))
        {
            return 0;
        }

        try
        {
            using var archive = ZipFile.OpenRead(source);
            return archive.Entries.Count(IsPiperVoiceEntry);
        }
        catch (InvalidDataException)
        {
            return 0;
        }
    }

    public static bool HasVoiceRepairResources(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        if (Directory.Exists(source))
        {
            var voiceRoot = ResolveVoiceRootFromDirectory(source);
            return voiceRoot is not null && HasVoiceRepairResourcesInDirectory(voiceRoot);
        }

        if (!File.Exists(source))
        {
            return false;
        }

        try
        {
            using var archive = ZipFile.OpenRead(source);
            return archive.Entries.Any(entry =>
            {
                var name = entry.FullName.Replace('\\', '/');
                return name.Contains("/python-runtime/python.exe", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("/local_kitten_tts.py", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("/local_whisper_stt.py", StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static string? ResolveVoiceRootFromDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (IsVoiceRoot(fullPath))
        {
            return fullPath;
        }

        var nested = Path.Combine(fullPath, "lib", "voice");
        if (IsVoiceRoot(nested))
        {
            return nested;
        }

        return null;
    }

    public static async Task<IReadOnlySet<string>?> TryListOllamaModelsAsync(
        string ollamaExecutable,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ollamaExecutable,
                    ArgumentList = { "list" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return null;
            }

            return output
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Skip(1)
                .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool HasEmbeddedPayload() =>
        Assembly.GetEntryAssembly()?.GetManifestResourceInfo(EmbeddedPayloadResourceName) is not null
        || Assembly.GetExecutingAssembly().GetManifestResourceInfo(EmbeddedPayloadResourceName) is not null;

    private static IEnumerable<string> EnumerateSidecarRoots()
    {
        yield return AppContext.BaseDirectory;

        var currentDirectory = Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(currentDirectory)
            && !currentDirectory.Equals(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            yield return currentDirectory;
        }
    }

    private static int CountPiperVoicesInDirectory(string voiceRoot)
    {
        var piperRoot = Path.Combine(voiceRoot, "piper");
        return Directory.Exists(piperRoot)
            ? Directory.EnumerateFiles(piperRoot, "en_US-*.onnx", SearchOption.TopDirectoryOnly).Count()
            : 0;
    }

    private static bool HasVoiceRepairResourcesInDirectory(string voiceRoot) =>
        File.Exists(Path.Combine(voiceRoot, "python-runtime", "python.exe"))
        || File.Exists(Path.Combine(voiceRoot, "local_kitten_tts.py"))
        || File.Exists(Path.Combine(voiceRoot, "local_whisper_stt.py"));

    private static bool IsVoiceRoot(string path) =>
        Directory.Exists(Path.Combine(path, "piper"))
        || Directory.Exists(Path.Combine(path, "python-runtime"))
        || Directory.Exists(Path.Combine(path, "python-venv"))
        || Directory.Exists(Path.Combine(path, "kitten"))
        || Directory.Exists(Path.Combine(path, "whisper"))
        || File.Exists(Path.Combine(path, "local_kitten_tts.py"))
        || File.Exists(Path.Combine(path, "local_whisper_stt.py"));

    private static bool IsPiperVoiceEntry(ZipArchiveEntry entry)
    {
        var name = entry.FullName.Replace('\\', '/');
        return !string.IsNullOrWhiteSpace(entry.Name)
            && name.Contains("/piper/", StringComparison.OrdinalIgnoreCase)
            && entry.Name.StartsWith("en_US-", StringComparison.OrdinalIgnoreCase)
            && entry.Name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ZipContainsVsix(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return archive.Entries.Any(entry =>
                entry.FullName.EndsWith("Ali.App.VisualStudioExtension.vsix", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
