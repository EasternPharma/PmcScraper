using PmcScraper;
using PmcScraper.DTOs;

const int XmlMethod = 1;
const int WebMethod = 2;

Dictionary<string, string> bases = new Dictionary<string, string>();
bases["pmc"] = "https://pmc.bregulator.com";
bases["local"] = "http://localhost:8000";
bases["office"] = "http://localhost:1368";

Dictionary<int, string> apikeys = new Dictionary<int, string>();
apikeys[1] = "adc039160e11aca97d1d65e0a2c3ff051708"; // behzad
apikeys[2] = "44ff531b462b4b3b4d6df81ec2fa71a0a809"; // navid
apikeys[3] = "30d301581c6419236c6d83ce614e24d53f08"; // hamid
apikeys[4] = "d7253ccdfd26fe9b1794958b92c7b641c908"; // hamid
apikeys[5] = "aa6e87f6bc1fb31e81ecbced3d1dd44d1109"; // hamid
apikeys[6] = "37552ed91e1df90c87d29caeae81e5878e09"; // hamid
apikeys[7] = "c076a802f9d39de06af03f4f531076a1ec08"; // hamid
apikeys[8] = "7076420f8af7e56a27f86d07ba313e966408"; // hamid

void ShowUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("dotnet run -- <method> [envBase] [workerName] [apiKeyIndex]");
    Console.WriteLine();
    Console.WriteLine("method:");
    Console.WriteLine("  1 = Xml");
    Console.WriteLine("  2 = Web");
    Console.WriteLine();
    Console.WriteLine($"envBase options: {string.Join(", ", bases.Keys)}");
    Console.WriteLine("example:");
    Console.WriteLine("dotnet run -- 1 pmc colab_xml_1 1");
}

if (!int.TryParse(args.Length > 0 ? args[0] : "1", out var methodArg) ||
    (methodArg != XmlMethod && methodArg != WebMethod))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine("[FATAL] Invalid method. Use 1 (Xml) or 2 (Web).");
    Console.ResetColor();
    ShowUsage();
    return;
}

int scrapMethod = methodArg;
string envBase = args.Length > 1 ? args[1].ToLowerInvariant() : "pmc";
string defaultWorker = scrapMethod == XmlMethod ? "colab_xml_1" : "colab_web_1";
string workerName = args.Length > 2 ? args[2] : defaultWorker;

if (!int.TryParse(args.Length > 3 ? args[3] : "1", out var selectApiKey))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine("[FATAL] Invalid apiKeyIndex.");
    Console.ResetColor();
    ShowUsage();
    return;
}

if (!bases.ContainsKey(envBase))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"[FATAL] Invalid envBase '{envBase}'. Valid values: {string.Join(", ", bases.Keys)}");
    Console.ResetColor();
    ShowUsage();
    return;
}

if (!apikeys.TryGetValue(selectApiKey, out string? apiKey))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"[FATAL] Invalid apiKeyIndex '{selectApiKey}'. Valid values: {string.Join(", ", apikeys.Keys.OrderBy(x => x))}");
    Console.ResetColor();
    ShowUsage();
    return;
}

async Task Status(string currentEnvBase)
{
    using var apiCall = new ArticleApiCall(bases[currentEnvBase]);
    var health = await apiCall.HealthCheckAsync();
    Console.WriteLine($"Health: {health.Status}");
    ArticleStaticsDto articleStatics = await apiCall.GetArticleStatisticsAsync();
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("||||||||||||||||||||||||");
    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine($"All:\t{articleStatics.TotalList}");
    Console.WriteLine($"Scraped:\t{articleStatics.TotalScraped}");
    Console.WriteLine($"FullTxt:\t{articleStatics.TotalFullText}");
    Console.WriteLine($"Error:\n{articleStatics.TotalError}");
    string methodName = scrapMethod == XmlMethod ? "Xml" : "Web";
    Console.WriteLine($"\nMethod: {methodName}\nWorker: {workerName}\nEnv Base: {envBase}\nSelect {selectApiKey} from apikeys: {apiKey}\n");
}

async Task<int> BatchScrap(string currentEnvBase, string selectedApiKey, int method)
{
    var ids = new List<int>();
    using (var apiCall = new ArticleApiCall(bases[currentEnvBase]))
    {
        var health = await apiCall.HealthCheckAsync();
        Console.WriteLine($"Health: {health.Status}");
        int batchSize = method == XmlMethod ? 50 : 10;
        var freeArticles = await apiCall.ClaimFreeArticlesAsync(new GetFreeArticleRequestDto { User = workerName, BatchSize = batchSize });
        ids = freeArticles.Select(x => x.PmcId).ToList();
        Console.WriteLine(string.Join("\t", ids));
        Console.WriteLine($"Claimed {ids.Count} free articles.");
    }

    var uniqueIds = ids.Distinct().ToArray();
    if (uniqueIds.Length == 0)
    {
        Console.WriteLine("No free articles claimed in this batch.");
        return 0;
    }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    List<ArticleDTO> articles = new List<ArticleDTO>();
    IPmcScraper pmcScraper = null;
    switch (method)
    {
        case 1:
            pmcScraper = new ArticleExtractorXml(
            apiKey: selectedApiKey,
            timeoutMs: 30_000,
            maxAttempts: 5,
            retryAfterMs: 110);
            break;
        case 2:
            pmcScraper = new ArticleExtractor();
            break;
        default:
            break;
    }
    if (method == XmlMethod)
    {
        articles.AddRange(await pmcScraper.GetArticlesAsync(uniqueIds, splitCount: 8, restTimeMs: 0));
    }
    if (method == WebMethod)
    {
        articles.AddRange(await pmcScraper.GetArticlesAsync(uniqueIds));
    }
    sw.Stop();

    articles ??= new List<ArticleDTO>();

    var claimedIdSet = uniqueIds.ToHashSet();
    var successIds = articles
        .Select(a => a.PmcId)
        .Where(claimedIdSet.Contains)
        .ToHashSet();
    var successDict = successIds.ToDictionary(id => id, _ => true);
    var errorDict = claimedIdSet
        .Where(id => !successIds.Contains(id))
        .ToDictionary(id => id, _ => $"Extraction failed ({(method == XmlMethod ? "Xml" : "Web")})");

    Console.WriteLine($"Extracted {articles.Count} / {uniqueIds.Length} articles in {sw.Elapsed.TotalSeconds:F1}s (errors: {errorDict.Count}).");

    var dedupedArticles = articles
        .GroupBy(a => a.PmcId)
        .Select(g =>
            g.OrderByDescending(a => a.Sections?.Count ?? 0)
                .ThenByDescending(a => a.AbstractText?.Length ?? 0)
                .First())
        .ToList();

    var fullTextIds = dedupedArticles
        .Where(x => x.Sections != null && x.Sections.Count > 0)
        .Select(y => y.PmcId)
        .Where(claimedIdSet.Contains)
        .Distinct()
        .ToList();
    var updateItems = dedupedArticles.Select(ArticleUpdateItemDto.FromArticleDTO).ToList();

    using (var apiCall = new ArticleApiCall(bases[currentEnvBase]))
    {
        var health1 = await apiCall.HealthCheckAsync();
        Console.WriteLine($"Health: {health1.Status}");
        Console.WriteLine($"Submitting {updateItems.Count} unique articles...");
        var response = await apiCall.SubmitScrapeResultsAsync(new ScrapeArticleRequestDto
        {
            User = workerName,
            Articles = updateItems,
            SuccessDict = successDict,
            ErrorDict = errorDict,
            FullTextIds = fullTextIds.Count > 0 ? fullTextIds : null
        });
        Console.WriteLine($"Submit result - success: {response.Success} - Full-text: {fullTextIds.Count}" +
            (response.Error != null ? $", error: {response.Error}" : ""));
    }

    return dedupedArticles.Count;
}

long totalProcessedCount = 0;
DateTime overallStartTime = DateTime.Now;
for (int k = 0; k < 1500; k++)
{
    await Status(envBase);
    for (var i = 0; i < 10; i++)
    {
        DateTime batchStartTime = DateTime.Now;
        var processedCount = await BatchScrap(envBase, apiKey, scrapMethod);
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
        await Task.Delay(Random.Shared.Next(1000, 3000));
    }
    await Task.Delay(1000);
    Console.Clear();
}