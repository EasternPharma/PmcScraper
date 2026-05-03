using PmcScraper;
using PmcScraper.DTOs;

Dictionary<string, string> bases = new Dictionary<string, string>();
bases["pmc"] = "https://pmc.bregulator.com";
bases["local"] = "http://localhost:8000";
bases["office"] = "http://localhost:1368";
string envBase = args.Length > 0 ? args[0].ToLowerInvariant() : "pmc";
string workerName = args.Length > 1 ? args[1] : "colab_xml_1";
if (!bases.ContainsKey(envBase))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"[FATAL] Invalid envBase '{envBase}'. Valid values: {string.Join(", ", bases.Keys)}");
    Console.Error.WriteLine("Usage: dotnet run -- <envBase> <workerName>");
    Console.ResetColor();
    return;
}

string apiKey = "adc039160e11aca97d1d65e0a2c3ff051708";

Console.WriteLine($"\nWorker: {workerName}\nEnv Base: {envBase}\n");

async Task<int> BatchXML(string currentEnvBase , string apiKey)
{
    List<int> ids = new List<int>();
    int processCount = 0;
    using (var apiCall = new ArticleApiCall(bases[currentEnvBase]))
    {
        var health = await apiCall.HealthCheckAsync();
        Console.WriteLine($"Health: {health.Status}");
        var freeArticles = await apiCall.ClaimFreeArticlesAsync(new GetFreeArticleRequestDto { User = workerName, BatchSize = 50 });
        ids = freeArticles.Select(x => x.PmcId).ToList();
        Console.WriteLine($"Claimed {ids.Count} free articles.");
    }
    using var xmlExtractor = new ArticleExtractorXml(
    apiKey: apiKey,
    timeoutMs: 30_000,
    maxAttempts: 5,
    retryAfterMs: 110);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    List<ArticleDTO> articles = await xmlExtractor.GetArticlesAsync(
        ids,
        splitCount: 8,
        restTimeMs: 0);
    sw.Stop();

    // One entry per claimed PMC id: duplicates in `ids` would make ToDictionary throw on errors.
    var claimedIdSet = ids.ToHashSet();
    var successIds = articles
        .Select(a => a.PmcId)
        .Where(claimedIdSet.Contains)
        .ToHashSet();
    var successDict = successIds.ToDictionary(id => id, _ => true);
    var errorDict = claimedIdSet
        .Where(id => !successIds.Contains(id))
        .ToDictionary(id => id, _ => "Extraction failed");

    processCount = articles.Count;

    Console.WriteLine(
        $"Extracted {articles.Count} / {ids.Count} articles in {sw.Elapsed.TotalSeconds:F1}s (errors: {errorDict.Count}).");

    var fullTextIds = articles
        .Where(x => x.Sections != null && x.Sections.Count > 0)
        .Select(y => y.PmcId)
        .Where(claimedIdSet.Contains)
        .Distinct()
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
        Console.WriteLine($"Submit result - success: {response.Success}" +
            (response.Error != null ? $", error: {response.Error}" : ""));
    }
    return processCount;
}


long totalProcessedCount = 0;
DateTime overallStartTime = DateTime.Now;
for (int k = 0; k < 1500; k++)
{
    for (var i = 0; i < 5; i++)
    {
        DateTime batchStartTime = DateTime.Now;
        var processedCount = await BatchXML(envBase, apiKey);
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
}