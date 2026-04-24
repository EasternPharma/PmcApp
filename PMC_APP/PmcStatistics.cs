using Newtonsoft.Json;
using PMC_APP.DTOs;

namespace PMC_APP;

public class PmcStatistics
{
    public List<FileInfo> ArticleJsonFiles { get; set; } = new();
    public int AbstractCount { get; set; }
    public int FullTextCount { get; set; }
    public int AbstractFullTextCount { get; set; }
    public int TotalCount { get; set; }

    public PmcStatistics(string dirPath)
    {
        ArticleJsonFiles = Directory.GetFiles(dirPath, "*.json", SearchOption.AllDirectories)
            .Select(x => new FileInfo(x)).ToList();
    }

    public void CalculateStatistics()
    {
        int total = ArticleJsonFiles.Count;
        int barWidth = 40;
        int processed = 0;

        Console.WriteLine($"Processing {total} JSON file(s)...\n");

        foreach (var file in ArticleJsonFiles)
        {
            // Stream one file at a time — never accumulate all articles in memory
            var articles = StreamJsonFile(file.FullName);
            foreach (var article in articles)
            {
                TotalCount++;
                bool hasAbstract = !string.IsNullOrEmpty(article.AbstractText);
                bool hasFullText = article.Sections != null && article.Sections.Count > 0;

                if (hasAbstract) AbstractCount++;
                if (hasFullText) FullTextCount++;
                if (hasAbstract && hasFullText) AbstractFullTextCount++;
            }

            processed++;
            double fraction = (double)processed / total;
            int filled = (int)(fraction * barWidth);

            Console.CursorLeft = 0;
            Console.Write("[");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(new string('#', filled));
            Console.ResetColor();
            Console.Write(new string('-', barWidth - filled));
            Console.Write($"] {processed}/{total} ({fraction:P0}) — {TotalCount:N0} articles  ");
        }

        Console.WriteLine("\n");
        Console.WriteLine($"Total Articles : {TotalCount:N0}");
        Console.WriteLine($"with Abstract  : {AbstractCount:N0}");
        Console.WriteLine($"with Full Text : {FullTextCount:N0}");
        Console.WriteLine($"Abs & Ful-Txt  : {AbstractFullTextCount:N0}");
    }

    // Streams articles one by one using JsonTextReader — constant memory per file
    private IEnumerable<ArticleDTO> StreamJsonFile(string jsonPath)
    {
        using var stream = File.OpenRead(jsonPath);
        using var streamReader = new StreamReader(stream);
        using var jsonReader = new JsonTextReader(streamReader);

        var serializer = new JsonSerializer();
        if (!jsonReader.Read() || jsonReader.TokenType != JsonToken.StartArray)
            yield break;

        while (jsonReader.Read() && jsonReader.TokenType != JsonToken.EndArray)
        {
            var article = serializer.Deserialize<ArticleDTO>(jsonReader);
            if (article != null)
                yield return article;
        }
    }
}
