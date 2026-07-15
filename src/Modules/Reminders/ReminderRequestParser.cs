using System.Globalization;

namespace Ali.Modules.Reminders;

public sealed record ReminderRequestDecision(
    bool Accepted,
    string? Title,
    DateTimeOffset? DueAt,
    string Message)
{
    public static ReminderRequestDecision None { get; } = new(false, null, null, string.Empty);
}

public static class ReminderRequestParser
{
    public static ReminderRequestDecision Evaluate(string text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ReminderRequestDecision.None;
        }

        var trimmed = text.Trim();
        const string prefix = "remind me to ";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return ReminderRequestDecision.None;
        }

        var body = trimmed[prefix.Length..].Trim();
        var splitIndex = body.LastIndexOf(" at ", StringComparison.OrdinalIgnoreCase);
        if (splitIndex <= 0 || splitIndex + 4 >= body.Length)
        {
            return new ReminderRequestDecision(
                false,
                null,
                null,
                "Reminder request needs a clear 'at' date/time and was not scheduled.");
        }

        var title = body[..splitIndex].Trim();
        var dueText = body[(splitIndex + 4)..].Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return new ReminderRequestDecision(false, null, null, "Reminder title was empty and was not scheduled.");
        }

        if (!DateTimeOffset.TryParse(
                dueText,
                CultureInfo.CurrentCulture,
                DateTimeStyles.AssumeLocal,
                out var dueAt))
        {
            return new ReminderRequestDecision(false, null, null, "Reminder date/time was not understood and was not scheduled.");
        }

        if (dueAt <= now)
        {
            return new ReminderRequestDecision(false, null, null, "Reminder date/time is not in the future and was not scheduled.");
        }

        return new ReminderRequestDecision(true, title, dueAt, "Reminder scheduled locally while Ali is running.");
    }
}
