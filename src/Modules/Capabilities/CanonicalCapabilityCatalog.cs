namespace Ali.Modules.Capabilities;

public static class CanonicalCapabilityCatalog
{
    public static IReadOnlyList<CapabilityGroupDescriptor> Groups { get; } =
        Array.AsReadOnly<CapabilityGroupDescriptor>(
    [
        new(CapabilityGroupIds.FilesAndArchives, "Files and archives", "Read, create, organize, compare, hash, and archive files and folders.", true),
        new(CapabilityGroupIds.ProgrammingCore, "Programming core", "Shared project inspection, source navigation, editing, build, run, and verification foundations.", true),
        new(CapabilityGroupIds.CSharpDotNetRoslyn, "C# / .NET / Roslyn", "C# and .NET engineering with Roslyn semantic inspection, transformations, builds, tests, debugging, and delivery.", true),
        new(CapabilityGroupIds.Python, "Python", "Python project creation, analysis, execution, testing, packaging, and debugging.", true),
        new(CapabilityGroupIds.WebHtmlCssJavaScriptTypeScript, "HTML / CSS / JavaScript / TypeScript", "Browser-oriented HTML, CSS, JavaScript, and TypeScript authoring, analysis, and verification.", true),
        new(CapabilityGroupIds.Java, "Java", "Java project creation, language tooling, builds, tests, packaging, and debugging.", true),
        new(CapabilityGroupIds.NativeCppGcc, "Native C / C++ / GCC", "Native and Visual C++ source analysis, GCC-family builds, tests, and diagnostics.", true),
        new(CapabilityGroupIds.Arduino, "Arduino", "Arduino sketches, board and library inspection, compilation, and embedded verification.", true),
        new(CapabilityGroupIds.RaspberryPi, "Raspberry Pi", "Raspberry Pi software, GPIO-oriented projects, remote deployment preparation, and diagnostics.", true),
        new(CapabilityGroupIds.DevOpsArchitectureQuality, "DevOps / architecture / quality", "Cross-language architecture, dependency, security, quality, delivery, and operations analysis.", true),
        new(CapabilityGroupIds.VisualStudio, "Visual Studio", "Visual Studio discovery, solution operations, IDE-aware project work, and integration support.", true)
    ]);

    public static IReadOnlyList<CapabilityPresetDescriptor> Presets { get; } =
        Array.AsReadOnly<CapabilityPresetDescriptor>(
    [
        new(
            CapabilityPresetIds.CSharp,
            "C#",
            "Enable files, shared programming, C#/.NET/Roslyn, and engineering quality tooling.",
            Array.AsReadOnly<string>(
            [
                CapabilityGroupIds.FilesAndArchives,
                CapabilityGroupIds.ProgrammingCore,
                CapabilityGroupIds.CSharpDotNetRoslyn,
                CapabilityGroupIds.DevOpsArchitectureQuality
            ])),
        new(
            CapabilityPresetIds.Java,
            "Java",
            "Enable files, shared programming, Java, and engineering quality tooling.",
            Array.AsReadOnly<string>(
            [
                CapabilityGroupIds.FilesAndArchives,
                CapabilityGroupIds.ProgrammingCore,
                CapabilityGroupIds.Java,
                CapabilityGroupIds.DevOpsArchitectureQuality
            ])),
        new(
            CapabilityPresetIds.Arduino,
            "Arduino",
            "Enable files, shared programming, native C/C++, Arduino, and engineering quality tooling.",
            Array.AsReadOnly<string>(
            [
                CapabilityGroupIds.FilesAndArchives,
                CapabilityGroupIds.ProgrammingCore,
                CapabilityGroupIds.NativeCppGcc,
                CapabilityGroupIds.Arduino,
                CapabilityGroupIds.DevOpsArchitectureQuality
            ])),
        new(
            CapabilityPresetIds.FileTools,
            "File Tools",
            "Enable file, folder, metadata, hashing, and archive tooling.",
            Array.AsReadOnly<string>([CapabilityGroupIds.FilesAndArchives]))
    ]);

    public static CapabilityGroupDescriptor GetGroup(string groupId) =>
        Groups.FirstOrDefault(group => string.Equals(group.Id, groupId, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Unknown capability group '{groupId}'.");

    public static CapabilityPresetDescriptor GetPreset(string presetId) =>
        Presets.FirstOrDefault(preset => string.Equals(preset.Id, presetId, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Unknown capability preset '{presetId}'.");
}
