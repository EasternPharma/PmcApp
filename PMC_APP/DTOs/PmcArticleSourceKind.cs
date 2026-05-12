namespace PMC_APP.DTOs;

/// <summary>
/// Which pipeline produced the indexed summary row in MongoDB.
/// </summary>
public enum PmcArticleSourceKind
{
    XmlBulk = 1,
    Scraped = 2,
}
