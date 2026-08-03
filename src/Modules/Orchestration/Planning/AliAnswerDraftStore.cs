using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.Orchestration.Planning;

internal sealed record AliCommittedAnswerSegment(
    string AnswerId,
    int Sequence,
    string PreviousSegmentHash,
    string SegmentHash,
    long CommittedOffset,
    string Text,
    IReadOnlyList<string> CoveredClaimIds);

internal sealed record AliAnswerDraftSnapshot(
    string AnswerId,
    int NextSequence,
    string PreviousSegmentHash,
    long CommittedOffset,
    string PriorTail,
    long PriorTailOffset,
    string PriorTailDigest,
    IReadOnlyList<string> RemainingClaimIds);

internal sealed record AliCommittedAnswerDraft(
    string AnswerId,
    string Text,
    string AnswerDigest,
    IReadOnlyList<AliCommittedAnswerSegment> Segments,
    IReadOnlyList<string> CoveredClaimIds);

/// <summary>
/// Owns one answer's append-only segment chain. Validation happens before any state changes, so a
/// clipped, malformed, stale, or cross-answer segment can be discarded without becoming
/// continuation context.
/// </summary>
internal sealed class AliAnswerDraftStore
{
    private readonly object _sync = new();
    private readonly string _answerId;
    private readonly string[] _requiredClaimIds;
    private readonly HashSet<string> _requiredClaimSet;
    private readonly HashSet<string> _coveredClaimIds = new(StringComparer.Ordinal);
    private readonly List<AliCommittedAnswerSegment> _segments = [];
    private readonly StringBuilder _text = new();
    private AliCommittedAnswerDraft? _completed;

    internal AliAnswerDraftStore(string answerId, IEnumerable<string>? requiredClaimIds)
    {
        TurnStateIntegrity.RequireBoundedValue(answerId, 256, nameof(answerId));
        _answerId = answerId;
        _requiredClaimIds = (requiredClaimIds ?? [])
            .Select(static value => value ?? string.Empty)
            .ToArray();
        if (_requiredClaimIds.Length > 256
            || _requiredClaimIds.Any(static value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 256)
            || _requiredClaimIds.Distinct(StringComparer.Ordinal).Count()
                != _requiredClaimIds.Length)
        {
            throw new ArgumentException(
                "Required completion claim identifiers must be unique bounded values.",
                nameof(requiredClaimIds));
        }

        _requiredClaimSet = _requiredClaimIds.ToHashSet(StringComparer.Ordinal);
    }

    internal string GenesisHash => CalculateGenesisHash(_answerId);

    internal AliCommittedAnswerSegment Append(AliAppendAnswerSegmentAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            if (_completed is not null)
            {
                throw new InvalidDataException("A completed answer draft cannot accept a segment.");
            }

            if (!string.Equals(action.AnswerId, _answerId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The answer segment does not match the active answer identity.");
            }

            if (action.Sequence != _segments.Count)
            {
                throw new InvalidDataException(
                    "The answer segment does not match the next committed sequence.");
            }

            var expectedPreviousHash = _segments.Count == 0
                ? GenesisHash
                : _segments[^1].SegmentHash;
            if (!string.Equals(
                    action.PreviousSegmentHash,
                    expectedPreviousHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The answer segment does not extend the committed hash chain.");
            }

            if (string.IsNullOrEmpty(action.Text))
            {
                throw new InvalidDataException("An answer segment cannot be empty.");
            }

            var covered = action.CoveredClaimIds
                .Select(static value => value ?? string.Empty)
                .ToArray();
            if (covered.Length > 256
                || covered.Any(static value =>
                    string.IsNullOrWhiteSpace(value) || value.Length > 256)
                || covered.Distinct(StringComparer.Ordinal).Count() != covered.Length
                || covered.Any(value => !_requiredClaimSet.Contains(value)))
            {
                throw new InvalidDataException(
                    "An answer segment declared invalid or unrequested claim coverage.");
            }

            var committedOffset = checked((long)_text.Length + action.Text.Length);
            var segmentHash = CalculateSegmentHash(
                _answerId,
                action.Sequence,
                expectedPreviousHash,
                committedOffset,
                action.Text,
                covered);
            var committed = new AliCommittedAnswerSegment(
                _answerId,
                action.Sequence,
                expectedPreviousHash,
                segmentHash,
                committedOffset,
                action.Text,
                Array.AsReadOnly(covered));

            _text.Append(action.Text);
            _segments.Add(committed);
            foreach (var claimId in covered)
            {
                _coveredClaimIds.Add(claimId);
            }

            return committed;
        }
    }

    internal AliAnswerDraftSnapshot Snapshot(int maximumPriorTailCharacters = int.MaxValue)
    {
        if (maximumPriorTailCharacters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPriorTailCharacters));
        }

        lock (_sync)
        {
            var tailLength = Math.Min(maximumPriorTailCharacters, _text.Length);
            var tailOffset = _text.Length - tailLength;
            var tail = tailLength == 0
                ? string.Empty
                : _text.ToString(tailOffset, tailLength);
            return new AliAnswerDraftSnapshot(
                _answerId,
                _segments.Count,
                _segments.Count == 0 ? GenesisHash : _segments[^1].SegmentHash,
                _text.Length,
                tail,
                tailOffset,
                TurnStateIntegrity.Digest(tail),
                Array.AsReadOnly(_requiredClaimIds
                    .Where(claimId => !_coveredClaimIds.Contains(claimId))
                    .ToArray()));
        }
    }

    internal AliCommittedAnswerDraft Finish(string answerId)
    {
        lock (_sync)
        {
            if (!string.Equals(answerId, _answerId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The finish action does not match the active answer identity.");
            }

            if (_completed is not null)
            {
                return _completed;
            }

            if (_segments.Count == 0)
            {
                throw new InvalidDataException(
                    "An answer cannot finish before one complete segment is committed.");
            }

            var remaining = _requiredClaimIds
                .Where(claimId => !_coveredClaimIds.Contains(claimId))
                .ToArray();
            if (remaining.Length > 0)
            {
                throw new InvalidDataException(
                    "An answer cannot finish before every required claim has segment coverage.");
            }

            var exactText = _text.ToString();
            _completed = new AliCommittedAnswerDraft(
                _answerId,
                exactText,
                TurnStateIntegrity.Digest(exactText),
                Array.AsReadOnly(_segments.ToArray()),
                Array.AsReadOnly(_requiredClaimIds.ToArray()));
            return _completed;
        }
    }

    internal static AliCommittedAnswerDraft BindExactRenderedText(
        AliCommittedAnswerDraft committed,
        string renderedText)
    {
        ArgumentNullException.ThrowIfNull(committed);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderedText);
        var replay = new AliAnswerDraftStore(
            committed.AnswerId,
            committed.CoveredClaimIds);
        foreach (var segment in committed.Segments.OrderBy(static item => item.Sequence))
        {
            var snapshot = replay.Snapshot();
            var accepted = replay.Append(new AliAppendAnswerSegmentAction(
                committed.AnswerId,
                snapshot.NextSequence,
                snapshot.PreviousSegmentHash,
                segment.Text,
                segment.CoveredClaimIds));
            if (!string.Equals(accepted.SegmentHash, segment.SegmentHash, StringComparison.Ordinal)
                || accepted.CommittedOffset != segment.CommittedOffset)
            {
                throw new InvalidDataException(
                    "The committed answer draft failed exact hash-chain replay before rendering.");
            }
        }

        var exact = replay.Finish(committed.AnswerId);
        if (!string.Equals(exact.Text, committed.Text, StringComparison.Ordinal)
            || !string.Equals(exact.AnswerDigest, committed.AnswerDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The committed answer draft failed exact text replay before rendering.");
        }

        if (string.Equals(renderedText, committed.Text, StringComparison.Ordinal))
        {
            return exact;
        }

        if (!renderedText.StartsWith(committed.Text, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The final renderer changed committed answer text instead of appending presentation material.");
        }

        // FinalAnswerRenderer is an append-only presentation boundary. Commit its exact suffix as
        // another hash-chained page so the critic audits precisely what publication will display.
        var suffix = renderedText[committed.Text.Length..];
        if (suffix.Length == 0)
        {
            return exact;
        }

        var renderedStore = new AliAnswerDraftStore(
            committed.AnswerId,
            committed.CoveredClaimIds);
        foreach (var segment in exact.Segments)
        {
            var snapshot = renderedStore.Snapshot();
            renderedStore.Append(new AliAppendAnswerSegmentAction(
                committed.AnswerId,
                snapshot.NextSequence,
                snapshot.PreviousSegmentHash,
                segment.Text,
                segment.CoveredClaimIds));
        }

        var renderedSnapshot = renderedStore.Snapshot();
        renderedStore.Append(new AliAppendAnswerSegmentAction(
            committed.AnswerId,
            renderedSnapshot.NextSequence,
            renderedSnapshot.PreviousSegmentHash,
            suffix,
            []));
        return renderedStore.Finish(committed.AnswerId);
    }

    private static string CalculateGenesisHash(string answerId) =>
        TurnStateIntegrity.Digest(JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocol = "Ali.AnswerSegment.Genesis.v1",
            answerId
        }));

    private static string CalculateSegmentHash(
        string answerId,
        int sequence,
        string previousSegmentHash,
        long committedOffset,
        string text,
        IReadOnlyList<string> coveredClaimIds) =>
        TurnStateIntegrity.Digest(JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocol = "Ali.AnswerSegment.v1",
            answerId,
            sequence,
            previousSegmentHash,
            committedOffset,
            text,
            coveredClaimIds
        }));
}
