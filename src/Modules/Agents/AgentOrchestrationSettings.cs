using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ali.Modules.Coordinator;

public static class MagenticPolicies
{
    public const string Off = "off";
    public const string AskFirst = "ask-first";
    public const string Automatic = "automatic-complex";

    public static IReadOnlyList<string> All { get; } = [Off, AskFirst, Automatic];

    public static string Normalize(string? value) =>
        All.Contains(value?.Trim(), StringComparer.OrdinalIgnoreCase)
            ? All.First(item => item.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase))
            : AskFirst;
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
    public string MagenticPolicy { get; init; } = MagenticPolicies.AskFirst;

    public int MagenticMaximumRounds { get; init; } = 6;

    [JsonIgnore]
    public string ProgrammingAgentMode { get; init; } = ProgrammingAgentModes.Off;

    [JsonIgnore]
    public bool AlwaysUseProgrammingAgent { get; init; }

    [JsonIgnore]
    public string OpenHandsWslDistribution { get; init; } = "Ubuntu-24.04";

    public AgentOrchestrationSettings Normalize() => this with
    {
        MagenticPolicy = MagenticPolicies.Normalize(MagenticPolicy),
        MagenticMaximumRounds = Math.Clamp(MagenticMaximumRounds, 2, 12),
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
