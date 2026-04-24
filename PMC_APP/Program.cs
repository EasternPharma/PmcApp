using PMC_APP;

#region GuidLines
void Guidelines()
{
    Console.WriteLine("1. Extract Test");
    Console.WriteLine("2. Extract All .tar.gz Files");
    Console.WriteLine("3. Filter Test");
    Console.WriteLine("4. Apply Filter on All JSON Files");
    Console.WriteLine("5. Search for Validation PMC Data");
    Console.WriteLine("6. Statistics");
    Console.WriteLine("7. PMC IDs");

    Console.Write("Enter your choice item from list: ");
    var _choice = Console.ReadLine();
    if (int.TryParse(_choice, out int choice))
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
    string DirPath = @"E:\PMC\2026\test\JSON";
    string outputDir = @"E:\PMC\2026\test\Filtered";
    var filter = new PmcArticleFilter(DirPath, outputDir);
    filter.FilterAllJsonFiles();
}
#endregion

#region #4 Method4: Apply filter on all JSON files
void ApplyFilterOnAllJsonFiles()
{
    string DirPath = @"E:\PMC\2026\1_JSON";
    string outputDir = @"E:\PMC\2026\2_Filtered_JSON\JSON\";
    var filter = new PmcArticleFilter(DirPath, outputDir);
    filter.FilterAllJsonFiles();
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
    var ids = pmcArticleIDs.GetRemindPmcIDs();
}
#endregion

Guidelines();