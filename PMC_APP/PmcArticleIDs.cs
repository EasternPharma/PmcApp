using Newtonsoft.Json;
using PMC_APP.DTOs;

namespace PMC_APP;

public class PmcArticleIDs
{
    public string JsonDirPath { get; set; }
    public string PmcAllIDsPath { get; set; }
    public string RemindPmcIdsPath { get; set; }
    public List<int> AllPmcIds { get; set; }
    public List<int> ExtractJsonPmcIds { get; set; }
    public List<int> RemindPmcIds { get; set; }
    public PmcArticleIDs()
    {
        JsonDirPath = @"E:\PMC\2026\1_JSON";
        PmcAllIDsPath = @"E:\PMC\PMC_All_IDs.txt";
        RemindPmcIdsPath = @"E:\PMC\PMC_Remind_IDs.txt";
    }

    private List<int> GetAllPmcIDs =>
        File.ReadAllLines(PmcAllIDsPath)
        .Select(x=> int.Parse(x))
        .ToList();

    private List<FileInfo> GetJsonFiles =>
        Directory.GetFiles(JsonDirPath, "*.json", SearchOption.AllDirectories)
                 .Select(x => new FileInfo(x))
                 .ToList();

    public List<int> GetJsonPmcIds()
    {
        List<int> pmcIds = new List<int>();

        var files = GetJsonFiles;
        int totalFiles = files.Count;
        int current = 0;

        foreach (var jsonFile in files)
        {
            current++;

            Console.Write($"\rProcessing files: {current}/{totalFiles} ({(current * 100 / totalFiles)}%)");

            var jsonContent = File.ReadAllText(jsonFile.FullName);

            var articles = JsonConvert.DeserializeObject<List<ArticleDTO>>(jsonContent);

            if (articles != null)
            {
                var ids = articles
                    .Select(x => x.PmcId)
                    .ToList();

                pmcIds.AddRange(ids);
            }
        }

        Console.WriteLine();
        Console.WriteLine("Completed.");

        return pmcIds;
    }

    public List<int> GetRemindPmcIDs()
    {
        ExtractJsonPmcIds = GetJsonPmcIds();
        AllPmcIds = GetAllPmcIDs;

        // FIX: set difference (List subtraction is invalid in C#)
        RemindPmcIds = AllPmcIds.Except(ExtractJsonPmcIds).ToList();

        Console.WriteLine(
            $"Extracted JSON PMC IDs: {ExtractJsonPmcIds.Count}\n" +
            $"All PMC IDs: {AllPmcIds.Count}\n" +
            $"Remaining PMC IDs: {RemindPmcIds.Count}"
        );

        File.WriteAllLines(RemindPmcIdsPath, RemindPmcIds.Select(x => x.ToString()));

        Console.WriteLine("Successfully wrote reminder PMC IDs to file.");

        return RemindPmcIds;
    }
}