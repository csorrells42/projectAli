using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Ali.Core.Identity;
using Ali.Infrastructure.Identity;

namespace Ali.Infrastructure.Installation;

public sealed class AliDesktopInstaller
{
    private static readonly string[] PersonalDataDirectoryNames =
    [
        "BootstrapData",
        "Profiles",
        "Conversations",
        "Memory",
        "Reminders",
        "DiagnosticSamples",
        "SessionAudio",
        "SessionImages",
        "SessionSpeech"
    ];

    private static readonly string[] PersonalDataFileNames =
    [
        "assistant-profile.json",
        "memories.json",
        "reminders.json",
        "conversations-index.json",
        "corrections.json"
    ];

    public async Task<AliDesktopInstallResult> InstallAsync(
        AliDesktopInstallOptions options,
        CancellationToken cancellationToken = default)
    {
        var normalizedOptions = AliDesktopInstallDiscovery.NormalizeOptions(options);
        var warnings = new List<string>();
        var dependencyMessages = new List<string>();
        var installedFiles = new List<string>();
        var targetDirectory = Path.Combine(normalizedOptions.LocalAliRoot, "DevRun");
        var dataRoot = Path.Combine(normalizedOptions.LocalAliRoot, "BootstrapData");
        var receiptPath = CreateReceiptPath(dataRoot);
        var stagingRoot = Path.Combine(Path.GetTempPath(), "Ali.Installer", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(stagingRoot);
            var payloadRoot = await MaterializePayloadIfNeededAsync(normalizedOptions, stagingRoot, cancellationToken)
                .ConfigureAwait(false);

            if (normalizedOptions.InstallApplication)
            {
                if (normalizedOptions.RepairExistingInstall)
                {
                    dependencyMessages.Add("Repair mode selected; app binaries will be refreshed while user data is preserved.");
                }

                ValidatePayload(payloadRoot!);
                Directory.CreateDirectory(targetDirectory);
                CopyPayload(payloadRoot!, targetDirectory, installedFiles, warnings);
                VerifyInstalledApp(targetDirectory, dependencyMessages);
                await HandleVoiceResourcesAsync(
                        normalizedOptions,
                        stagingRoot,
                        targetDirectory,
                        installedFiles,
                        dependencyMessages,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(normalizedOptions.AssistantName)
                    && !AssistantProfileStore.Exists(dataRoot))
                {
                    AssistantProfileStore.Save(dataRoot, AssistantProfile.Create(normalizedOptions.AssistantName));
                    dependencyMessages.Add("Assistant profile was created from installer input.");
                }
                else if (!string.IsNullOrWhiteSpace(normalizedOptions.AssistantName))
                {
                    dependencyMessages.Add("Assistant profile already exists; installer did not overwrite the saved assistant name.");
                }
                else
                {
                    dependencyMessages.Add("Assistant profile not created; first app launch will ask for the assistant name.");
                }

                await HandleOllamaAsync(normalizedOptions, dependencyMessages, cancellationToken).ConfigureAwait(false);
                CreateShortcuts(normalizedOptions, targetDirectory, dependencyMessages, warnings);
            }
            else
            {
                dependencyMessages.Add("Ali app payload install was not requested; profile, model, and launch steps were skipped.");
            }

            await HandleVisualStudioExtensionAsync(
                    normalizedOptions,
                    payloadRoot,
                    targetDirectory,
                    dependencyMessages,
                    cancellationToken)
                .ConfigureAwait(false);

            if (normalizedOptions.InstallApplication && normalizedOptions.LaunchAfterInstall)
            {
                LaunchInstalledApp(targetDirectory, warnings);
            }

            await WriteReceiptAsync(
                    receiptPath,
                    normalizedOptions,
                    targetDirectory,
                    installedFiles,
                    warnings,
                    dependencyMessages,
                    cancellationToken)
                .ConfigureAwait(false);

            var message = normalizedOptions.InstallApplication
                ? $"Ali installed to {targetDirectory}."
                : "Ali setup completed without reinstalling the app payload.";

            return new AliDesktopInstallResult(
                true,
                message,
                targetDirectory,
                receiptPath,
                installedFiles,
                warnings,
                dependencyMessages);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            warnings.Add(ex.Message);
            try
            {
                await WriteReceiptAsync(
                        receiptPath,
                        normalizedOptions,
                        targetDirectory,
                        installedFiles,
                        warnings,
                        dependencyMessages,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // A failed install should report the original failure even if receipt writing also fails.
            }

            return new AliDesktopInstallResult(
                false,
                $"Ali install failed: {ex.Message}",
                targetDirectory,
                receiptPath,
                installedFiles,
                warnings,
                dependencyMessages);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private static async Task<string?> MaterializePayloadIfNeededAsync(
        AliDesktopInstallOptions options,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var needsPayload = options.InstallApplication
            || (options.InstallVisualStudioExtension && string.IsNullOrWhiteSpace(options.VsixPath));
        return needsPayload
            ? await MaterializePayloadAsync(options.PayloadPath, stagingRoot, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private static async Task<string> MaterializePayloadAsync(
        string? payloadPath,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(payloadPath))
        {
            return await MaterializeExplicitPayloadAsync(payloadPath, stagingRoot, cancellationToken).ConfigureAwait(false);
        }

        var sideBySideZip = Path.Combine(AppContext.BaseDirectory, "ali-payload.zip");
        if (File.Exists(sideBySideZip))
        {
            return await ExtractZipPayloadAsync(sideBySideZip, stagingRoot, cancellationToken).ConfigureAwait(false);
        }

        var sideBySideDirectory = Path.Combine(AppContext.BaseDirectory, "payload");
        if (Directory.Exists(sideBySideDirectory))
        {
            return sideBySideDirectory;
        }

        var embeddedPayload = Assembly.GetEntryAssembly()?.GetManifestResourceStream(AliDesktopInstallDiscovery.EmbeddedPayloadResourceName)
            ?? Assembly.GetExecutingAssembly().GetManifestResourceStream(AliDesktopInstallDiscovery.EmbeddedPayloadResourceName);
        if (embeddedPayload is not null)
        {
            var zipPath = Path.Combine(stagingRoot, "embedded-payload.zip");
            await using (embeddedPayload)
            await using (var file = File.Create(zipPath))
            {
                await embeddedPayload.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            return await ExtractZipPayloadAsync(zipPath, stagingRoot, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("No Ali app payload was found. Provide --payload, place ali-payload.zip beside the installer, or package an embedded payload.");
    }

    private static async Task<string> MaterializeExplicitPayloadAsync(
        string payloadPath,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(payloadPath.Trim());
        if (Directory.Exists(fullPath))
        {
            return fullPath;
        }

        if (File.Exists(fullPath))
        {
            return await ExtractZipPayloadAsync(fullPath, stagingRoot, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Payload path was not found: {fullPath}");
    }

    private static async Task<string> ExtractZipPayloadAsync(
        string zipPath,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var extractRoot = Path.Combine(stagingRoot, "payload");
        Directory.CreateDirectory(extractRoot);
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractRoot, overwriteFiles: true), cancellationToken)
            .ConfigureAwait(false);

        var nestedRoot = Directory.GetFiles(extractRoot, "Ali.App.Wpf.exe", SearchOption.TopDirectoryOnly).Length > 0
            ? extractRoot
            : Directory.GetDirectories(extractRoot)
                .FirstOrDefault(directory => File.Exists(Path.Combine(directory, "Ali.App.Wpf.exe")));
        return nestedRoot ?? extractRoot;
    }

    private static void ValidatePayload(string payloadRoot)
    {
        if (!File.Exists(Path.Combine(payloadRoot, "Ali.App.Wpf.exe")))
        {
            throw new InvalidOperationException($"Payload does not contain Ali.App.Wpf.exe at its root: {payloadRoot}");
        }
    }

    private static void CopyPayload(
        string payloadRoot,
        string targetDirectory,
        List<string> installedFiles,
        List<string> warnings)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(payloadRoot, sourcePath);
            if (ShouldSkipPayloadPath(relativePath))
            {
                warnings.Add($"Skipped personal data payload item: {relativePath}");
                continue;
            }

            var targetPath = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
            installedFiles.Add(targetPath);
        }
    }

    private static bool ShouldSkipPayloadPath(string relativePath)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part => PersonalDataDirectoryNames.Contains(part, StringComparer.OrdinalIgnoreCase))
            || PersonalDataFileNames.Contains(Path.GetFileName(relativePath), StringComparer.OrdinalIgnoreCase);
    }

    private static void VerifyInstalledApp(string targetDirectory, List<string> dependencyMessages)
    {
        var appPath = Path.Combine(targetDirectory, "Ali.App.Wpf.exe");
        dependencyMessages.Add(File.Exists(appPath)
            ? $"Installed app verified: {appPath}"
            : $"Installed app verification failed; Ali.App.Wpf.exe was not found at {appPath}");
    }

    private static async Task HandleVoiceResourcesAsync(
        AliDesktopInstallOptions options,
        string stagingRoot,
        string targetDirectory,
        List<string> installedFiles,
        List<string> dependencyMessages,
        CancellationToken cancellationToken)
    {
        if (!options.InstallVoiceResources)
        {
            dependencyMessages.Add("Local voice resources install was not requested.");
            return;
        }

        var source = AliDesktopInstallDiscovery.ResolveVoiceResourcesSource(options.VoiceResourcesPath);
        if (source is null)
        {
            dependencyMessages.Add("Local voice resources install was requested, but no sidecar voice pack was found.");
            return;
        }

        var voiceCount = AliDesktopInstallDiscovery.CountPiperVoices(source);
        if (voiceCount == 0)
        {
            dependencyMessages.Add($"Local voice resources skipped; no Piper .onnx voices were found in {source}.");
            return;
        }

        var voiceRoot = await MaterializeVoiceResourcesAsync(source, stagingRoot, cancellationToken).ConfigureAwait(false);
        var targetVoiceRoot = Path.Combine(targetDirectory, "lib", "voice");
        CopyDirectory(voiceRoot, targetVoiceRoot, installedFiles);
        dependencyMessages.Add($"Local voice resources installed: {voiceCount} Piper voice(s) from {source}.");
    }

    private static async Task<string> MaterializeVoiceResourcesAsync(
        string source,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(source))
        {
            return AliDesktopInstallDiscovery.ResolveVoiceRootFromDirectory(source)
                ?? throw new InvalidOperationException($"Voice resource folder does not contain a lib\\voice or voice root: {source}");
        }

        if (!File.Exists(source))
        {
            throw new InvalidOperationException($"Voice resources path was not found: {source}");
        }

        var extractRoot = Path.Combine(stagingRoot, "voice-resources");
        Directory.CreateDirectory(extractRoot);
        await Task.Run(() => ZipFile.ExtractToDirectory(source, extractRoot, overwriteFiles: true), cancellationToken)
            .ConfigureAwait(false);
        return AliDesktopInstallDiscovery.ResolveVoiceRootFromDirectory(extractRoot)
            ?? throw new InvalidOperationException($"Voice resource zip does not contain a lib\\voice or voice root: {source}");
    }

    private static void CopyDirectory(string sourceRoot, string targetRoot, List<string> installedFiles)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var targetPath = Path.Combine(targetRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
            installedFiles.Add(targetPath);
        }
    }

    private static async Task HandleOllamaAsync(
        AliDesktopInstallOptions options,
        List<string> dependencyMessages,
        CancellationToken cancellationToken)
    {
        var modelPullsRequested = options.PullRuntimeModel || options.PullVisionModel;
        if (!modelPullsRequested && !options.InstallOllamaIfMissing)
        {
            dependencyMessages.Add("Ollama model pulls were not requested.");
            return;
        }

        var ollama = AliDesktopInstallDiscovery.ResolveOllamaExecutable(options.OllamaExecutablePath);
        if (ollama is null && options.InstallOllamaIfMissing)
        {
            dependencyMessages.Add(await InstallOllamaAsync(options, cancellationToken).ConfigureAwait(false));
            ollama = AliDesktopInstallDiscovery.ResolveOllamaExecutable(options.OllamaExecutablePath);
        }

        if (!modelPullsRequested)
        {
            dependencyMessages.Add(ollama is null
                ? "Ollama install was requested, but ollama.exe was not found after the installer completed."
                : $"Ollama is available: {ollama}");
            return;
        }

        if (ollama is null)
        {
            dependencyMessages.Add("Ollama executable was not found; requested model pulls were skipped.");
            return;
        }

        var installedModels = await AliDesktopInstallDiscovery.TryListOllamaModelsAsync(
                ollama,
                TimeSpan.FromSeconds(20),
                cancellationToken)
            .ConfigureAwait(false);

        if (options.PullRuntimeModel)
        {
            dependencyMessages.Add(await PullModelIfMissingAsync(ollama, options.RuntimeModel!, installedModels, cancellationToken).ConfigureAwait(false));
        }

        if (options.PullVisionModel)
        {
            dependencyMessages.Add(await PullModelIfMissingAsync(ollama, options.VisionModel!, installedModels, cancellationToken).ConfigureAwait(false));
        }
    }

    private static async Task<string> InstallOllamaAsync(
        AliDesktopInstallOptions options,
        CancellationToken cancellationToken)
    {
        var tempInstallerPath = string.Empty;
        try
        {
            var installerPath = options.OllamaInstallerPath;
            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            {
                tempInstallerPath = Path.Combine(Path.GetTempPath(), "Ali.Installer", Guid.NewGuid().ToString("N"), "OllamaSetup.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(tempInstallerPath)!);
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                await using var download = await client.GetStreamAsync(options.OllamaInstallerUri!, cancellationToken).ConfigureAwait(false);
                await using var file = File.Create(tempInstallerPath);
                await download.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                installerPath = tempInstallerPath;
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true
            });

            if (process is null)
            {
                return "Ollama installer could not be started.";
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0
                ? "Ollama installer completed."
                : $"Ollama installer exited with code {process.ExitCode}.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
        {
            return $"Ollama installer failed: {ex.Message}";
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempInstallerPath))
            {
                TryDeleteDirectory(Path.GetDirectoryName(tempInstallerPath)!);
            }
        }
    }

    private static async Task<string> PullModelIfMissingAsync(
        string ollamaExecutable,
        string model,
        IReadOnlySet<string>? installedModels,
        CancellationToken cancellationToken)
    {
        if (installedModels?.Contains(model) == true)
        {
            return $"Ollama model already installed: {model}.";
        }

        return await PullModelAsync(ollamaExecutable, model, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> PullModelAsync(
        string ollamaExecutable,
        string model,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ollamaExecutable,
                ArgumentList = { "pull", model },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var message = string.IsNullOrWhiteSpace(error) ? output : error;
        return process.ExitCode == 0
            ? $"Ollama model installed: {model}."
            : $"Ollama model pull failed for {model}: {TrimForReceipt(message)}";
    }

    private static async Task HandleVisualStudioExtensionAsync(
        AliDesktopInstallOptions options,
        string? payloadRoot,
        string targetDirectory,
        List<string> dependencyMessages,
        CancellationToken cancellationToken)
    {
        if (!options.InstallVisualStudioExtension)
        {
            dependencyMessages.Add("Visual Studio extension install was not requested.");
            return;
        }

        var vsixPath = AliDesktopInstallDiscovery.ResolveVsixPath(options.VsixPath, payloadRoot, targetDirectory);
        if (vsixPath is null)
        {
            dependencyMessages.Add("Visual Studio extension install was requested, but no Ali Companion VSIX package was found.");
            return;
        }

        var vsixInstallerPath = AliDesktopInstallDiscovery.ResolveVsixInstallerPath(options.VsixInstallerPath);
        if (vsixInstallerPath is null)
        {
            dependencyMessages.Add("VSIXInstaller.exe was not found; Ali Companion VSIX install was skipped.");
            return;
        }

        dependencyMessages.Add(await InstallVsixAsync(vsixInstallerPath, vsixPath, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<string> InstallVsixAsync(
        string vsixInstallerPath,
        string vsixPath,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = vsixInstallerPath,
                ArgumentList = { "/quiet", vsixPath },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var message = string.IsNullOrWhiteSpace(error) ? output : error;
        return process.ExitCode == 0
            ? $"Ali Companion VSIX installed from {vsixPath}."
            : $"Ali Companion VSIX install failed: {TrimForReceipt(message)}";
    }

    private static void LaunchInstalledApp(string targetDirectory, List<string> warnings)
    {
        var appPath = Path.Combine(targetDirectory, "Ali.App.Wpf.exe");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = appPath,
                WorkingDirectory = targetDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            warnings.Add($"Installed app could not be launched: {ex.Message}");
        }
    }

    private static void CreateShortcuts(
        AliDesktopInstallOptions options,
        string targetDirectory,
        List<string> dependencyMessages,
        List<string> warnings)
    {
        if (!options.CreateDesktopShortcut && !options.CreateStartMenuShortcut)
        {
            dependencyMessages.Add("Shortcut creation was not requested.");
            return;
        }

        var appPath = Path.Combine(targetDirectory, "Ali.App.Wpf.exe");
        if (!File.Exists(appPath))
        {
            warnings.Add("Shortcuts were skipped because Ali.App.Wpf.exe was not found.");
            return;
        }

        if (options.CreateDesktopShortcut)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            TryCreateShortcut(Path.Combine(desktop, "Ali.lnk"), appPath, targetDirectory, dependencyMessages, warnings);
        }

        if (options.CreateStartMenuShortcut)
        {
            var startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs",
                "Ali");
            Directory.CreateDirectory(startMenu);
            TryCreateShortcut(Path.Combine(startMenu, "Ali.lnk"), appPath, targetDirectory, dependencyMessages, warnings);
        }
    }

    private static void TryCreateShortcut(
        string shortcutPath,
        string targetPath,
        string workingDirectory,
        List<string> dependencyMessages,
        List<string> warnings)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                warnings.Add("Windows shortcut creation is only available on Windows.");
                return;
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                warnings.Add("Windows shortcut service was not available.");
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.Description = "Ali local desktop assistant";
            shortcut.IconLocation = targetPath;
            shortcut.Save();
            dependencyMessages.Add($"Shortcut created: {shortcutPath}");
        }
        catch (Exception ex)
        {
            warnings.Add($"Shortcut could not be created at {shortcutPath}: {ex.Message}");
        }
    }

    private static string CreateReceiptPath(string dataRoot)
    {
        var receiptsRoot = Path.Combine(dataRoot, "install-receipts");
        Directory.CreateDirectory(receiptsRoot);
        return Path.Combine(receiptsRoot, $"install-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
    }

    private static async Task WriteReceiptAsync(
        string receiptPath,
        AliDesktopInstallOptions options,
        string targetDirectory,
        IReadOnlyList<string> installedFiles,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> dependencyMessages,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        var assistantProfilePath = Path.Combine(options.LocalAliRoot, "BootstrapData", "assistant-profile.json");
        var readiness = await new AliDesktopInstallReadinessService()
            .EvaluateAsync(options, cancellationToken)
            .ConfigureAwait(false);
        var receipt = new
        {
            createdAt = DateTimeOffset.Now,
            targetDirectory,
            targetAppPath = Path.Combine(targetDirectory, "Ali.App.Wpf.exe"),
            payloadPath = options.PayloadPath,
            localAliRoot = options.LocalAliRoot,
            assistantProfilePath,
            assistantProfileExists = File.Exists(assistantProfilePath),
            installApplication = options.InstallApplication,
            assistantProfileSeeded = !string.IsNullOrWhiteSpace(options.AssistantName),
            pullRuntimeModel = options.PullRuntimeModel,
            runtimeModel = options.RuntimeModel,
            pullVisionModel = options.PullVisionModel,
            visionModel = options.VisionModel,
            installOllamaIfMissing = options.InstallOllamaIfMissing,
            ollamaInstallerPath = options.OllamaInstallerPath,
            ollamaInstallerUri = options.OllamaInstallerUri,
            installVisualStudioExtension = options.InstallVisualStudioExtension,
            vsixPath = options.VsixPath,
            vsixInstallerPath = options.VsixInstallerPath,
            installVoiceResources = options.InstallVoiceResources,
            voiceResourcesPath = options.VoiceResourcesPath,
            createDesktopShortcut = options.CreateDesktopShortcut,
            createStartMenuShortcut = options.CreateStartMenuShortcut,
            repairExistingInstall = options.RepairExistingInstall,
            readiness = new
            {
                readiness.IsReadyForSelectedActions,
                items = readiness.Items.Select(item => new
                {
                    item.Name,
                    status = item.Status.ToString(),
                    item.Message
                }).ToArray()
            },
            installedFileCount = installedFiles.Count,
            warnings,
            dependencyMessages
        };

        await using var stream = File.Create(receiptPath);
        await JsonSerializer.SerializeAsync(
                stream,
                receipt,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Temporary staging cleanup must not mask install results.
        }
    }

    private static string TrimForReceipt(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 500 ? collapsed : collapsed[..500] + "...";
    }
}
