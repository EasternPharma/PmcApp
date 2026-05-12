using MongoDB.Driver;
using Newtonsoft.Json;
using PMC_APP.DTOs;

namespace PMC_APP;

public sealed class PmcArticleTxtDbCompareSettings
{
    public required string MongoHost { get; init; }

    public int MongoPort { get; init; } = 27017;

    public required string DatabaseName { get; init; }

    public required string ArticlesCollectionName { get; init; }

    public required string SummariesCollectionName { get; init; }

    /// <summary>Root folder containing Xml bulk JSON parts (*.json recursive).</summary>
    public required string XmlBulkJsonRootPath { get; init; }
}

/// <summary>
/// Builds MongoDB summary indexes from Xml bulk JSON and scraped articles, then compares PMC ID lists from text files to the database.
/// </summary>
public sealed class PmcArticleTxtDbComparer : IDisposable
{
    private readonly PmcArticleTxtDbCompareSettings _settings;
    private readonly MongoClient _client;
    private bool _disposed;

    public PmcArticleTxtDbComparer(PmcArticleTxtDbCompareSettings settings)
    {
        _settings = settings;
        _client = new MongoClient($"mongodb://{settings.MongoHost}:{settings.MongoPort}");
    }

    private IMongoDatabase Database => _client.GetDatabase(_settings.DatabaseName);

    private IMongoCollection<PmcIndexedArticleSummary> Summaries =>
        Database.GetCollection<PmcIndexedArticleSummary>(_settings.SummariesCollectionName);

    private IMongoCollection<MongoPmcArticleDocument> Articles =>
        Database.GetCollection<MongoPmcArticleDocument>(_settings.ArticlesCollectionName);

    /// <summary>Ensures both Xml bulk and scraped summary collections are populated (skips if rows already exist per source).</summary>
    public async Task EnsureSummariesReadyAsync(CancellationToken cancellationToken = default)
    {
        await EnsureXmlBulkSummariesAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine("Completed XML Bulk!");
        await EnsureScrapedSummariesAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine("Completed Scrap!");
    }

    public async Task EnsureXmlBulkSummariesAsync(CancellationToken cancellationToken = default)
    {
        var kind = PmcArticleSourceKind.XmlBulk;
        long existing = await Summaries.CountDocumentsAsync(
            Builders<PmcIndexedArticleSummary>.Filter.Eq(x => x.SourceKind, kind),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (existing > 0)
            return;

        if (!Directory.Exists(_settings.XmlBulkJsonRootPath))
            throw new DirectoryNotFoundException($"Xml bulk JSON path not found: {_settings.XmlBulkJsonRootPath}");

        var jsonFiles = Directory.GetFiles(_settings.XmlBulkJsonRootPath, "*.json", SearchOption.AllDirectories);
        int totalFiles = jsonFiles.Length;
        for (int fileIndex = 0; fileIndex < totalFiles; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string jsonPath = jsonFiles[fileIndex];
            string jsonContent = await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false);
            var articles = JsonConvert.DeserializeObject<List<ArticleDTO>>(jsonContent);
            if (articles == null || articles.Count == 0)
            {
                WriteConsoleProgress("Xml bulk summaries (JSON files)", fileIndex + 1, totalFiles);
                continue;
            }

            var summaries = articles.Select(a => PmcIndexedArticleSummary.FromArticle(a, kind)).ToList();
            await UpsertSummariesAsync(summaries, cancellationToken).ConfigureAwait(false);
            WriteConsoleProgress("Xml bulk summaries (JSON files)", fileIndex + 1, totalFiles);
        }

        if (totalFiles > 0)
            WriteConsoleProgressFinish();
    }

    public async Task EnsureScrapedSummariesAsync(CancellationToken cancellationToken = default)
    {
        var kind = PmcArticleSourceKind.Scraped;
        long existing = await Summaries.CountDocumentsAsync(
            Builders<PmcIndexedArticleSummary>.Filter.Eq(x => x.SourceKind, kind),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (existing > 0)
            return;

        long articleCount = await Articles.CountDocumentsAsync(
            FilterDefinition<MongoPmcArticleDocument>.Empty,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Scraped articles to index: {articleCount}");
        if (articleCount == 0)
            return;

        // Only fetch the 3 fields needed for a summary — skips title, authors, doi, keywords, etc.
        var projection = Builders<MongoPmcArticleDocument>.Projection
            .Include(x => x.PmcId)
            .Include(x => x.PmcIdMongoField)
            .Include(x => x.AbstractText)
            .Include(x => x.Sections);

        var sortAsc = Builders<MongoPmcArticleDocument>.Sort.Ascending(x => x.PmcId);

        const int pageSize = 10_000;
        long processedArticles = 0;
        int lastSeenId = 0; // keyset cursor: _id is always > 0

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Keyset pagination: filter _id > lastSeenId, no skip needed
            var pageFilter = Builders<MongoPmcArticleDocument>.Filter.Gt(x => x.PmcId, lastSeenId);

            List<MongoPmcArticleDocument> page = await Articles
                .Find(pageFilter)
                .Project<MongoPmcArticleDocument>(projection)
                .Sort(sortAsc)
                .Limit(pageSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            if (page.Count == 0)
                break;

            lastSeenId = page[^1].PmcId;

            // Build summaries inline — no ToArticleDto() allocation
            var summaries = page.Select(a =>
            {
                int pmcId = a.PmcId != 0 ? a.PmcId : a.PmcIdMongoField;
                bool hasSections = a.Sections != null && a.Sections.Count > 0;
                return new PmcIndexedArticleSummary
                {
                    Id = PmcIndexedArticleSummary.BuildId(pmcId, kind),
                    PmcId = pmcId,
                    SourceKind = kind,
                    HasFullTextSections = hasSections,
                    SectionCount = a.Sections?.Count ?? 0,
                    HasAbstract = !string.IsNullOrWhiteSpace(a.AbstractText),
                };
            }).ToList();

            // InsertMany is 3-5x faster than BulkWrite/upsert when collection is known empty
            await Summaries.InsertManyAsync(
                summaries,
                new InsertManyOptions { IsOrdered = false },
                cancellationToken).ConfigureAwait(false);

            processedArticles += page.Count;
            WriteConsoleProgress("Scraped summaries (articles)", processedArticles, articleCount);
        }

        if (articleCount > 0)
            WriteConsoleProgressFinish();
    }

    private const int ConsoleProgressBarWidth = 28;

    private static void WriteConsoleProgress(string label, long current, long total)
    {
        if (total <= 0)
            return;
        current = Math.Min(current, total);
        double ratio = current / (double)total;
        int filled = (int)Math.Round(ConsoleProgressBarWidth * ratio);
        if (filled > ConsoleProgressBarWidth)
            filled = ConsoleProgressBarWidth;
        string bar = new string('#', filled) + new string('-', ConsoleProgressBarWidth - filled);
        int pct = (int)Math.Round(100.0 * ratio);
        Console.Write($"\r{label} [{bar}] {current}/{total} {pct,3}% ");
    }

    private static void WriteConsoleProgressFinish()
    {
        Console.WriteLine();
    }

    private async Task UpsertSummariesAsync(
        IReadOnlyList<PmcIndexedArticleSummary> summaries,
        CancellationToken cancellationToken)
    {
        if (summaries.Count == 0)
            return;

        const int chunkSize = 1000;
        for (int i = 0; i < summaries.Count; i += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = summaries.Skip(i).Take(chunkSize).ToList();
            var models = new List<WriteModel<PmcIndexedArticleSummary>>(chunk.Count);
            foreach (var s in chunk)
            {
                var filter = Builders<PmcIndexedArticleSummary>.Filter.Eq(x => x.Id, s.Id);
                models.Add(new ReplaceOneModel<PmcIndexedArticleSummary>(filter, s) { IsUpsert = true });
            }

            await Summaries.BulkWriteAsync(models, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Parses lines like "PMC12345", "pmc12345", or "12345"; skips blanks.</summary>
    public static List<int> ParsePmcNumericIdsFromLines(IEnumerable<string> lines)
    {
        var ids = new List<int>();
        foreach (var raw in lines)
        {
            var t = raw.Trim();
            if (t.Length == 0)
                continue;
            if (t.StartsWith("PMC", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(3).TrimStart();
            if (int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int id))
                ids.Add(id);
        }

        return ids;
    }

    public async Task<PmcTxtDbCompareReport> CompareSearchResultTxtAsync(
        IReadOnlyList<int> pmcNumericIds,
        string searchQueryLabel,
        CancellationToken cancellationToken = default)
    {
        if (pmcNumericIds.Count == 0)
        {
            return new PmcTxtDbCompareReport
            {
                SearchQueryLabel = searchQueryLabel,
                ExpectedPmcIdCount = 0,
            };
        }

        var filter = Builders<PmcIndexedArticleSummary>.Filter.In(x => x.PmcId, pmcNumericIds);
        List<PmcIndexedArticleSummary> rows = await Summaries.Find(filter)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        static int CountRows(IEnumerable<PmcIndexedArticleSummary> source, Func<PmcIndexedArticleSummary, bool> predicate) =>
            source.Count(predicate);

        var report = new PmcTxtDbCompareReport
        {
            SearchQueryLabel = searchQueryLabel,
            ExpectedPmcIdCount = pmcNumericIds.Count,
            MatchedSummaryRowCount = rows.Count,
            MatchedWithFullTextCount = CountRows(rows, x => x.HasFullTextSections),
            MatchedWithAbstractCount = CountRows(rows, x => x.HasAbstract),
            MatchedWithFullTextAndAbstractCount = CountRows(rows, x => x.HasFullTextSections && x.HasAbstract),
        };

        var xmlRows = rows.Where(x => x.SourceKind == PmcArticleSourceKind.XmlBulk).ToList();
        report.XmlBulkRowCount = xmlRows.Count;
        report.XmlBulkWithFullTextCount = CountRows(xmlRows, x => x.HasFullTextSections);
        report.XmlBulkWithAbstractCount = CountRows(xmlRows, x => x.HasAbstract);
        report.XmlBulkWithFullTextAndAbstractCount = CountRows(xmlRows, x => x.HasFullTextSections && x.HasAbstract);

        var scrapedRows = rows.Where(x => x.SourceKind == PmcArticleSourceKind.Scraped).ToList();
        report.ScrapedRowCount = scrapedRows.Count;
        report.ScrapedWithFullTextCount = CountRows(scrapedRows, x => x.HasFullTextSections);
        report.ScrapedWithAbstractCount = CountRows(scrapedRows, x => x.HasAbstract);
        report.ScrapedWithFullTextAndAbstractCount = CountRows(scrapedRows, x => x.HasFullTextSections && x.HasAbstract);

        return report;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
