namespace Ali.Modules;

public static class AliModuleIds
{
    public const string Internet = "ali.internet";
    public const string Rag = "ali.rag";
    public const string Voice = "ali.voice";
    public const string Memory = "ali.memory";
    public const string Time = "ali.time";
    public const string Conversation = "ali.conversation";
    public const string Runtime = "ali.runtime";
    public const string Identity = "ali.identity";
    public const string Permissions = "ali.permissions";
    public const string Evidence = "ali.evidence";
    public const string Feedback = "ali.feedback";
    public const string Reminders = "ali.reminders";
    public const string Truthfulness = "ali.truthfulness";
    public const string Storage = "ali.storage";
}

public sealed record AliModuleDescriptor(
    string Id,
    string DisplayName,
    string Purpose,
    IReadOnlyList<string> CapabilityKinds);

public static class AliModuleCatalog
{
    public static IReadOnlyList<AliModuleDescriptor> Default { get; } =
    [
        new(AliModuleIds.Internet, "Internet", "Current web lookup, source retrieval, scrape-backed answering, and citation evidence.", ["sources", "search", "scrape"]),
        new(AliModuleIds.Rag, "RAG", "Local document indexing, retrieval, source excerpts, and grounded local-library answers.", ["documents", "retrieval", "citations"]),
        new(AliModuleIds.Voice, "Voice", "Speech input, speech output, voice calibration, and audio-device behavior.", ["speech-to-text", "text-to-speech", "audio"]),
        new(AliModuleIds.Memory, "Memory", "User, workspace, and conversation memory behavior with clear scope boundaries.", ["memory", "profile", "recall"]),
        new(AliModuleIds.Time, "Time", "Current clock context, date reasoning, reminders, and schedule grounding.", ["clock", "dates", "schedules"]),
        new(AliModuleIds.Conversation, "Conversation", "Chat sessions, titles, history, and conversation records.", ["chat", "history"]),
        new(AliModuleIds.Runtime, "Runtime", "Model provider settings, health, streaming, and task-specific model routing.", ["models", "providers", "routing"]),
        new(AliModuleIds.Identity, "Identity", "Assistant profile and per-user identity settings.", ["profile", "persona"]),
        new(AliModuleIds.Permissions, "Permissions", "Approval, safety, receipts, and action-risk policy.", ["approval", "safety", "receipts"]),
        new(AliModuleIds.Evidence, "Evidence", "Evidence status, receipts, provenance, and answer trust markers.", ["evidence", "provenance"]),
        new(AliModuleIds.Feedback, "Feedback", "Correction queue and user feedback loops.", ["corrections", "feedback"]),
        new(AliModuleIds.Reminders, "Reminders", "Reminder parsing, reminder storage, and scheduled user follow-ups.", ["reminders", "schedule"]),
        new(AliModuleIds.Truthfulness, "Truthfulness", "Truthfulness policy and answer reliability constraints.", ["policy", "reliability"]),
        new(AliModuleIds.Storage, "Storage", "File-backed persistence for user data and module state.", ["persistence", "backup"])
    ];
}

