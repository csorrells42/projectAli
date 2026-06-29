namespace Ali.Infrastructure.Voice;

public sealed record VoiceCaptureSafetyResult(
    bool Accepted,
    string Reason,
    string Message);

public static class VoiceCaptureSafetyGate
{
    public const string Silence = "silence";
    public const string TooQuiet = "too quiet";
    public const string Clipping = "clipping";
    public const string AcceptedReason = "usable";

    public static VoiceCaptureSafetyResult Evaluate(VoiceCaptureDiagnostics diagnostics)
    {
        if (diagnostics.IsSilent)
        {
            return Reject(Silence, "No speech signal was detected. I did not send anything.");
        }

        if (diagnostics.IsTooQuiet)
        {
            return Reject(TooQuiet, "The microphone input is too quiet. Raise gain or move closer and try again.");
        }

        if (diagnostics.IsClipping)
        {
            return Reject(Clipping, "The microphone input is clipping. Lower gain and try again.");
        }

        return new VoiceCaptureSafetyResult(
            Accepted: true,
            AcceptedReason,
            "Voice capture level is usable.");
    }

    private static VoiceCaptureSafetyResult Reject(string reason, string message) =>
        new(Accepted: false, reason, message);
}
