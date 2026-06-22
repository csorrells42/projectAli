using Ali.Core.Evidence;
using Ali.Core.Models;
using Ali.Core.Voice;

namespace Ali.Core.Feedback;

public sealed class CorrectionQueueService(ICorrectionQueueStore store)
{
    public Task<CorrectionReport> FlagIncorrectAsync(
        string conversationId,
        string userMessageId,
        string assistantMessageId,
        string question,
        string answer,
        ModelProfile modelProfile,
        EvidenceStatus answerEvidenceStatus,
        CorrectionCategory category,
        string? userNote,
        CancellationToken cancellationToken) =>
        FlagIncorrectAsync(
            conversationId,
            userMessageId,
            assistantMessageId,
            question,
            answer,
            modelProfile,
            answerEvidenceStatus,
            category,
            userNote,
            voiceMetadata: null,
            cancellationToken);

    public async Task<CorrectionReport> FlagIncorrectAsync(
        string conversationId,
        string userMessageId,
        string assistantMessageId,
        string question,
        string answer,
        ModelProfile modelProfile,
        EvidenceStatus answerEvidenceStatus,
        CorrectionCategory category,
        string? userNote,
        VoiceTurnMetadata? voiceMetadata,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);

        var report = new CorrectionReport(
            Id: $"corr_{Guid.NewGuid():N}",
            ConversationId: conversationId,
            UserMessageId: userMessageId,
            AssistantMessageId: assistantMessageId,
            Question: question,
            Answer: answer,
            Category: category,
            Status: CorrectionStatus.New,
            CreatedAt: DateTimeOffset.UtcNow,
            RuntimeKind: modelProfile.RuntimeKind,
            RuntimeLocation: modelProfile.RuntimeLocation,
            RuntimeEndpoint: modelProfile.RuntimeEndpoint,
            ModelPackage: modelProfile.PackageId,
            Quantization: modelProfile.Quantization,
            ContextTokens: modelProfile.ContextTokens,
            OutputTokenLimit: modelProfile.OutputTokenLimit,
            Temperature: modelProfile.Temperature,
            StreamingEnabled: modelProfile.StreamingEnabled,
            AnswerEvidenceStatus: answerEvidenceStatus,
            UserNote: userNote,
            InputOrigin: voiceMetadata?.InputOrigin ?? VoiceInputOrigin.Typed,
            VoiceTranscript: voiceMetadata?.Transcript,
            SpeechToTextProvider: voiceMetadata?.SpeechToTextProvider,
            SpeechToTextMode: voiceMetadata?.SpeechToTextMode,
            TextToSpeechProvider: voiceMetadata?.TextToSpeechProvider,
            TextToSpeechVoice: voiceMetadata?.TextToSpeechVoice,
            RawAudioRetained: voiceMetadata?.RawAudioRetained ?? false,
            VoiceInputDeviceNumber: voiceMetadata?.InputDeviceNumber,
            VoiceInputDeviceName: voiceMetadata?.InputDeviceName,
            VoiceInputChannelMode: voiceMetadata?.InputChannelMode,
            VoiceInputPreset: voiceMetadata?.InputPreset,
            VoiceExtraInputGainDb: voiceMetadata?.ExtraInputGainDb,
            VoiceNormalizeBeforeStt: voiceMetadata?.NormalizeBeforeStt ?? false,
            SpeechToTextModel: voiceMetadata?.SpeechToTextModel,
            TextToSpeechModel: voiceMetadata?.TextToSpeechModel,
            SuspiciousOrNoSpeech: voiceMetadata?.SuspiciousOrNoSpeech ?? false);

        await store.SaveAsync(report, cancellationToken).ConfigureAwait(false);
        return report;
    }
}
