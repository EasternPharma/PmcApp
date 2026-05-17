using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PMC_APP.DTOs;

public enum PmcArticleFilterOutputTypes
{
    JsonFile =1,
    Mongodb = 2
}

public class PmcArticleFilterSettingsDTO
{
    public string JsonFilesDirectoryPath { get; set; }
    public string OutputDirectoryPath { get; set; }
    public PmcArticleFilterOutputTypes OutputType { get; set; }
    public string ExclusionExcelPath { get; set; }

    public string MongodbHost { get; set; }
    public int MongodbPort { get; set; }
    public string MongodbDatabaseName { get; set; }
    public string MongodbInputCollectionName { get; set; }
    public string MongodbArticleListCollectionName { get; set; }
    public string MongodbOutputCollectionName { get; set; }

    public string? IngredientKeywordsFilePath { get; set; }

    /// <summary>
    /// Output path for a plain-text file containing one PMC ID per line.
    /// Used by task 11 (GetIngredientArticleIdsAsync).
    /// </summary>
    public string? OutputTxtFilePath { get; set; }

    /// <summary>
    /// When true, the constructor skips collection creation, index setup, and
    /// exclusion keyword loading. Use for read-only tasks (e.g. task 11).
    /// </summary>
    public bool ReadOnlyMode { get; set; } = false;
}
