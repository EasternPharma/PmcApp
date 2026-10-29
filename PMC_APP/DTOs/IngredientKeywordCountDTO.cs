namespace PMC_APP.DTOs;

/// <summary>An array entry avoids using ingredient names containing '.' or '$' as BSON keys.</summary>
public sealed class IngredientKeywordCountDTO
{
    public string Keyword { get; set; } = string.Empty;
    public long Count { get; set; }
}
