using System.Globalization;

namespace RotrTrainer.Core;

/// <summary>A byte pattern such as "A1 ?? ?? ?? ?? 8B 40 0C"; "??" matches any byte.</summary>
public sealed class BytePattern
{
    private readonly byte[] _bytes;
    private readonly bool[] _isWildcard;

    private BytePattern(byte[] bytes, bool[] isWildcard)
    {
        _bytes = bytes;
        _isWildcard = isWildcard;
        Anchor = Array.IndexOf(isWildcard, false);
    }

    public int Length => _bytes.Length;

    // First fixed byte; the scanner jumps between its occurrences instead of testing every offset.
    internal int Anchor { get; }
    internal byte AnchorByte => _bytes[Anchor];

    public static BytePattern Parse(string text)
    {
        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            throw new ArgumentException("pattern is empty", nameof(text));

        var bytes = new byte[tokens.Length];
        var isWildcard = new bool[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "??")
                isWildcard[i] = true;
            else if (tokens[i].Length != 2
                || !byte.TryParse(tokens[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i]))
                throw new ArgumentException($"bad pattern token '{tokens[i]}'", nameof(text));
        }
        if (Array.TrueForAll(isWildcard, wildcard => wildcard))
            throw new ArgumentException("pattern has no fixed bytes", nameof(text));
        return new BytePattern(bytes, isWildcard);
    }

    internal bool MatchesAt(ReadOnlySpan<byte> data, int start)
    {
        for (int i = 0; i < _bytes.Length; i++)
            if (!_isWildcard[i] && data[start + i] != _bytes[i])
                return false;
        return true;
    }
}

public static class SignatureScanner
{
    /// <summary>Offset of the only match, or null when there are zero or several; never the first of many.</summary>
    public static int? FindUnique(ReadOnlySpan<byte> data, BytePattern pattern, out int matchCount)
    {
        matchCount = 0;
        int? found = null;
        int lastStart = data.Length - pattern.Length;
        int searchFrom = pattern.Anchor;
        while (searchFrom <= lastStart + pattern.Anchor)
        {
            int hit = data[searchFrom..].IndexOf(pattern.AnchorByte);
            if (hit < 0)
                break;
            int start = searchFrom + hit - pattern.Anchor;
            if (start > lastStart)
                break;
            if (pattern.MatchesAt(data, start))
            {
                matchCount++;
                found = start;
            }
            searchFrom += hit + 1;
        }
        return matchCount == 1 ? found : null;
    }
}
