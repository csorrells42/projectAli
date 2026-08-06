using System.Text.Json;

namespace Ali.Modules.Serena;

public sealed record SerenaRuntimeSettings(
    bool Enabled,
    string Command,
    string Context,
    string Transport,
    int StartupTimeoutSeconds,
    int RestartDelayMilliseconds,
    bool EnableWebDashboard,
    bool OpenWebDashboard)
{
    public const string ConfigurationFileName = "serena-runtime-defaults.json";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static string DefaultPath => Path.Combine(
        AppContext.BaseDirectory,
        "Configuration",
        ConfigurationFileName);

    public static SerenaRuntimeSettings LoadDefault() => Load(DefaultPath);

    public static SerenaRuntimeSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "The external Serena runtime configuration was not found.",
                fullPath);
        }

        SerenaRuntimeSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<SerenaRuntimeSettings>(
                    File.ReadAllText(fullPath),
                    JsonOptions)
                ?? throw new InvalidDataException(
                    "The Serena runtime configuration is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "The Serena runtime configuration is not valid JSON.",
                ex);
        }

        if (string.IsNullOrWhiteSpace(settings.Command)
            || string.IsNullOrWhiteSpace(settings.Context)
            || !string.Equals(settings.Transport, "stdio", StringComparison.OrdinalIgnoreCase)
            || settings.StartupTimeoutSeconds is < 1 or > 300
            || settings.RestartDelayMilliseconds is < 0 or > 30_000)
        {
            throw new InvalidDataException(
                "The Serena runtime configuration requires a command, context, stdio transport, a 1-300 second startup timeout, and a 0-30000 millisecond restart delay.");
        }

        return settings with
        {
            Command = settings.Command.Trim(),
            Context = settings.Context.Trim(),
            Transport = "stdio"
        };
    }
}
