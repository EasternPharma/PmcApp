using Newtonsoft.Json;
using PMC_APP.DTOs;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace PMC_APP;

public class PmcArticleExecution : IDisposable
{
    private bool disposedValue;

    public FileInfo TarGzFile { get; set; }
    public string OutputDirectory { get; set; }
    public int ArticleSplitCount { get; set; }

    public PmcArticleExecution(string filePath, string outputDirectory, int articleSplitCount = 10000)
    {
        TarGzFile = new FileInfo(filePath);
        ArticleSplitCount = articleSplitCount;
        OutputDirectory = outputDirectory;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing) { }
            disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private string ExtractFileName(string key)
    {
        int idx = key.LastIndexOf('/');
        return idx >= 0 ? key.Substring(idx + 1) : key;
    }

    private void ProcessAndWriteBatch(
        List<(string name, byte[] data)> batch,
        string baseDir,
        string baseName,
        int partIndex)
    {
        var articles = new ArticleDTO?[batch.Count];

        Parallel.For(0, batch.Count, i =>
        {
            var (name, data) = batch[i];
            string fn = ExtractFileName(name);
            using var parser = new PmcArticleParser();
            articles[i] = parser.GetArticle(fn, data);
        });

        var valid = articles.Where(a => a != null).Select(a => a!).ToArray();
        string json = JsonConvert.SerializeObject(valid, Formatting.None);
        string outputFile = Path.Combine(baseDir, $"{baseName}_part{partIndex}.json");
        File.WriteAllText(outputFile, json);
        Console.WriteLine($"  [Part {partIndex}] {valid.Length} articles -> {outputFile}");
    }

    public void Execute()
    {
        Directory.CreateDirectory(OutputDirectory);
        string baseName = Path.GetFileNameWithoutExtension(
                          Path.GetFileNameWithoutExtension(TarGzFile.Name));
        string baseDir = Path.Combine(OutputDirectory, baseName);
        Directory.CreateDirectory(baseDir);

        int partIndex = 0;
        int totalProcessed = 0;
        var batch = new List<(string name, byte[] data)>(ArticleSplitCount);
        var sw = Stopwatch.StartNew();

        using var fs = File.OpenRead(TarGzFile.FullName);
        using var gzip = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            if (entry.EntryType is TarEntryType.Directory) continue;
            if (entry.DataStream == null) continue;

            using var ms = new MemoryStream();
            entry.DataStream.CopyTo(ms);
            batch.Add((entry.Name, ms.ToArray()));

            if (batch.Count >= ArticleSplitCount)
            {
                totalProcessed += batch.Count;
                ProcessAndWriteBatch(batch, baseDir, baseName, ++partIndex);
                batch.Clear();
                GC.Collect();
            }
        }

        if (batch.Count > 0)
        {
            totalProcessed += batch.Count;
            ProcessAndWriteBatch(batch, baseDir, baseName, ++partIndex);
            batch.Clear();
            GC.Collect();
        }

        sw.Stop();
        Console.WriteLine(
            $"Done: {totalProcessed} files in {partIndex} part(s), " +
            $"{sw.Elapsed.TotalSeconds:F3}s | {Environment.ProcessorCount} cores");
    }
}
}
}
}
}