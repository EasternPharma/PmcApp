using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PMC_APP.DTOs;

/// <summary>
/// Lightweight article row for validation: one document per (PMC id, source kind).
/// </summary>
public class PmcIndexedArticleSummary
{
    /// <summary>Stable key: "{pmcId}:{sourceKind}" so Xml bulk and scraped rows never collide.</summary>
    [BsonId]
    public string Id { get; set; } = "";

    public int PmcId { get; set; }

    [BsonRepresentation(BsonType.Int32)]
    public PmcArticleSourceKind SourceKind { get; set; }

    public bool HasFullTextSections { get; set; }

    public int SectionCount { get; set; }

    public bool HasAbstract { get; set; }

    public static PmcIndexedArticleSummary FromArticle(ArticleDTO article, PmcArticleSourceKind sourceKind)
    {
        var hasSections = article.Sections != null && article.Sections.Count > 0;
        return new PmcIndexedArticleSummary
        {
            Id = BuildId(article.PmcId, sourceKind),
            PmcId = article.PmcId,
            SourceKind = sourceKind,
            HasFullTextSections = hasSections,
            SectionCount = article.Sections?.Count ?? 0,
            HasAbstract = !string.IsNullOrWhiteSpace(article.AbstractText),
        };
    }

    public static string BuildId(int pmcId, PmcArticleSourceKind sourceKind) =>
        $"{pmcId}:{(int)sourceKind}";
}
