using System.Text.RegularExpressions;

namespace Ali.Core.Voice;

public static partial class VoiceCommandSafety
{
    public static bool RequiresVisibleConfirmation(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        var normalized = transcript.Trim();
        return DestructiveCommandRegex().IsMatch(normalized)
            || ExecutionCommandRegex().IsMatch(normalized)
            || InstallCommandRegex().IsMatch(normalized)
            || CalendarWriteRegex().IsMatch(normalized)
            || EmailWriteRegex().IsMatch(normalized)
            || FileWriteRegex().IsMatch(normalized)
            || MemoryDeleteRegex().IsMatch(normalized)
            || ModelSwitchRegex().IsMatch(normalized);
    }

    public static string BlockedPhaseOneCMessage() =>
        "Risky voice actions are not enabled in Phase 1C. Type the request and confirm it visibly before Ali can do anything destructive or state-changing.";

    [GeneratedRegex(@"\b(delete|remove|erase|wipe|destroy|format|overwrite)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DestructiveCommandRegex();

    [GeneratedRegex(@"\b(run|execute|launch|start|open powershell|open command prompt|cmd|terminal)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutionCommandRegex();

    [GeneratedRegex(@"\b(install|uninstall|upgrade|download package|pull model)\b", RegexOptions.IgnoreCase)]
    private static partial Regex InstallCommandRegex();

    [GeneratedRegex(@"\b(add|create|move|modify|change|delete|cancel)\b.*\b(calendar|appointment|meeting|reminder)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CalendarWriteRegex();

    [GeneratedRegex(@"\b(send|delete|remove|modify|change|draft|reply|forward)\b.*\b(email|mail|message)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailWriteRegex();

    [GeneratedRegex(@"\b(copy|move|rename|edit|modify|change|write|save)\b.*\b(file|folder|directory)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FileWriteRegex();

    [GeneratedRegex(@"\b(delete|forget|remove|change|modify)\b.*\b(memory|memories|remembered)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MemoryDeleteRegex();

    [GeneratedRegex(@"\b(switch|change|load|activate)\b.*\b(model|quantization|context)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ModelSwitchRegex();
}
