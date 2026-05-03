using PmcScraper;
using PmcScraper.DTOs;
using System.Net;
using System.Text.Json;

Dictionary<string, string> bases = new Dictionary<string, string>();
bases["pmc"] = "https://pmc.bregulator.com";
bases["local"] = "http://localhost:8000";
bases["office"] = "http://localhost:1368";
string envBase = args.Length > 0 ? args[0].ToLowerInvariant() : "pmc";
string workerName = args.Length > 1 ? args[1] : "colab1";

if (!bases.ContainsKey(envBase))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"[FATAL] Invalid envBase '{envBase}'. Valid values: {string.Join(", ", bases.Keys)}");
    Console.Error.WriteLine("Usage: dotnet run -- <envBase> <workerName>");
    Console.ResetColor();
    return;
}

Console.WriteLine($"\nWorker: {workerName}\nEnv Base: {envBase}\n");

Console.WriteLine($"Delay: {TicketManager._delay}");

// Pool a single handler so that repeated FetchPmcHeadersAsync calls do not exhaust
// ephemeral sockets on Google Colab (the previous code created a new handler per
// iteration which leads to TIME_WAIT pile-up and SocketException after a few hundred runs).
// CookieContainer is fixed for the lifetime of the handler — HttpClientHandler forbids
// reassigning it after the first request. We clear it at the start of each warmup instead.
var sharedCookieContainer = new CookieContainer();
var sharedHeaderHandler = new HttpClientHandler
{
    AutomaticDecompression   = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
    AllowAutoRedirect        = true,
    UseCookies               = true,
    CookieContainer          = sharedCookieContainer,
    MaxAutomaticRedirections = 10,
};

List<string> userAgentPool = new List<string>
{
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36",
    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
};
var userAgentRandom = new Random();

// Warms up against PMC to harvest fresh anti-bot cookies (incap_ses_*, _abck, bm_sz, etc.)
// and returns the header set the scraper should reuse for its article requests.
async Task<SeleniumHeaderDTO> FetchPmcHeadersAsync()
{
    var dto = new SeleniumHeaderDTO();

    // Reset the shared cookie jar in-place (we cannot reassign CookieContainer after
    // the handler has sent its first request — that throws InvalidOperationException).
    var cookieContainer = sharedCookieContainer;
    foreach (Cookie c in cookieContainer.GetAllCookies())
        c.Expired = true;

    // The handler is shared but a per-call HttpClient is cheap; do NOT dispose it
    // (disposing it would dispose the shared handler too).
    var http = new HttpClient(sharedHeaderHandler, disposeHandler: false)
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    string userAgent = userAgentPool[userAgentRandom.Next(0, userAgentPool.Count)];

    void AddBrowserHeaders(HttpRequestMessage req, string? referer)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        req.Headers.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
        req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        req.Headers.TryAddWithoutValidation("DNT", "1");
        req.Headers.TryAddWithoutValidation("Connection", "keep-alive");
        req.Headers.TryAddWithoutValidation("sec-ch-ua",
            "\"Chromium\";v=\"124\", \"Google Chrome\";v=\"124\", \"Not-A.Brand\";v=\"99\"");
        req.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        req.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", referer == null ? "none" : "same-origin");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        if (referer != null)
            req.Headers.TryAddWithoutValidation("Referer", referer);
    }

    const string pmcHome   = "https://pmc.ncbi.nlm.nih.gov/";
    const string pmcSearch = "https://pmc.ncbi.nlm.nih.gov/?term=cancer";
    Console.WriteLine($"Fetching PMC headers from {pmcHome} ...");

    try
    {
        // First hit: home page (Sec-Fetch-Site: none, like a typed URL)
        using (var req1 = new HttpRequestMessage(HttpMethod.Get, pmcHome))
        {
            AddBrowserHeaders(req1, referer: null);
            using var resp1 = await http.SendAsync(req1, HttpCompletionOption.ResponseHeadersRead);
            Console.WriteLine($"PMC home responded: {(int)resp1.StatusCode} {resp1.ReasonPhrase}");
            // Drain body so the connection can be reused
            _ = await resp1.Content.ReadAsByteArrayAsync();
        }

        // Second hit: a search-like URL with home as Referer; this triggers PMC to
        // upgrade the cookie jar (anti-bot tokens often only land on the second request).
        using (var req2 = new HttpRequestMessage(HttpMethod.Get, pmcSearch))
        {
            AddBrowserHeaders(req2, referer: pmcHome);
            using var resp2 = await http.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead);
            Console.WriteLine($"PMC search responded: {(int)resp2.StatusCode} {resp2.ReasonPhrase}");
            _ = await resp2.Content.ReadAsByteArrayAsync();
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"[WARN] PMC warmup failed: {ex.Message} — proceeding with whatever cookies we got.");
        Console.ResetColor();
    }

    foreach (Cookie c in cookieContainer.GetAllCookies())
        dto.Cookies[c.Name] = c.Value;

    // The headers the scraper will replay on every article fetch. ApplyBrowserHeaders
    // in ArticleExtractor adds the rest (Sec-Fetch-*, sec-ch-ua, Referer, Encoding).
    dto.Headers["User-Agent"]      = userAgent;
    dto.Headers["Accept"]          =
        "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7";
    dto.Headers["Accept-Language"] = "en-US,en;q=0.9";
    dto.Headers["Cache-Control"]   = "max-age=0";

    Console.WriteLine($"Harvested {dto.Cookies.Count} cookies from PMC.");
    return dto;
}

#pragma warning disable CS8321
async Task TestFromFilesAsync()
{
    string dirPath = @"E:\pmc_txt";

    if (!Directory.Exists(dirPath))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"[FATAL] Directory not found: {dirPath}");
        Console.ResetColor();
        return;
    }

    var htmlFiles = Directory.GetFiles(dirPath, "*.html").Select(x => new FileInfo(x)).ToList();
    int succeeded = 0, failed = 0;
    foreach (var file in htmlFiles)
    {
        using var extractor = new ArticleExtractor();
        Console.WriteLine($"Processing file: {file}");
        try
        {
            await extractor.ExtractDataFromFileAsync(file);
            succeeded++;
        }
        catch (Exception ex)
        {
            failed++;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[ERROR] Failed to process: {file}");
            Console.Error.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
            Console.ResetColor();
        }
    }
    Console.WriteLine($"\nDone — {succeeded} succeeded, {failed} failed out of {htmlFiles.Count} files.");
}

async Task TestFromUrlAsync()
{
    try
    {
        using var scraper = new ArticleExtractor();
        await scraper.ExtractDataFromUrlAsync(2000461, "https://pmc.ncbi.nlm.nih.gov/articles/PMC2000461/");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(ex.Message);
        Console.ResetColor();
    }
}
#pragma warning restore CS8321

//await TestFromUrlAsync();
//await TestFromFilesAsync();
async Task<int> TestBatch(SeleniumHeaderDTO pmcHeaders, string currentEnvBase)
{
    List<int> ids = new List<int>();
    using (var apiCall = new ArticleApiCall(bases[currentEnvBase]))
    {
        var health = await apiCall.HealthCheckAsync();
        Console.WriteLine($"Health: {health.Status}");
        var freeArticles = await apiCall.ClaimFreeArticlesAsync(new GetFreeArticleRequestDto { User = workerName, BatchSize = 10 });
        ids = freeArticles.Select(x => x.PmcId).ToList();
        Console.WriteLine($"Claimed {ids.Count} free articles.");
    }

    if (ids.Count > 0)
    {
        using var idBatchExtractor = new ArticleExtractor(pmcHeaders.Headers, pmcHeaders.Cookies);
        var articles = await idBatchExtractor.ExtractDataFromIdsAsync(ids);

        // Articles returned by the extractor are guaranteed to have a Title now
        // (failed fetches return null and are filtered out). Anything in `ids` that
        // is not in this list is considered failed and goes into errorDict.
        var successIds  = articles.Select(a => a.PmcId).ToHashSet();
        var successDict = successIds.ToDictionary(id => id, _ => true);
        var errorDict   = ids
            .Where(id => !successIds.Contains(id))
            .ToDictionary(id => id, _ => "Extraction failed");

        Console.WriteLine($"Extracted {articles.Count} / {ids.Count} (errors: {errorDict.Count})");
        var fullTextIds = articles
            .Where(x => x.Sections != null && x.Sections.Count > 0)
            .Select(y => y.PmcId)
            .ToList();

        var updateItems = articles.Select(ArticleUpdateItemDto.FromArticleDTO).ToList();

        using (var apiCall = new ArticleApiCall(bases[currentEnvBase]))
        {
            var health1 = await apiCall.HealthCheckAsync();
            Console.WriteLine($"Health: {health1.Status}");
            Console.WriteLine($"Submitting {updateItems.Count} articles...");
            var response = await apiCall.SubmitScrapeResultsAsync(new ScrapeArticleRequestDto
            {
                User = workerName,
                Articles = updateItems,
                SuccessDict = successDict,
                ErrorDict = errorDict,
                FullTextIds = fullTextIds.Count > 0 ? fullTextIds : null
            });
            Console.WriteLine($"Submit result — success: {response.Success}" +
                (response.Error != null ? $", error: {response.Error}" : ""));
        }

        return ids.Count;
    }

    return 0;
}

long totalProcessedCount = 0;
DateTime overallStartTime = DateTime.Now;
for (int k = 0; k < 1500; k++)
{
    SeleniumHeaderDTO pmcHeaders = await FetchPmcHeadersAsync();
    Console.WriteLine(
        JsonSerializer.Serialize(pmcHeaders,
        new JsonSerializerOptions { WriteIndented = true })
    );
    for (var i = 0; i < 5; i++)
    {
        DateTime batchStartTime = DateTime.Now;
        var processedCount = await TestBatch(pmcHeaders, envBase);
        totalProcessedCount += processedCount;
        var lastBatchDurationSeconds = (DateTime.Now - batchStartTime).TotalSeconds;
        var totalDurationMinutes = (DateTime.Now - overallStartTime).TotalMinutes;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n\n██████████████████████\n");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"Batch completed in: {lastBatchDurationSeconds:F2} seconds");
        Console.WriteLine($"Processed this batch: {processedCount}");
        Console.WriteLine($"Total processed: {totalProcessedCount} (elapsed: {totalDurationMinutes:F2} minutes)");
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("\n██████████████████████\n\n");
        Console.ForegroundColor = ConsoleColor.White;
        await Task.Delay(new Random().Next(1000, 3000));
    }
    await Task.Delay(1000);
}


// List<int> ids =
// [
//     2928517, 1131548, 2136977, 3874642, 5599854,
//     6623846, 1149676, 9744321, 12315489, 1161522
// ];

// using var idBatchExtractor = new ArticleExtractor(delayTime: 300);
// var articles = await idBatchExtractor.ExtractDataFromIdsAsync(ids);

// Console.WriteLine($"Extracted {articles.Count} articles from {ids.Count} PMC IDs.");
// await idBatchExtractor.SaveToJsonAsync(articles, new FileInfo(@"/home/behzad/pmc_json/articles_temp.json"));
