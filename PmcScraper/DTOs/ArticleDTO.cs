namespace PmcScraper.DTOs;

public class ArticleDTO
{
    public int PmcId { get; set; }
    public int? PmId { get; set; }
    public string? Doi { get; set; }
    public string? Title { get; set; }
    public string? Category { get; set; }
    public string? Journal { get; set; }
    public string? Publisher { get; set; }
    public string? Volume { get; set; }
    public string? Issue { get; set; }
    public string? ISSN { get; set; }
    public string? FPage { get; set; }
    public string? LPage { get; set; }
    public List<string>? Authors { get; set; }
    public DateTime? PublishDate { get; set; }
    public string? AbstractText { get; set; }
    public List<string>? Keywords { get; set; }
    public Dictionary<string, string>? Sections { get; set; } = new Dictionary<string, string>();
}