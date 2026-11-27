namespace AgentOrchestrator.Core.Engine;

/// <summary>
/// Watches a streamed response for a model stuck repeating itself. Feed it text as it arrives; it reports the first
/// point where the tail is one unit of text (containing a letter or digit) repeated back to back enough times.
/// </summary>
public sealed class RepetitionDetector
{
    private const int MaxUnitLength = 200;
    private const int MinRepeats = 5;
    private const int MinRepeatedChars = 60;
    private const int WindowChars = 1000;

    private string _tail = "";
    private int _tailStart;
    private int _total;

    /// <summary>Index in the full response where the repetition begins, after one copy of the unit. Valid once <see cref="Feed"/> returns true.</summary>
    public int CutIndex { get; private set; }

    public bool Feed(string delta)
    {
        _total += delta.Length;
        _tail += delta;
        if (_tail.Length > WindowChars)
            _tail = _tail[^WindowChars..];
        _tailStart = _total - _tail.Length;

        var end = _tail.Length;
        for (var unit = 1; unit <= Math.Min(MaxUnitLength, end); unit++)
        {
            var pattern = _tail.AsSpan(end - unit, unit);
            if (!ContainsLetterOrDigit(pattern))
                continue;

            var start = end - unit;
            var repeats = 1;
            while (start - unit >= 0 && _tail.AsSpan(start - unit, unit).SequenceEqual(pattern))
            {
                start -= unit;
                repeats++;
            }

            if (repeats >= MinRepeats && end - start >= MinRepeatedChars)
            {
                CutIndex = _tailStart + start + unit;
                return true;
            }
        }
        return false;
    }

    private static bool ContainsLetterOrDigit(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
            if (char.IsLetterOrDigit(c))
                return true;
        return false;
    }
}
