using ExcelDataReader;
using Newtonsoft.Json;
using PMC_APP.DTOs;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PMC_APP;

public class PmcArticleFilter
{
    #region Initialization
    private string ExclusionExcelPath { get; set; }
    public List<string> ExclusionKeywords { get; set; }
    public Dictionary<string, string> Keyword2Category { get; set; }
    public List<FileInfo> ArticleJsonFile { get; set; }

    // Pre-compiled regex patterns, built once for O(1) reuse per article
    private List<(string Keyword, string Category, Regex Pattern)> _compiledPatterns = new();
    private string OutputDirectory { get; set; }

    public PmcArticleFilter(string dirPath, string outputDir)
    {
        ArticleJsonFile = Directory.GetFiles(dirPath, "*.json", SearchOption.AllDirectories)
            .Select(x => new FileInfo(x)).ToList();
        ExclusionExcelPath = "D:\\PMC\\Dataset\\2026\\PMC safe-exclusion keywords.csv";
        LoadExclusionKeywords();
        OutputDirectory = outputDir;
        if (!Directory.Exists(OutputDirectory))
        {
            Directory.CreateDirectory(OutputDirectory);
        }
    }
    #endregion

    #region Load exclusion keywords from Excel file
    private void LoadExclusionKeywords()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        this.Keyword2Category = new Dictionary<string, string>();
        this.Keyword2Category = ReadExcelToDictionary(this.ExclusionExcelPath);
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

    private List<ArticleFilterDTO> FilterJsonFile(FileInfo fileInfo)
    {
        string jsonContent = File.ReadAllText(fileInfo.FullName);
        var articles = JsonConvert.DeserializeObject<List<ArticleDTO>>(jsonContent);
        return ApplyFilters(articles);
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
                outputPath = Path.Combine(OutputDirectory, $"Part_{i + 1}.json");
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
            Console.WriteLine($"Saved to: {OutputDirectory}");
    }
    #endregion
}