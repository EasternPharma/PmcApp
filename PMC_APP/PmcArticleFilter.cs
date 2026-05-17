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

        // Read-only mode: skip exclusion keywords and write-setup (collection creation, indexes).
        // Only connect to MongoDB and get the collection handle.
        if (settings.ReadOnlyMode)
        {
            if (settings.OutputType == PmcArticleFilterOutputTypes.Mongodb)
            {
                var mongoClient = new MongoClient($"mongodb://{settings.MongodbHost}:{settings.MongodbPort}");
                var database = mongoClient.GetDatabase(settings.MongodbDatabaseName);
                _mongoCollection = database.GetCollection<ArticleLabelDTO>(settings.MongodbOutputCollectionName);
            }
            return;
        }

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

            // Ensure an index on PmcId exists; skip creation if any index on that field is already present
            var existingIndexes = _mongoCollection.Indexes.List().ToList();
            bool pmcIdIndexExists = existingIndexes.Any(idx =>
                idx.Contains("key") && idx["key"].AsBsonDocument.Contains("PmcId"));

            if (!pmcIdIndexExists)
            {
                var indexKeys = Builders<ArticleLabelDTO>.IndexKeys.Ascending(a => a.PmcId);
                var indexOptions = new CreateIndexOptions { Unique = true, Background = true };
                _mongoCollection.Indexes.CreateOne(new CreateIndexModel<ArticleLabelDTO>(indexKeys, indexOptions));
            }

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

    #region Task 11: Collect ingredient article PMC IDs and save to TXT
    /// <summary>
    /// Queries <c>all_articles</c> for documents where <c>ContainsIngredient = true</c>,
    /// collects their integer PMC IDs, and writes one ID per line to
    /// <see cref="PmcArticleFilterSettingsDTO.OutputTxtFilePath"/>.
    /// </summary>
    public async Task GetIngredientArticleIdsAsync(CancellationToken cancellationToken = default)
    {
        if (_mongoCollection is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("MongoDB output collection is not initialized. Ensure OutputType is Mongodb.");
            Console.ResetColor();
            return;
        }

        string outputPath = Settings.OutputTxtFilePath
            ?? throw new InvalidOperationException("OutputTxtFilePath must be set in settings.");

        // ── Step 1: query all articles where ContainsIngredient = true ────────
        Console.WriteLine("Querying MongoDB for articles with ContainsIngredient = true …");

        var queryFilter = Builders<ArticleLabelDTO>.Filter.Eq(a => a.ContainsIngredient, true);

        long totalCount = await _mongoCollection
            .CountDocumentsAsync(queryFilter, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  Matching articles found: {totalCount}");
        Console.ResetColor();

        // ── Step 2: keyset-paginate and collect PMC IDs ───────────────────────
        const int pageSize = 10_000;
        int lastSeenId = 0;
        long processed = 0;
        var pmcIds = new List<int>((int)Math.Min(totalCount, int.MaxValue));

        var projection = Builders<ArticleLabelDTO>.Projection.Include(a => a.PmcId);
        var sort = Builders<ArticleLabelDTO>.Sort.Ascending(a => a.PmcId);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageFilter = Builders<ArticleLabelDTO>.Filter.And(
                queryFilter,
                Builders<ArticleLabelDTO>.Filter.Gt(a => a.PmcId, lastSeenId));

            var page = await _mongoCollection
                .Find(pageFilter)
                .Project<ArticleLabelDTO>(projection)
                .Sort(sort)
                .Limit(pageSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (page.Count == 0)
                break;

            foreach (var doc in page)
                pmcIds.Add(doc.PmcId);

            lastSeenId = page[^1].PmcId;
            processed += page.Count;

            int percent = totalCount > 0 ? (int)(processed * 100 / totalCount) : 100;
            Console.Write($"\r  Loading: [{new string('#', percent / 2)}{new string('-', 50 - percent / 2)}] {percent,3}%  ({processed}/{totalCount})  ");
        }

        Console.WriteLine();

        // ── Step 3: save PMC IDs to TXT file ─────────────────────────────────
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllLinesAsync(outputPath, pmcIds.Select(id => id.ToString()), cancellationToken)
            .ConfigureAwait(false);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Saved {pmcIds.Count} PMC IDs to: {outputPath}");
        Console.ResetColor();
    }
    #endregion

    #region Task 12: Update articles from JSON backup folder
    /// <summary>
    /// Loads all *.json files from <paramref name="folderPath"/>,
    /// deserializes each file as <see cref="List{ArticleLabelDTO}"/>,
    /// and upserts into the <c>all_articles</c> MongoDB collection by PmcId.
    /// </summary>
    public async Task UpdateArticlesFromJsonFolderAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        if (_mongoCollection is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("MongoDB output collection is not initialized. Ensure OutputType is Mongodb.");
            Console.ResetColor();
            return;
        }

        // Step 1: list all *.json files in the folder
        var jsonFiles = Directory.GetFiles(folderPath, "*.json", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .ToList();

        if (jsonFiles.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"No JSON files found in: {folderPath}");
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Found {jsonFiles.Count} JSON file(s) in: {folderPath}");
        Console.ResetColor();

        int totalUpserted = 0;
        int totalModified = 0;
        int filesDone = 0;

        // Step 2: process each file
        foreach (var file in jsonFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string jsonContent = await File.ReadAllTextAsync(file.FullName, cancellationToken)
                .ConfigureAwait(false);

            var articles = JsonConvert.DeserializeObject<List<ArticleLabelDTO>>(jsonContent)
                           ?? new List<ArticleLabelDTO>();

            if (articles.Count == 0)
            {
                filesDone++;
                continue;
            }

            // Step 3: upsert articles by PmcId in batches of 1000
            const int batchSize = 1_000;
            int totalBatches = (int)Math.Ceiling(articles.Count / (double)batchSize);

            for (int b = 0; b < totalBatches; b++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = articles.Skip(b * batchSize).Take(batchSize).ToList();
                var writeModels = batch.Select(article =>
                {
                    var filter = Builders<ArticleLabelDTO>.Filter.Eq(a => a.PmcId, article.PmcId);
                    return new ReplaceOneModel<ArticleLabelDTO>(filter, article) { IsUpsert = true };
                }).ToList<WriteModel<ArticleLabelDTO>>();

                var bulkResult = await _mongoCollection
                    .BulkWriteAsync(writeModels, new BulkWriteOptions { IsOrdered = false }, cancellationToken)
                    .ConfigureAwait(false);

                totalUpserted += (int)bulkResult.Upserts.Count;
                totalModified += (int)bulkResult.ModifiedCount;
            }

            filesDone++;
            int percent = (int)((double)filesDone / jsonFiles.Count * 100);
            Console.Write($"\r  Updating: [{new string('#', percent / 2)}{new string('-', 50 - percent / 2)}] {percent,3}%  ({filesDone}/{jsonFiles.Count})  {file.Name}  ");
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Update complete. Inserted: {totalUpserted}  |  Updated: {totalModified}  |  Files processed: {filesDone}");
        Console.ResetColor();
    }
    #endregion

    #region Task 14: Backup all_articles to JSON files
    /// <summary>
    /// Keyset-paginates the <c>all_articles</c> collection and writes each page as a
    /// JSON file containing <see cref="List{ArticleLabelDTO}"/>.
    /// Files are grouped under <c>Folder_1</c>, <c>Folder_2</c>, … (50 JSON files per folder).
    /// Compatible with <see cref="UpdateArticlesFromJsonFolderAsync"/> (recursive search).
    /// </summary>
    public async Task BackupAllArticlesToJsonAsync(CancellationToken cancellationToken = default)
    {
        if (_mongoCollection is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("MongoDB output collection is not initialized. Ensure OutputType is Mongodb.");
            Console.ResetColor();
            return;
        }

        string outputDir = Settings.OutputDirectoryPath
            ?? throw new InvalidOperationException("OutputDirectoryPath must be set in settings.");

        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        long totalCount = await _mongoCollection
            .CountDocumentsAsync(FilterDefinition<ArticleLabelDTO>.Empty, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Backing up {totalCount} article(s) to: {outputDir}");
        Console.ResetColor();

        const int pageSize = 10_000;
        const int jsonFilesPerFolder = 50;
        int lastSeenId = 0;
        long processed = 0;
        int filesWritten = 0;

        var sort = Builders<ArticleLabelDTO>.Sort.Ascending(a => a.PmcId);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageFilter = Builders<ArticleLabelDTO>.Filter.Gt(a => a.PmcId, lastSeenId);
            var page = await _mongoCollection
                .Find(pageFilter)
                .Sort(sort)
                .Limit(pageSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (page.Count == 0)
                break;

            lastSeenId = page[^1].PmcId;

            int folderIndex = filesWritten / jsonFilesPerFolder + 1;
            string batchFolder = Path.Combine(outputDir, $"backup_Pmc_Articles_{folderIndex}");
            if (!Directory.Exists(batchFolder))
                Directory.CreateDirectory(batchFolder);

            string fileName = $"Pmc_Articles_{page[0].PmcId}_{page[^1].PmcId}.json";
            string filePath = Path.Combine(batchFolder, fileName);
            string json = JsonConvert.SerializeObject(page, Formatting.None);
            await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);

            processed += page.Count;
            filesWritten++;

            int percent = totalCount > 0 ? (int)(processed * 100 / totalCount) : 100;
            Console.Write($"\r  Backup: [{new string('#', percent / 2)}{new string('-', 50 - percent / 2)}] {percent,3}%  ({processed}/{totalCount})  Folder_{folderIndex}\\{fileName}  ");
        }

        int folderCount = filesWritten > 0
            ? (filesWritten - 1) / jsonFilesPerFolder + 1
            : 0;

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Backup complete. Articles: {processed}  |  Files: {filesWritten}  |  Subfolders: {folderCount}  |  Root: {outputDir}");
        Console.ResetColor();
    }
    #endregion

    #region Task 13: Re-filter articles where IsHumanStudy is null
    /// <summary>
    /// Queries <c>all_articles</c> for documents where <c>IsHumanStudy</c> is null,
    /// applies the exclusion keyword filter on Title + Abstract,
    /// and patches each document with <c>IsFiltered</c>, <c>IsHumanStudy</c>,
    /// <c>ExcludedKeywords</c>, and <c>ExcludedCategories</c>.
    /// </summary>
    public async Task FilterOnHumanStudyNullAsync(CancellationToken cancellationToken = default)
    {
        if (_mongoCollection is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("MongoDB output collection is not initialized. Ensure OutputType is Mongodb.");
            Console.ResetColor();
            return;
        }

        var targetFilter = Builders<ArticleLabelDTO>.Filter.Eq(a => a.IsHumanStudy, null);

        long totalArticles = await _mongoCollection
            .CountDocumentsAsync(targetFilter, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Articles with IsHumanStudy = null: {totalArticles}");
        Console.ResetColor();

        if (totalArticles == 0)
        {
            Console.WriteLine("Nothing to process.");
            return;
        }

        const int pageSize = 2_000;
        const int bulkBatchSize = 1_000;
        int lastSeenId = 0;
        long processedArticles = 0;
        long totalUpdated = 0;

        var sort = Builders<ArticleLabelDTO>.Sort.Ascending(a => a.PmcId);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageFilter = Builders<ArticleLabelDTO>.Filter.And(
                targetFilter,
                Builders<ArticleLabelDTO>.Filter.Gt(a => a.PmcId, lastSeenId));

            List<ArticleLabelDTO> page = await _mongoCollection
                .Find(pageFilter)
                .Sort(sort)
                .Limit(pageSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (page.Count == 0)
                break;

            lastSeenId = page[^1].PmcId;

            var updateModels = new ConcurrentBag<WriteModel<ArticleLabelDTO>>();

            Parallel.ForEach(page, article =>
            {
                var text = string.Concat(
                    article.Title ?? string.Empty,
                    " ",
                    article.AbstractText ?? string.Empty);

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

                var update = Builders<ArticleLabelDTO>.Update
                    .Set(a => a.IsFiltered, true)
                    .Set(a => a.IsHumanStudy, matchedKeywords.Count == 0)
                    .Set(a => a.ExcludedKeywords, matchedKeywords)
                    .Set(a => a.ExcludedCategories, matchedCategories.Distinct().ToList());

                var docFilter = Builders<ArticleLabelDTO>.Filter.Eq(a => a.PmcId, article.PmcId);
                updateModels.Add(new UpdateOneModel<ArticleLabelDTO>(docFilter, update));
            });

            var modelList = updateModels.ToList();
            for (int i = 0; i < modelList.Count; i += bulkBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = modelList.Skip(i).Take(bulkBatchSize).ToList<WriteModel<ArticleLabelDTO>>();
                var bulkResult = await _mongoCollection
                    .BulkWriteAsync(batch, new BulkWriteOptions { IsOrdered = false }, cancellationToken)
                    .ConfigureAwait(false);
                totalUpdated += bulkResult.ModifiedCount;
            }

            processedArticles += page.Count;
            int percent = totalArticles > 0
                ? (int)(processedArticles * 100 / totalArticles)
                : 100;
            Console.Write($"\r  Filtering: [{new string('#', percent / 2)}{new string('-', 50 - percent / 2)}] {percent,3}%  ({processedArticles}/{totalArticles})  ");
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Human study filter complete. Documents updated: {totalUpdated}");
        Console.ResetColor();
    }
    #endregion

    #region Task 10 : Ingredient keyword filter
    /// <summary>
    /// Scans <c>all_articles</c> documents where <c>IsHumanStudy = true</c> and <c>HasFullText = false</c>,
    /// matches Title + Abstract against ingredient keywords loaded from <paramref name="ingredientKeywordsFilePath"/>,
    /// and patches each document with <c>IsIngredientReviewed</c>, <c>ContainsIngredient</c>, and <c>Ingredients</c>.
    /// </summary>
    public async Task FilterIngredientKeywordsAsync(
        string ingredientKeywordsFilePath,
        CancellationToken cancellationToken = default)
    {
        if (_mongoCollection is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("MongoDB output collection is not initialized. Ensure OutputType is Mongodb.");
            Console.ResetColor();
            return;
        }

        // ── Step 1: load and compile ingredient keyword patterns ─────────────
        Console.WriteLine($"Loading ingredient keywords from: {ingredientKeywordsFilePath}");
        var ingredientPatterns = File.ReadLines(ingredientKeywordsFilePath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(k => k.Length)
            .Select(k => (
                Keyword: k,
                Pattern: new Regex(
                    $@"\b{Regex.Escape(k)}\b",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled)
            ))
            .ToList();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  Ingredient keywords loaded: {ingredientPatterns.Count}");
        Console.ResetColor();

        // ── Step 2: count target documents ───────────────────────────────────
        var targetFilter = Builders<ArticleLabelDTO>.Filter.And(
            Builders<ArticleLabelDTO>.Filter.Eq(a => a.IsHumanStudy, true),
            Builders<ArticleLabelDTO>.Filter.Eq(a => a.HasFullText, false));
        //var targetFilter = Builders<ArticleLabelDTO>.Filter.Eq(a => a.IsHumanStudy, null);

        long totalArticles = await _mongoCollection
            .CountDocumentsAsync(targetFilter, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"Target articles (IsHumanStudy=true, HasFullText=false): {totalArticles}");

        // ── Step 3: keyset-paginate, match, and patch ─────────────────────────
        const int pageSize = 10_000;
        const int bulkBatchSize = 1_000;
        int lastSeenId = 0;
        long processedArticles = 0;
        long totalUpdated = 0;

        var sort = Builders<ArticleLabelDTO>.Sort.Ascending(a => a.PmcId);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageFilter = Builders<ArticleLabelDTO>.Filter.And(
                targetFilter,
                Builders<ArticleLabelDTO>.Filter.Gt(a => a.PmcId, lastSeenId));

            List<ArticleLabelDTO> page = await _mongoCollection
                .Find(pageFilter)
                .Sort(sort)
                .Limit(pageSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (page.Count == 0)
                break;

            lastSeenId = page[^1].PmcId;

            // ── match keywords in parallel ────────────────────────────────────
            var updateModels = new ConcurrentBag<WriteModel<ArticleLabelDTO>>();

            Parallel.ForEach(page, article =>
            {
                var text = string.Concat(
                    article.Title ?? string.Empty,
                    " ",
                    article.AbstractText ?? string.Empty);

                var matchedKeywords = new List<string>();
                foreach (var (keyword, pattern) in ingredientPatterns)
                {
                    if (pattern.IsMatch(text))
                        matchedKeywords.Add(keyword);
                }

                bool containsIngredient = matchedKeywords.Count > 0;

                var update = Builders<ArticleLabelDTO>.Update
                    .Set(a => a.IsIngredientReviewed, true)
                    .Set(a => a.ContainsIngredient, containsIngredient)
                    .Set(a => a.Ingredients, containsIngredient ? matchedKeywords : null);

                var docFilter = Builders<ArticleLabelDTO>.Filter.Eq(a => a.PmcId, article.PmcId);
                updateModels.Add(new UpdateOneModel<ArticleLabelDTO>(docFilter, update));
            });

            // ── bulk-write in batches of 1000 ─────────────────────────────────
            var modelList = updateModels.ToList();
            for (int i = 0; i < modelList.Count; i += bulkBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = modelList.Skip(i).Take(bulkBatchSize).ToList<WriteModel<ArticleLabelDTO>>();
                var bulkResult = await _mongoCollection
                    .BulkWriteAsync(batch, new BulkWriteOptions { IsOrdered = false }, cancellationToken)
                    .ConfigureAwait(false);
                totalUpdated += bulkResult.ModifiedCount;
            }

            processedArticles += page.Count;
            int percent = totalArticles > 0
                ? (int)(processedArticles * 100 / totalArticles)
                : 100;
            Console.Write($"\r  Ingredient filter: [{new string('#', percent / 2)}{new string('-', 50 - percent / 2)}] {percent,3}%  ({processedArticles}/{totalArticles})  ");
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Ingredient review complete. Documents updated: {totalUpdated}");
        Console.ResetColor();
    }
    #endregion
}