using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Ali.Modules.Reminders;

namespace Ali.Modules.Calendar;

public sealed record CalendarPublishReceipt(string InterchangeFilePath, string? SystemScheduleName);

public interface ICalendarEventPublisher
{
    CalendarPublishReceipt Publish(ReminderEntry calendarEvent);

    void Cancel(string eventId);
}

public sealed class NullCalendarEventPublisher : ICalendarEventPublisher
{
    public static NullCalendarEventPublisher Instance { get; } = new();

    private NullCalendarEventPublisher()
    {
    }

    public CalendarPublishReceipt Publish(ReminderEntry calendarEvent) => new(string.Empty, null);

    public void Cancel(string eventId)
    {
    }
}

/// <summary>
/// Publishes portable iCalendar files and registers a Windows notification that is owned by
/// Task Scheduler rather than Ali's process. The notification therefore survives Ali closing.
/// </summary>
public sealed class WindowsCalendarEventPublisher : ICalendarEventPublisher
{
    private const string TaskPrefix = "Ali Calendar - ";
    private readonly string _calendarDirectory;
    private readonly string _taskXmlDirectory;
    private readonly Func<ProcessStartInfo, Process> _startProcess;

    public WindowsCalendarEventPublisher(
        string profileDataRoot,
        Func<ProcessStartInfo, Process>? startProcess = null)
    {
        _calendarDirectory = Path.Combine(profileDataRoot, "Calendar", "Events");
        _taskXmlDirectory = Path.Combine(profileDataRoot, "Calendar", "TaskDefinitions");
        _startProcess = startProcess
            ?? (startInfo => Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows Task Scheduler could not be started."));
    }

    public CalendarPublishReceipt Publish(ReminderEntry calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        if (calendarEvent.DueAt <= DateTimeOffset.Now)
        {
            throw new ArgumentOutOfRangeException(nameof(calendarEvent), "Calendar events must be scheduled in the future.");
        }

        Directory.CreateDirectory(_calendarDirectory);
        Directory.CreateDirectory(_taskXmlDirectory);

        var eventId = NormalizeIdentifier(calendarEvent.ReminderId);
        var interchangePath = Path.Combine(_calendarDirectory, eventId + ".ics");
        var taskXmlPath = Path.Combine(_taskXmlDirectory, eventId + ".xml");
        var taskName = TaskPrefix + eventId;

        File.WriteAllText(interchangePath, BuildIcs(calendarEvent), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(taskXmlPath, BuildTaskXml(calendarEvent), Encoding.Unicode);

        try
        {
            RunTaskScheduler("/Create", "/TN", taskName, "/XML", taskXmlPath, "/F");
            return new CalendarPublishReceipt(interchangePath, taskName);
        }
        catch
        {
            TryDelete(interchangePath);
            throw;
        }
        finally
        {
            TryDelete(taskXmlPath);
        }
    }

    public void Cancel(string eventId)
    {
        var normalized = NormalizeIdentifier(eventId);
        var interchangePath = Path.Combine(_calendarDirectory, normalized + ".ics");
        try
        {
            RunTaskScheduler(["/Delete", "/TN", TaskPrefix + normalized, "/F"], ignoreMissingTask: true);
        }
        finally
        {
            TryDelete(interchangePath);
        }
    }

    internal static string BuildIcs(ReminderEntry calendarEvent)
    {
        var dueUtc = calendarEvent.DueAt.UtcDateTime;
        var createdUtc = calendarEvent.CreatedAt.UtcDateTime;
        var description = string.IsNullOrWhiteSpace(calendarEvent.Prompt)
            ? calendarEvent.Title
            : calendarEvent.Prompt;
        return string.Join(
            "\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Project Ali//Local Calendar//EN",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            "BEGIN:VEVENT",
            $"UID:{EscapeIcs(calendarEvent.ReminderId)}@project-ali.local",
            $"DTSTAMP:{createdUtc:yyyyMMdd'T'HHmmss'Z'}",
            $"DTSTART:{dueUtc:yyyyMMdd'T'HHmmss'Z'}",
            $"DTEND:{dueUtc.AddMinutes(15):yyyyMMdd'T'HHmmss'Z'}",
            $"SUMMARY:{EscapeIcs(calendarEvent.Title)}",
            $"DESCRIPTION:{EscapeIcs(description)}",
            "STATUS:CONFIRMED",
            "BEGIN:VALARM",
            "TRIGGER:-PT5M",
            "ACTION:DISPLAY",
            $"DESCRIPTION:{EscapeIcs(calendarEvent.Title)}",
            "END:VALARM",
            "END:VEVENT",
            "END:VCALENDAR",
            string.Empty);
    }

    internal static string BuildTaskXml(ReminderEntry calendarEvent)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var safeTitle = SanitizeNotificationText(calendarEvent.Title);
        var notificationCommand = BuildNotificationCommand(safeTitle);
        var encodedNotificationCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(notificationCommand));
        var localStart = calendarEvent.DueAt.ToLocalTime().DateTime;
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(ns + "Task",
                new XAttribute("version", "1.4"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Description", "Project Ali calendar notification: " + safeTitle)),
                new XElement(ns + "Triggers",
                    new XElement(ns + "TimeTrigger",
                        new XElement(ns + "StartBoundary", localStart.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
                        new XElement(ns + "EndBoundary", localStart.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
                        new XElement(ns + "Enabled", "true"))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "LeastPrivilege"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "WakeToRun", "false"),
                    new XElement(ns + "ExecutionTimeLimit", "PT5M"),
                    new XElement(ns + "DeleteExpiredTaskAfter", "PT1H"),
                    new XElement(ns + "Priority", "7")),
                new XElement(ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")),
                        new XElement(ns + "Arguments", $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encodedNotificationCommand}")))));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildNotificationCommand(string safeTitle)
    {
        var quotedTitle = (safeTitle ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
        return "Add-Type -AssemblyName PresentationFramework; "
            + $"[System.Windows.MessageBox]::Show('Ali calendar: {quotedTitle}', 'Ali Calendar') | Out-Null";
    }

    private void RunTaskScheduler(params string[] arguments) =>
        RunTaskScheduler(arguments, ignoreMissingTask: false);

    private void RunTaskScheduler(string[] arguments, bool ignoreMissingTask)
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

        using var process = _startProcess(startInfo);
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException("Windows Task Scheduler did not respond within 15 seconds.");
        }

        if (process.ExitCode != 0
            && !(ignoreMissingTask && (standardError.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
                                       || standardOutput.Contains("cannot find", StringComparison.OrdinalIgnoreCase))))
        {
            var details = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
            throw new InvalidOperationException("Windows Task Scheduler rejected the calendar event: " + details.Trim());
        }
    }

    private static string EscapeIcs(string value) =>
        (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal);

    private static string SanitizeNotificationText(string value)
    {
        var flattened = string.Join(" ", (value ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        var safe = flattened.Replace('"', '\'').Trim();
        return safe.Length <= 180 ? safe : safe[..180];
    }

    private static string NormalizeIdentifier(string eventId)
    {
        var value = new string((eventId ?? string.Empty)
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .Take(80)
            .ToArray());
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The calendar event ID was invalid.", nameof(eventId));
        }

        return value;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
