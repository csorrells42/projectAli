namespace Ali.Core.Voice;

public enum VoiceInputOrigin
{
    Typed,
    Voice
}

public sealed record VoiceAudioInput(
    string FilePath,
    string ContentType,
    bool RetainAudio,
    DateTimeOffset CreatedAt);

public sealed record SpeechTranscript(
    string Text,
    string ProviderName,
    string Mode,
    DateTimeOffset CreatedAt);

public sealed record VoiceSettings(
    string VoiceId,
    double Rate,
    bool RetainAudio);

public sealed record SpeechSynthesisResult(
    string AudioPath,
    string ProviderName,
    string VoiceId,
    bool RetainAudio,
    DateTimeOffset CreatedAt);

public sealed record VoiceTurnMetadata(
    VoiceInputOrigin InputOrigin,
    string? Transcript,
    string? SpeechToTextProvider,
    string? SpeechToTextMode,
    string? TextToSpeechProvider,
    string? TextToSpeechVoice,
    bool RawAudioRetained,
    int? InputDeviceNumber = null,
    string? InputDeviceName = null,
    string? InputPreset = null,
    string? SpeechToTextModel = null,
    string? TextToSpeechModel = null,
    bool SuspiciousOrNoSpeech = false);

public interface IVoiceRecorder
{
    bool IsRecording { get; }

    Task StartAsync(string outputDirectory, CancellationToken cancellationToken);

    Task<VoiceAudioInput> StopAsync(CancellationToken cancellationToken);

    void Cancel();
}

public interface ISpeechToTextProvider
{
    string ProviderName { get; }

    string Mode { get; }

    bool IsConfigured { get; }

    Task<SpeechTranscript> TranscribeAsync(VoiceAudioInput audioInput, CancellationToken cancellationToken);
}

public interface ITextToSpeechProvider
{
    string ProviderName { get; }

    string VoiceId { get; }

    bool IsConfigured { get; }

    Task<SpeechSynthesisResult> SynthesizeAsync(
        string text,
        VoiceSettings settings,
        CancellationToken cancellationToken);
}

public interface ISpeechPlayer
{
    bool IsSpeaking { get; }

    Task PlayAsync(string audioPath, CancellationToken cancellationToken);

    void Stop();
}
