using System.ComponentModel;
using Ali.Modules.Reminders;

namespace Ali.Modules.Coordinator;

internal sealed class AliReminderTools(
    IReminderStore reminders,
    Func<CoordinatorTurnContext?> turnAccessor)
{
    public Task<CoordinatorReminderResult> CreateAsync(
        [Description("The short reminder title and action.")] string title,
        [Description("The due date-time in ISO 8601 format including the local UTC offset.")] string dueAtLocal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(title)
            || !DateTimeOffset.TryParse(dueAtLocal, out var dueAt))
        {
            return Task.FromResult(new CoordinatorReminderResult(
                false,
                "The calendar event was not saved because its title or due time was invalid."));
        }

        if (dueAt <= DateTimeOffset.Now)
        {
            return Task.FromResult(new CoordinatorReminderResult(
                false,
                "The calendar event was not saved because its due time is not in the future."));
        }

        var now = DateTimeOffset.UtcNow;
        var context = turnAccessor();
        string reminderId;
        if (context?.TryGetActiveToolCallId(
                AliCapabilityCatalog.CreateCalendarEventName,
                out _) == true)
        {
            if (!context.TryGetActiveDurableOperationId(
                    AliCapabilityCatalog.CreateCalendarEventName,
                    out var durableOperationId)
                || string.IsNullOrWhiteSpace(durableOperationId))
            {
                return Task.FromResult(new CoordinatorReminderResult(
                    false,
                    "The calendar event was not scheduled because its durable operation identity is unavailable."));
            }

            reminderId = durableOperationId;
        }
        else
        {
            // The headless MCP server has no visible-turn execution context. Preserve that
            // existing direct boundary while requiring the durable ID for Ali's agent loop.
            reminderId = $"cal_{Guid.NewGuid():N}";
        }
        try
        {
            var reminder = reminders.Save(new ReminderEntry(
                reminderId,
                title.Trim(),
                title.Trim(),
                dueAt,
                now,
                ReminderStatus.Scheduled,
                ConversationId: context?.ConversationId,
                MessageId: context?.UserMessageId));
            return Task.FromResult(new CoordinatorReminderResult(
                true,
                "Calendar event saved. Its iCalendar file and Windows notification remain available after Ali closes.",
                reminder.ReminderId,
                reminder.DueAt));
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidOperationException
                                   or ArgumentException
                                   or TimeoutException
                                   or Win32Exception)
        {
            return Task.FromResult(new CoordinatorReminderResult(
                false,
                "The calendar event could not be scheduled: " + ex.Message));
        }
    }
}
