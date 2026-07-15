using Ali.Modules.Reminders;

namespace Ali.UI.ViewModels;

public sealed class ReminderEntryViewModel(ReminderEntry reminder) : ObservableObject
{
    public ReminderEntry Reminder { get; } = reminder;

    public string Id => Reminder.ReminderId;

    public string Title => Reminder.Title;

    public string Prompt => Reminder.Prompt;

    public string DueAtText => Reminder.DueAt.ToLocalTime().ToString("g");

    public string StatusText => Reminder.Status.ToString();
}

