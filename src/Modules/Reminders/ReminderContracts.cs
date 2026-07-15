namespace Ali.Modules.Reminders;

public enum ReminderStatus
{
    Scheduled,
    Completed,
    Dismissed,
    Cancelled
}

public sealed record ReminderEntry(
    string ReminderId,
    string Title,
    string Prompt,
    DateTimeOffset DueAt,
    DateTimeOffset CreatedAt,
    ReminderStatus Status,
    string? Recurrence = null,
    string? ConversationId = null,
    string? MessageId = null);

public sealed record ReminderListResult(
    IReadOnlyList<ReminderEntry> Reminders,
    IReadOnlyList<string> Warnings);

public interface IReminderStore
{
    ReminderListResult List();

    IReadOnlyList<ReminderEntry> ListDue(DateTimeOffset now);

    ReminderEntry Save(ReminderEntry reminder);

    ReminderEntry? SetStatus(string reminderId, ReminderStatus status);

    bool Delete(string reminderId);

    int Clear();
}
