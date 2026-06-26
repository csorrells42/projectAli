using Ali.Infrastructure.Identity;

namespace Ali.Infrastructure.Installation;

public enum AliInstallReadinessStatus
{
    Ready,
    Warning,
    Missing,
    Skipped
}

public sealed record AliInstallReadinessItem(
    string Name,
    AliInstallReadinessStatus Status,
    string Message);

public sealed record AliDesktopInstallReadinessResult(
    bool IsReadyForSelectedActions,
    IReadOnlyList<AliInstallReadinessItem> Items);

public sealed class AliDesktopInstallReadinessService
{
    private static readonly TimeSpan OllamaListTimeout = TimeSpan.FromSeconds(8);

    public async Task<AliDesktopInstallReadinessResult> EvaluateAsync(
        AliDesktopInstallOptions options,
        CancellationToken cancellationToken = default)
    {
        var normalizedOptions = AliDesktopInstallDiscovery.NormalizeOptions(options);
        var targetDirectory = Path.Combine(normalizedOptions.LocalAliRoot, "DevRun");
        var dataRoot = Path.Combine(normalizedOptions.LocalAliRoot, "BootstrapData");
        var items = new List<AliInstallReadinessItem>();

        var payloadSource = AliDesktopInstallDiscovery.ResolvePayloadSource(normalizedOptions.PayloadPath);
        AddPayloadStatus(items, normalizedOptions, payloadSource);
        AddRepairStatus(items, normalizedOptions);
        AddDevRunStatus(items, targetDirectory);
        AddAssistantProfileStatus(items, normalizedOptions, dataRoot);
        AddShortcutStatus(items, normalizedOptions);

        var ollama = AliDesktopInstallDiscovery.ResolveOllamaExecutable(normalizedOptions.OllamaExecutablePath);
        IReadOnlySet<string>? installedModels = null;
        if (ollama is not null)
        {
            installedModels = await AliDesktopInstallDiscovery.TryListOllamaModelsAsync(
                    ollama,
                    OllamaListTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        AddOllamaStatus(items, normalizedOptions, ollama, installedModels);
        AddVisualStudioStatus(items, normalizedOptions, payloadSource, targetDirectory);

        var isReady = !items.Any(item => item.Status == AliInstallReadinessStatus.Missing);
        return new AliDesktopInstallReadinessResult(isReady, items);
    }

    private static void AddPayloadStatus(
        List<AliInstallReadinessItem> items,
        AliDesktopInstallOptions options,
        string? payloadSource)
    {
        if (!options.InstallApplication)
        {
            items.Add(new("Ali payload", AliInstallReadinessStatus.Skipped, "App payload install is not selected."));
            return;
        }

        if (payloadSource is null)
        {
            items.Add(new("Ali payload", AliInstallReadinessStatus.Missing, "No app payload was found."));
            return;
        }

        var status = AliDesktopInstallDiscovery.HasUsablePayload(payloadSource)
            ? AliInstallReadinessStatus.Ready
            : AliInstallReadinessStatus.Missing;
        var message = status == AliInstallReadinessStatus.Ready
            ? $"Payload found: {payloadSource}"
            : $"Payload was found but does not contain Ali.App.Wpf.exe: {payloadSource}";
        items.Add(new("Ali payload", status, message));
    }

    private static void AddRepairStatus(List<AliInstallReadinessItem> items, AliDesktopInstallOptions options)
    {
        if (!options.InstallApplication || !options.RepairExistingInstall)
        {
            return;
        }

        items.Add(new("Repair mode", AliInstallReadinessStatus.Ready, "App binaries will be refreshed; chats, memories, reminders, and assistant profile are preserved."));
    }

    private static void AddDevRunStatus(List<AliInstallReadinessItem> items, string targetDirectory)
    {
        var appPath = Path.Combine(targetDirectory, "Ali.App.Wpf.exe");
        items.Add(File.Exists(appPath)
            ? new("Existing Ali app", AliInstallReadinessStatus.Ready, $"Existing DevRun app found: {appPath}")
            : new("Existing Ali app", AliInstallReadinessStatus.Warning, "No existing DevRun app found; a fresh app install will create it."));
    }

    private static void AddAssistantProfileStatus(
        List<AliInstallReadinessItem> items,
        AliDesktopInstallOptions options,
        string dataRoot)
    {
        if (!options.InstallApplication)
        {
            items.Add(new("Assistant profile", AliInstallReadinessStatus.Skipped, "Profile setup is skipped in component-only mode."));
            return;
        }

        if (AssistantProfileStore.Exists(dataRoot))
        {
            items.Add(new("Assistant profile", AliInstallReadinessStatus.Ready, "Existing assistant profile will be preserved."));
            return;
        }

        if (!string.IsNullOrWhiteSpace(options.AssistantName))
        {
            items.Add(new("Assistant profile", AliInstallReadinessStatus.Ready, "Installer will seed the assistant name into the single profile file."));
            return;
        }

        items.Add(new("Assistant profile", AliInstallReadinessStatus.Ready, "First Ali launch will ask for the assistant name."));
    }

    private static void AddShortcutStatus(List<AliInstallReadinessItem> items, AliDesktopInstallOptions options)
    {
        if (!options.InstallApplication)
        {
            items.Add(new("Shortcuts", AliInstallReadinessStatus.Skipped, "Shortcut creation is skipped in component-only mode."));
            return;
        }

        if (!options.CreateDesktopShortcut && !options.CreateStartMenuShortcut)
        {
            items.Add(new("Shortcuts", AliInstallReadinessStatus.Skipped, "Shortcut creation is not selected."));
            return;
        }

        var targets = new List<string>();
        if (options.CreateDesktopShortcut)
        {
            targets.Add("desktop");
        }

        if (options.CreateStartMenuShortcut)
        {
            targets.Add("Start menu");
        }

        items.Add(new("Shortcuts", AliInstallReadinessStatus.Ready, $"Installer will create: {string.Join(", ", targets)}."));
    }

    private static void AddOllamaStatus(
        List<AliInstallReadinessItem> items,
        AliDesktopInstallOptions options,
        string? ollama,
        IReadOnlySet<string>? installedModels)
    {
        var needsOllama = options.InstallApplication && (options.PullRuntimeModel || options.PullVisionModel);
        if (ollama is null)
        {
            if (options.InstallApplication && options.InstallOllamaIfMissing)
            {
                var source = !string.IsNullOrWhiteSpace(options.OllamaInstallerPath)
                    ? options.OllamaInstallerPath
                    : options.OllamaInstallerUri?.ToString() ?? AliDesktopInstallDiscovery.OfficialOllamaInstallerUri.ToString();
                items.Add(new("Ollama", AliInstallReadinessStatus.Ready, $"Ollama is missing; installer will run: {source}"));
            }
            else
            {
                var status = needsOllama ? AliInstallReadinessStatus.Missing : AliInstallReadinessStatus.Warning;
                items.Add(new("Ollama", status, "Ollama executable was not found."));
            }

            AddModelStatus(items, "Runtime model", options.RuntimeModel, options.PullRuntimeModel, installedModels);
            AddModelStatus(items, "Vision model", options.VisionModel, options.PullVisionModel, installedModels);
            return;
        }

        items.Add(new("Ollama", AliInstallReadinessStatus.Ready, $"Ollama found: {ollama}"));
        AddModelStatus(items, "Runtime model", options.RuntimeModel, options.PullRuntimeModel, installedModels);
        AddModelStatus(items, "Vision model", options.VisionModel, options.PullVisionModel, installedModels);
    }

    private static void AddModelStatus(
        List<AliInstallReadinessItem> items,
        string name,
        string? model,
        bool pullRequested,
        IReadOnlySet<string>? installedModels)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            items.Add(new(name, AliInstallReadinessStatus.Missing, "No model id is selected."));
            return;
        }

        if (installedModels is null)
        {
            var status = pullRequested ? AliInstallReadinessStatus.Warning : AliInstallReadinessStatus.Skipped;
            var message = pullRequested
                ? $"Could not confirm whether {model} is already installed; installer can still request a pull."
                : $"Model check skipped for {model}.";
            items.Add(new(name, status, message));
            return;
        }

        if (installedModels.Contains(model))
        {
            items.Add(new(name, AliInstallReadinessStatus.Ready, $"Installed: {model}"));
            return;
        }

        var missingStatus = pullRequested ? AliInstallReadinessStatus.Ready : AliInstallReadinessStatus.Warning;
        var missingMessage = pullRequested
            ? $"Not installed yet; installer will request: {model}"
            : $"Not installed: {model}";
        items.Add(new(name, missingStatus, missingMessage));
    }

    private static void AddVisualStudioStatus(
        List<AliInstallReadinessItem> items,
        AliDesktopInstallOptions options,
        string? payloadSource,
        string targetDirectory)
    {
        if (!options.InstallVisualStudioExtension)
        {
            items.Add(new("Visual Studio Companion", AliInstallReadinessStatus.Skipped, "VSIX install is not selected."));
            return;
        }

        var vsix = AliDesktopInstallDiscovery.ResolveVsixPath(options.VsixPath, payloadSource, targetDirectory);
        items.Add(vsix is null
            ? new("Ali Companion VSIX", AliInstallReadinessStatus.Missing, "No Ali Companion VSIX package was found.")
            : new("Ali Companion VSIX", AliInstallReadinessStatus.Ready, $"VSIX package found: {vsix}"));

        var vsixInstaller = AliDesktopInstallDiscovery.ResolveVsixInstallerPath(options.VsixInstallerPath);
        items.Add(vsixInstaller is null
            ? new("VSIXInstaller", AliInstallReadinessStatus.Missing, "VSIXInstaller.exe was not found.")
            : new("VSIXInstaller", AliInstallReadinessStatus.Ready, $"VSIXInstaller found: {vsixInstaller}"));
    }
}
