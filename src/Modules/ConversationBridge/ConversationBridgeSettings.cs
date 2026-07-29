using System.Security.Cryptography;
using System.Text.Json;

namespace Ali.Modules.ConversationBridge;

public sealed record ConversationBridgeSettings
{
    public bool Enabled { get; init; }

    public bool AllowPermissionDecisions { get; init; } = true;

    public int Port { get; init; } = 8772;

    public string AuthenticationToken { get; init; } = CreateToken();

    public string Endpoint => $"http://127.0.0.1:{Port}";

    public ConversationBridgeSettings Normalize() => this with
    {
        Port = Port is >= 1024 and <= 65535 ? Port : 8772,
        AuthenticationToken = string.IsNullOrWhiteSpace(AuthenticationToken)
            ? CreateToken()
            : AuthenticationToken.Trim()
    };

    private static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}

public static class ConversationBridgeSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string GetPath(string dataRoot) =>
        Path.Combine(dataRoot, "ConversationBridge", "conversation-bridge.json");

    public static ConversationBridgeSettings LoadOrCreate(string dataRoot)
    {
        var path = GetPath(dataRoot);
        if (File.Exists(path))
        {
            try
            {
                return (JsonSerializer.Deserialize<ConversationBridgeSettings>(
                    File.ReadAllText(path),
                    JsonOptions) ?? new()).Normalize();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A fresh current-format settings file is generated below.
            }
        }

        return Save(dataRoot, new ConversationBridgeSettings());
    }

    public static ConversationBridgeSettings Save(
        string dataRoot,
        ConversationBridgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        var path = GetPath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
        return normalized;
    }
}
