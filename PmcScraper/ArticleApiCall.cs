using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using PmcScraper.DTOs;

namespace PmcScraper;

/// <summary>PMC Store HTTP client per <c>api_docs.md</c> (header <c>app-sec: bregulator</c>).</summary>
public sealed class ArticleApiCall : IDisposable
{
    private readonly HttpClient _httpClient;
    private bool _disposed;

    private static readonly JsonSerializerSettings ApiJsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        DateFormatString = "yyyy-MM-ddTHH:mm:ss"
    };

    public ArticleApiCall(string apiBaseUrl)
    {
        var baseUri = apiBaseUrl.TrimEnd('/') + "/";
        _httpClient = new HttpClient { BaseAddress = new Uri(baseUri) };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("app-sec", "bregulator");
    }

    public async Task<HealthResponseDto> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        var healthUri = new Uri(_httpClient.BaseAddress!, "/");
        using var response = await _httpClient.GetAsync(healthUri, cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(response, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<HealthResponseDto>(json, ApiJsonSettings)
               ?? throw new InvalidOperationException("Empty health response body.");
    }

    /// <summary>POST /articles/list — raw JSON array of PMC IDs.</summary>
    public async Task<InsertListResponseDto> InsertArticleListAsync(
        IReadOnlyList<int> pmcIds,
        CancellationToken cancellationToken = default)
    {
        var body = JsonConvert.SerializeObject(pmcIds.ToArray());
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("articles/list", content, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccess(response, cancellationToken, HttpStatusCode.Created).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<InsertListResponseDto>(json, ApiJsonSettings)
               ?? throw new InvalidOperationException("Empty insert list response body.");
    }

    /// <summary>POST /articles/list/upload — multipart field <c>file</c>. The stream is disposed with the request content after the call completes.</summary>
    public async Task<UploadListResponseDto> UploadArticleListAsync(
        Stream fileStream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        using var multipart = new MultipartFormDataContent();
        var streamContent = new StreamContent(fileStream, 81920);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        multipart.Add(streamContent, "file", fileName);

        using var response = await _httpClient.PostAsync("articles/list/upload", multipart, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccess(response, cancellationToken, HttpStatusCode.Created).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<UploadListResponseDto>(json, ApiJsonSettings)
               ?? throw new InvalidOperationException("Empty upload list response body.");
    }

    /// <summary>POST /articles/free — claim a batch for a worker.</summary>
    public async Task<IReadOnlyList<ArticleListDto>> ClaimFreeArticlesAsync(
        GetFreeArticleRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var body = JsonConvert.SerializeObject(request, ApiJsonSettings);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("articles/free", content, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccess(response, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<List<ArticleListDto>>(json, ApiJsonSettings)
               ?? throw new InvalidOperationException("Empty claim articles response body.");
    }

    /// <summary>POST /articles/update — submit scrape results.</summary>
    public async Task<ScrapeArticleResponseDto> SubmitScrapeResultsAsync(
        ScrapeArticleRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var body = JsonConvert.SerializeObject(request, ApiJsonSettings);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("articles/update", content, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccess(response, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<ScrapeArticleResponseDto>(json, ApiJsonSettings)
               ?? throw new InvalidOperationException("Empty update response body.");
    }

    /// <summary>GET /articles/statics — aggregate counters.</summary>
    public async Task<ArticleStaticsDto> GetArticleStatisticsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("articles/statics", cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccess(response, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<ArticleStaticsDto>(json, ApiJsonSettings)
               ?? throw new InvalidOperationException("Empty statistics response body.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _httpClient.Dispose();
        _disposed = true;
    }

    private static async Task EnsureSuccess(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        HttpStatusCode? expected = null)
    {
        if (expected.HasValue)
        {
            if (response.StatusCode == expected.Value)
                return;
        }
        else if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            $"PMC Store API returned {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
    }
}
