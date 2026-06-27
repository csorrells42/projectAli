namespace Ali.Infrastructure.Voice;

public sealed record KittenVoiceChoice(string Label, string VoiceId);

public static class KittenVoiceCatalog
{
    public const string DefaultVoiceId = "Bella";

    private static readonly KittenVoiceChoice[] Voices =
    [
        new("Bella", "Bella"),
        new("Jasper", "Jasper"),
        new("Luna", "Luna"),
        new("Bruno", "Bruno"),
        new("Rosie", "Rosie"),
        new("Hugo", "Hugo"),
        new("Kiki", "Kiki"),
        new("Leo", "Leo")
    ];

    public static IReadOnlyList<KittenVoiceChoice> All => Voices;

    public static bool IsKnownVoice(string? voiceId) =>
        Voices.Any(voice => voice.VoiceId.Equals(voiceId, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string? voiceId) =>
        Voices.FirstOrDefault(voice => voice.VoiceId.Equals(voiceId, StringComparison.OrdinalIgnoreCase))?.VoiceId
        ?? DefaultVoiceId;
}
