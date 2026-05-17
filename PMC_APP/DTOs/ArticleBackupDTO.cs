using Newtonsoft.Json;

namespace PMC_APP.DTOs;

/// <summary>
/// Represents an article as stored in the JSON backup files.
/// Property names map to the snake_case keys used in those files.
/// </summary>
public class ArticleBackupDTO
{
    [JsonProperty("pmc_id")]
    public int PmcId { get; set; }

    [JsonProperty("pm_id")]
    public double? PmId { get; set; }

    [JsonProperty("doi")]
    public string? Doi { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("category")]
    public string? Category { get; set; }

    [JsonProperty("journal")]
    public string? Journal { get; set; }

    [JsonProperty("publisher")]
    public string? Publisher { get; set; }

    [JsonProperty("volume")]
    public string? Volume { get; set; }

    [JsonProperty("issue")]
    public string? Issue { get; set; }

    [JsonProperty("issn")]
    public string? ISSN { get; set; }

    [JsonProperty("f_page")]
    public string? FPage { get; set; }

    [JsonProperty("l_page")]
    public string? LPage { get; set; }

    [JsonProperty("authors")]
    public List<string>? Authors { get; set; }

    [JsonProperty("publish_date")]
    public DateTime? PublishDate { get; set; }

    [JsonProperty("abstract_text")]
    public string? AbstractText { get; set; }

    [JsonProperty("keywords")]
    public List<string>? Keywords { get; set; }

    [JsonProperty("sections")]
    public Dictionary<string, string>? Sections { get; set; } = new Dictionary<string, string>();
}
