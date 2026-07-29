using System.Text.Json;

namespace Ali.Modules.Internet;

internal sealed record GeminiGroundingReservation(
    bool Allowed,
    string? ReservationId,
    string Status);

/// <summary>
/// Local, fail-closed cost and rate guard for Google-grounded requests.
/// It is deliberately independent of Google's project-level budget so either
/// layer can stop an accidental request loop.
/// </summary>
internal sealed class GeminiGroundingUsageLedger
{
    private const decimal FlashLiteInputUsdPerMillionTokens = 0.30m;
    private const decimal FlashLiteOutputUsdPerMillionTokens = 2.50m;
    private const decimal GroundingUsdPerSearchQueryAfterFreeAllowance = 0.014m;
    private const int PaidTierFreeSearchQueriesPerMonth = 5000;
    private const decimal PendingRequestReservationUsd = 0.02m;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object sync = new();
    private readonly string? path;
    private UsageState? state;

    public GeminiGroundingUsageLedger(string? dataRoot)
    {
        path = string.IsNullOrWhiteSpace(dataRoot)
            ? null
            : Path.Combine(dataRoot, "Sources", "gemini_grounding_usage.json");
    }

    public GeminiGroundingReservation TryReserve(
        WebSourceBackendSettings settings,
        DateTimeOffset now)
    {
        lock (sync)
        {
            try
            {
                var current = LoadCurrent(now);
                var hourStart = now - TimeSpan.FromHours(1);
                var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
                var hourCount = current.Requests.Count(item => item.RequestedAtUtc >= hourStart);
                var dayCount = current.Requests.Count(item => item.RequestedAtUtc >= dayStart);
                var hourlyLimit = Math.Clamp(settings.GeminiMaxRequestsPerHour, 1, 1000);
                var dailyLimit = Math.Clamp(settings.GeminiMaxRequestsPerDay, 1, 5000);
                var spendLimit = Math.Clamp(settings.GeminiMonthlySpendLimitUsd, 0.10m, 1000m);
                var estimatedSpend = EstimateSpend(CurrentMonth(current.Requests, now));

                if (hourCount >= hourlyLimit)
                {
                    return new(false, null, $"Local Google safety limit reached: {hourlyLimit} grounded requests per rolling hour.");
                }
                if (dayCount >= dailyLimit)
                {
                    return new(false, null, $"Local Google safety limit reached: {dailyLimit} grounded requests per UTC day.");
                }
                if (estimatedSpend + PendingRequestReservationUsd > spendLimit)
                {
                    return new(false, null, $"Local Google monthly breaker reached: ${spendLimit:0.00}.");
                }

                var id = Guid.NewGuid().ToString("N");
                current.Requests.Add(new UsageEntry
                {
                    Id = id,
                    RequestedAtUtc = now,
                    EstimatedCostUsd = PendingRequestReservationUsd
                });
                Save(current);
                return new(
                    true,
                    id,
                    BuildStatus(current, settings, now));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new(false, null, "Google grounding was stopped because its local safety ledger is unavailable: " + ex.Message);
            }
        }
    }

    public void RecordUsage(
        string reservationId,
        int promptTokens,
        int outputTokens,
        bool grounded,
        int searchQueryCount,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reservationId)) return;
        lock (sync)
        {
            var current = LoadCurrent(now);
            var entry = current.Requests.FirstOrDefault(item =>
                string.Equals(item.Id, reservationId, StringComparison.Ordinal));
            if (entry is null) return;
            entry.PromptTokens = Math.Max(0, promptTokens);
            entry.OutputTokens = Math.Max(0, outputTokens);
            entry.Grounded = grounded;
            entry.SearchQueryCount = grounded
                ? Math.Max(1, searchQueryCount)
                : 0;
            entry.EstimatedCostUsd = TokenCost(entry.PromptTokens, entry.OutputTokens);
            Save(current);
        }
    }

    public string GetStatus(WebSourceBackendSettings settings, DateTimeOffset now)
    {
        lock (sync)
        {
            try
            {
                return BuildStatus(LoadCurrent(now), settings, now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return "Local Google usage ledger unavailable: " + ex.Message;
            }
        }
    }

    private UsageState LoadCurrent(DateTimeOffset now)
    {
        state ??= Load();
        var monthStart = MonthStart(now);
        var weekStart = WeekStart(now);
        var retentionStart = monthStart <= weekStart ? monthStart : weekStart;
        state.Requests.RemoveAll(item => item.RequestedAtUtc < retentionStart);
        return state;
    }

    private UsageState Load()
    {
        if (path is null || !File.Exists(path)) return new UsageState();
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<UsageState>(stream, JsonOptions) ?? new UsageState();
    }

    private void Save(UsageState value)
    {
        if (path is null) return;
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, value, JsonOptions);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static string BuildStatus(
        UsageState value,
        WebSourceBackendSettings settings,
        DateTimeOffset now)
    {
        var hourStart = now - TimeSpan.FromHours(1);
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var weekStart = WeekStart(now);
        var monthStart = MonthStart(now);
        var hour = value.Requests.Where(item => item.RequestedAtUtc >= hourStart).ToArray();
        var day = value.Requests.Where(item => item.RequestedAtUtc >= dayStart).ToArray();
        var week = value.Requests.Where(item => item.RequestedAtUtc >= weekStart).ToArray();
        var month = value.Requests.Where(item => item.RequestedAtUtc >= monthStart).ToArray();
        var spend = EstimateSpend(month);
        return $"Grounded requests - hour {hour.Length}/{Math.Clamp(settings.GeminiMaxRequestsPerHour, 1, 1000)}, "
            + $"day {day.Length}/{Math.Clamp(settings.GeminiMaxRequestsPerDay, 1, 5000)}, "
            + $"week {week.Length:N0}, month {month.Length:N0}."
            + Environment.NewLine
            + $"Google search queries - hour {CountSearchQueries(hour):N0}, day {CountSearchQueries(day):N0}, "
            + $"week {CountSearchQueries(week):N0}, month {CountSearchQueries(month):N0}/{PaidTierFreeSearchQueriesPerMonth:N0} included."
            + Environment.NewLine
            + $"Model tokens (input/output) - hour {CountPromptTokens(hour):N0}/{CountOutputTokens(hour):N0}, "
            + $"day {CountPromptTokens(day):N0}/{CountOutputTokens(day):N0}, "
            + $"week {CountPromptTokens(week):N0}/{CountOutputTokens(week):N0}, "
            + $"month {CountPromptTokens(month):N0}/{CountOutputTokens(month):N0}."
            + Environment.NewLine
            + $"Estimated month cost ${spend:0.0000}/${Math.Clamp(settings.GeminiMonthlySpendLimitUsd, 0.10m, 1000m):0.00} local breaker.";
    }

    private static DateTimeOffset WeekStart(DateTimeOffset now)
    {
        var utcDate = now.UtcDateTime.Date;
        var daysSinceMonday = ((int)utcDate.DayOfWeek + 6) % 7;
        return new DateTimeOffset(utcDate.AddDays(-daysSinceMonday), TimeSpan.Zero);
    }

    private static DateTimeOffset MonthStart(DateTimeOffset now) => new(
        new DateTime(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, DateTimeKind.Utc));

    private static UsageEntry[] CurrentMonth(IReadOnlyList<UsageEntry> entries, DateTimeOffset now)
    {
        var start = MonthStart(now);
        return entries.Where(item => item.RequestedAtUtc >= start).ToArray();
    }

    private static decimal EstimateSpend(IReadOnlyList<UsageEntry> entries)
    {
        var tokenSpend = entries.Sum(item => item.EstimatedCostUsd);
        var groundingSpend = Math.Max(
                0,
                CountSearchQueries(entries) - PaidTierFreeSearchQueriesPerMonth)
            * GroundingUsdPerSearchQueryAfterFreeAllowance;
        return tokenSpend + groundingSpend;
    }

    private static int CountSearchQueries(IReadOnlyList<UsageEntry> entries) =>
        entries.Sum(item => item.Grounded
            ? Math.Max(1, item.SearchQueryCount)
            : 0);

    private static int CountPromptTokens(IReadOnlyList<UsageEntry> entries) =>
        entries.Sum(item => item.PromptTokens);

    private static int CountOutputTokens(IReadOnlyList<UsageEntry> entries) =>
        entries.Sum(item => item.OutputTokens);

    private static decimal TokenCost(int promptTokens, int outputTokens) =>
        promptTokens / 1_000_000m * FlashLiteInputUsdPerMillionTokens
        + outputTokens / 1_000_000m * FlashLiteOutputUsdPerMillionTokens;

    private sealed class UsageState
    {
        public List<UsageEntry> Requests { get; set; } = [];
    }

    private sealed class UsageEntry
    {
        public string Id { get; set; } = string.Empty;

        public DateTimeOffset RequestedAtUtc { get; set; }

        public int PromptTokens { get; set; }

        public int OutputTokens { get; set; }

        public bool Grounded { get; set; }

        public int SearchQueryCount { get; set; }

        public decimal EstimatedCostUsd { get; set; }
    }
}
