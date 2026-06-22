using System.Text.Json;

namespace Ali.Infrastructure.Voice;

public static class VoiceRuntimeSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string GetSettingsPath(string dataDirectory) =>
        Path.Combine(dataDirectory, "voice-settings.json");

    public static VoiceRuntimeSettings LoadOrDefault(string dataDirectory)
    {
        var filePath = GetSettingsPath(dataDirectory);
        if (!File.Exists(filePath))
        {
            return new VoiceRuntimeSettings();
        }

        var json = File.ReadAllText(filePath);
        var settings = JsonSerializer.Deserialize<VoiceRuntimeSettings>(json, JsonOptions);
        return settings is null
            ? new VoiceRuntimeSettings()
            : settings with { SelectedInputPreset = VoiceInputPreset.Normalize(settings.SelectedInputPreset) };
    }

    public static void Save(string dataDirectory, VoiceRuntimeSettings settings)
    {
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(
            GetSettingsPath(dataDirectory),
            JsonSerializer.Serialize(
                settings with { SelectedInputPreset = VoiceInputPreset.Normalize(settings.SelectedInputPreset) },
                JsonOptions));
    }
}
