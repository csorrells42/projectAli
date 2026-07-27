using System.ComponentModel;
using Ali.Modules.Permissions;
using Ali.Modules.Reminders;

namespace Ali.Modules.Coordinator;

internal sealed class AliReminderTools(
    IReminderStore reminders,
    PermissionService permissions,
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
                "The reminder was not saved because its title or due time was invalid."));
        }

        var permission = permissions.Evaluate(PermissionRequest.Create(
            "reminder.write",
            PermissionRisk.FileWrite,
            "Create an explicitly requested local reminder.",
            userConfirmed: true));
        if (permission.Kind != PermissionDecisionKind.Allow)
        {
            return Task.FromResult(new CoordinatorReminderResult(false, permission.Reason));
        }

        var now = DateTimeOffset.UtcNow;
        var context = turnAccessor();
        var reminder = reminders.Save(new ReminderEntry(
            $"rem_{Guid.NewGuid():N}",
            title.Trim(),
            title.Trim(),
            dueAt,
            now,
            ReminderStatus.Scheduled,
            ConversationId: context?.ConversationId,
            MessageId: context?.UserMessageId));
        return Task.FromResult(new CoordinatorReminderResult(
            true,
            "Reminder saved locally.",
            reminder.ReminderId,
            reminder.DueAt));
    }
}
