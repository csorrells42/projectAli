namespace Ali.Modules.Voice;

public sealed record VoiceTranscriptRoutingDecision(
    bool PlaceTranscriptInComposer,
    bool SendAutomatically,
    string Description);

public static class VoiceTranscriptRouting
{
    public static VoiceTranscriptRoutingDecision Decide(bool voiceModeEnabled) =>
        voiceModeEnabled
            ? new VoiceTranscriptRoutingDecision(
                PlaceTranscriptInComposer: false,
                SendAutomatically: true,
                Description: "Voice mode is on; accepted transcripts may be sent hands-free.")
            : new VoiceTranscriptRoutingDecision(
                PlaceTranscriptInComposer: true,
                SendAutomatically: false,
                Description: "Voice mode is off; accepted transcripts go to the composer for review.");
}
