using System.Text.Json;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.Orchestration.Planning;

internal sealed record AliCompletionProjectionPage(
    string SourceDigest,
    long Cursor,
    long NextCursor,
    bool HasMore,
    string Text,
    string PageDigest);

/// <summary>
/// Retains the exact completion dossier locally and exposes stable, bounded slices. A slice does
/// not advance until a complete answer segment extending that slice is committed.
/// </summary>
internal sealed class AliCompletionProjectionPager
{
    private readonly string _source;
    private readonly string _sourceDigest;
    private int _cursor;

    internal AliCompletionProjectionPager(TemporaryCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _source = BuildSource(request);
        _sourceDigest = TurnStateIntegrity.Digest(_source);
    }

    internal string SourceDigest => _sourceDigest;

    internal long Cursor => _cursor;

    internal long Length => _source.Length;

    internal bool IsExhausted => _cursor == _source.Length;

    internal AliCompletionProjectionPage Peek(int maximumCharacters)
    {
        if (maximumCharacters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        var remaining = _source.Length - _cursor;
        if (remaining > 0 && maximumCharacters == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCharacters),
                "A nonempty projection page requires positive capacity.");
        }

        var length = Math.Min(remaining, maximumCharacters);
        if (length > 0
            && _cursor + length < _source.Length
            && char.IsHighSurrogate(_source[_cursor + length - 1])
            && char.IsLowSurrogate(_source[_cursor + length]))
        {
            length--;
        }

        if (remaining > 0 && length == 0)
        {
            throw new InvalidOperationException(
                "The admitted projection capacity would split one Unicode scalar value.");
        }

        var text = length == 0 ? string.Empty : _source.Substring(_cursor, length);
        var nextCursor = checked(_cursor + length);
        return new AliCompletionProjectionPage(
            _sourceDigest,
            _cursor,
            nextCursor,
            nextCursor < _source.Length,
            text,
            CalculatePageDigest(_sourceDigest, _cursor, nextCursor, text));
    }

    internal void Commit(AliCompletionProjectionPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!string.Equals(page.SourceDigest, _sourceDigest, StringComparison.Ordinal)
            || page.Cursor != _cursor
            || page.NextCursor < page.Cursor
            || page.NextCursor > _source.Length
            || page.NextCursor - page.Cursor != page.Text.Length
            || !string.Equals(
                page.PageDigest,
                CalculatePageDigest(
                    page.SourceDigest,
                    page.Cursor,
                    page.NextCursor,
                    page.Text),
                StringComparison.Ordinal)
            || !string.Equals(
                page.Text,
                _source.Substring(
                    checked((int)page.Cursor),
                    checked((int)(page.NextCursor - page.Cursor))),
                StringComparison.Ordinal)
            || page.HasMore != (page.NextCursor < _source.Length))
        {
            throw new InvalidDataException(
                "The completion projection page does not match the active stable cursor.");
        }

        _cursor = checked((int)page.NextCursor);
    }

    internal static string BuildSource(TemporaryCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completionPlan = request.AcceptedDecision.NextAction as BeginCompletionAction
            ?? throw new InvalidDataException(
                "The completion projection requires an accepted BeginCompletion action.");
        return JsonSerializer.Serialize(new
        {
            completionPlan = completionPlan.Plan,
            immutableOriginalRequest = request.ImmutableOriginalRequest,
            authoritativeState = request.AuthoritativeInput.StateProjection,
            requiredOutcomes = request.RequiredOutcomes.Select(item => new
            {
                outcomeId = item.Id,
                item.Objective,
                status = item.Status.ToString(),
                evidenceIds = item.EvidenceIds
            }),
            materialClaims = request.RequiredClaims,
            citedAcceptedEvidence = request.CitedEvidence.Select(item => new
            {
                evidenceId = item.EvidenceId,
                workItemId = item.WorkItemId,
                toolName = item.ToolName,
                invocationStatus = item.InvocationStatus.ToString(),
                domainOutcome = item.DomainOutcome.ToString(),
                projection = item.Projection
            })
        });
    }

    private static string CalculatePageDigest(
        string sourceDigest,
        long cursor,
        long nextCursor,
        string text) =>
        TurnStateIntegrity.Digest(JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocol = "Ali.CompletionProjectionPage.v1",
            sourceDigest,
            cursor,
            nextCursor,
            text
        }));
}
