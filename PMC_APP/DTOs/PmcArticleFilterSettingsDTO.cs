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
    public string MongodbCollectionName { get; set; }

}
