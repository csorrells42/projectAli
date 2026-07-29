using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coding.Toolchains;

namespace Ali.Modules.Integrations;

public sealed record AliEditorIntegrationReport(
    bool NotepadPlusPlusInstalled,
    string NotepadPlusPlusVersion,
    bool NotepadPlusPlusRunning,
    int InstalledNotepadPlusPlusPlugins,
    int DesiredNotepadPlusPlusPlugins,
    int VisualStudioInstances,
    string Summary,
    string Details);

public static class AliEditorIntegrationManager
{
    public static AliEditorIntegrationReport Inspect(string? applicationRoot = null)
    {
        var root = Path.GetFullPath(applicationRoot ?? AppContext.BaseDirectory);
        var manifest = LoadManifest(root);
        var notepadPath = FindNotepadPlusPlus();
        var running = Process.GetProcessesByName("notepad++").Length > 0;
        var pluginRoot = notepadPath is null
            ? string.Empty
            : Path.Combine(Path.GetDirectoryName(notepadPath)!, "plugins");
        var installed = manifest.Plugins.Count(plugin =>
            File.Exists(Path.Combine(pluginRoot, plugin.Folder, $"{plugin.Folder}.dll")));
        var version = notepadPath is null
            ? "not installed"
            : FileVersionInfo.GetVersionInfo(notepadPath).ProductVersion ?? "unknown";
        var visualStudio = AliDeveloperToolPaths.DiscoverVisualStudio();
        var summary = notepadPath is null
            ? $"Notepad++ was not found. Visual Studio instances: {visualStudio.Count}."
            : $"Notepad++ {version}: {installed}/{manifest.Plugins.Count} toolkit plugins installed. Visual Studio instances: {visualStudio.Count}.";
        var details = new StringBuilder()
            .AppendLine("Notepad++")
            .AppendLine($"  Executable: {notepadPath ?? "not found"}")
            .AppendLine($"  Running: {(running ? "yes - close it before installing or repairing plugins" : "no")}")
            .AppendLine($"  Toolkit: {installed}/{manifest.Plugins.Count} installed")
            .AppendLine()
            .AppendLine("Visual Studio")
            .AppendLine(visualStudio.Count == 0
                ? "  No instance detected. Ali's standalone Roslyn, LSP, DAP, compiler, and build tools still work."
                : $"  {visualStudio.Count} instance(s) detected; Ali can launch the IDE and use its MSBuild, MSVC, CMake, test, debugger, and LLVM components when installed.")
            .AppendLine()
            .AppendLine("Upgrade behavior")
            .AppendLine("  Refresh re-detects editor versions. Install/Repair consults Notepad++'s official x64 catalog, verifies SHA-256, and reapplies the toolkit without changing your editing preferences.")
            .ToString().TrimEnd();
        return new(notepadPath is not null, version, running, installed, manifest.Plugins.Count, visualStudio.Count, summary, details);
    }

    public static async Task<int> InstallOrRepairNotepadPlusPlusAsync(string? applicationRoot = null)
    {
        var root = Path.GetFullPath(applicationRoot ?? AppContext.BaseDirectory);
        var script = Path.Combine(root, "tools", "ConfigureEditorIntegrations.ps1");
        if (!File.Exists(script))
            throw new FileNotFoundException("Ali's editor integration repair tool is missing.", script);

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = root
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-Action");
        start.ArgumentList.Add("InstallNotepadPlusPlus");
        start.ArgumentList.Add("-ApplicationRoot");
        start.ArgumentList.Add(root);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the editor integration installer.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    public static void OpenGuide(string? applicationRoot = null)
    {
        var root = Path.GetFullPath(applicationRoot ?? AppContext.BaseDirectory);
        var guide = Path.Combine(root, "docs", "EDITOR-INTEGRATIONS.md");
        if (!File.Exists(guide)) throw new FileNotFoundException("Ali's editor integration guide is missing.", guide);
        Process.Start(new ProcessStartInfo(guide) { UseShellExecute = true });
    }

    private static string? FindNotepadPlusPlus()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Notepad++", "notepad++.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Notepad++", "notepad++.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Notepad++", "notepad++.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static EditorManifest LoadManifest(string root)
    {
        var path = Path.Combine(root, "editor-integrations.json");
        if (!File.Exists(path)) return new([]);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var plugins = document.RootElement.GetProperty("notepadPlusPlus").GetProperty("plugins")
            .EnumerateArray()
            .Select(item => new EditorPlugin(item.GetProperty("folder").GetString() ?? string.Empty))
            .Where(item => item.Folder.Length > 0)
            .ToArray();
        return new(plugins);
    }

    private sealed record EditorManifest(IReadOnlyList<EditorPlugin> Plugins);
    private sealed record EditorPlugin(string Folder);
}
