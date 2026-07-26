using System.Text.RegularExpressions;
using Ali.Modules.Runtime;

namespace Ali.Modules.Internet;

/// <summary>
/// Routes obvious current/source-backed requests without spending a model generation.
/// The answer model is reserved for the user's answer, not orchestration bookkeeping.
/// </summary>
public sealed class RuleBasedSourceQueryPlanner : ISourceQueryPlanner
{
    private static readonly Regex UrlRegex = new(
        @"\b(?:https?://|www\.)\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CurrentSourceRegex = new(
        @"\b(?:search|browse|look\s+up|verify|source|sources|citation|official|current|currently|latest|recent|today|tonight|tomorrow|live|breaking|news|weather|forecast|temperature|score|scores|schedule|price|prices|stock|crypto|exchange\s+rate|president|governor|mayor|ceo|law|laws|regulation|regulations|available|availability|release\s+date)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LocalDocumentRegex = new(
        @"\b(?:local\s+(?:file|files|document|documents|library)|my\s+(?:file|files|document|documents)|folder|manual|pdf)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public Task<SourceQueryPlan> PlanAsync(
        string userText,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = userText?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return Task.FromResult(SourceQueryPlan.NoSources);
        }

        if (LocalDocumentRegex.IsMatch(query))
        {
            return Task.FromResult(new SourceQueryPlan(
                true,
                true,
                "local_documents",
                query,
                [query],
                ["local_documents"]));
        }

        if (!UrlRegex.IsMatch(query) && !CurrentSourceRegex.IsMatch(query))
        {
            return Task.FromResult(SourceQueryPlan.NoSources);
        }

        var intent = query.Contains("weather", StringComparison.OrdinalIgnoreCase)
                     || query.Contains("forecast", StringComparison.OrdinalIgnoreCase)
            ? "weather"
            : query.Contains("news", StringComparison.OrdinalIgnoreCase)
              || query.Contains("breaking", StringComparison.OrdinalIgnoreCase)
                ? "current_news"
                : UrlRegex.IsMatch(query)
                    ? "docs"
                    : "general_sources";
        var topics = intent switch
        {
            "weather" => new[] { "weather" },
            "current_news" => new[] { "news" },
            "docs" => new[] { "reference" },
            _ => Array.Empty<string>()
        };

        return Task.FromResult(new SourceQueryPlan(
            true,
            true,
            intent,
            query,
            [query],
            topics));
    }
}
