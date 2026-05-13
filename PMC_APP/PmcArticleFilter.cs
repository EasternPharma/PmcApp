using ExcelDataReader;
using MongoDB.Driver;
using Newtonsoft.Json;
using PMC_APP.DTOs;
using System.Collections.Concurrent;
using System.Data;
using System.Text.RegularExpressions;

namespace PMC_APP;

public class PmcArticleFilter
{
    #region Initialization
    public List<string> ExclusionKeywords { get; set; }
    public Dictionary<string, string> Keyword2Category { get; set; }
    public List<FileInfo> ArticleJsonFile { get; set; }
    public PmcArticleFilterSettingsDTO Settings { get; set; }
    // Pre-compiled regex patterns, built once for O(1) reuse per article
    private List<(string Keyword, string Category, Regex Pattern)> _compiledPatterns = new();
    private IMongoCollection<ArticleLabelDTO>? _mongoCollection;
    private IMongoCollection<MongoPmcArticleDocument>? _mongoInputCollection;
    private IMongoCollection<MongoArticleListDocument>? _mongoArticleListCollection;

    public PmcArticleFilter(PmcArticleFilterSettingsDTO settings)
    {
        Settings = settings;

        bool hasJsonPath = !string.IsNullOrWhiteSpace(settings.JsonFilesDirectoryPath);
        ArticleJsonFile = hasJsonPath
            ? Directory.GetFiles(settings.JsonFilesDirectoryPath, "*.json", SearchOption.AllDirectories)
                .Select(x => new FileInfo(x)).ToList()
            : new List<FileInfo>();

        LoadExclusionKeywords();

        if (settings.OutputType == PmcArticleFilterOutputTypes.JsonFile)
        {
            if (!Directory.Exists(settings.OutputDirectoryPath))
                Directory.CreateDirectory(settings.OutputDirectoryPath);
        }
        else if (settings.OutputType == PmcArticleFilterOutputTypes.Mongodb)
        {
            var mongoClient = new MongoClient($"mongodb://{settings.MongodbHost}:{settings.MongodbPort}");
            var database = mongoClient.GetDatabase(settings.MongodbDatabaseName);

            var existingCollections = database.ListCollectionNames().ToList();
            if (!existingCollections.Contains(settings.MongodbOutputCollectionName))
                database.CreateCollection(settings.MongodbOutputCollectionName);

            _mongoCollection = database.GetCollection<ArticleLabelDTO>(settings.MongodbOutputCollectionName);

            // Ensure a unique index on PmcId to support upserts and prevent duplicates
            var indexKeys = Builders<ArticleLabelDTO>.IndexKeys.Ascending(a => a.PmcId);
            var indexOptions = new CreateIndexOptions { Unique = true, Background = true };
            _mongoCollection.Indexes.CreateOne(new CreateIndexModel<ArticleLabelDTO>(indexKeys, indexOptions));

            if (!string.IsNullOrWhiteSpace(settings.MongodbInputCollectionName))
                _mongoInputCollection = database.GetCollection<MongoPmcArticleDocument>(settings.MongodbInputCollectionName);

            if (!string.IsNullOrWhiteSpace(settings.MongodbArticleListCollectionName))
                _mongoArticleListCollection = database.GetCollection<MongoArticleListDocument>(settings.MongodbArticleListCollectionName);
        }
    }
    #endregion

    #region Load exclusion keywords from Excel file
    private void LoadExclusionKeywords()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        this.Keyword2Category = new Dictionary<string, string>();
        this.Keyword2Category = ReadExcelToDictionary(Settings.ExclusionExcelPath);
        this.ExclusionKeywords = Keyword2Category.Keys.OrderBy(x => x).ToList();

        // Build compiled patterns once — word-boundary aware, case-insensitive
        // Longer keywords first so multi-word phrases are matched with priority
        _compiledPatterns = Keyword2Category
            .OrderByDescending(kv => kv.Key.Length)
            .Select(kv => (
                Keyword: kv.Key,
                Category: kv.Value,
                Pattern: new Regex(
                    @"\b" + Regex.Escape(kv.Key) + @"\b",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled)
            ))
            .ToList();
    }
    private Dictionary<string, string> ReadExcelToDictionary(string filePath)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read);
        using var reader = ExcelReaderFactory.CreateCsvReader(stream);

        var dataSet = reader.AsDataSet();
        DataTable table = dataSet.Tables[0];

        int categoryIndex = -1;
        int keywordIndex = -1;

        for (int i = 0; i < table.Columns.Count; i++)
        {
            string header = table.Rows[0][i]?.ToString()?.Trim();

            if (header == "Category")
                categoryIndex = i;

            if (header == "Keyword")
                keywordIndex = i;
        }

        var result = new Dictionary<string, string>();

        for (int row = 1; row < table.Rows.Count; row++)
        {
            string category = table.Rows[row][categoryIndex]?.ToString()?.Trim().ToLower();
            string keyword = table.Rows[row][keywordIndex]?.ToString()?.Trim().ToLower();

            if (!string.IsNullOrWhiteSpace(keyword))
                result[keyword] = category;
        }

        return result;
    }
    #endregion

    #region Search and filter articles based on exclusion keywords
    private List<ArticleFilterDTO> ApplyFilters(List<ArticleDTO> articles)
    {
        var results = new ConcurrentBag<ArticleFilterDTO>();

        Parallel.ForEach(articles, article =>
        {
            // Combine title + abstract into one searchable string
            // Using a space separator (non-word char) so word boundaries remain intact
            var text = string.Concat(
                article.Title ?? string.Empty,
                " ",
                article.AbstractText ?? string.Empty
            );

            var matchedKeywords = new List<string>();
            var matchedCategories = new HashSet<string>();

            foreach (var (keyword, category, pattern) in _compiledPatterns)
            {
                if (pattern.IsMatch(text))
                {
                    matchedKeywords.Add(keyword);
                    if (!string.IsNullOrEmpty(category))
                        matchedCategories.Add(category);
                }
            }

            //if (matchedKeywords.Count > 0)
            //{
            results.Add(new ArticleFilterDTO
            {
                PmcID = article.PmcId,
                Keywords = matchedKeywords,
                Categories = matchedCategories.Distinct().ToList(),
                Title = article.Title,
                Link = $"https://pmc.ncbi.nlm.nih.gov/articles/PMC{article.PmcId}/"
            });
            //}
        });

        return results.ToList();
    }

    private List<ArticleLabelDTO> ApplyFiltersLabel(List<ArticleDTO> articles)
    {
        var results = new ConcurrentBag<ArticleLabelDTO>();

        Parallel.ForEach(articles, article =>
        {
            // Combine title + abstract into one searchable string
            // Using a space separator (non-word char) so word boundaries remain intact
            var text = string.Concat(
                article.Title ?? string.Empty,
                " ",
                article.AbstractText ?? string.Empty
            );

            var matchedKeywords = new List<string>();
            var matchedCategories = new HashSet<string>();

            foreach (var (keyword, category, pattern) in _compiledPatterns)
            {
                if (pattern.IsMatch(text))
                {
                    matchedKeywords.Add(keyword);
                    if (!string.IsNullOrEmpty(category))
                        matchedCategories.Add(category);
                }
            }

            //if (matchedKeywords.Count > 0)
            //{
            var articleLabel = new ArticleLabelDTO
            {
                PmcId = article.PmcId,
                PmId = article.PmId,
                Doi = article.Doi,
                Title = article.Title,
                Category = article.Category,
                Journal = article.Journal,
                Publisher = article.Publisher,
                Volume = article.Volume,
                Issue = article.Issue,
                ISSN = article.ISSN,
                FPage = article.FPage,
                LPage = article.LPage,
                Authors = article.Authors,
                PublishDate = article.PublishDate,
                AbstractText = article.AbstractText,
                Keywords = article.Keywords,
                Sections = article.Sections,
                ExcludedKeywords = matchedKeywords,
                ExcludedCategories = matchedCategories.Distinct().ToList(),
                HasFullText = article.Sections != null && article.Sections.Count > 0,
                SourceType = 1, // 1: Article from XML bulk download
                HasAbstract = !string.IsNullOrEmpty(article.AbstractText),
                IsFiltered = true,
                IsHumanStudy = matchedKeywords.Count == 0, // if keywords > 0 => not a human study
            };

            results.Add(articleLabel);
            //}
        });

        return results.ToList();
    }

    private List<ArticleFilterDTO> FilterJsonFile(FileInfo fileInfo)
    {
        string jsonContent = File.ReadAllText(fileInfo.FullName);
        var articles = JsonConvert.DeserializeObject<List<ArticleDTO>>(jsonContent) ?? new List<ArticleDTO>();
        return ApplyFilters(articles);
    }

    private List<ArticleLabelDTO> FilterJsonFileLabel(FileInfo fileInfo)
    {
        string jsonContent = File.ReadAllText(fileInfo.FullName);
        var articles = JsonConvert.DeserializeObject<List<ArticleDTO>>(jsonContent) ?? new List<ArticleDTO>();
        return ApplyFiltersLabel(articles);
    }

    private const int PartSize = 100_000;

    private bool WriteFilteredResults(List<ArticleFilterDTO> results)
    {
        string? outputPath = null;
        try
        {
            int partCount = (int)Math.Ceiling(results.Count / (double)PartSize);
            partCount = Math.Max(partCount, 1);

            for (int i = 0; i < partCount; i++)
            {
                var chunk = results.Skip(i * PartSize).Take(PartSize).ToList();
                outputPath = Path.Combine(Settings.OutputDirectoryPath, $"Part_{i + 1}.json");
                File.WriteAllText(outputPath, JsonConvert.SerializeObject(chunk, Formatting.None));
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error writing filtered results to {outputPath}: {ex.Message}");
            return false;
        }
    }

    private bool InsertToMongoDB(List<ArticleLabelDTO> results)
    {
        if (_mongoCollection is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("MongoDB collection is not initialized. Set OutputType to Mongodb and provide connection settings.");
            Console.ResetColor();
            return false;
        }

        try
        {
            int upserted = 0;
            int modified = 0;
            const int batchSize = 1000;
            int totalBatches = (int)Math.Ceiling(results.Count / (double)batchSize);

            for (int b = 0; b < totalBatches; b++)
            {
                var batch = results.Skip(b * batchSize).Take(batchSize).ToList();
                var writeModels = batch.Select(article =>
                {
                    var filter = Builders<ArticleLabelDTO>.Filter.Eq(a => a.PmcId, article.PmcId);
                    return new ReplaceOneModel<ArticleLabelDTO>(filter, article) { IsUpsert = true };
                }).ToList<WriteModel<ArticleLabelDTO>>();

                var bulkResult = _mongoCollection.BulkWrite(writeModels, new BulkWriteOptions { IsOrdered = false });
                upserted += (int)bulkResult.Upserts.Count;
                modified += (int)bulkResult.ModifiedCount;

                int percent = (int)((double)(b + 1) / totalBatches * 100);
                Console.Write($"\r  Saving to MongoDB: [{new string('#', percent / 2)}{new string('-', 50 - percent / 2)}] {percent,3}%  (batch {b + 1}/{totalBatches})  ");
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  Inserted: {upserted}  |  Updated: {modified}  |  Total: {results.Count}");
            Console.ResetColor();
            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"MongoDB write error: {ex.Message}");
            Console.ResetColor();
            return false;
        }
    }

    public void FilterAllJsonFiles()
    {
        List<ArticleFilterDTO> allResults = new List<ArticleFilterDTO>();
        int total = ArticleJsonFile.Count;
        int done = 0;

        foreach (var fileInfo in ArticleJsonFile)
        {
            var results = FilterJsonFile(fileInfo);
            allResults.AddRange(results);

            done++;
            int percent = (int)((double)done / total * 100);
            Console.Write($"\r  Filtering: [{new string('#', percent / 2)}{new string('-', 50 - percent / 2)}] {percent,3}%  ({done}/{total})  ");
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Successfully filtered {allResults.Count} articles");
        Console.ResetColor();

        bool success = WriteFilteredResults(allResults);
        if (success)
            Console.WriteLine($"Saved to: {Settings.OutputDirectoryPath}");

    }
    public void FilterAllJsonFilesLabel()
    {
        int total = ArticleJsonFile.Count;
        int done = 0;
        int totalInserted = 0;

        foreach (var fileInfo in ArticleJsonFile)
        {
            var batch = FilterJsonFileLabel(fileInfo);

            done++;
            int percent = (int)((double)done / total * 100);
            Console.Write($"\r  Filtering: [{new string('#', percent / 2)}{new string('-', 50 - percent / 2)}] {percent,3}%  ({done}/{total})  ");

            bool success = InsertToMongoDB(batch);
            if (success)
                totalInserted += batch.Count;
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Successfully filtered and stored {totalInserted} articles to: {Settings.MongodbOutputCollectionName}");
        Console.ResetColor();
    }

    /// <summary>
    /// Reads scraped articles from MongoDB with pagination, applies keyword filters,
    /// maps to <see cref="ArticleLabelDTO"/> (SourceType = 2), and upserts into the output collection.
    /// Step 1 – load pmcid→user map from <c>MongodbArticleListCollectionName</c>.
    /// Step 2 – paginate through <c>MongodbInputCollectionName</c>.
    /// Step 3 – apply keyword filter per batch.
    /// Step 4 – convert to <see cref="ArticleLabelDTO"/>.
    /// Step 5 – upsert batch into output collection.
    /// </summary>
    public async Task FilterAllMongoDbLabelAsync(CancellationToken cancellationToken = default)
    {
        if (_mongoInputCollection is null || _mongoArticleListCollection is null || _mongoCollection is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("MongoDB collections are not initialized. Ensure OutputType is Mongodb and all collection names are set.");
            Console.ResetColor();
            return;
        }

        // ── Step 1: load pmcid → user map ────────────────────────────────────
        Console.WriteLine("Loading article_list (pmcid → user) …");
        var pmcid2user = new Dictionary<int, string>();

        const int listPageSize = 50_000;
        int listLastSeenId = 0;
        var listProjection = Builders<MongoArticleListDocument>.Projection
            .Include(x => x.PmcId)
            .Include(x => x.User);
        var listSort = Builders<MongoArticleListDocument>.Sort.Ascending(x => x.PmcId);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageFilter = Builders<MongoArticleListDocument>.Filter.Gt(x => x.PmcId, listLastSeenId);
            var page = await _mongoArticleListCollection
                .Find(pageFilter)
                .Project<MongoArticleListDocument>(listProjection)
                .Sort(listSort)
                .Limit(listPageSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            if (page.Count == 0)
                break;

            foreach (var doc in page)
                pmcid2user[doc.PmcId] = doc.User ?? string.Empty;

            listLastSeenId = page[^1].PmcId;
            Console.Write($"\r  article_list loaded: {pmcid2user.Count}   ");
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  article_list entries loaded: {pmcid2user.Count}");
        Console.ResetColor();

        // ── Step 2-5: paginate input, filter, convert, upsert ────────────────
        long totalArticles = await _mongoInputCollection
            .CountDocumentsAsync(FilterDefinition<MongoPmcArticleDocument>.Empty, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"Total articles in input collection: {totalArticles}");

        const int articlePageSize = 10_000;
        int articleLastSeenId = 0;
        long processedArticles = 0;
        int totalInserted = 0;

        var articleSort = Builders<MongoPmcArticleDocument>.Sort.Ascending(x => x.PmcId);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // ── Step 2: load page ─────────────────────────────────────────────
            var pageFilter = Builders<MongoPmcArticleDocument>.Filter.Gt(x => x.PmcId, articleLastSeenId);
            List<MongoPmcArticleDocument> page = await _mongoInputCollection
                .Find(pageFilter)
                .Sort(articleSort)
                .Limit(articlePageSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            if (page.Count == 0)
                break;

            articleLastSeenId = page[^1].PmcId;

            // ── Step 3 & 4: filter and convert ───────────────────────────────
            var batch = ApplyFiltersFromMongo(page, pmcid2user);

            // ── Step 5: upsert ────────────────────────────────────────────────
            InsertToMongoDB(batch);
            totalInserted += batch.Count;

            processedArticles += page.Count;
            int percent = totalArticles > 0
                ? (int)(processedArticles * 100 / totalArticles)
                : 100;
            Console.Write($"\r  Filtering: [{new string('#', percent / 2)}{new string('-', 50 - percent / 2)}] {percent,3}%  ({processedArticles}/{totalArticles})  ");
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Successfully filtered and stored {totalInserted} articles to: {Settings.MongodbOutputCollectionName}");
        Console.ResetColor();
    }

    /// <summary>
    /// Applies keyword exclusion filter on a batch of <see cref="MongoPmcArticleDocument"/> records
    /// and converts them to <see cref="ArticleLabelDTO"/> (SourceType = 2, ScrapedByUser from map).
    /// </summary>
    private List<ArticleLabelDTO> ApplyFiltersFromMongo(
        List<MongoPmcArticleDocument> articles,
        Dictionary<int, string> pmcid2user)
    {
        var results = new ConcurrentBag<ArticleLabelDTO>();

        Parallel.ForEach(articles, article =>
        {
            var text = string.Concat(
                article.Title ?? string.Empty,
                " ",
                article.AbstractText ?? string.Empty
            );

            var matchedKeywords = new List<string>();
            var matchedCategories = new HashSet<string>();

            foreach (var (keyword, category, pattern) in _compiledPatterns)
            {
                if (pattern.IsMatch(text))
                {
                    matchedKeywords.Add(keyword);
                    if (!string.IsNullOrEmpty(category))
                        matchedCategories.Add(category);
                }
            }

            int pmcId = article.PmcId != 0 ? article.PmcId : article.PmcIdMongoField;
            pmcid2user.TryGetValue(pmcId, out string? user);

            var articleLabel = new ArticleLabelDTO
            {
                PmcId = pmcId,
                PmId = article.PmId,
                Doi = article.Doi,
                Title = article.Title,
                Category = article.Category,
                Journal = article.Journal,
                Publisher = article.Publisher,
                Volume = article.Volume,
                Issue = article.Issue,
                ISSN = article.ISSN,
                FPage = article.FPage,
                LPage = article.LPage,
                Authors = article.Authors,
                PublishDate = article.PublishDate,
                AbstractText = article.AbstractText,
                Keywords = article.Keywords,
                Sections = article.Sections ?? new Dictionary<string, string>(),
                ExcludedKeywords = matchedKeywords,
                ExcludedCategories = matchedCategories.Distinct().ToList(),
                HasFullText = article.Sections != null && article.Sections.Count > 0,
                SourceType = 2, // 2: Web scraping
                HasAbstract = !string.IsNullOrEmpty(article.AbstractText),
                IsFiltered = true,
                IsHumanStudy = matchedKeywords.Count == 0,
                ScrapedByUser = user,
            };

            results.Add(articleLabel);
        });

        return results.ToList();
    }
    #endregion
}