namespace PMC_APP.DTOs;

/// <summary>
/// Counts for PMC IDs from a search-result text file against indexed MongoDB summaries.
/// </summary>
public class PmcTxtDbCompareReport
{
    /// <summary>Human-readable label for the search (e.g. ingredient + indication).</summary>
    public string SearchQueryLabel { get; set; } = "";

    /// <summary>Number of PMC IDs listed in the input text file (non-blank lines).</summary>
    public int ExpectedPmcIdCount { get; set; }

    /// <summary>Summary documents returned for those PMC IDs (any source).</summary>
    public int MatchedSummaryRowCount { get; set; }

    public int MatchedWithFullTextCount { get; set; }

    public int MatchedWithAbstractCount { get; set; }

    public int MatchedWithFullTextAndAbstractCount { get; set; }

    public int XmlBulkRowCount { get; set; }

    public int XmlBulkWithFullTextCount { get; set; }

    public int XmlBulkWithAbstractCount { get; set; }

    public int XmlBulkWithFullTextAndAbstractCount { get; set; }

    public int ScrapedRowCount { get; set; }

    public int ScrapedWithFullTextCount { get; set; }

    public int ScrapedWithAbstractCount { get; set; }

    public int ScrapedWithFullTextAndAbstractCount { get; set; }
}
