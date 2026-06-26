using System.Windows;
using Ali.Infrastructure.Installation;

namespace Ali.App.Installer;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--gui", StringComparer.OrdinalIgnoreCase))
        {
            var app = new Application();
            var window = new InstallerWindow();
            app.Run(window);
            return window.ExitCode;
        }

        var parse = InstallerArguments.TryParse(args);
        if (!parse.Succeeded)
        {
            Console.Error.WriteLine(parse.Message);
            Console.WriteLine(InstallerArguments.Usage);
            return 2;
        }

        if (parse.ShowHelp)
        {
            Console.WriteLine(InstallerArguments.Usage);
            return 0;
        }

        var installer = new AliDesktopInstaller();
        var result = installer.InstallAsync(parse.Options, CancellationToken.None).GetAwaiter().GetResult();

        Console.WriteLine(result.Message);
        Console.WriteLine($"Target: {result.TargetDirectory}");
        Console.WriteLine($"Receipt: {result.ReceiptPath}");
        Console.WriteLine($"Installed files: {result.InstalledFiles.Count}");
        foreach (var dependency in result.DependencyMessages)
        {
            Console.WriteLine($"Dependency: {dependency}");
        }

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"Warning: {warning}");
        }

        return result.Succeeded ? 0 : 1;
    }
}

internal sealed record InstallerArguments(
    bool Succeeded,
    bool ShowHelp,
    string Message,
    AliDesktopInstallOptions Options)
{
    public const string Usage =
        """
        Ali.Setup usage:
          Ali.Setup.exe [--gui]
          Ali.Setup.exe [--payload <folder-or-zip>] [--local-root <path>] [--assistant-name <name>]
                        [--pull-runtime-model] [--runtime-model <model>]
                        [--pull-vision-model] [--vision-model <model>]
                        [--install-voice-resources] [--voice-resources <folder-or-zip>]
                        [--install-ollama] [--ollama-installer <path>] [--ollama-installer-url <url>]
                        [--install-vsix] [--vsix <path-to-vsix>] [--vsix-installer <path-to-VSIXInstaller.exe>]
                        [--install-vsix-only]
                        [--repair]
                        [--desktop-shortcut] [--start-menu-shortcut]
                        [--ollama <path-to-ollama.exe>] [--launch]

        Defaults:
          GUI opens when no arguments are supplied.
          Install target: %LOCALAPPDATA%\Ali\DevRun
          Personal data: %LOCALAPPDATA%\Ali\Profiles\<profileId>
          Assistant name: not created by default; first app launch asks the user
          Model pulls: skipped unless explicitly requested
          Ollama install: skipped unless explicitly requested
          Visual Studio Companion VSIX: skipped unless explicitly requested
          Voice resources: skipped unless explicitly requested on the command line

        Payload:
          If --payload is omitted, the installer looks for ali-payload.zip or payload\ beside itself,
          then for an embedded Payload\ali-payload.zip resource in packaged single-file builds.

        Voice resources:
          Place Ali.VoicePack.zip or lib\voice beside setup, or pass --voice-resources.
          Voice assets are sidecar files because local Piper/Whisper assets can be multi-GB.
        """;

    public static InstallerArguments TryParse(IReadOnlyList<string> args)
    {
        var options = AliDesktopInstallOptions.CreateDefault();
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            switch (arg.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                case "/?":
                    return new InstallerArguments(true, true, string.Empty, options);

                case "--gui":
                    break;

                case "--payload":
                    if (!TryReadValue(args, ref index, out var payload))
                    {
                        return Fail("--payload requires a folder or zip path.", options);
                    }

                    options = options with { PayloadPath = payload };
                    break;

                case "--local-root":
                    if (!TryReadValue(args, ref index, out var localRoot))
                    {
                        return Fail("--local-root requires a path.", options);
                    }

                    options = options with { LocalAliRoot = localRoot };
                    break;

                case "--assistant-name":
                    if (!TryReadValue(args, ref index, out var assistantName))
                    {
                        return Fail("--assistant-name requires a value.", options);
                    }

                    options = options with { AssistantName = assistantName };
                    break;

                case "--pull-runtime-model":
                    options = options with { PullRuntimeModel = true };
                    break;

                case "--runtime-model":
                    if (!TryReadValue(args, ref index, out var runtimeModel))
                    {
                        return Fail("--runtime-model requires a model id.", options);
                    }

                    options = options with { RuntimeModel = runtimeModel };
                    break;

                case "--pull-vision-model":
                    options = options with { PullVisionModel = true };
                    break;

                case "--install-voice-resources":
                    options = options with { InstallVoiceResources = true };
                    break;

                case "--voice-resources":
                    if (!TryReadValue(args, ref index, out var voiceResources))
                    {
                        return Fail("--voice-resources requires a folder or zip path.", options);
                    }

                    options = options with { VoiceResourcesPath = voiceResources, InstallVoiceResources = true };
                    break;

                case "--vision-model":
                    if (!TryReadValue(args, ref index, out var visionModel))
                    {
                        return Fail("--vision-model requires a model id.", options);
                    }

                    options = options with { VisionModel = visionModel };
                    break;

                case "--install-ollama":
                    options = options with { InstallOllamaIfMissing = true };
                    break;

                case "--ollama-installer":
                    if (!TryReadValue(args, ref index, out var ollamaInstaller))
                    {
                        return Fail("--ollama-installer requires a path to OllamaSetup.exe.", options);
                    }

                    options = options with { OllamaInstallerPath = ollamaInstaller, InstallOllamaIfMissing = true };
                    break;

                case "--ollama-installer-url":
                    if (!TryReadValue(args, ref index, out var ollamaInstallerUrl))
                    {
                        return Fail("--ollama-installer-url requires an absolute URL.", options);
                    }

                    if (!Uri.TryCreate(ollamaInstallerUrl, UriKind.Absolute, out var ollamaInstallerUri))
                    {
                        return Fail("--ollama-installer-url requires an absolute URL.", options);
                    }

                    options = options with { OllamaInstallerUri = ollamaInstallerUri, InstallOllamaIfMissing = true };
                    break;

                case "--install-vsix":
                    options = options with { InstallVisualStudioExtension = true };
                    break;

                case "--install-vsix-only":
                    options = options with
                    {
                        InstallApplication = false,
                        InstallVisualStudioExtension = true,
                        PullRuntimeModel = false,
                        PullVisionModel = false,
                        InstallOllamaIfMissing = false,
                        LaunchAfterInstall = false,
                        AssistantName = null,
                        InstallVoiceResources = false,
                        VoiceResourcesPath = null,
                        CreateDesktopShortcut = false,
                        CreateStartMenuShortcut = false
                    };
                    break;

                case "--repair":
                    options = options with { RepairExistingInstall = true };
                    break;

                case "--vsix":
                    if (!TryReadValue(args, ref index, out var vsix))
                    {
                        return Fail("--vsix requires a path to an Ali Companion VSIX package.", options);
                    }

                    options = options with { VsixPath = vsix, InstallVisualStudioExtension = true };
                    break;

                case "--vsix-installer":
                    if (!TryReadValue(args, ref index, out var vsixInstaller))
                    {
                        return Fail("--vsix-installer requires a path to VSIXInstaller.exe.", options);
                    }

                    options = options with { VsixInstallerPath = vsixInstaller };
                    break;

                case "--ollama":
                    if (!TryReadValue(args, ref index, out var ollama))
                    {
                        return Fail("--ollama requires a path to ollama.exe.", options);
                    }

                    options = options with { OllamaExecutablePath = ollama };
                    break;

                case "--launch":
                    options = options with { LaunchAfterInstall = true };
                    break;

                case "--desktop-shortcut":
                    options = options with { CreateDesktopShortcut = true };
                    break;

                case "--start-menu-shortcut":
                    options = options with { CreateStartMenuShortcut = true };
                    break;

                default:
                    return Fail($"Unknown argument: {arg}", options);
            }
        }

        return new InstallerArguments(true, false, string.Empty, options);
    }

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, out string value)
    {
        value = string.Empty;
        if (index + 1 >= args.Count)
        {
            return false;
        }

        value = args[++index];
        return !string.IsNullOrWhiteSpace(value);
    }

    private static InstallerArguments Fail(string message, AliDesktopInstallOptions options) =>
        new(false, false, message, options);
}
