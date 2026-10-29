using System.IO.Compression;
using System.Text.Json;
using IngredientKeywordSearch;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using PMC_APP.DTOs;
using PMC_APP.IngredientSearch;

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
}

using (var analyzer = new IngredientFrequencyAnalyzer(new[] { "biotin", "Biotin", "acid", "lactic acid", "ipê" }))
{
    Check(analyzer.Matcher.KeywordCount == 4, "Keywords must be deduplicated.");
    var labels = analyzer.Analyze("Biotin biotin biotine _biotin", "IPÊ and lactic acid", new[] { "lactic", "acid", "biotin BIOTIN" });
    Check(labels.Title.Single().Count == 2, "Whole-word title count.");
    Check(labels.Abstract.Count == 3, "Unicode and overlapping phrase matches.");
    Check(labels.Sections.All(c => c.Keyword != "lactic acid"), "Phrases must not span sections.");
    Check(labels.Sections.Single(c => c.Keyword == "biotin").Count == 2, "Counts sum across section bodies.");
    Check(analyzer.Matcher.Search("𐐀biotin biotin𐐀 biotin\u0301 biotin² biotin😀")["biotin"] == 1,
        "Unicode word boundaries must match the original matcher.");
    Parallel.For(0, 300, _ => Check(analyzer.Analyze("biotin biotin", "acid", new[] { "IPÊ" }).Title.Single().Count == 2,
        "Shared matcher with worker-local counters."));
    Check(BsonSerializer.Deserialize<ArticleLabelDTO>(new BsonDocument { { "PmcId", 1 }, { "Unknown", true } })
        .IngredientTitleCounts.Count == 0, "Old documents must still deserialize.");
}

string root = Path.Combine(Path.GetTempPath(), "pmc-ingredient-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string keywords = Path.Combine(root, "keywords.xlsx");
using (var zip = ZipFile.Open(keywords, ZipArchiveMode.Create))
{
    void Part(string name, string xml) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(xml); }
    Part("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Words\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
    Part("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
    Part("xl/worksheets/sheet1.xml", "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"
        + string.Concat(new[] { "keyword", "biotin", "lactic acid", "acid", "never matched" }.Select((word, i) =>
            $"<row r=\"{i + 1}\"><c r=\"A{i + 1}\" t=\"inlineStr\"><is><t>{word}</t></is></c></row>"))
        + "</sheetData></worksheet>");
}
Check(XlsxKeywordReader.Read(keywords).Count == 4, "Read keyword workbook.");

var client = new MongoClient("mongodb://localhost:27017");
string databaseName = "pmc_ingredient_test_" + Guid.NewGuid().ToString("N");
var collection = client.GetDatabase(databaseName).GetCollection<BsonDocument>("articles");
try
{
    var fixtures = new List<BsonDocument>
    {
        new() { { "PmcId", -1 }, { "Title", "biotin biotin" }, { "AbstractText", "lactic acid biotin" },
            { "Sections", new BsonDocument { { "biotin heading", "lactic" }, { "two", "acid biotin BIOTIN" } } },
            { "HasFullText", false }, { "HasAbstract", false }, { "IsHumanStudy", false },
            { "Reviewed1", "preserve me" }, { "Ingredients", new BsonArray { "old ingredient" } } },
        new() { { "PmcId", 2 }, { "AbstractText", "Nothing" }, { "Sections", new BsonDocument("body", "Nothing") } },
        new() { { "PmcId", 3 }, { "AbstractText", "\u00a0\u2003\t" }, { "Sections", new BsonDocument("body", "biotin") } },
        new() { { "PmcId", 4 }, { "AbstractText", "biotin" }, { "Sections", new BsonDocument("body", " \n") } },
        new() { { "PmcId", 5 }, { "AbstractText", "biotin" }, { "Sections", new BsonDocument() } },
        new() { { "PmcId", 6 }, { "AbstractText", BsonNull.Value }, { "Sections", new BsonDocument("body", "biotin") } },
        new() { { "PmcId", 7 }, { "AbstractText", "biotin" }, { "Sections", new BsonArray { "biotin" } } },
        new() { { "PmcId", 8 }, { "AbstractText", "biotin" }, { "Sections", new BsonDocument("body", 123) } },
        new() { { "PmcId", 9 }, { "AbstractText", "biotin" }, { "Sections", new BsonDocument("body", "\u3000\u0085") } },
        new() { { "PmcId", 10 }, { "AbstractText", "\u001c" }, { "Sections", new BsonDocument("body", "biotin") } },
        new() { { "PmcId", 11 }, { "AbstractText", "biotin" }, { "Sections", new BsonDocument("body", "biotin") } }
    };
    await collection.InsertManyAsync(fixtures);
    await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("PmcId", 1),
        new CreateIndexOptions { Name = "PmcId_1", Unique = true }));
    var options = new IngredientFrequencyOptions
    {
        Database = databaseName, Collection = "articles", KeywordsPath = keywords,
        OutputDirectory = root, BatchSize = 2, MigrationBatchSize = 3, Threads = 2
    };
    string report = await new PmcIngredientFrequencyLabeler(options).RunAsync();
    var stored = await collection.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
    foreach (var doc in stored)
    {
        var original = fixtures.Single(f => f["PmcId"] == doc["PmcId"]);
        bool expected = IngredientFrequencyAnalyzer.IsEligible(original);
        Check(doc["IsIngredientFrequencyEligible"].AsBoolean == expected, "Mongo and .NET eligibility differ.");
        Check(doc["IsIngredientFrequencyLabeled"].AsBoolean == expected, "Only eligible documents should be labeled.");
        Check(doc.Contains("IngredientTitleCounts") && doc.Contains("HasIngredientInSections"), "Schema migration must cover all documents.");
    }
    var first = stored.Single(a => a["PmcId"] == -1);
    Check(first["Reviewed1"] == "preserve me" && first["Ingredients"][0] == "old ingredient", "Existing fields must be preserved.");
    Check(first["IngredientTitleCounts"][0]["Count"].ToInt64() == 2, "Stored title counts.");
    Check(first["IngredientSectionCounts"].AsBsonArray.All(v => v["Keyword"] != "lactic acid"), "No phrase across section boundary.");
    var savedAt = first["IngredientFrequencyLabeledAt"];
    Check((await collection.Indexes.ListAsync()).ToList().Count == 6, "Unique identity plus four new indexes.");
    // Simulate one interrupted/uncommitted article and resume; already saved rows retain their timestamp.
    await collection.UpdateOneAsync(new BsonDocument("PmcId", 11), new BsonDocument("$set", new BsonDocument("IsIngredientFrequencyLabeled", false)));
    // Also simulate a partially finished schema wave. The acknowledged prefix must be skipped safely.
    await collection.UpdateManyAsync(new BsonDocument("PmcId", new BsonDocument("$gt", 6)),
        new BsonDocument("$unset", new BsonDocument("IsIngredientFrequencyEligible", "")));
    File.WriteAllText(Path.Combine(root, "migration-checkpoint.json"), JsonSerializer.Serialize(new
    {
        Database = databaseName, Collection = "articles", HighWatermark = 11,
        LastPmcId = 6, Processed = 6L, Complete = false
    }));
    await new PmcIngredientFrequencyLabeler(options).RunAsync();
    Check(await collection.CountDocumentsAsync(new BsonDocument("IsIngredientFrequencyEligible", new BsonDocument("$exists", false))) == 0,
        "Migration checkpoint must resume the unfinished suffix.");
    Check((await collection.Find(new BsonDocument("PmcId", -1)).SingleAsync())["IngredientFrequencyLabeledAt"] == savedAt,
        "Resumption must skip already committed labels.");
    using var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(report, ".json")));
    Check(json.RootElement.GetProperty("articles").GetInt64() == 4, "Report denominator includes eligible articles with no matches.");
    var biotin = json.RootElement.GetProperty("keywords").EnumerateArray().Single(k => k.GetProperty("Keyword").GetString() == "biotin");
    Check(biotin.GetProperty("Occurrences").EnumerateArray().Select(v => v.GetInt64()).SequenceEqual(new long[] { 2, 2, 4 }),
        "Report field occurrence totals.");
    Check(biotin.GetProperty("AnyFieldArticleCount").GetInt64() == 3, "Union count must not double-count articles.");
    options.ResumeLabeling = true;
    await new PmcIngredientFrequencyLabeler(options).RunAsync();
    Check((await collection.Find(new BsonDocument("PmcId", -1)).SingleAsync())["IngredientFrequencyLabeledAt"] == savedAt,
        "Fast labeling resumption must preserve saved labels.");
    using var workbook = ZipFile.OpenRead(report);
    Check(workbook.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/")) == 5, "Five XLSX worksheets.");
    var stress = client.GetDatabase(databaseName).GetCollection<BsonDocument>("stress");
    await stress.InsertManyAsync(Enumerable.Range(1, 257).Select(id => new BsonDocument
    {
        { "PmcId", id }, { "Title", "biotin" }, { "AbstractText", "BIOTIN" },
        { "Sections", new BsonDocument("body", "biotin biotin") }
    }));
    await stress.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("PmcId", 1),
        new CreateIndexOptions { Name = "PmcId_1", Unique = true }));
    var stressOptions = new IngredientFrequencyOptions
    {
        Database = databaseName, Collection = "stress", KeywordsPath = keywords,
        OutputDirectory = Path.Combine(root, "stress"), BatchSize = 3, MigrationBatchSize = 7, Threads = 4
    };
    string stressReport = await new PmcIngredientFrequencyLabeler(stressOptions).RunAsync();
    Check(await stress.CountDocumentsAsync(new BsonDocument("IsIngredientFrequencyLabeled", true)) == 257,
        "Bounded queue must neither skip nor double-process articles across small batches.");
    using var stressJson = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(stressReport, ".json")));
    Check(stressJson.RootElement.GetProperty("occurrences").EnumerateArray().Select(v => v.GetInt64())
        .SequenceEqual(new long[] { 257, 257, 514 }), "Parallel pipeline report totals and partial final batches.");
    stressOptions.ResumeLabeling = true;
    await stress.UpdateOneAsync(new BsonDocument("PmcId", 1), new BsonDocument("$set", new BsonDocument
    {
        { "Sections", BsonNull.Value }, { "IsIngredientFrequencyLabeled", false }
    }));
    bool failedSafely = false;
    try { await new PmcIngredientFrequencyLabeler(stressOptions).RunAsync(); }
    catch (InvalidDataException) { failedSafely = true; }
    Check(failedSafely, "Worker failure must cancel the bounded pipeline, propagate and avoid a false successful report.");
    Console.WriteLine($"PASS: matcher, Unicode, separate sections, parallel counts, DTO, eligibility, migration, indexes, preservation, resume and report. Fixture report: {report}");
}
finally
{
    await client.DropDatabaseAsync(databaseName);
}
