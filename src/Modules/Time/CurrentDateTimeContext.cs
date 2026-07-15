namespace Ali.Modules.Time;

public sealed record CurrentDateTimeSnapshot(
    DateTimeOffset LocalNow,
    string TimeZoneId,
    string TimeZoneDisplayName)
{
    public static CurrentDateTimeSnapshot Capture()
    {
        var localZone = TimeZoneInfo.Local;
        return new CurrentDateTimeSnapshot(
            DateTimeOffset.Now,
            localZone.Id,
            localZone.DisplayName);
    }

    public DateOnly LocalDate => DateOnly.FromDateTime(LocalNow.DateTime);

    public string BuildSystemInstruction() =>
        string.Join(
            Environment.NewLine,
            "Ali current date/time context from the local computer clock.",
            $"Current local date: {LocalNow:yyyy-MM-dd} ({LocalNow:dddd, MMMM d, yyyy}).",
            $"Current local time: {LocalNow:HH:mm:ss zzz}.",
            $"Current local time zone: {TimeZoneId} ({TimeZoneDisplayName}).",
            "Use this clock context as authoritative for relative dates, current/future/past comparisons, schedules, deadlines, reminders, and source-backed answers.",
            "Do not answer from an old training cutoff when this clock context or app-provided source evidence is relevant.");

    public string BuildCompactFactLine() =>
        $"Current local date/time: {LocalNow:yyyy-MM-dd HH:mm:ss zzz}; time zone: {TimeZoneId}.";
}
