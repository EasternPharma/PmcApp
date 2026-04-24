using Newtonsoft.Json;
using PMC_APP.DTOs;

namespace PMC_APP;

public class PmcSearchHelper
{
    public string JsonFilteredDirPath { get; set; }
    public List<ArticleFilterDTO> articleFilters { get; set; }

    public PmcSearchHelper(string jsonFilteredDirPath)
    {
        JsonFilteredDirPath = jsonFilteredDirPath;
        articleFilters = new List<ArticleFilterDTO>();
        LoadJsonFile();
    }

    private void LoadJsonFile()
    {
        var jsonFilePath = Directory.GetFiles(this.JsonFilteredDirPath, "*.json").ToList();
        for (int i = 0; i < jsonFilePath.Count(); i++)
        {
            string jsonContent = File.ReadAllText(jsonFilePath[i]);
            var articles = JsonConvert.DeserializeObject<List<ArticleFilterDTO>>(jsonContent);
            articleFilters.AddRange(articles);
        }

        int FullBodyCount = articleFilters.Where(x => x.Keywords != null && x.Keywords.Count() > 0).Count();
        Console.WriteLine($"\nLoad {articleFilters.Count()} Articles.\n");
    }

    public List<SearchArticleDTO> SingleSearch(string pmcIdPath)
    {
        int yesCount = 0;
        int noCount = 0;
        int keywordCount = 0;
        var res = new List<SearchArticleDTO>();

        List<int> pmcIDs = File.ReadLines(pmcIdPath)
            .Select(x => int.Parse(x.Trim().ToLower().Replace("pmc", "")))
            .ToList();

        // Build a dictionary keyed by PmcID for O(1) lookup instead of O(n) scan per ID
        var lookup = this.articleFilters
            .Where(x => pmcIDs.Contains(x.PmcID))
            .ToDictionary(x => x.PmcID);

        foreach (int pmcId in pmcIDs)
        {
            if (lookup.TryGetValue(pmcId, out var article))
            {
                res.Add(new SearchArticleDTO
                {
                    PmcID = article.PmcID,
                    Title = article.Title,
                    Keywords = article.Keywords,
                    Categories = article.Categories,
                    Link = article.Link,
                    IsSearch = true
                });
                yesCount += 1;
                if (article.Keywords != null && article.Keywords.Count() > 0)
                {
                    keywordCount += 1;
                }
            }
            else
            {
                res.Add(new SearchArticleDTO
                {
                    PmcID = pmcId,
                    Link = $"https://pmc.ncbi.nlm.nih.gov/articles/PMC{pmcId}/",
                    IsSearch = false
                });
                noCount += 1;
            }
        }
        Console.WriteLine($"_____\n{pmcIdPath}\n\t\tYes: {yesCount}\t\tNo: {noCount}\t\tKeyword: {keywordCount}");
        return res.Where(x => (x.Keywords == null || x.Keywords.Count() == 0) & x.IsSearch == true).ToList();
    }

    public void Search(string dirPath)
    {
        var files = Directory.GetFiles(dirPath, "*.txt");
        foreach (var file in files)
        {
            var articles = SingleSearch(file);
            if (articles != null && articles.Count > 0)
            {
                string fileName = Path.GetFileNameWithoutExtension(file) + ".json";
                string jsonPath = Path.Combine(dirPath, fileName);
                string jsonContent = JsonConvert.SerializeObject(articles, Formatting.Indented);
                File.WriteAllText(jsonPath, jsonContent);
                Console.WriteLine($"Saved {articles.Count} articles -> {jsonPath}");
            }
        }
    }
}