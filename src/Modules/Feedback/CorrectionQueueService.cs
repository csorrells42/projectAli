using Ali.Modules.Evidence;
using Ali.Modules.Runtime.Models;
using Ali.Modules.Voice;
using System.Text;

namespace Ali.Modules.Feedback;

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
            SuspiciousOrNoSpeech: voiceMetadata?.SuspiciousOrNoSpeech ?? false,
            VoiceRejectionReason: voiceMetadata?.RejectionReason,
            VoiceInputPeak: voiceMetadata?.InputPeak,
            VoiceInputRms: voiceMetadata?.InputRms,
            VoiceInputLevelState: voiceMetadata?.InputLevelState);

        await store.SaveAsync(report, cancellationToken).ConfigureAwait(false);
        return report;
    }

    public async Task<IReadOnlyList<CorrectionReport>> ListAsync(CancellationToken cancellationToken)
    {
        var reports = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        return reports
            .OrderByDescending(report => report.CreatedAt)
            .ToList();
    }

    public async Task<CorrectionReport?> SetStatusAsync(
        string correctionId,
        CorrectionStatus status,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(correctionId))
        {
            return null;
        }

        var reports = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        var report = reports.FirstOrDefault(item => item.Id.Equals(correctionId, StringComparison.OrdinalIgnoreCase));
        if (report is null)
        {
            return null;
        }

        var updated = report with { Status = status };
        await store.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<string?> ExportOneMarkdownAsync(
        string correctionId,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var reports = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        var report = reports.FirstOrDefault(item => item.Id.Equals(correctionId, StringComparison.OrdinalIgnoreCase));
        if (report is null)
        {
            return null;
        }

        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"{SafeFileName(report.Id)}.md");
        await File.WriteAllTextAsync(path, RenderMarkdown([report]), cancellationToken).ConfigureAwait(false);
        await SetStatusAsync(report.Id, CorrectionStatus.Exported, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<string> ExportAllMarkdownAsync(
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var reports = await ListAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"corrections_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.md");
        await File.WriteAllTextAsync(path, RenderMarkdown(reports), cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string RenderMarkdown(IReadOnlyList<CorrectionReport> reports)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Ali Correction Queue Export");
        builder.AppendLine();
        builder.AppendLine($"Exported: {DateTimeOffset.Now:O}");
        builder.AppendLine();

        foreach (var report in reports)
        {
            builder.AppendLine($"## {report.Id}");
            builder.AppendLine();
            builder.AppendLine($"- Status: {DisplayStatus(report.Status)}");
            builder.AppendLine($"- Created: {report.CreatedAt:O}");
            builder.AppendLine($"- Conversation: {report.ConversationId}");
            builder.AppendLine($"- User message: {report.UserMessageId}");
            builder.AppendLine($"- Assistant message: {report.AssistantMessageId}");
            builder.AppendLine($"- Category: {report.Category}");
            builder.AppendLine($"- Model: {report.ModelPackage}");
            builder.AppendLine($"- Runtime endpoint: {report.RuntimeEndpoint}");
            builder.AppendLine($"- Input origin: {report.InputOrigin}");
            if (!string.IsNullOrWhiteSpace(report.UserNote))
            {
                builder.AppendLine($"- Note: {report.UserNote}");
            }

            builder.AppendLine();
            builder.AppendLine("### Exact User Question");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine(report.Question);
            builder.AppendLine("```");
            builder.AppendLine();
            builder.AppendLine("### Exact Assistant Answer");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine(report.Answer);
            builder.AppendLine("```");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static string DisplayStatus(CorrectionStatus status) =>
        status switch
        {
            CorrectionStatus.New => "unresolved",
            CorrectionStatus.Reviewed => "reviewed",
            CorrectionStatus.Exported => "exported",
            CorrectionStatus.Ignored => "ignored",
            _ => status.ToString().ToLowerInvariant()
        };

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }
}
