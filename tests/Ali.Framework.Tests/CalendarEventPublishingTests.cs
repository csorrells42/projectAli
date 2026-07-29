using System.Xml.Linq;
using System.Diagnostics;
using Ali.Modules.Calendar;
using Ali.Modules.Reminders;
using Ali.Modules.Storage;

namespace Ali.Framework.Tests;

public sealed class CalendarEventPublishingTests
{
    [Fact]
    public void ICalendarExport_ContainsPortableEventAndAlarmWithEscapedText()
    {
        var dueAt = new DateTimeOffset(2026, 8, 1, 14, 30, 0, TimeSpan.FromHours(-5));
        var calendarEvent = Event(
            "cal_export",
            "Meet Bill, review; drawings",
            dueAt,
            "Line one\nLine two");

        var text = WindowsCalendarEventPublisher.BuildIcs(calendarEvent);

        Assert.Contains("BEGIN:VCALENDAR\r\n", text, StringComparison.Ordinal);
        Assert.Contains("UID:cal_export@project-ali.local", text, StringComparison.Ordinal);
        Assert.Contains("DTSTART:20260801T193000Z", text, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Meet Bill\\, review\\; drawings", text, StringComparison.Ordinal);
        Assert.Contains("DESCRIPTION:Line one\\nLine two", text, StringComparison.Ordinal);
        Assert.Contains("BEGIN:VALARM", text, StringComparison.Ordinal);
        Assert.Contains("TRIGGER:-PT5M", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskDefinition_IsLeastPrivilegeInteractiveAndDoesNotEmbedShellCommands()
    {
        var calendarEvent = Event(
            "cal_safe",
            "Review \"drawing\" & dimensions\r\nwith Bill",
            DateTimeOffset.Now.AddHours(1));

        var document = XDocument.Parse(WindowsCalendarEventPublisher.BuildTaskXml(calendarEvent));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("InteractiveToken", document.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("LeastPrivilege", document.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("true", document.Descendants(ns + "StartWhenAvailable").Single().Value);
        Assert.EndsWith("powershell.exe", document.Descendants(ns + "Command").Single().Value, StringComparison.OrdinalIgnoreCase);
        var arguments = document.Descendants(ns + "Arguments").Single().Value;
        var encodedCommand = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();
        var decodedCommand = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encodedCommand));
        Assert.Contains("Review ''drawing'' & dimensions with Bill", decodedCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("Review 'drawing'", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void CalendarStore_PublishesAndCancelsOperatingSystemNotificationLifecycle()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProjectAliCalendarTests", Guid.NewGuid().ToString("N"));
        var publisher = new RecordingCalendarPublisher();
        try
        {
            var store = new FileReminderStore(root, publisher);
            var first = store.Save(Event("cal_one", "First", DateTimeOffset.Now.AddHours(1)));
            var second = store.Save(Event("cal_two", "Second", DateTimeOffset.Now.AddHours(2)));

            Assert.Equal(["cal_one", "cal_two"], publisher.PublishedIds);
            Assert.Equal("events.json", Path.GetFileName(store.FilePath));
            Assert.Equal("Calendar", Path.GetFileName(store.RootDirectory));

            store.SetStatus(first.ReminderId, ReminderStatus.Completed);
            Assert.Contains("cal_one", publisher.CancelledIds);

            var cleared = store.Clear();
            Assert.Equal(2, cleared);
            Assert.Contains("cal_two", publisher.CancelledIds);
            Assert.Empty(store.List().Reminders);
            Assert.Equal(ReminderStatus.Scheduled, second.Status);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void WindowsTaskScheduler_RegistersEventOutsideAliProcess_WhenExplicitlyEnabled()
    {
        if (!OperatingSystem.IsWindows()
            || !string.Equals(Environment.GetEnvironmentVariable("ALI_RUN_WINDOWS_TASK_SCHEDULER_TEST"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "ProjectAliCalendarOsTests", Guid.NewGuid().ToString("N"));
        var id = "cal_os_" + Guid.NewGuid().ToString("N");
        var taskName = "Ali Calendar - " + id;
        var publisher = new WindowsCalendarEventPublisher(root);
        try
        {
            var receipt = publisher.Publish(Event(id, "Ali calendar scheduler integration test", DateTimeOffset.Now.AddHours(3)));

            Assert.True(File.Exists(receipt.InterchangeFilePath));
            Assert.Equal(taskName, receipt.SystemScheduleName);
            Assert.Equal(0, RunSchtasks("/Query", "/TN", taskName));

            publisher.Cancel(id);

            Assert.False(File.Exists(receipt.InterchangeFilePath));
            Assert.NotEqual(0, RunSchtasks("/Query", "/TN", taskName));
        }
        finally
        {
            publisher.Cancel(id);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ReminderEntry Event(
        string id,
        string title,
        DateTimeOffset dueAt,
        string? prompt = null) =>
        new(
            id,
            title,
            prompt ?? title,
            dueAt,
            DateTimeOffset.UtcNow,
            ReminderStatus.Scheduled);

    private sealed class RecordingCalendarPublisher : ICalendarEventPublisher
    {
        public List<string> PublishedIds { get; } = [];

        public List<string> CancelledIds { get; } = [];

        public CalendarPublishReceipt Publish(ReminderEntry calendarEvent)
        {
            PublishedIds.Add(calendarEvent.ReminderId);
            return new CalendarPublishReceipt(calendarEvent.ReminderId + ".ics", "Ali Calendar - " + calendarEvent.ReminderId);
        }

        public void Cancel(string eventId) => CancelledIds.Add(eventId);
    }

    private static int RunSchtasks(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("schtasks.exe did not start.");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15_000));
        return process.ExitCode;
    }
}
