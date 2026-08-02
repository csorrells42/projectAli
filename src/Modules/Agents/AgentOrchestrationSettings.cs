using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Legacy names retained for source and settings-file compatibility only.
/// No saved value can enable the retired secondary orchestration path.
/// </summary>
public static class MagenticPolicies
{
    public const string Off = "off";
    public const string AskFirst = "ask-first";
    public const string Automatic = "automatic-complex";

    public static IReadOnlyList<string> All { get; } = [Off];

    public static string Normalize(string? _) => Off;
}

public static class ProgrammingAgentModes
{
    public const string Off = "off";
    public const string Aider = "aider";
    public const string OpenHands = "openhands";
    public const string Hybrid = "hybrid";

    public static IReadOnlyList<string> All { get; } = [Off];

    public static string Normalize(string? _) => Off;
}

public sealed record AgentOrchestrationSettings
{
    // These two members keep dormant callers source-compatible while the single-loop
    // cut is completed. They are fixed, ignored on read, and omitted on write.
    [JsonIgnore]
    public string MagenticPolicy { get; init; } = MagenticPolicies.Off;

    [JsonIgnore]
    public int MagenticMaximumRounds { get; init; } = 6;

    [JsonIgnore]
    public string ProgrammingAgentMode { get; init; } = ProgrammingAgentModes.Off;

    [JsonIgnore]
    public bool AlwaysUseProgrammingAgent { get; init; }

    [JsonIgnore]
    public string OpenHandsWslDistribution { get; init; } = "Ubuntu-24.04";

    public AgentOrchestrationSettings Normalize() => this with
    {
        MagenticPolicy = MagenticPolicies.Off,
        MagenticMaximumRounds = 6,
        ProgrammingAgentMode = ProgrammingAgentModes.Off,
        AlwaysUseProgrammingAgent = false,
        OpenHandsWslDistribution = string.IsNullOrWhiteSpace(OpenHandsWslDistribution)
            ? "Ubuntu-24.04"
            : OpenHandsWslDistribution.Trim()
    };
}

public static class AgentOrchestrationSettingsStore
{
    private const string FileName = "agent-orchestration-settings.json";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string GetPath(string settingsRoot) => Path.Combine(settingsRoot, FileName);

    public static string GetCheckpointPath(string userDataRoot) =>
        Path.Combine(userDataRoot, "AgentWorkspaces", "WorkflowCheckpoints");

    public static AgentOrchestrationSettings LoadOrDefault(string settingsRoot)
    {
        try
        {
            var path = GetPath(settingsRoot);
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<AgentOrchestrationSettings>(File.ReadAllText(path), JsonOptions) ?? new()).Normalize()
                : new AgentOrchestrationSettings().Normalize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AgentOrchestrationSettings().Normalize();
        }
    }

    public static void Save(string settingsRoot, AgentOrchestrationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(settingsRoot);
        File.WriteAllText(GetPath(settingsRoot), JsonSerializer.Serialize(settings.Normalize(), JsonOptions));
    }
}
