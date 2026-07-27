namespace Ali.Modules.Internet;

public sealed record SourceExcerpt(
    int Index,
    string Topic,
    string Name,
    string Url,
    DateTimeOffset RetrievedAt,
    string Excerpt);

public sealed record SourceRetrievalResult(
    IReadOnlyList<SourceExcerpt> Excerpts,
    IReadOnlyList<string> Warnings,
    bool RequiresSourceGrounding = true)
{
    public bool HasSources => Excerpts.Count > 0;

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
