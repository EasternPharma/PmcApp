using Newtonsoft.Json;
using PMC_APP;
using PMC_APP.DTOs;

#region GuidLines
async Task GuidelinesAsync()
{
    Console.WriteLine("1. Extract Test");
    Console.WriteLine("2. Extract All .tar.gz Files");
    Console.WriteLine("3. Filter Test");
    Console.WriteLine("4. Apply Filter on All JSON Files");
    Console.WriteLine("5. Search for Validation PMC Data");
    Console.WriteLine("6. Statistics");
    Console.WriteLine("7. PMC IDs");
    Console.WriteLine("8. Compare PMC search TXT results vs DB (Xml bulk + scraped summaries)");
    Console.WriteLine("9. Apply Filter on Scraped MongoDB Articles");
    Console.WriteLine("10. Ingredient Keyword Filter on all_articles");
    Console.WriteLine("11. Save Ingredient Article PMC IDs to TXT file");
    Console.WriteLine("12. Update Articles from JSON Backup Folder");
    Console.WriteLine("13. Filter Human Study (re-filter articles where IsHumanStudy is null)");

    Console.Write("Enter your choice item from list: ");
    var choiceInput = Console.ReadLine();
    if (int.TryParse(choiceInput, out int choice))
    {
        switch (choice)
        {
            case 1:
                ExtractTest();
                break;
            case 2:
                EctractTarGzFiles();
                break;
            case 3:
                TestFilterJsonFile();
                break;
            case 4:
                ApplyFilterOnAllJsonFiles();
                break;
            case 5:
                Search();
                break;
            case 6:
                Statistics();
                break;
            case 7:
                PmcIDs();
                break;
            case 8:
                await Method8CompareTxtToDatabaseAsync();
                break;
            case 9:
                await ApplyFilterOnAllJsonFiles_scrapAsync();
                break;
            case 10:
                await FilterIngredientKeywordsAsync();
                break;
            case 11:
                await GetIngredientArticleIdsAsync();
                break;
            case 12:
                await UpdateArticles();
                break;
            case 13:
                await FilterHumanStudyNullAsync();
                break;
            default:
                Console.WriteLine("Invalid choice. Please select from list");
                break;
        }
    }
    else
    {
        Console.WriteLine("Invalid input. Please enter a number.");
    }
}
#endregion

#region #1 Method1: Test code for extracting a single XML file
void ExtractTest()
{
    var file_test = new FileInfo("E:\\xml\\PMC12650674.xml");
    var parser = new PmcArticleParser();
    string xmlContent = File.ReadAllText(file_test.FullName);
    var article = parser.GetArticle(file_test.Name, xmlContent);
}
ThreadPool.SetMinThreads(Environment.ProcessorCount, Environment.ProcessorCount);
#endregion

#region #2 Method2: Code for extracting multiple tar.gz files
void EctractTarGzFiles()
{
    //List<FileInfo> tar_files = Directory.GetFiles("D:\\PMC\\Dataset\\2026\\tar_gz", "*.tar.gz").Select(x => new FileInfo(x)).ToList();
    List<FileInfo> tar_files = Directory.GetFiles("D:\\PMC\\Dataset\\2026\\noncom", "*.tar.gz").Select(x => new FileInfo(x)).ToList();


    //var file = "D:\\PMC\\Dataset\\2026\\tar_gz\\oa_comm_xml.PMC000xxxxxx.baseline.2026-01-23.tar.gz";
    //var file = "D:\\PMC\\Dataset\\2026\\tar_gz\\oa_comm_xml.PMC001xxxxxx.baseline.2026-01-23.tar.gz";
    //var file = "D:\\PMC\\Dataset\\2026\\tar_gz\\oa_comm_xml.PMC001xxxxxx.baseline.2026-01-23.tar.gz";
    //var outputDirectory = "E:\\PMC\\2026\\1_JSON\\commercial\\";
    var outputDirectory = "E:\\PMC\\2026\\1_JSON\\non_commercial\\";

    foreach (var file in tar_files)
    {
        try
        {
            var pmc_executer = new PmcArticleExecution(file.FullName, outputDirectory, 10000);
            pmc_executer.Execute();
            Thread.Sleep(3000);
            pmc_executer.Dispose();
            Console.WriteLine("Disposed resources for file: " + file.FullName);
            Thread.Sleep(3000);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error processing file {file.FullName}: {ex.Message}");
            Console.ResetColor();
        }
    }
}
#endregion

#region #3 Method3 Filter Json files base on keywords
void TestFilterJsonFile()
{
    var settings = new PmcArticleFilterSettingsDTO
    {
        JsonFilesDirectoryPath = @"E:\PMC\2026\test\JSON",
        OutputDirectoryPath = @"E:\PMC\2026\test\Filtered",
        OutputType = PmcArticleFilterOutputTypes.JsonFile,
        ExclusionExcelPath = @"E:\PMC\2026\exclusion_list.xlsx"
    };
    var filter = new PmcArticleFilter(settings);
    filter.FilterAllJsonFiles();
}
#endregion

#region #4 Method4: Apply filter on all JSON files
void ApplyFilterOnAllJsonFiles()
{
    var settings = new PmcArticleFilterSettingsDTO
    {
        JsonFilesDirectoryPath = @"E:\PMC\2026\1_JSON",
        OutputType = PmcArticleFilterOutputTypes.Mongodb,
        ExclusionExcelPath = @"D:\PMC\Dataset\2026\PMC safe-exclusion keywords.csv",
        MongodbHost = "localhost",
        MongodbPort = 27017,
        MongodbDatabaseName = "pmc",
        MongodbOutputCollectionName = "all_articles"
    };
    var filter = new PmcArticleFilter(settings);
    filter.FilterAllJsonFilesLabel();
}

async Task ApplyFilterOnAllJsonFiles_scrapAsync()
{
    var settings = new PmcArticleFilterSettingsDTO
    {
        MongodbInputCollectionName = "articles",
        MongodbArticleListCollectionName = "article_list",
        MongodbOutputCollectionName = "all_articles",
        OutputType = PmcArticleFilterOutputTypes.Mongodb,
        ExclusionExcelPath = @"D:\PMC\Dataset\2026\PMC safe-exclusion keywords.csv",
        MongodbHost = "localhost",
        MongodbPort = 27017,
        MongodbDatabaseName = "pmc",
    };
    var filter = new PmcArticleFilter(settings);
    await filter.FilterAllMongoDbLabelAsync();
}
#endregion

#region #5 Mehtod5: Test and Search for Validation PMC Data
void Search()
{
    string jsonFiltered = @"E:\PMC\2026\2_Filtered_JSON\JSON";
    string txtDirPath = @"E:\PMC\2026\2_Filtered_JSON\TXT";
    PmcSearchHelper pmcSearch = new PmcSearchHelper(jsonFiltered);
    pmcSearch.Search(txtDirPath);
}
#endregion

#region #6 Method6: Statistics PMC Data
void Statistics()
{
    string JsonDirPath = @"E:\PMC\2026\1_JSON";
    PmcStatistics pmcStatistics = new PmcStatistics(JsonDirPath);
    pmcStatistics.CalculateStatistics();
}
#endregion

#region #7 Method7: PMC IDs
void PmcIDs()
{
    PmcArticleIDs pmcArticleIDs = new PmcArticleIDs();
    // var ids = pmcArticleIDs.GetRemindPmcIDs();
    pmcArticleIDs.GetRemindPmcIds_ingredient();
}
#endregion

#region #8 Method8: Compare PMC search TXT files vs MongoDB (Xml bulk + scraped)
/// <summary>
/// For each search query, reads a TXT of PMC IDs, ensures summary indexes exist, then writes a JSON report next to the TXT.
/// </summary>
async Task Method8CompareTxtToDatabaseAsync()
{
    string xmlBulkJsonRoot = @"E:\PMC\2026\1_JSON";

    string mongoHost = "localhost";
    int mongoPort = 27017;
    string mongoDatabase = "pmc";
    string mongoArticlesCollection = "articles";
    string mongoSummariesCollection = "simple_articles";

    string searchResultsDirectory = @"E:\PMC\2026\Task8_Compare_PMC_Search\PMC_Search_Results\";
    Dictionary<string, string> searchQueryToTxtFileName = new Dictionary<string, string>
    {
        //["BIOTIN Maintain support hair growth"] = "BIOTIN Maintain support hair growth.txt",
        //["ASCORBIC ACID Maintain support collagen formation"] = "ASCORBIC ACID Maintain support collagen formation.txt",
        //["PUERARIA LOBATA Helps decrease reduce relieve symptoms of occasional hangovers"] = "PUERARIA LOBATA  hangovers.txt",
        //["LILIUM LONGIFLORUM Soothe relieve skin inflammation"] = "LILIUM LONGIFLORUM Soothe relieve skin inflammation_gpt.txt",
        ["Withania somnifera sleep"] = "Withania somnifera sleep.txt",
        ["allium sativum immunity system"] = "allium sativum immunity system.txt",
        ["magnesium glycinate improves sleep"] = "magnesium glycinate improves sleep.txt",
        ["chamomile improve sleep"] = "chamomile improve sleep.txt",
        ["echium vulgare anti-inflammatory"] = "echium vulgare anti-inflammatory.txt",
        ["cholecalciferol immune system"] = "cholecalciferol immune system.txt",
        ["vitamin b12 cognitive function"] = "vitamin b12 cognitive function.txt",
        ["vitamin b1 energy"] = "vitamin b1 energy.txt",
    };

    var settings = new PmcArticleTxtDbCompareSettings
    {
        MongoHost = mongoHost,
        MongoPort = mongoPort,
        DatabaseName = mongoDatabase,
        ArticlesCollectionName = mongoArticlesCollection,
        SummariesCollectionName = mongoSummariesCollection,
        XmlBulkJsonRootPath = xmlBulkJsonRoot,
    };

    using var comparer = new PmcArticleTxtDbComparer(settings);
    await comparer.EnsureSummariesReadyAsync().ConfigureAwait(false);

    foreach (var entry in searchQueryToTxtFileName)
    {
        string searchQueryLabel = entry.Key;
        string txtFileName = entry.Value;
        string txtPath = Path.Combine(searchResultsDirectory, txtFileName);
        if (!File.Exists(txtPath))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Skip (file missing): {txtPath}");
            Console.ResetColor();
            continue;
        }

        var lines = await File.ReadAllLinesAsync(txtPath).ConfigureAwait(false);
        var pmcNumericIds = PmcArticleTxtDbComparer.ParsePmcNumericIdsFromLines(lines);
        var report = await comparer.CompareSearchResultTxtAsync(pmcNumericIds, searchQueryLabel).ConfigureAwait(false);

        string jsonPath = Path.Combine(searchResultsDirectory, Path.GetFileNameWithoutExtension(txtFileName) + ".json");
        await File.WriteAllTextAsync(jsonPath, JsonConvert.SerializeObject(report, Formatting.Indented)).ConfigureAwait(false);
        Console.WriteLine($"Wrote report: {jsonPath}");
    }
}
#endregion

#region #10 Method10: Ingredient keyword filter on all_articles
async Task FilterIngredientKeywordsAsync()
{
    var settings = new PmcArticleFilterSettingsDTO
    {
        OutputType = PmcArticleFilterOutputTypes.Mongodb,
        ExclusionExcelPath = @"D:\PMC\Dataset\2026\PMC safe-exclusion keywords.csv",
        MongodbHost = "localhost",
        MongodbPort = 27017,
        MongodbDatabaseName = "pmc",
        MongodbOutputCollectionName = "all_articles",
        IngredientKeywordsFilePath = @"E:\PMC\ingredients_Nhmrc_keys_uniq.txt",
    };
    var filter = new PmcArticleFilter(settings);
    await filter.FilterIngredientKeywordsAsync(settings.IngredientKeywordsFilePath!);
}
#endregion

#region #11 Method11: Ingredient Articles to txt file
async Task GetIngredientArticleIdsAsync()
{
    var settings = new PmcArticleFilterSettingsDTO
    {
        OutputType = PmcArticleFilterOutputTypes.Mongodb,
        MongodbHost = "localhost",
        MongodbPort = 27017,
        MongodbDatabaseName = "pmc",
        MongodbOutputCollectionName = "all_articles",
        OutputTxtFilePath = @"E:\PMC\ingredient_article_ids.txt",
        ReadOnlyMode = true,
    };
    var filter = new PmcArticleFilter(settings);
    await filter.GetIngredientArticleIdsAsync();
}
#endregion

#region #12 Method12: Update Articles from JSON backup folder to mongodb
async Task UpdateArticles()
{
    string folderPath = @"E:\PMC\articles_ingredient_backup";
    var settings = new PmcArticleFilterSettingsDTO
    {
        OutputType = PmcArticleFilterOutputTypes.Mongodb,
        MongodbHost = "localhost",
        MongodbPort = 27017,
        MongodbDatabaseName = "pmc",
        MongodbOutputCollectionName = "all_articles",
        ReadOnlyMode = true,
    };
    var filter = new PmcArticleFilter(settings);
    await filter.UpdateArticlesFromJsonFolderAsync(folderPath);
}
#endregion

#region #13 Method13: Re-filter articles where IsHumanStudy is null
async Task FilterHumanStudyNullAsync()
{
    var settings = new PmcArticleFilterSettingsDTO
    {
        OutputType = PmcArticleFilterOutputTypes.Mongodb,
        ExclusionExcelPath = @"D:\PMC\Dataset\2026\PMC safe-exclusion keywords.csv",
        MongodbHost = "localhost",
        MongodbPort = 27017,
        MongodbDatabaseName = "pmc",
        MongodbOutputCollectionName = "all_articles",
    };
    var filter = new PmcArticleFilter(settings);
    await filter.FilterOnHumanStudyNullAsync();
}
#endregion

await GuidelinesAsync();
