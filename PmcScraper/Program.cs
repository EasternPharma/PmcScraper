using PmcScraper;
using PmcScraper.DTOs;
using System.Net;
using System.Text.Json;

Dictionary<string, string> bases = new Dictionary<string, string>();
bases["pmc"] = "https://pmc.bregulator.com";
bases["local"] = "http://localhost:8000";
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

// Fetch cookies and a plausible User-Agent from PMC without any browser dependency.
// This replaces the old Selenium test_browser(); HttpClient works on any OS / container.
async Task<SeleniumHeaderDTO> FetchPmcHeadersAsync()
{
    var dto = new SeleniumHeaderDTO();

    var cookieContainer = new CookieContainer();
    using var handler = new HttpClientHandler
    {
        CookieContainer = cookieContainer,
        AllowAutoRedirect = true,
        UseCookies = true,
    };
    using var http = new HttpClient(handler);

    const string userAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0.0.0 Safari/537.36";

    http.DefaultRequestHeaders.Add("User-Agent", userAgent);
    http.DefaultRequestHeaders.Add("Accept",
        "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
    http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

    const string pmcHome = "https://pmc.ncbi.nlm.nih.gov/";
    Console.WriteLine($"Fetching PMC headers from {pmcHome} ...");

    HttpResponseMessage response;
    try
    {
        response = await http.GetAsync(pmcHome);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"[FATAL] Could not reach {pmcHome}: {ex.Message}");
        Console.ResetColor();
        throw;
    }

    Console.WriteLine($"PMC responded: {(int)response.StatusCode} {response.ReasonPhrase}");

    // Collect all cookies set by PMC (including after redirects).
    foreach (Cookie c in cookieContainer.GetAllCookies())
        dto.Cookies.TryAdd(c.Name, c.Value);

    dto.Headers["User-Agent"] = userAgent;
    dto.Headers["Accept"] =
        "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7";
    dto.Headers["Accept-Language"] = "en-US,en;q=0.9";
    dto.Headers["Cache-Control"] = "max-age=0";

    return dto;
}

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

//await TestFromUrlAsync();
//await TestFromFilesAsync();
async Task TestBatch(SeleniumHeaderDTO pmcHeaders, string currentEnvBase)
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
        using var idBatchExtractor = new ArticleExtractor(delayTime: 300, pmcHeaders.Headers, pmcHeaders.Cookies);
        var articles = await idBatchExtractor.ExtractDataFromIdsAsync(ids);
        Console.WriteLine($"Extracted {articles.Count} articles from {ids.Count} PMC IDs.");

        var scrapedIds = articles.Select(a => a.PmcId).ToHashSet();
        var successDict = scrapedIds.ToDictionary(id => id, _ => true);
        var errorDict = ids
            .Where(id => !scrapedIds.Contains(id))
            .ToDictionary(id => id, _ => "Extraction failed");
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
    }
}

for (int k = 0; k < 100; k++)
{
    SeleniumHeaderDTO pmcHeaders = await FetchPmcHeadersAsync();
    Console.WriteLine(
        JsonSerializer.Serialize(pmcHeaders,
        new JsonSerializerOptions { WriteIndented = true })
    );
    for (var i = 0; i < 5; i++)
    {
        await TestBatch(pmcHeaders, envBase);
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("\n______________________________\n\n");
        Console.ForegroundColor = ConsoleColor.White;
        await Task.Delay(1000);
    }
    await Task.Delay(5000);
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
