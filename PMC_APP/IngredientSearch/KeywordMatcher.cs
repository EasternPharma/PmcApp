using System.Globalization;

namespace IngredientKeywordSearch;

/// <summary>
/// An immutable Aho-Corasick multi-pattern matcher.
/// Construct it once, then reuse it concurrently for any number of documents.
/// Matching ignores invariant-case differences and requires Unicode word boundaries.
/// </summary>
public sealed class KeywordMatcher
{
    private sealed class Node
    {
        public readonly Dictionary<char, int> Edges = new();
        public int Failure;
        public int OutputLink = -1;
        public int KeywordId = -1;
    }

    private readonly Node[] _nodes;
    private readonly string[] _keywords;
    private readonly int[] _keywordLengths;
    private readonly IReadOnlyList<string> _keywordView;

    public IReadOnlyList<string> Keywords => _keywordView;
    public int KeywordCount => _keywords.Length;

    public KeywordMatcher(IEnumerable<string> keywords)
    {
        ArgumentNullException.ThrowIfNull(keywords);

        var original = new List<string>();
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string? raw in keywords)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            string keyword = raw.Trim();
            string folded = Fold(keyword);
            if (seen.Add(folded))
            {
                original.Add(keyword);
                normalized.Add(folded);
            }
        }

        if (original.Count == 0)
            throw new ArgumentException("At least one non-empty keyword is required.", nameof(keywords));

        _keywords = original.ToArray();
        _keywordView = Array.AsReadOnly(_keywords);
        _keywordLengths = _keywords.Select(word => word.Length).ToArray();

        var nodes = new List<Node> { new() };
        for (int id = 0; id < normalized.Count; id++)
        {
            int state = 0;
            foreach (char ch in normalized[id])
            {
                if (!nodes[state].Edges.TryGetValue(ch, out int next))
                {
                    next = nodes.Count;
                    nodes[state].Edges.Add(ch, next);
                    nodes.Add(new Node());
                }
                state = next;
            }
            nodes[state].KeywordId = id;
        }

        // Breadth-first construction of failure and terminal-output links.
        // Output links avoid copying long output arrays to every trie node.
        var queue = new Queue<int>();
        foreach (int child in nodes[0].Edges.Values)
            queue.Enqueue(child);

        while (queue.Count != 0)
        {
            int parent = queue.Dequeue();
            foreach (var edge in nodes[parent].Edges)
            {
                char ch = edge.Key;
                int child = edge.Value;
                int fallback = nodes[parent].Failure;

                while (fallback != 0 && !nodes[fallback].Edges.ContainsKey(ch))
                    fallback = nodes[fallback].Failure;

                nodes[child].Failure = nodes[fallback].Edges.TryGetValue(ch, out int target)
                    ? target
                    : 0;

                int failure = nodes[child].Failure;
                nodes[child].OutputLink = nodes[failure].KeywordId >= 0
                    ? failure
                    : nodes[failure].OutputLink;

                queue.Enqueue(child);
            }
        }

        _nodes = nodes.ToArray();
    }

    /// <summary>Convenient single-document API returning only keywords with positive counts.</summary>
    public Dictionary<string, long> Search(string text)
    {
        var counts = new long[KeywordCount];
        CountInto(text, counts);
        return ToDictionary(counts);
    }

    /// <summary>
    /// Add occurrences to the supplied counters without allocating a match list.
    /// The caller must give each parallel worker its own counters array.
    /// </summary>
    public void CountInto(string text, long[] counts)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Length != KeywordCount)
            throw new ArgumentException("Counter length must match KeywordCount.", nameof(counts));

        int state = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = FoldChar(text[i]);

            // Follow failure links until the transition exists or we reach root.
            while (true)
            {
                if (_nodes[state].Edges.TryGetValue(ch, out int next))
                {
                    state = next;
                    break;
                }

                if (state == 0)
                    break;

                state = _nodes[state].Failure;
            }

            int terminal = _nodes[state].KeywordId >= 0
                ? state
                : _nodes[state].OutputLink;

            // A longer phrase and its suffixes may all match at the same position.
            while (terminal >= 0)
            {
                int id = _nodes[terminal].KeywordId;
                int start = i - _keywordLengths[id] + 1;

                bool leftBoundary = start == 0 || !IsWordAt(text, start - 1);
                bool rightBoundary = i + 1 == text.Length || !IsWordAt(text, i + 1);
                if (leftBoundary && rightBoundary)
                    counts[id]++;

                terminal = _nodes[terminal].OutputLink;
            }
        }
    }

    public Dictionary<string, long> ToDictionary(long[] counts, bool includeZeros = false)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Length != KeywordCount)
            throw new ArgumentException("Counter length must match KeywordCount.", nameof(counts));

        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < KeywordCount; i++)
            if (includeZeros || counts[i] != 0)
                result.Add(_keywords[i], counts[i]);
        return result;
    }

    private static string Fold(string value)
    {
        var chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            chars[i] = FoldChar(chars[i]);
        return new string(chars);
    }

    private static char FoldChar(char ch)
    {
        if (ch is >= 'A' and <= 'Z')
            return (char)(ch + ('a' - 'A'));
        return ch <= 127 ? ch : char.ToLowerInvariant(ch);
    }

    private static bool IsWordAt(string text, int index)
    {
        char ch = text[index];
        if (ch == '_' || ch is >= 'a' and <= 'z' || ch is >= 'A' and <= 'Z'
            || ch is >= '0' and <= '9')
            return true;

        // A supplementary-plane letter is encoded by two UTF-16 code units.
        if (char.IsLowSurrogate(ch) && index > 0 && char.IsHighSurrogate(text[index - 1]))
            index--;

        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(text, index);
        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber
            or UnicodeCategory.OtherNumber
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.ConnectorPunctuation;
    }
}

/// <summary>One-off API matching the requested Search(keywords, text) signature.</summary>
public static class KeywordSearch
{
    // For millions of documents, reuse KeywordMatcher instead of rebuilding it per call.
    public static Dictionary<string, long> Search(IEnumerable<string> keywords, string text) =>
        new KeywordMatcher(keywords).Search(text);
}
