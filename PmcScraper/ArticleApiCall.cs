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

    private const int MaxRetries = 5;

    private static readonly JsonSerializerSettings ApiJsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        DateFormatString = "yyyy-MM-ddTHH:mm:ss"
    };

    public ArticleApiCall(string apiBaseUrl)
    {
        var baseUri = apiBaseUrl.TrimEnd('/') + "/";
        // 5s was way too aggressive for cross-region calls (e.g. Colab US → IR backend),
        // causing constant timeouts and retry storms. 60s is a safer ceiling.
        _httpClient = new HttpClient { BaseAddress = new Uri(baseUri), Timeout = TimeSpan.FromSeconds(60) };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("app-sec", "bregulator");
    }

    public Task<HealthResponseDto> HealthCheckAsync(CancellationToken cancellationToken = default)
        => RetryAsync(async () =>
        {
            var healthUri = new Uri(_httpClient.BaseAddress!, "/");
            using var response = await _httpClient.GetAsync(healthUri, cancellationToken).ConfigureAwait(false);
            await EnsureSuccess(response, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<HealthResponseDto>(json, ApiJsonSettings)
                   ?? throw new InvalidOperationException("Empty health response body.");
        }, cancellationToken);

    /// <summary>POST /articles/list — raw JSON array of PMC IDs.</summary>
    public Task<InsertListResponseDto> InsertArticleListAsync(
        IReadOnlyList<int> pmcIds,
        CancellationToken cancellationToken = default)
        => RetryAsync(async () =>
        {
            var body = JsonConvert.SerializeObject(pmcIds.ToArray());
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync("articles/list", content, cancellationToken)
                .ConfigureAwait(false);
            await EnsureSuccess(response, cancellationToken, HttpStatusCode.Created).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<InsertListResponseDto>(json, ApiJsonSettings)
                   ?? throw new InvalidOperationException("Empty insert list response body.");
        }, cancellationToken);

    /// <summary>POST /articles/list/upload — multipart field <c>file</c>. Requires a seekable stream so it can be rewound on retry.</summary>
    public Task<UploadListResponseDto> UploadArticleListAsync(
        Stream fileStream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        long? initialPosition = fileStream.CanSeek ? fileStream.Position : null;
        return RetryAsync(async () =>
        {
            if (initialPosition.HasValue)
                fileStream.Position = initialPosition.Value;

            using var multipart = new MultipartFormDataContent();
            // Wrap the stream so that StreamContent does not dispose it between retries.
            var streamContent = new StreamContent(new LeaveOpenStreamWrapper(fileStream), 81920);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            multipart.Add(streamContent, "file", fileName);

            using var response = await _httpClient.PostAsync("articles/list/upload", multipart, cancellationToken)
                .ConfigureAwait(false);
            await EnsureSuccess(response, cancellationToken, HttpStatusCode.Created).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<UploadListResponseDto>(json, ApiJsonSettings)
                   ?? throw new InvalidOperationException("Empty upload list response body.");
        }, cancellationToken);
    }

    /// <summary>POST /articles/free — claim a batch for a worker.</summary>
    public Task<IReadOnlyList<ArticleListDto>> ClaimFreeArticlesAsync(
        GetFreeArticleRequestDto request,
        CancellationToken cancellationToken = default)
        => RetryAsync<IReadOnlyList<ArticleListDto>>(async () =>
        {
            var body = JsonConvert.SerializeObject(request, ApiJsonSettings);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync("articles/free", content, cancellationToken)
                .ConfigureAwait(false);
            await EnsureSuccess(response, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<List<ArticleListDto>>(json, ApiJsonSettings)
                   ?? throw new InvalidOperationException("Empty claim articles response body.");
        }, cancellationToken);

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
    public Task<ArticleStaticsDto> GetArticleStatisticsAsync(CancellationToken cancellationToken = default)
        => RetryAsync(async () =>
        {
            using var response = await _httpClient.GetAsync("articles/statics", cancellationToken)
                .ConfigureAwait(false);
            await EnsureSuccess(response, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<ArticleStaticsDto>(json, ApiJsonSettings)
                   ?? throw new InvalidOperationException("Empty statistics response body.");
        }, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
            return;
        _httpClient.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Retries <paramref name="operation"/> up to <see cref="MaxRetries"/> times.
    /// Any exception (including timeouts) triggers a retry unless the caller's
    /// <paramref name="cancellationToken"/> has been cancelled, in which case the
    /// exception propagates immediately. After all attempts are exhausted the last
    /// exception is re-thrown.
    /// </summary>
    private static async Task<T> RetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
                // Exponential backoff with jitter: 0.5s, 1s, 2s, 4s. Avoids hammering
                // a sleeping/overloaded backend with 5 requests in a row.
                if (attempt < MaxRetries - 1)
                {
                    int delayMs = 500 * (int)Math.Pow(2, attempt) + Random.Shared.Next(0, 250);
                    try { await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                }
            }
        }
        throw lastException!;
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

    /// <summary>Wraps a stream without taking ownership — <see cref="Dispose"/> is a no-op so that
    /// <see cref="StreamContent"/> cannot close the underlying stream between retry attempts.</summary>
    private sealed class LeaveOpenStreamWrapper(Stream inner) : Stream
    {
        public override bool CanRead  => inner.CanRead;
        public override bool CanSeek  => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length   => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void  Flush()                                            => inner.Flush();
        public override int   Read(byte[] buffer, int offset, int count)         => inner.Read(buffer, offset, count);
        public override long  Seek(long offset, SeekOrigin origin)               => inner.Seek(offset, origin);
        public override void  SetLength(long value)                              => inner.SetLength(value);
        public override void  Write(byte[] buffer, int offset, int count)        => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { /* intentionally leave inner stream open */ }
    }
}
