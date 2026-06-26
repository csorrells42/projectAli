namespace Ali.Infrastructure.Installation;

public sealed record AliDesktopInstallOptions(
    string? PayloadPath,
    string LocalAliRoot,
    string? AssistantName = null,
    bool PullRuntimeModel = false,
    string? RuntimeModel = null,
    bool PullVisionModel = false,
    string? VisionModel = null,
    string? OllamaExecutablePath = null,
    bool InstallOllamaIfMissing = false,
    string? OllamaInstallerPath = null,
    Uri? OllamaInstallerUri = null,
    bool LaunchAfterInstall = false,
    bool InstallApplication = true,
    bool InstallVisualStudioExtension = false,
    string? VsixPath = null,
    string? VsixInstallerPath = null,
    bool CreateDesktopShortcut = false,
    bool CreateStartMenuShortcut = false,
    bool RepairExistingInstall = false)
{
    public static AliDesktopInstallOptions CreateDefault(string? payloadPath = null) =>
        new(
            payloadPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ali"),
            RuntimeModel: "ali-deepseek-coder-v2:16b-low",
            VisionModel: "qwen3-vl:8b");
}

public sealed record AliDesktopInstallResult(
    bool Succeeded,
    string Message,
    string TargetDirectory,
    string ReceiptPath,
    IReadOnlyList<string> InstalledFiles,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> DependencyMessages);
