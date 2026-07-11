using System.Text.RegularExpressions;

namespace Ali.Core.Voice;

public static partial class VoiceCommandSafety
{
    public static bool RequiresVisibleConfirmation(string transcript, bool programmingModeActive = false)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        var normalized = transcript.Trim();
        var alwaysRequiresConfirmation = DestructiveCommandRegex().IsMatch(normalized)
            || ShellToolRegex().IsMatch(normalized)
            || InstallCommandRegex().IsMatch(normalized)
            || CalendarWriteRegex().IsMatch(normalized)
            || EmailWriteRegex().IsMatch(normalized)
            || MemoryDeleteRegex().IsMatch(normalized)
            || ModelSwitchRegex().IsMatch(normalized);
        if (alwaysRequiresConfirmation)
        {
            return true;
        }

        if (programmingModeActive)
        {
            return FileWriteRegex().IsMatch(normalized)
                   && !CodingArtifactOrTaskRegex().IsMatch(normalized);
        }

        return ExecutionCommandRegex().IsMatch(normalized)
            || FileWriteRegex().IsMatch(normalized);
    }

    public static string BlockedPhaseOneCMessage() =>
        "Risky voice actions are not enabled in Phase 1C. Type the request and confirm it visibly before Ali can do anything destructive or state-changing.";

    [GeneratedRegex(@"\b(delete|remove|erase|wipe|destroy|format|overwrite)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DestructiveCommandRegex();

    [GeneratedRegex(@"\b(run|execute|launch|start|open powershell|open command prompt|cmd|terminal)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutionCommandRegex();

    [GeneratedRegex(@"\b(powershell|pwsh|command prompt|cmd\.exe|shell command|terminal)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ShellToolRegex();

    [GeneratedRegex(@"\b(install|uninstall|upgrade|download package|pull model)\b", RegexOptions.IgnoreCase)]
    private static partial Regex InstallCommandRegex();

    [GeneratedRegex(@"\b(add|create|move|modify|change|delete|cancel)\b.*\b(calendar|appointment|meeting|reminder)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CalendarWriteRegex();

    [GeneratedRegex(@"\b(send|delete|remove|modify|change|draft|reply|forward)\b.*\b(email|mail|message)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailWriteRegex();

    [GeneratedRegex(@"\b(copy|move|rename|edit|modify|change|write|save)\b.*\b(file|folder|directory)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FileWriteRegex();

    [GeneratedRegex(@"\b(wpf|xaml|window|app|application|project|solution|csproj|sln|class|method|function|viewmodel|view\s+model|code|test|tests|build|compile|debug|feature|bug|ui|screen|dashboard|form|button|grid|textbox|program)\b|\.(?:cs|xaml|csproj|sln|slnx)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CodingArtifactOrTaskRegex();

    [GeneratedRegex(@"\b(delete|forget|remove|change|modify)\b.*\b(memory|memories|remembered)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MemoryDeleteRegex();

    [GeneratedRegex(@"\b(switch|change|load|activate)\b.*\b(model|quantization|context)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ModelSwitchRegex();
}
