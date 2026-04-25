using OpenQA.Selenium;
using OpenQA.Selenium.Firefox;
using PmcScraper;
using PmcScraper.DTOs;
using System.Text.Json;

Dictionary<string, string> bases = new Dictionary<string, string>();
bases["pmc"] = "https://pmc.bregulator.com";
bases["local"] = "http://localhost:8000";
string EnvBase = "pmc";
string WorkerName = "colab1";

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
    Console.WriteLine($"\nDone — {succeeded} succeeded, {failed} failed out of {htmlFiles.Count()} files.");
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
async Task TestBatch(SeleniumHeaderDTO seleniumHeaders, string envBase)
{
    List<int> ids = new List<int>();
    using (var apiCall = new ArticleApiCall(bases[envBase]))
    {
        var health = await apiCall.HealthCheckAsync();
        Console.WriteLine($"Health: {health.Status}");
        var freeArticles = await apiCall.ClaimFreeArticlesAsync(new GetFreeArticleRequestDto { User = WorkerName, BatchSize = 10 });
        ids = freeArticles.Select(x => x.PmcId).ToList();
        Console.WriteLine($"Claimed {ids.Count} free articles.");
    }

    if (ids.Count > 0)
    {
        using var idBatchExtractor = new ArticleExtractor(delayTime: 300, seleniumHeaders.Headers, seleniumHeaders.Cookies);
        var articles = await idBatchExtractor.ExtractDataFromIdsAsync(ids);
        Console.WriteLine($"Extracted {articles.Count} articles from {ids.Count} PMC IDs.");
        //await idBatchExtractor.SaveToJsonAsync(articles, new FileInfo(@"/home/behzad/pmc_json/articles_temp.json"));

        var scrapedIds = articles.Select(a => a.PmcId).ToHashSet();
        var successDict = scrapedIds.ToDictionary(id => id, _ => true);
        var errorDict = ids
            .Where(id => !scrapedIds.Contains(id))
            .ToDictionary(id => id, _ => "Extraction failed");
        List<int>? _FullTextIds = articles.Where(x => x.Sections != null && x.Sections.Count > 0).Select(y => y.PmcId).ToList();

        var updateItems = articles.Select(ArticleUpdateItemDto.FromArticleDTO).ToList();

        using (var apiCall = new ArticleApiCall(bases[envBase]))
        {
            var health1 = await apiCall.HealthCheckAsync();
            Console.WriteLine($"Health: {health1.Status}");
            Console.WriteLine($"Submitting {updateItems.Count} articles...");
            var response = await apiCall.SubmitScrapeResultsAsync(new ScrapeArticleRequestDto
            {
                User = WorkerName,
                Articles = updateItems,
                SuccessDict = successDict,
                ErrorDict = errorDict,
                FullTextIds = _FullTextIds.Count() > 0 ? _FullTextIds : null
            });
            Console.WriteLine($"Submit result — success: {response.Success}" +
                (response.Error != null ? $", error: {response.Error}" : ""));
        }
    }

}


async Task<SeleniumHeaderDTO> test_browser()
{
    SeleniumHeaderDTO seleniumHeaders = new SeleniumHeaderDTO();
    var options = new FirefoxOptions();

    // Silent / no UI
    options.AddArgument("--headless");

    options.AddArgument("--window-size=1920,1080");

    // When Firefox is not installed, Selenium Manager can provision a managed browser.
    options.BrowserVersion = "stable";
    using var driver = new FirefoxDriver(options);

    driver.Navigate().GoToUrl("https://pmc.ncbi.nlm.nih.gov/");

    Thread.Sleep(5000);

    // Cookies
    var cookies = driver.Manage().Cookies.AllCookies;
    var cookies_str = driver.Manage().Cookies.ToString();

    var cookieDict = new Dictionary<string, string>();

    foreach (var c in cookies)
    {
        seleniumHeaders.Cookies.Add(c.Name, c.Value);
    }
    seleniumHeaders.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
    seleniumHeaders.Headers.Add("Cache-Control", "max-age=0");
    seleniumHeaders.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:138.0) Gecko/20100101 Firefox/138.0");
    return seleniumHeaders;
}

//var seleniumHeaders = await test_browser();
//Console.WriteLine(
//        JsonSerializer.Serialize(seleniumHeaders,
//        new JsonSerializerOptions { WriteIndented = true })
//    );
for (int k = 0; k < 100; k++)
{
    SeleniumHeaderDTO SeleniumHeaders = await test_browser();
    Console.WriteLine(
            JsonSerializer.Serialize(SeleniumHeaders,
            new JsonSerializerOptions { WriteIndented = true })
        );
    for (var i = 0; i < 20; i++)
    {
        await TestBatch(SeleniumHeaders, EnvBase);
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("\n______________________________\n\n");
        Console.ResetColor();
    }
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
