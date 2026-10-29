using System.Security.Cryptography;
using System.Text;
using IngredientKeywordSearch;
using MongoDB.Bson;
using PMC_APP.DTOs;

namespace PMC_APP.IngredientSearch;

public sealed record IngredientFrequencyLabels(
    List<IngredientKeywordCountDTO> Title,
    List<IngredientKeywordCountDTO> Abstract,
    List<IngredientKeywordCountDTO> Sections);

/// <summary>Uses the original IngredientKeywordSearch matcher unchanged.</summary>
public sealed class IngredientFrequencyAnalyzer : IDisposable
{
    public KeywordMatcher Matcher { get; }
    public string Version { get; }
    private readonly ThreadLocal<long[][]> _counters;

    public IngredientFrequencyAnalyzer(IEnumerable<string> keywords)
    {
        Matcher = new KeywordMatcher(keywords);
        _counters = new(() => Enumerable.Range(0, 3).Select(_ => new long[Matcher.KeywordCount]).ToArray());
        // Length-prefixing prevents ambiguous fingerprints for keywords containing newlines.
        string effectiveList = string.Concat(Matcher.Keywords.Select(k => $"{k.Length}:{k}"));
        Version = "aho-corasick-whole-word-separate-sections-v1:"
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(effectiveList))).ToLowerInvariant();
    }

    public static bool IsEligible(BsonDocument article) =>
        article.TryGetValue("AbstractText", out var abstractText) && abstractText.IsString
        && !string.IsNullOrWhiteSpace(abstractText.AsString)
        && article.TryGetValue("Sections", out var sections) && sections.IsBsonDocument
        && sections.AsBsonDocument.Elements.Any(s => s.Value.IsString
            && !string.IsNullOrWhiteSpace(s.Value.AsString));

    public IngredientFrequencyLabels Analyze(string? title, string abstractText, IEnumerable<string> sections)
    {
        var counts = _counters.Value!;
        foreach (var field in counts) Array.Clear(field);
        Matcher.CountInto(title ?? string.Empty, counts[0]);
        Matcher.CountInto(abstractText, counts[1]);
        foreach (string section in sections)
            Matcher.CountInto(section, counts[2]);
        return new(ToEntries(Matcher.ToDictionary(counts[0])),
            ToEntries(Matcher.ToDictionary(counts[1])), ToEntries(Matcher.ToDictionary(counts[2])));
    }

    private static List<IngredientKeywordCountDTO> ToEntries(Dictionary<string, long> counts) =>
        counts.Select(kv => new IngredientKeywordCountDTO { Keyword = kv.Key, Count = kv.Value }).ToList();

    public BsonDocument BuildUpdate(IngredientFrequencyLabels labels, DateTime labeledAtUtc) =>
        new("$set", new BsonDocument
        {
            { "IngredientTitleCounts", ToBson(labels.Title) },
            { "HasIngredientInTitle", labels.Title.Count > 0 },
            { "IngredientAbstractCounts", ToBson(labels.Abstract) },
            { "HasIngredientInAbstract", labels.Abstract.Count > 0 },
            { "IngredientSectionCounts", ToBson(labels.Sections) },
            { "HasIngredientInSections", labels.Sections.Count > 0 },
            { "IngredientFrequencyVersion", Version },
            { "IngredientFrequencyLabeledAt", labeledAtUtc },
            { "IsIngredientFrequencyLabeled", true }
        });

    private static BsonArray ToBson(IEnumerable<IngredientKeywordCountDTO> entries) =>
        new(entries.Select(e => new BsonDocument { { "Keyword", e.Keyword }, { "Count", e.Count } }));

    public void Dispose() => _counters.Dispose();
}
