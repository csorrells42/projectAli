using System.Text.Json;
using Ali.Core.Identity;

namespace Ali.Infrastructure.Identity;

public static class AssistantProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string GetProfilePath(string dataDirectory) =>
        Path.Combine(dataDirectory, "assistant-profile.json");

    public static bool Exists(string dataDirectory) =>
        File.Exists(GetProfilePath(dataDirectory));

    public static AssistantProfile? Load(string dataDirectory)
    {
        var path = GetProfilePath(dataDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<AssistantProfile>(stream, JsonOptions)?.Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public static AssistantProfile LoadOrDefault(string dataDirectory) =>
        Load(dataDirectory) ?? AssistantProfile.CreateDefault();

    public static AssistantProfile Save(string dataDirectory, AssistantProfile profile)
    {
        var normalized = profile.Normalize();
        Directory.CreateDirectory(dataDirectory);
        var path = GetProfilePath(dataDirectory);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, normalized, JsonOptions);
            }

            File.Move(tempPath, path, overwrite: true);
            return normalized;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
