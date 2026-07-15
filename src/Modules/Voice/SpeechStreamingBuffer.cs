using System.Text;

namespace Ali.Modules.Voice;

public sealed class SpeechStreamingBuffer
{
    private readonly StringBuilder _buffer = new();
    private readonly int _firstMinimumSegmentCharacters;
    private readonly int _minimumSegmentCharacters;
    private readonly int _maximumSegmentCharacters;
    private bool _hasEmittedSegment;

    public SpeechStreamingBuffer(
        int minimumSegmentCharacters = 180,
        int maximumSegmentCharacters = 700,
        int firstMinimumSegmentCharacters = 80)
    {
        if (minimumSegmentCharacters < 20)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSegmentCharacters));
        }

        if (firstMinimumSegmentCharacters < 20)
        {
            throw new ArgumentOutOfRangeException(nameof(firstMinimumSegmentCharacters));
        }

        if (maximumSegmentCharacters <= minimumSegmentCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSegmentCharacters));
        }

        _firstMinimumSegmentCharacters = Math.Min(firstMinimumSegmentCharacters, minimumSegmentCharacters);
        _minimumSegmentCharacters = minimumSegmentCharacters;
        _maximumSegmentCharacters = maximumSegmentCharacters;
    }

    public IReadOnlyList<string> Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        _buffer.Append(text);
        return Drain(force: false);
    }

    public IReadOnlyList<string> Complete() => Drain(force: true);

    private IReadOnlyList<string> Drain(bool force)
    {
        var segments = new List<string>();
        while (_buffer.Length > 0)
        {
            var length = force ? _buffer.Length : FindReadySegmentLength();
            if (length <= 0)
            {
                break;
            }

            var raw = _buffer.ToString(0, length);
            _buffer.Remove(0, length);
            _hasEmittedSegment = true;
            var cleaned = SpeechOutputCleaner.Clean(raw);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                segments.Add(cleaned);
            }
        }

        return segments;
    }

    private int FindReadySegmentLength()
    {
        var minimumSegmentCharacters = _hasEmittedSegment
            ? _minimumSegmentCharacters
            : _firstMinimumSegmentCharacters;

        if (_buffer.Length < minimumSegmentCharacters)
        {
            return 0;
        }

        for (var index = minimumSegmentCharacters - 1; index < _buffer.Length; index++)
        {
            if (IsSentenceBoundary(index))
            {
                return index + 1;
            }
        }

        if (_buffer.Length < _maximumSegmentCharacters)
        {
            return 0;
        }

        for (var index = Math.Min(_maximumSegmentCharacters, _buffer.Length - 1); index >= minimumSegmentCharacters; index--)
        {
            if (char.IsWhiteSpace(_buffer[index]))
            {
                return index + 1;
            }
        }

        return Math.Min(_maximumSegmentCharacters, _buffer.Length);
    }

    private bool IsSentenceBoundary(int index)
    {
        var current = _buffer[index];
        if (current == '\n')
        {
            return true;
        }

        if (current is not ('.' or '!' or '?'))
        {
            return false;
        }

        var previous = index > 0 ? _buffer[index - 1] : '\0';
        var next = index + 1 < _buffer.Length ? _buffer[index + 1] : '\0';
        if (char.IsDigit(previous) && char.IsDigit(next))
        {
            return false;
        }

        return next == '\0' || char.IsWhiteSpace(next);
    }
}
