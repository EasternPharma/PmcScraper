using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Firefox;
using PmcScraper;
using PmcScraper.DTOs;
using System.Runtime.InteropServices;
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
    Console.Error.WriteLine("Usage: dotnet run -- <envBase> <workerName> [firefox|chrome]");
    Console.ResetColor();
    return;
}

// Linux/Colab: Firefox often exits with status 255 as root; Chromium + --no-sandbox is the reliable path.
string browserKind = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2])
    ? args[2].Trim().ToLowerInvariant()
    : (Environment.GetEnvironmentVariable("PMC_BROWSER")?.Trim().ToLowerInvariant()
       ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "chrome" : "firefox"));

if (browserKind is not ("firefox" or "chrome"))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"[FATAL] Invalid browser '{browserKind}'. Use firefox or chrome.");
    Console.Error.WriteLine("Usage: dotnet run -- <envBase> <workerName> [firefox|chrome]");
    Console.Error.WriteLine("Or set PMC_BROWSER=firefox|chrome");
    Console.ResetColor();
    return;
}

Console.WriteLine($"\nWorker: {workerName}\nEnv Base: {envBase}\nBrowser: {browserKind}\n");

static string? ResolveFirefoxBinaryPath()
{
    foreach (var key in new[] { "PMC_FIREFOX_BIN", "FIREFOX_BIN", "MOZ_FIREFOX_BIN" })
    {
        var fromEnv = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;
    }

    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        foreach (var candidate in new[]
                 {
                     "/usr/bin/firefox",
                     "/usr/bin/firefox-esr",
                     "/usr/local/bin/firefox",
                     "/snap/bin/firefox",
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }
    }
    else
    {
        foreach (var candidate in new[]
                 {
                     @"C:\Program Files\Mozilla Firefox\firefox.exe",
                     @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe",
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }
    }

    return null;
}

// Colab/Docker often run as root; Firefox can exit 255 unless sandboxing is relaxed for headless.
static void PrepareFirefoxForLinuxContainers(FirefoxOptions options)
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        return;

    // Inherited by the Firefox child process.
    Environment.SetEnvironmentVariable("MOZ_HEADLESS", "1");
    Environment.SetEnvironmentVariable("MOZ_DISABLE_CONTENT_SANDBOX", "1");
    Environment.SetEnvironmentVariable("MOZ_DISABLE_GMP_SANDBOX", "1");
    Environment.SetEnvironmentVariable("MOZ_FORCE_DISABLE_SANDBOX", "1");

    options.SetPreference("security.sandbox.content.level", 0);
    options.SetPreference("layers.acceleration.disabled", true);
}

static string? ResolveChromeBinaryPath()
{
    foreach (var key in new[] { "PMC_CHROME_BIN", "CHROME_BIN", "GOOGLE_CHROME_BIN" })
    {
        var fromEnv = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;
    }

    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        foreach (var candidate in new[]
                 {
                     "/usr/bin/google-chrome-stable",
                     "/usr/bin/google-chrome",
                     "/usr/bin/chromium",
                     "/usr/bin/chromium-browser",
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }
    }
    else
    {
        foreach (var candidate in new[]
                 {
                     @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                     @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }
    }

    return null;
}

static void ApplyChromeHeadlessWindow(ChromeOptions options)
{
    options.AddArgument("--headless=new");
    options.AddArgument("--window-size=1920,1080");
}

// Colab/Docker run as root; Chromium needs these flags or the renderer is killed.
static void PrepareChromeForLinuxContainers(ChromeOptions options)
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        return;

    ApplyChromeHeadlessWindow(options);
    options.AddArgument("--no-sandbox");
    options.AddArgument("--disable-dev-shm-usage");
    options.AddArgument("--disable-gpu");
    options.AddArgument("--disable-software-rasterizer");
}

static void FillHeadersFromDriver(IWebDriver driver, SeleniumHeaderDTO dto)
{
    foreach (var c in driver.Manage().Cookies.AllCookies)
        dto.Cookies.Add(c.Name, c.Value);

    dto.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
    dto.Headers.Add("Cache-Control", "max-age=0");

    string userAgent;
    try
    {
        var ua = ((IJavaScriptExecutor)driver).ExecuteScript("return navigator.userAgent;");
        userAgent = ua as string ?? "Mozilla/5.0 (compatible; PmcScraper/1.0)";
    }
    catch
    {
        userAgent = "Mozilla/5.0 (compatible; PmcScraper/1.0)";
    }

    if (string.IsNullOrWhiteSpace(userAgent))
        userAgent = "Mozilla/5.0 (compatible; PmcScraper/1.0)";

    dto.Headers.Add("User-Agent", userAgent);
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
        var freeArticles = await apiCall.ClaimFreeArticlesAsync(new GetFreeArticleRequestDto { User = workerName, BatchSize = 10 });
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
                User = workerName,
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


async Task<SeleniumHeaderDTO> test_browser(string browserKind)
{
    if (browserKind == "chrome")
    {
        var seleniumHeaders = new SeleniumHeaderDTO();
        var options = new ChromeOptions();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            PrepareChromeForLinuxContainers(options);
        else
            ApplyChromeHeadlessWindow(options);

        var chromeBin = ResolveChromeBinaryPath();
        if (chromeBin != null)
            options.BinaryLocation = chromeBin;

        ChromeDriver driver;
        try
        {
            driver = new ChromeDriver(options);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[FATAL] Could not start Chrome / ChromeDriver: {ex.Message}");
            Console.Error.WriteLine("On Colab, install Chromium, for example:");
            Console.Error.WriteLine("  !apt-get update -qq && apt-get install -y chromium-browser");
            Console.Error.WriteLine("Or: apt-get install -y chromium (on newer Debian/Ubuntu).");
            Console.Error.WriteLine("Or set PMC_CHROME_BIN to the chrome/chromium executable.");
            Console.Error.WriteLine("On Linux you can use Firefox instead: dotnet run -- <envBase> <worker> firefox");
            Console.ResetColor();
            throw;
        }

        using (driver)
        {
            driver.Navigate().GoToUrl("https://pmc.ncbi.nlm.nih.gov/");
            Thread.Sleep(5000);
            FillHeadersFromDriver(driver, seleniumHeaders);
            return seleniumHeaders;
        }
    }

    {
        var seleniumHeaders = new SeleniumHeaderDTO();
        var options = new FirefoxOptions();

        PrepareFirefoxForLinuxContainers(options);

        options.AddArgument("--headless");
        options.AddArgument("-headless");
        options.AddArgument("--window-size=1920,1080");

        var firefoxBin = ResolveFirefoxBinaryPath();
        if (firefoxBin != null)
        {
            options.BinaryLocation = firefoxBin;
        }
        else
        {
            options.BrowserVersion = "stable";
        }

        FirefoxDriver driver;
        try
        {
            driver = new FirefoxDriver(options);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[FATAL] Could not start Firefox / GeckoDriver: {ex.Message}");
            Console.Error.WriteLine("On Google Colab or other minimal Linux images, install Firefox first, for example:");
            Console.Error.WriteLine("  !apt-get update -qq && apt-get install -y firefox-esr");
            Console.Error.WriteLine("Or set PMC_FIREFOX_BIN to the full path of the firefox executable.");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Console.Error.WriteLine("Firefox often still fails on Colab (root, status 255). Prefer Chromium:");
                Console.Error.WriteLine("  dotnet run -- <envBase> <worker> chrome");
                Console.Error.WriteLine("  !apt-get update -qq && apt-get install -y chromium-browser");
            }

            Console.ResetColor();
            throw;
        }

        using (driver)
        {
            driver.Navigate().GoToUrl("https://pmc.ncbi.nlm.nih.gov/");
            Thread.Sleep(5000);
            FillHeadersFromDriver(driver, seleniumHeaders);
            return seleniumHeaders;
        }
    }
}

//var seleniumHeaders = await test_browser();
//Console.WriteLine(
//        JsonSerializer.Serialize(seleniumHeaders,
//        new JsonSerializerOptions { WriteIndented = true })
//    );
for (int k = 0; k < 100; k++)
{
    SeleniumHeaderDTO SeleniumHeaders = await test_browser(browserKind);
    Console.WriteLine(
            JsonSerializer.Serialize(SeleniumHeaders,
            new JsonSerializerOptions { WriteIndented = true })
        );
    for (var i = 0; i < 20; i++)
    {
        await TestBatch(SeleniumHeaders, envBase);
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
