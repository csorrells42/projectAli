using Ali.Core.Feedback;
using Ali.Core.Voice;

namespace Ali.App.Wpf.ViewModels;

public sealed class CorrectionReviewItemViewModel(CorrectionReport report) : ObservableObject
{
    private CorrectionReport _report = report;

    public CorrectionReport Report
    {
        get => _report;
        private set
        {
            if (SetProperty(ref _report, value))
            {
                OnPropertyChanged(nameof(Id));
                OnPropertyChanged(nameof(DisplayStatus));
                OnPropertyChanged(nameof(CreatedAtText));
                OnPropertyChanged(nameof(ConversationText));
                OnPropertyChanged(nameof(QuestionPreview));
                OnPropertyChanged(nameof(AnswerPreview));
                OnPropertyChanged(nameof(Question));
                OnPropertyChanged(nameof(Answer));
                OnPropertyChanged(nameof(RuntimeText));
                OnPropertyChanged(nameof(VoiceText));
                OnPropertyChanged(nameof(AttachmentText));
                OnPropertyChanged(nameof(SafetyText));
            }
        }
    }

    public string Id => Report.Id;

    public string DisplayStatus => CorrectionQueueService.DisplayStatus(Report.Status);

    public string CreatedAtText => Report.CreatedAt.ToLocalTime().ToString("g");

    public string ConversationText => $"{Report.ConversationId} / {Report.AssistantMessageId}";

    public string QuestionPreview => Preview(Report.Question);

    public string AnswerPreview => Preview(Report.Answer);

    public string Question => Report.Question;

    public string Answer => Report.Answer;

    public string RuntimeText =>
        $"{Report.ModelPackage} | {Report.Quantization} | {Report.ContextTokens} ctx | {Report.OutputTokenLimit} out";

    public string VoiceText =>
        Report.InputOrigin == VoiceInputOrigin.Voice
            ? $"Voice: {Report.SpeechToTextProvider} -> {Report.TextToSpeechProvider} / {Report.TextToSpeechVoice}"
            : "Voice: not voice-origin";

    public string AttachmentText =>
        Report.Category == CorrectionCategory.MisreadScreenshot
            ? "Attachment: image-origin correction"
            : "Attachment: none recorded";

    public string SafetyText =>
        Report.SuspiciousOrNoSpeech
            ? $"Safety: rejected/suspicious voice path ({Report.VoiceRejectionReason ?? "no reason recorded"})"
            : $"Evidence: {Report.AnswerEvidenceStatus}";

    public void Update(CorrectionReport report) => Report = report;

    private static string Preview(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 120 ? collapsed : $"{collapsed[..117].TrimEnd()}...";
    }
}
