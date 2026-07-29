using System.Text.Json;
using System.Text.Json.Serialization;
using Ali.Modules.Calendar;
using Ali.Modules.Reminders;

namespace Ali.Modules.Storage;

public sealed class FileReminderStore : IReminderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly ICalendarEventPublisher _publisher;

    public FileReminderStore(string localAliRoot, ICalendarEventPublisher? publisher = null)
    {
        RootDirectory = Path.Combine(localAliRoot, "Calendar");
        _filePath = Path.Combine(RootDirectory, "events.json");
        _publisher = publisher ?? NullCalendarEventPublisher.Instance;
    }

    public string RootDirectory { get; }

    public string FilePath => _filePath;

    public void EnsureCreated()
    {
        if (File.Exists(_filePath))
        {
            return;
        }

        WriteAll(Array.Empty<ReminderEntry>());
    }

    public ReminderListResult List()
    {
        if (!File.Exists(_filePath))
        {
            return new ReminderListResult(Array.Empty<ReminderEntry>(), Array.Empty<string>());
        }

        try
        {
            var reminders = ReadAll()
                .OrderBy(reminder => reminder.DueAt)
                .ToList();
            return new ReminderListResult(reminders, Array.Empty<string>());
        }
        catch (Exception ex) when (IsJsonOrIoException(ex))
        {
            return new ReminderListResult(Array.Empty<ReminderEntry>(), [$"Reminder file was unreadable: {ex.Message}"]);
        }
    }

    public IReadOnlyList<ReminderEntry> ListDue(DateTimeOffset now) =>
        List().Reminders
            .Where(reminder => reminder.Status == ReminderStatus.Scheduled && reminder.DueAt <= now)
            .OrderBy(reminder => reminder.DueAt)
            .ToList();

    public ReminderEntry Save(ReminderEntry reminder)
    {
        Directory.CreateDirectory(RootDirectory);
        var normalized = reminder with
        {
            Title = string.IsNullOrWhiteSpace(reminder.Title) ? "Untitled reminder" : reminder.Title.Trim(),
            Prompt = string.IsNullOrWhiteSpace(reminder.Prompt) ? reminder.Title.Trim() : reminder.Prompt.Trim()
        };
        _publisher.Publish(normalized);
        try
        {
            var reminders = File.Exists(_filePath) ? ReadAll().ToList() : [];
            var index = reminders.FindIndex(existing => existing.ReminderId.Equals(normalized.ReminderId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                reminders[index] = normalized;
            }
            else
            {
                reminders.Add(normalized);
            }

            WriteAll(reminders);
            return normalized;
        }
        catch
        {
            _publisher.Cancel(normalized.ReminderId);
            throw;
        }
    }

    public ReminderEntry? SetStatus(string reminderId, ReminderStatus status)
    {
        var reminders = File.Exists(_filePath) ? ReadAll().ToList() : [];
        var index = reminders.FindIndex(existing => existing.ReminderId.Equals(reminderId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var updated = reminders[index] with { Status = status };
        if (status is ReminderStatus.Cancelled or ReminderStatus.Completed or ReminderStatus.Dismissed)
        {
            _publisher.Cancel(reminderId);
        }
        reminders[index] = updated;
        WriteAll(reminders);
        return updated;
    }

    public bool Delete(string reminderId)
    {
        var reminders = File.Exists(_filePath) ? ReadAll().ToList() : [];
        var removed = reminders.RemoveAll(reminder => reminder.ReminderId.Equals(reminderId, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            _publisher.Cancel(reminderId);
        }
        WriteAll(reminders);
        return removed > 0;
    }

    public int Clear()
    {
        var reminders = File.Exists(_filePath) ? ReadAll() : [];
        foreach (var reminder in reminders)
        {
            _publisher.Cancel(reminder.ReminderId);
        }
        var count = reminders.Count;
        WriteAll(Array.Empty<ReminderEntry>());
        return count;
    }

    private IReadOnlyList<ReminderEntry> ReadAll()
    {
        using var stream = File.OpenRead(_filePath);
        return JsonSerializer.Deserialize<List<ReminderEntry>>(stream, JsonOptions) ?? [];
    }

    private void WriteAll(IReadOnlyList<ReminderEntry> reminders)
    {
        Directory.CreateDirectory(RootDirectory);
        using var stream = File.Create(_filePath);
        JsonSerializer.Serialize(stream, reminders, JsonOptions);
    }

    private static bool IsJsonOrIoException(Exception ex) =>
        ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException;
}
