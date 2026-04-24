using Newtonsoft.Json;

namespace PmcScraper.DTOs;

public class HealthResponseDto
{
    [JsonProperty("status")]
    public string Status { get; set; } = "";
}

public class InsertListResponseDto
{
    [JsonProperty("inserted")]
    public int Inserted { get; set; }
}

public class UploadListResponseDto
{
    [JsonProperty("filename")]
    public string Filename { get; set; } = "";

    [JsonProperty("inserted")]
    public int Inserted { get; set; }
}

public class ArticleListDto
{
    [JsonProperty("pmc_id")]
    public int PmcId { get; set; }

    [JsonProperty("scraped")]
    public bool Scraped { get; set; }

    [JsonProperty("scraped_at")]
    public DateTime? ScrapedAt { get; set; }

    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("success_at")]
    public DateTime? SuccessAt { get; set; }

    [JsonProperty("error")]
    public string? Error { get; set; }

    [JsonProperty("error_at")]
    public DateTime? ErrorAt { get; set; }

    [JsonProperty("user")]
    public string? User { get; set; }

    [JsonProperty("full_text")]
    public bool FullText { get; set; }
}

public class GetFreeArticleRequestDto
{
    [JsonProperty("user")]
    public string User { get; set; } = "";

    [JsonProperty("batch_size")]
    public int BatchSize { get; set; } = 100;
}

/// <summary>Single article in POST /articles/update body (snake_case JSON).</summary>
public class ArticleUpdateItemDto
{
    [JsonProperty("pmc_id")]
    public int PmcId { get; set; }

    [JsonProperty("pm_id")]
    public int? PmId { get; set; }

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
    public string? Issn { get; set; }

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
    public Dictionary<string, string>? Sections { get; set; }

    public static ArticleUpdateItemDto FromArticleDTO(ArticleDTO a) => new()
    {
        PmcId = a.PmcId,
        PmId = a.PmId,
        Doi = a.Doi,
        Title = a.Title,
        Category = a.Category,
        Journal = a.Journal,
        Publisher = a.Publisher,
        Volume = a.Volume,
        Issue = a.Issue,
        Issn = a.ISSN,
        FPage = a.FPage,
        LPage = a.LPage,
        Authors = a.Authors,
        PublishDate = a.PublishDate,
        AbstractText = a.AbstractText,
        Keywords = a.Keywords,
        Sections = a.Sections is { Count: > 0 } ? a.Sections : null
    };
}

public class ScrapeArticleRequestDto
{
    [JsonProperty("user")]
    public string User { get; set; } = "";

    [JsonProperty("articles")]
    public List<ArticleUpdateItemDto>? Articles { get; set; }

    [JsonProperty("full_text_ids")]
    public List<int>? FullTextIds { get; set; }

    [JsonProperty("success_dict")]
    public Dictionary<int, bool> SuccessDict { get; set; } = new();

    [JsonProperty("error_dict")]
    public Dictionary<int, string> ErrorDict { get; set; } = new();
}

public class ScrapeArticleResponseDto
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("error")]
    public string? Error { get; set; }
}

public class ArticleStaticsDto
{
    [JsonProperty("Total_List")]
    public int TotalList { get; set; }

    [JsonProperty("Total_Scraped")]
    public int TotalScraped { get; set; }

    [JsonProperty("Total_Success")]
    public int TotalSuccess { get; set; }

    [JsonProperty("Total_Full_Text")]
    public int TotalFullText { get; set; }

    [JsonProperty("Total_Error")]
    public int TotalError { get; set; }
}
