namespace PmcScraper.DTOs;

public interface IPmcScraper : IDisposable
{
    string EnvironmentName { get; set; }
    string WorkerName { get; set; }
    Task<ArticleDTO?> GetArticleAsync(int pmcId, CancellationToken cancellationToken = default);
    Task<IEnumerable<ArticleDTO>> GetArticlesAsync(int[] pmcIds, int splitCount = 5, int restTimeMs = 0, bool showProgressBar = true, CancellationToken cancellationToken = default);
}