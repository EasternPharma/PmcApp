using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using IngredientKeywordSearch;
using MongoDB.Bson;
using MongoDB.Driver;

namespace PMC_APP.IngredientSearch;

public sealed class IngredientFrequencyOptions
{
    public string MongoUri { get; set; } = "mongodb://localhost:27017";
    public string Database { get; set; } = "pmc";
    public string Collection { get; set; } = "all_articles";
    public string KeywordsPath { get; set; } = "/home/breg/IngredientKeywordSearch_0/IngredientKeywordSearch/keywords.xlsx";
    public string OutputDirectory { get; set; } = Path.Combine(Environment.CurrentDirectory, "reports", "ingredient-frequency");
    public int BatchSize { get; set; } = 256;
    public int MigrationBatchSize { get; set; } = 10_000;
    public int Threads { get; set; } = Math.Max(1, (int)Math.Round(Environment.ProcessorCount * 0.8));
    public int Readers { get; set; } = 4;
    public int? MigrationStartAfter { get; set; }
    public bool ReportOnly { get; set; }
    public bool ResumeLabeling { get; set; }
}

/// <summary>
/// Adds the schema, builds indexes, labels every eligible article and exports XLSX.
/// Uses only $set updates: unknown fields and previous labeling data are preserved.
/// Successful batches are resumable by the completion flag and keyword fingerprint.
/// </summary>
public sealed class PmcIngredientFrequencyLabeler
{
    private sealed record MigrationCheckpoint(string Database, string Collection, int HighWatermark,
        int? LastPmcId, long Processed, bool Complete);
    public const string WorkIndex = "ingredient_frequency_work_v1";
    private readonly IngredientFrequencyOptions _options;
    private readonly IMongoCollection<BsonDocument> _articles;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly object _progressLock = new();
    private string _phase = "Starting";
    private long _done;
    private long _total;
    private long _changed;

    public PmcIngredientFrequencyLabeler(IngredientFrequencyOptions options)
    {
        if (options.BatchSize <= 0 || options.MigrationBatchSize <= 0 || options.Threads <= 0 || options.Readers <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Batch sizes and threads must be positive.");
        _options = options;
        _articles = new MongoClient(options.MongoUri).GetDatabase(options.Database)
            .GetCollection<BsonDocument>(options.Collection).WithWriteConcern(WriteConcern.Acknowledged);
    }

    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.OutputDirectory);
        using var runLock = new FileStream(Path.Combine(_options.OutputDirectory, "run.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var analyzer = new IngredientFrequencyAnalyzer(XlsxKeywordReader.Read(_options.KeywordsPath));
        Console.WriteLine($"Keywords: {analyzer.Matcher.KeywordCount:N0}; version: {analyzer.Version}");
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task heartbeat = HeartbeatAsync(heartbeatCancellation.Token);
        try
        {
            if (!_options.ReportOnly && !_options.ResumeLabeling)
            {
                await MigrateSchemaAsync(cancellationToken);
                await CreateIndexesAsync(cancellationToken);
            }
            if (!_options.ReportOnly)
            {
                if (_options.ResumeLabeling)
                {
                    var indexes = await (await _articles.Indexes.ListAsync(cancellationToken)).ToListAsync(cancellationToken);
                    if (!indexes.Any(i => i["name"] == WorkIndex))
                        throw new InvalidOperationException("Resume labeling requires a completed migration and the ingredient frequency indexes.");
                    string checkpoint = Path.Combine(_options.OutputDirectory, "migration-checkpoint.json");
                    var saved = File.Exists(checkpoint)
                        ? JsonSerializer.Deserialize<MigrationCheckpoint>(await File.ReadAllTextAsync(checkpoint, cancellationToken)) : null;
                    if (saved is not { Complete: true } || saved.Database != _options.Database || saved.Collection != _options.Collection)
                        throw new InvalidOperationException("Resume labeling requires a completed migration checkpoint for this database and collection.");
                }
                await LabelArticlesAsync(analyzer, cancellationToken);
            }
            string report = await ExportReportAsync(analyzer, cancellationToken);
            SetProgress("Complete", _done, _total);
            WriteProgress();
            Console.WriteLine($"Excel report: {report}");
            return report;
        }
        catch (Exception ex)
        {
            SetProgress(cancellationToken.IsCancellationRequested ? "Cancelled (safe to resume)" : "Failed", _done, _total);
            WriteProgress(ex.Message);
            throw;
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }
    }

    // Explicit Unicode whitespace set matches .NET string.IsNullOrWhiteSpace (also in MongoDB PCRE).
    private const string NonWhitespace = "[^\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]";

    public static BsonDocument BuildEligibilityExpression()
    {
        BsonDocument StringOrEmpty(string path) => new("$cond", new BsonArray
        {
            new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", path), "string" }), path, ""
        });
        BsonDocument HasText(string path) => new("$regexMatch", new BsonDocument
        {
            { "input", StringOrEmpty(path) }, { "regex", NonWhitespace }
        });
        var safeSections = new BsonDocument("$cond", new BsonArray
        {
            new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$Sections"), "object" }),
            "$Sections", new BsonDocument()
        });
        return new("$and", new BsonArray
        {
            HasText("$AbstractText"),
            new BsonDocument("$anyElementTrue", new BsonArray
            {
                new BsonDocument("$map", new BsonDocument
                {
                    { "input", new BsonDocument("$objectToArray", safeSections) },
                    { "as", "section" }, { "in", HasText("$$section.v") }
                })
            })
        });
    }

    public static BsonDocument BuildMigrationStage()
    {
        var defaults = new BsonDocument
        {
            { "IsIngredientFrequencyLabeled", false },
            { "IngredientTitleCounts", new BsonArray() }, { "HasIngredientInTitle", false },
            { "IngredientAbstractCounts", new BsonArray() }, { "HasIngredientInAbstract", false },
            { "IngredientSectionCounts", new BsonArray() }, { "HasIngredientInSections", false },
            { "IngredientFrequencyVersion", BsonNull.Value }, { "IngredientFrequencyLabeledAt", BsonNull.Value }
        };
        var set = new BsonDocument();
        foreach (var field in defaults)
            set[field.Name] = new BsonDocument("$ifNull", new BsonArray { "$" + field.Name, field.Value });
        set["IsIngredientFrequencyEligible"] = BuildEligibilityExpression();
        return new("$set", set);
    }

    public async Task MigrateSchemaAsync(CancellationToken cancellationToken = default)
    {
        long total = await _articles.EstimatedDocumentCountAsync(cancellationToken: cancellationToken);
        SetProgress("Schema migration", 0, total);
        var highest = await _articles.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(new BsonDocument("PmcId", -1)).Project(new BsonDocument { { "PmcId", 1 }, { "_id", 0 } })
            .Limit(1).FirstOrDefaultAsync(cancellationToken);
        if (highest is null) return;
        int highWatermark = highest["PmcId"].AsInt32;
        int? last = _options.MigrationStartAfter;
        long done = 0;
        string checkpointPath = Path.Combine(_options.OutputDirectory, "migration-checkpoint.json");
        if (!last.HasValue && File.Exists(checkpointPath))
        {
            var saved = JsonSerializer.Deserialize<MigrationCheckpoint>(await File.ReadAllTextAsync(checkpointPath, cancellationToken));
            if (saved is { Complete: false } && saved.Database == _options.Database && saved.Collection == _options.Collection)
            {
                last = saved.LastPmcId;
                highWatermark = saved.HighWatermark;
                done = saved.Processed;
            }
        }
        if (_options.MigrationStartAfter.HasValue)
            done = await _articles.CountDocumentsAsync(new BsonDocument("PmcId", new BsonDocument("$lte", last!.Value)),
                new CountOptions { Hint = "PmcId_1" }, cancellationToken);
        void SaveCheckpoint(bool complete)
        {
            File.WriteAllText(checkpointPath + ".tmp", JsonSerializer.Serialize(
                new MigrationCheckpoint(_options.Database, _options.Collection, highWatermark, last, done, complete)));
            File.Move(checkpointPath + ".tmp", checkpointPath, true);
        }
        SaveCheckpoint(false);
        int concurrency = Math.Max(1, Math.Min(_options.Threads, (int)Math.Floor(Environment.ProcessorCount * 0.8)));
        int waveSize = checked(_options.MigrationBatchSize * concurrency);
        SetProgress("Schema migration", done, total);
        UpdateDefinition<BsonDocument> pipeline = new PipelineUpdateDefinition<BsonDocument>(new[] { BuildMigrationStage() });
        while (true)
        {
            var range = new BsonDocument("PmcId", new BsonDocument("$lte", highWatermark));
            if (last.HasValue) range["PmcId"].AsBsonDocument["$gt"] = last.Value;
            var ids = await _articles.Find(range, new FindOptions { Hint = "PmcId_1" }).Sort(new BsonDocument("PmcId", 1))
                .Project(new BsonDocument { { "PmcId", 1 }, { "_id", 0 } }).Limit(waveSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0) break;
            var updates = new List<Task>();
            int? lower = last;
            for (int offset = 0; offset < ids.Count; offset += _options.MigrationBatchSize)
            {
                int end = ids[Math.Min(offset + _options.MigrationBatchSize, ids.Count) - 1]["PmcId"].AsInt32;
                var batchRange = new BsonDocument("PmcId", new BsonDocument("$lte", end));
                if (lower.HasValue) batchRange["PmcId"].AsBsonDocument["$gt"] = lower.Value;
                updates.Add(UpdateRangeAsync(batchRange));
                lower = end;
            }
            await Task.WhenAll(updates);
            last = ids[^1]["PmcId"].AsInt32;
            SaveCheckpoint(false);

            async Task UpdateRangeAsync(BsonDocument batchRange)
            {
                var result = await _articles.UpdateManyAsync(batchRange, pipeline, cancellationToken: cancellationToken);
                long completed = Interlocked.Add(ref done, result.MatchedCount);
                Interlocked.Add(ref _changed, result.ModifiedCount);
                SetProgress("Schema migration", completed, total);
            }
        }
        SaveCheckpoint(true);
        WriteProgress();
    }

    public async Task CreateIndexesAsync(CancellationToken cancellationToken = default)
    {
        SetProgress("Creating indexes", 0, 0);
        var models = new List<CreateIndexModel<BsonDocument>>
        {
            new(new BsonDocument
            {
                { "IsIngredientFrequencyEligible", 1 }, { "IsIngredientFrequencyLabeled", 1 },
                { "IngredientFrequencyVersion", 1 }, { "PmcId", 1 }
            }, new CreateIndexOptions { Name = WorkIndex })
        };
        foreach (string field in new[] { "HasIngredientInTitle", "HasIngredientInAbstract", "HasIngredientInSections" })
            models.Add(new(new BsonDocument { { field, 1 }, { "PmcId", 1 } },
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "ingredient_frequency_" + field,
                    PartialFilterExpression = new BsonDocument("IsIngredientFrequencyLabeled", true)
                }));
        await _articles.Indexes.CreateManyAsync(models, cancellationToken);
        Console.WriteLine("Four ingredient frequency indexes ready.");
    }

    public async Task LabelArticlesAsync(IngredientFrequencyAnalyzer analyzer, CancellationToken cancellationToken = default)
    {
        var eligible = new BsonDocument("IsIngredientFrequencyEligible", true);
        long total = await _articles.CountDocumentsAsync(eligible, new CountOptions { Hint = WorkIndex }, cancellationToken);
        var currentVersion = new BsonDocument
        {
            { "IsIngredientFrequencyEligible", true }, { "IsIngredientFrequencyLabeled", true },
            { "IngredientFrequencyVersion", analyzer.Version }
        };
        long alreadyDone = await _articles.CountDocumentsAsync(currentVersion, new CountOptions { Hint = WorkIndex }, cancellationToken);
        SetProgress("Ingredient labeling", alreadyDone, total);
        var pending = new BsonDocument
        {
            { "IsIngredientFrequencyEligible", true },
            { "$or", new BsonArray
                {
                    new BsonDocument("IsIngredientFrequencyLabeled", new BsonDocument("$ne", true)),
                    new BsonDocument("IngredientFrequencyVersion", new BsonDocument("$ne", analyzer.Version))
                }
            }
        };
        // A bounded producer/consumer queue overlaps reads, CPU work and acknowledged writes.
        // Each of the requested workers searches one article at a time and flushes its own write batch.
        var findOptions = new FindOptions<BsonDocument>
        {
            BatchSize = _options.BatchSize, NoCursorTimeout = true,
            Projection = new BsonDocument { { "PmcId", 1 }, { "Title", 1 }, { "AbstractText", 1 }, { "Sections", 1 } },
            Hint = WorkIndex
        };
        long processed = alreadyDone;
        using var pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pipelineToken = pipelineCancellation.Token;
        var queue = Channel.CreateBounded<BsonDocument>(new BoundedChannelOptions(checked(_options.BatchSize * 2))
        {
            SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });
        int writeBatchSize = Math.Min(_options.BatchSize, 64);
        var tasks = Enumerable.Range(0, _options.Threads).Select(_ => Task.Run(ConsumeAsync)).ToList();
        tasks.Add(ProduceAsync());
        try { await Task.WhenAll(tasks); }
        catch
        {
            var rootCause = tasks.Where(t => t.IsFaulted).SelectMany(t => t.Exception!.Flatten().InnerExceptions)
                .FirstOrDefault(e => e is not OperationCanceledException);
            if (rootCause is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(rootCause).Throw();
            throw;
        }

        async Task ProduceAsync()
        {
            try
            {
                // Disjoint integer ranges include both endpoint IDs and never overlap, even for negative IDs.
                // Several readers avoid serializing all MongoDB decompression/projection work on one cursor.
                var idProjection = new BsonDocument { { "_id", 0 }, { "PmcId", 1 } };
                var first = await _articles.Find(FilterDefinition<BsonDocument>.Empty, new FindOptions { Hint = "PmcId_1" })
                    .Project(idProjection).Sort(new BsonDocument("PmcId", 1)).FirstOrDefaultAsync(pipelineToken);
                var last = await _articles.Find(FilterDefinition<BsonDocument>.Empty, new FindOptions { Hint = "PmcId_1" })
                    .Project(idProjection).Sort(new BsonDocument("PmcId", -1)).FirstOrDefaultAsync(pipelineToken);
                if (first is not null && last is not null)
                {
                    long minimum = first["PmcId"].ToInt64();
                    long span = last["PmcId"].ToInt64() - minimum + 1;
                    int readers = Math.Min(_options.Readers, _options.Threads);
                    await Task.WhenAll(Enumerable.Range(0, readers).Select(ReadRangeAsync));

                    async Task ReadRangeAsync(int reader)
                    {
                        try
                        {
                            long lower = minimum + span * reader / readers;
                            long upper = minimum + span * (reader + 1) / readers;
                            if (lower == upper) return;
                            var range = new BsonDocument("PmcId", new BsonDocument { { "$gte", lower }, { "$lt", upper } });
                            var filter = new BsonDocument("$and", new BsonArray { pending, range });
                            using var cursor = await _articles.FindAsync(filter, findOptions, pipelineToken);
                            while (await cursor.MoveNextAsync(pipelineToken))
                                foreach (var article in cursor.Current) await queue.Writer.WriteAsync(article, pipelineToken);
                        }
                        catch { pipelineCancellation.Cancel(); throw; }
                    }
                }
                queue.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                queue.Writer.TryComplete(ex);
                pipelineCancellation.Cancel();
                throw;
            }
        }

        async Task ConsumeAsync()
        {
            try
            {
                var writes = new List<WriteModel<BsonDocument>>(writeBatchSize);
                await foreach (var article in queue.Reader.ReadAllAsync(pipelineToken))
                {
                    if (!IngredientFrequencyAnalyzer.IsEligible(article))
                        throw new InvalidDataException($"Article {article["PmcId"]} changed after migration; rerun the job.");
                    var labels = analyzer.Analyze(article.GetValue("Title", BsonNull.Value).IsString
                        ? article["Title"].AsString : null, article["AbstractText"].AsString,
                        article["Sections"].AsBsonDocument.Elements.Where(s => s.Value.IsString).Select(s => s.Value.AsString));
                    writes.Add(new UpdateOneModel<BsonDocument>(new BsonDocument("_id", article["_id"]),
                        analyzer.BuildUpdate(labels, DateTime.UtcNow)));
                    if (writes.Count >= writeBatchSize) await FlushAsync();
                }
                if (writes.Count > 0) await FlushAsync();

                async Task FlushAsync()
                {
                    var result = await _articles.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, pipelineToken);
                    if (result.MatchedCount != writes.Count)
                        throw new InvalidOperationException("Some articles disappeared before their labels were saved. Rerun to reconcile.");
                    lock (_progressLock)
                    {
                        processed += result.MatchedCount;
                        SetProgress("Ingredient labeling", processed, total);
                    }
                    writes.Clear();
                }
            }
            catch
            {
                pipelineCancellation.Cancel();
                throw;
            }
        }
        // Do not report success if cursor/index movement or concurrent data changes left work behind.
        long remaining = await _articles.CountDocumentsAsync(pending, new CountOptions { Hint = WorkIndex }, cancellationToken);
        if (remaining != 0)
            throw new InvalidOperationException($"{remaining:N0} eligible articles remain. Rerun to resume.");
        WriteProgress();
    }

    public async Task<string> ExportReportAsync(IngredientFrequencyAnalyzer analyzer, CancellationToken cancellationToken = default)
    {
        var filter = new BsonDocument
        {
            { "IsIngredientFrequencyEligible", true }, { "IsIngredientFrequencyLabeled", true },
            { "IngredientFrequencyVersion", analyzer.Version }
        };
        long count = await _articles.CountDocumentsAsync(filter, new CountOptions { Hint = WorkIndex }, cancellationToken);
        long eligible = await _articles.CountDocumentsAsync(new BsonDocument("IsIngredientFrequencyEligible", true),
            new CountOptions { Hint = WorkIndex }, cancellationToken);
        if (count != eligible)
            throw new InvalidOperationException($"Report would be incomplete: labeled {count:N0} of {eligible:N0} eligible articles.");
        SetProgress("Aggregating Excel report", 0, count);
        var report = new IngredientFrequencyReport(analyzer.Matcher.Keywords);
        var projection = new BsonDocument { { "_id", 0 }, { "IngredientTitleCounts", 1 },
            { "IngredientAbstractCounts", 1 }, { "IngredientSectionCounts", 1 } };
        var findOptions = new FindOptions<BsonDocument>
        {
            Projection = projection, BatchSize = 2000, Hint = WorkIndex, NoCursorTimeout = true
        };
        var idProjection = new BsonDocument { { "_id", 0 }, { "PmcId", 1 } };
        var first = await _articles.Find(FilterDefinition<BsonDocument>.Empty, new FindOptions { Hint = "PmcId_1" })
            .Project(idProjection).Sort(new BsonDocument("PmcId", 1)).FirstOrDefaultAsync(cancellationToken);
        var last = await _articles.Find(FilterDefinition<BsonDocument>.Empty, new FindOptions { Hint = "PmcId_1" })
            .Project(idProjection).Sort(new BsonDocument("PmcId", -1)).FirstOrDefaultAsync(cancellationToken);
        long done = 0;
        if (first is not null && last is not null)
        {
            long minimum = first["PmcId"].ToInt64();
            long span = last["PmcId"].ToInt64() - minimum + 1;
            int readers = Math.Min(_options.Readers, _options.Threads);
            using var reportCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var partialReports = await Task.WhenAll(Enumerable.Range(0, readers)
                .Select(reader => Task.Run(() => ReadRangeAsync(reader))));
            foreach (var partial in partialReports) report.Merge(partial);

            async Task<IngredientFrequencyReport> ReadRangeAsync(int reader)
            {
                try
                {
                    var partial = new IngredientFrequencyReport(analyzer.Matcher.Keywords);
                    long lower = minimum + span * reader / readers;
                    long upper = minimum + span * (reader + 1) / readers;
                    if (lower == upper) return partial;
                    var range = new BsonDocument("PmcId", new BsonDocument { { "$gte", lower }, { "$lt", upper } });
                    var rangeFilter = new BsonDocument("$and", new BsonArray { filter, range });
                    using var cursor = await _articles.FindAsync(rangeFilter, findOptions, reportCancellation.Token);
                    while (await cursor.MoveNextAsync(reportCancellation.Token))
                    {
                        long batchCount = 0;
                        foreach (var article in cursor.Current) { partial.AddArticle(article); batchCount++; }
                        lock (_progressLock)
                        {
                            done += batchCount;
                            SetProgress("Aggregating Excel report", done, count);
                        }
                    }
                    return partial;
                }
                catch { reportCancellation.Cancel(); throw; }
            }
        }
        if (done != count) throw new InvalidOperationException("Report article count changed while reading; rerun report-only.");
        string path = Path.Combine(_options.OutputDirectory, "ingredient_keyword_frequencies.xlsx");
        report.Save(path, _options, analyzer.Version, _elapsed.Elapsed);
        return path;
    }

    private void SetProgress(string phase, long done, long total)
    {
        lock (_progressLock) { _phase = phase; _done = done; _total = total; }
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            WriteProgress();
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }

    private void WriteProgress(string? error = null)
    {
        lock (_progressLock)
        {
            double percent = _total == 0 ? 0 : Math.Min(100, _done * 100d / _total);
            Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {_phase}: {_done:N0}/{_total:N0} ({percent:F2}%) | elapsed {_elapsed.Elapsed:hh\\:mm\\:ss}");
            string path = Path.Combine(_options.OutputDirectory, "progress.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new
            {
                phase = _phase, processed = _done, total = _total, percent,
                schemaDocumentsModified = Interlocked.Read(ref _changed),
                elapsedSeconds = _elapsed.Elapsed.TotalSeconds, updatedAtUtc = DateTime.UtcNow, error
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
        }
    }
}
