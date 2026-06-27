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
            : Normalize(settings);
    }

    public static void Save(string dataDirectory, VoiceRuntimeSettings settings)
    {
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(
            GetSettingsPath(dataDirectory),
            JsonSerializer.Serialize(Normalize(settings), JsonOptions));
    }

    private static VoiceRuntimeSettings Normalize(VoiceRuntimeSettings settings) =>
        settings with
        {
            SelectedInputPreset = VoiceInputPreset.Normalize(settings.SelectedInputPreset),
            TextToSpeechEngine = TextToSpeechEngines.Normalize(settings.TextToSpeechEngine),
            KittenVoiceId = KittenVoiceCatalog.Normalize(settings.KittenVoiceId)
        };
}
