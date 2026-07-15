namespace Ali.Modules.Voice;

public static class TextToSpeechEngines
{
    public const string Piper = "Piper";
    public const string Kitten = "KittenTTS";

    public static readonly string[] All = [Piper, Kitten];

    public static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Piper
            : value.Trim() switch
            {
                var text when text.Equals(Kitten, StringComparison.OrdinalIgnoreCase) => Kitten,
                var text when text.Equals("Kitten", StringComparison.OrdinalIgnoreCase) => Kitten,
                var text when text.Equals(Piper, StringComparison.OrdinalIgnoreCase) => Piper,
                _ => Piper
            };
}
