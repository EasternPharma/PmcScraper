using Newtonsoft.Json;

namespace PmcScraper.DTOs;

/// <summary>
/// JSON shape for a JATS <c>&lt;table-wrap&gt;</c> when stored inside
/// <see cref="ArticleDTO.Sections"/> as a serialized string.
/// </summary>
public class ArticleTableJsonDto
{
    [JsonProperty("TableName")]
    public string TableName { get; set; } = string.Empty;

    [JsonProperty("Description")]
    public string? Description { get; set; }

    /// <summary>All <c>&lt;th&gt;</c> cells in the table, document order (spelling matches requested JSON).</summary>
    [JsonProperty("Colmuns")]
    public List<string> Colmuns { get; set; } = new();

    [JsonProperty("Data")]
    public List<List<string>> Data { get; set; } = new();
}
