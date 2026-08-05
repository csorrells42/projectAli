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

public sealed record AgentOrchestrationSettings
{
    // Legacy orchestration members are fixed, ignored on read, and omitted on write.
    [JsonIgnore]
    public string MagenticPolicy { get; init; } = MagenticPolicies.Off;

    [JsonIgnore]
    public int MagenticMaximumRounds { get; init; } = 6;

    public AgentOrchestrationSettings Normalize() => this with
    {
        MagenticPolicy = MagenticPolicies.Off,
        MagenticMaximumRounds = 6
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
