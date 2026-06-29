namespace Ali.Core.Sources;

public sealed class CompositeSourceRetriever : ISourceRetriever
{
    private readonly IReadOnlyList<ISourceRetriever> _retrievers;

    public CompositeSourceRetriever(params ISourceRetriever[] retrievers)
    {
        _retrievers = retrievers.Where(retriever => retriever is not null).ToArray();
    }

    public Task<SourceRetrievalResult> RetrieveAsync(string userText, CancellationToken cancellationToken) =>
        RetrieveAsync(
            new SourceQueryPlan(
                true,
                true,
                "general_sources",
                userText,
                Array.Empty<string>(),
                Array.Empty<string>()),
            cancellationToken);

    public async Task<SourceRetrievalResult> RetrieveAsync(SourceQueryPlan plan, CancellationToken cancellationToken)
    {
        if (!plan.UseSources || _retrievers.Count == 0)
        {
            return SourceRetrievalResult.Empty;
        }

        var excerpts = new List<SourceExcerpt>();
        var warnings = new List<string>();
        var requiresSourceGrounding = plan.RequiresSourceGrounding;

        foreach (var retriever in _retrievers)
        {
            var result = await retriever.RetrieveAsync(plan, cancellationToken).ConfigureAwait(false);
            requiresSourceGrounding = requiresSourceGrounding || result.RequiresSourceGrounding;
            warnings.AddRange(result.Warnings);

            foreach (var excerpt in result.Excerpts)
            {
                excerpts.Add(excerpt with { Index = excerpts.Count + 1 });
            }
        }

        if (excerpts.Count == 0 && warnings.Count == 0)
        {
            return SourceRetrievalResult.Empty;
        }

        return new SourceRetrievalResult(excerpts, warnings, requiresSourceGrounding);
    }
}
