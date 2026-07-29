using System.Net;
using System.Text;
using Ali.Modules.Internet;

namespace Ali.Framework.Tests;

public sealed class GeminiGroundedSearchProviderTests
{
    [Fact]
    public async Task ProviderPinsFlashLiteUsesGroundingMetadataAndNeverPlacesKeyInBody()
    {
        const string apiKey = "test-secret-key";
        var handler = new RecordingHandler(GroundedResponse());
        using var client = new HttpClient(handler);
        var settings = Settings(apiKey);
        var provider = new GeminiGroundedSearchProvider(
            client,
            () => settings,
            new GeminiGroundingUsageLedger(dataRoot: null));
        var warnings = new List<string>();

        var hits = await provider.SearchAsync(
            "current software engineering news",
            Plan("current software engineering news"),
            warnings,
            CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal("Primary source", hit.Title);
        Assert.Equal("https://example.com/current", hit.Url);
        Assert.Equal("The supported current fact.", hit.Content);
        Assert.Empty(warnings);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("gemini-3.5-flash-lite:generateContent", handler.LastUri?.AbsoluteUri);
        Assert.Equal(apiKey, handler.LastApiKey);
        Assert.Contains("\"google_search\":{}", handler.LastBody);
        Assert.Contains("\"thinkingLevel\":\"minimal\"", handler.LastBody);
        Assert.DoesNotContain("temperature", handler.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("Grounded requests - hour 1/30", provider.UsageStatus());
    }

    [Fact]
    public async Task RetrieverUsesGoogleGroundingBeforeLegacyFallbacks()
    {
        var handler = new RecordingHandler(GroundedResponse());
        using var client = new HttpClient(handler);
        var settings = Settings("configured-key");
        var provider = new GeminiGroundedSearchProvider(
            client,
            () => settings,
            new GeminiGroundingUsageLedger(dataRoot: null));
        var retriever = new TavilyFirecrawlSourceRetriever(
            client,
            () => settings,
            contentExtractor: null,
            provider);

        var result = await retriever.RetrieveAsync(
            Plan("current software engineering news"),
            CancellationToken.None);

        var excerpt = Assert.Single(result.Excerpts);
        Assert.Equal("https://example.com/current", excerpt.Url);
        Assert.Contains("The supported current fact.", excerpt.Excerpt);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task LocalRateGuardStopsASecondRequestBeforeNetworkAccess()
    {
        var handler = new RecordingHandler(GroundedResponse());
        using var client = new HttpClient(handler);
        var settings = Settings("configured-key");
        settings.GeminiMaxRequestsPerHour = 1;
        var provider = new GeminiGroundedSearchProvider(
            client,
            () => settings,
            new GeminiGroundingUsageLedger(dataRoot: null));

        var firstWarnings = new List<string>();
        Assert.Single(await provider.SearchAsync("first", Plan("first"), firstWarnings, CancellationToken.None));
        var secondWarnings = new List<string>();
        Assert.Empty(await provider.SearchAsync("second", Plan("second"), secondWarnings, CancellationToken.None));

        Assert.Equal(1, handler.CallCount);
        Assert.Contains(secondWarnings, warning => warning.Contains("rolling hour", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HttpFailureRedactsApiKeyFromVisibleDiagnostics()
    {
        const string apiKey = "do-not-leak-me";
        var handler = new RecordingHandler(
            "{\"error\":{\"message\":\"Invalid key do-not-leak-me\"}}",
            HttpStatusCode.BadRequest);
        using var client = new HttpClient(handler);
        var settings = Settings(apiKey);
        var provider = new GeminiGroundedSearchProvider(
            client,
            () => settings,
            new GeminiGroundingUsageLedger(dataRoot: null));
        var warnings = new List<string>();

        Assert.Empty(await provider.SearchAsync("test", Plan("test"), warnings, CancellationToken.None));

        var warning = Assert.Single(warnings);
        Assert.Contains("[redacted]", warning);
        Assert.DoesNotContain(apiKey, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageLedgerPersistsSafetyReservationsAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliGeminiLedgerTests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = Settings("configured-key");
            settings.GeminiMaxRequestsPerHour = 1;
            var now = DateTimeOffset.UtcNow;
            var first = new GeminiGroundingUsageLedger(root).TryReserve(settings, now);
            var afterRestart = new GeminiGroundingUsageLedger(root).TryReserve(settings, now.AddSeconds(1));

            Assert.True(first.Allowed, first.Status);
            Assert.False(afterRestart.Allowed);
            Assert.Contains("rolling hour", afterRestart.Status, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UsageLedgerReportsRequestsAndSearchQueriesForHourDayWeekAndMonth()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliGeminiUsageTests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new WebSourceBackendSettings
            {
                GeminiMaxRequestsPerHour = 30,
                GeminiMaxRequestsPerDay = 40,
                GeminiMonthlySpendLimitUsd = 5m
            };
            var ledger = new GeminiGroundingUsageLedger(root);
            var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
            Record(now.AddDays(-19), 1);
            Record(now.AddDays(-2), 2);
            Record(now.AddHours(-4), 3);
            Record(now.AddMinutes(-30), 4);

            var status = ledger.GetStatus(settings, now);

            Assert.Contains("hour 1/30", status, StringComparison.Ordinal);
            Assert.Contains("day 2/40", status, StringComparison.Ordinal);
            Assert.Contains("week 3", status, StringComparison.Ordinal);
            Assert.Contains("month 4", status, StringComparison.Ordinal);
            Assert.Contains("hour 4, day 7, week 9, month 10/5,000", status, StringComparison.Ordinal);
            Assert.Contains("hour 100/25, day 200/50, week 300/75, month 400/100", status, StringComparison.Ordinal);

            void Record(DateTimeOffset at, int queries)
            {
                var reservation = ledger.TryReserve(settings, at);
                Assert.True(reservation.Allowed);
                ledger.RecordUsage(reservation.ReservationId!, 100, 25, grounded: true, queries, at);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static WebSourceBackendSettings Settings(string apiKey) => new()
    {
        Enabled = true,
        GeminiGroundedSearchEnabled = true,
        GeminiApiKey = apiKey,
        GeminiMaxRequestsPerHour = 30,
        GeminiMaxRequestsPerDay = 100,
        GeminiMonthlySpendLimitUsd = 5m,
        UseFirecrawlForPageExtraction = false,
        TavilyApiKey = null,
        FirecrawlApiKey = null,
        BraveSearchApiKey = null,
        SerperApiKey = null
    };

    private static SourceQueryPlan Plan(string query) => new(
        true,
        true,
        "current_events",
        query,
        [query],
        ["news"])
    {
        TemporalSelection = "current"
    };

    private static string GroundedResponse() =>
        """
        {
          "candidates": [
            {
              "content": { "parts": [ { "text": "A grounded research answer." } ] },
              "groundingMetadata": {
                "webSearchQueries": ["current software engineering news"],
                "groundingChunks": [
                  { "web": { "uri": "https://example.com/current", "title": "Primary source" } }
                ],
                "groundingSupports": [
                  {
                    "segment": { "text": "The supported current fact." },
                    "groundingChunkIndices": [0]
                  }
                ]
              }
            }
          ],
          "usageMetadata": {
            "promptTokenCount": 100,
            "candidatesTokenCount": 20,
            "totalTokenCount": 120
          }
        }
        """;

    private sealed class RecordingHandler(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public Uri? LastUri { get; private set; }

        public string LastBody { get; private set; } = string.Empty;

        public string? LastApiKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastUri = request.RequestUri;
            LastApiKey = request.Headers.TryGetValues("x-goog-api-key", out var values)
                ? values.Single()
                : null;
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
