namespace Ali.Modules.Internet;

public sealed record SourceExcerpt(
    int Index,
    string Topic,
    string Name,
    string Url,
    DateTimeOffset RetrievedAt,
    string Excerpt);

public static class SourceProviderNames
{
    public const string GoogleGrounding = "Google Grounding";
}

public sealed record SourceProviderAttempt(
    string Provider,
    string Query,
    bool ProducedResults);

public sealed record SourceRetrievalResult(
    IReadOnlyList<SourceExcerpt> Excerpts,
    IReadOnlyList<string> Warnings,
    bool RequiresSourceGrounding = true,
    IReadOnlyList<SourceProviderAttempt>? ProviderAttempts = null)
{
    public bool HasSources => Excerpts.Count > 0;

    public IReadOnlyList<SourceProviderAttempt> Attempts => ProviderAttempts ?? [];

    public static SourceRetrievalResult Empty { get; } = new([], [], false);
}

/// <summary>
/// Structured retrieval request chosen by Ali's semantic model routing pass.
/// No English keyword classifier creates this plan.
/// </summary>
public sealed record SourceQueryPlan(
    bool UseSources,
    bool RequiresSourceGrounding,
    string Intent,
    string Topic,
    IReadOnlyList<string> QueryTerms,
    IReadOnlyList<string> PreferredSourceTopics)
{
    public string TemporalSelection { get; init; } = "none";

    public IReadOnlySet<string> ExcludedProviders { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static SourceQueryPlan NoSources { get; } = new(
        false,
        false,
        "none",
        string.Empty,
        [],
        []);

    public string SearchText =>
        string.Join(
            ' ',
            new[] { Intent, Topic }
                .Concat(QueryTerms)
                .Concat(PreferredSourceTopics)
                .Where(term => !string.IsNullOrWhiteSpace(term)));
}

public interface ISourceRetriever
{
    Task<SourceRetrievalResult> RetrieveAsync(string userText, CancellationToken cancellationToken);

    Task<SourceRetrievalResult> RetrieveAsync(SourceQueryPlan plan, CancellationToken cancellationToken) =>
        plan.UseSources
            ? RetrieveAsync(plan.SearchText, cancellationToken)
            : Task.FromResult(SourceRetrievalResult.Empty);
}
