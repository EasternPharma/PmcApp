using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PMC_APP.DTOs;

/// <summary>
/// Lightweight read model for the <c>article_list</c> collection.
/// Only two fields are projected: <c>_id</c> (pmc_id) and <c>user</c>.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class MongoArticleListDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.Int32)]
    public int PmcId { get; set; }

    [BsonElement("user")]
    public string? User { get; set; }
}

/// <summary>
/// BSON field contract for the MongoDB <c>articles</c> collection (snake_case, int32 <c>_id</c>).
/// </summary>
[BsonIgnoreExtraElements]
public abstract class PmcMongoArticleDocumentBase
{
    [BsonId]
    [BsonRepresentation(BsonType.Int32)]
    public int PmcId { get; set; }

    /// <summary>Same value as <see cref="PmcId"/>; Python writes both <c>_id</c> and <c>pmc_id</c> and the driver requires an explicit member for <c>pmc_id</c>.</summary>
    [BsonElement("pmc_id")]
    [BsonRepresentation(BsonType.Int32)]
    public int PmcIdMongoField { get; set; }

    [BsonElement("pm_id")]
    public int? PmId { get; set; }

    [BsonElement("doi")]
    public string? Doi { get; set; }

    [BsonElement("title")]
    public string? Title { get; set; }

    [BsonElement("category")]
    public string? Category { get; set; }

    [BsonElement("journal")]
    public string? Journal { get; set; }

    [BsonElement("publisher")]
    public string? Publisher { get; set; }

    [BsonElement("volume")]
    public string? Volume { get; set; }

    [BsonElement("issue")]
    public string? Issue { get; set; }

    [BsonElement("issn")]
    public string? ISSN { get; set; }

    [BsonElement("f_page")]
    public string? FPage { get; set; }

    [BsonElement("l_page")]
    public string? LPage { get; set; }

    [BsonElement("authors")]
    public List<string>? Authors { get; set; }

    [BsonElement("publish_date")]
    public DateTime? PublishDate { get; set; }

    [BsonElement("abstract_text")]
    public string? AbstractText { get; set; }

    [BsonElement("keywords")]
    public List<string>? Keywords { get; set; }

    [BsonElement("sections")]
    public Dictionary<string, string>? Sections { get; set; }
}

/// <summary>
/// Concrete Mongo read model for <c>articles</c>. Inherits BSON mapping from <see cref="PmcMongoArticleDocumentBase"/>;
/// maps to <see cref="ArticleDTO"/> for the Xml/JSON pipeline (unchanged).
/// </summary>
[BsonIgnoreExtraElements]
public sealed class MongoPmcArticleDocument : PmcMongoArticleDocumentBase
{
    /// <summary>Maps this document to <see cref="ArticleDTO"/>.</summary>
    public ArticleDTO ToArticleDto() =>
        new()
        {
            PmcId = PmcId != 0 ? PmcId : PmcIdMongoField,
            PmId = PmId,
            Doi = Doi,
            Title = Title,
            Category = Category,
            Journal = Journal,
            Publisher = Publisher,
            Volume = Volume,
            Issue = Issue,
            ISSN = ISSN,
            FPage = FPage,
            LPage = LPage,
            Authors = Authors,
            PublishDate = PublishDate,
            AbstractText = AbstractText,
            Keywords = Keywords,
            Sections = Sections ?? new Dictionary<string, string>(),
        };
}
