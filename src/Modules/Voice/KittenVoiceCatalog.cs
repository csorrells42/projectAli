namespace Ali.Modules.Voice;

public sealed record KittenVoiceChoice(string Label, string VoiceId);

public static class KittenVoiceCatalog
{
    public const string DefaultVoiceId = "expr-voice-2-f";

    private static readonly KittenVoiceChoice[] Voices =
    [
        new("Luna", "expr-voice-2-f"),
        new("Jasper", "expr-voice-2-m"),
        new("Rosie", "expr-voice-3-f"),
        new("Bruno", "expr-voice-3-m"),
        new("Bella", "expr-voice-4-f"),
        new("Hugo", "expr-voice-4-m"),
        new("Kiki", "expr-voice-5-f"),
        new("Leo", "expr-voice-5-m")
    ];

    public static IReadOnlyList<KittenVoiceChoice> All => Voices;

    public static bool IsKnownVoice(string? voiceId) =>
        Voices.Any(voice => voice.VoiceId.Equals(voiceId, StringComparison.OrdinalIgnoreCase)
                            || voice.Label.Equals(voiceId, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string? voiceId) =>
        Voices.FirstOrDefault(voice =>
            voice.VoiceId.Equals(voiceId, StringComparison.OrdinalIgnoreCase)
            || voice.Label.Equals(voiceId, StringComparison.OrdinalIgnoreCase))?.VoiceId
        ?? DefaultVoiceId;
}
