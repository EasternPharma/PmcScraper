//using PmcScraper;
//using PmcScraper.DTOs;

//Dictionary<string, string> bases1 = new Dictionary<string, string>();
//bases1["pmc"] = "https://pmc.bregulator.com";
//bases1["local"] = "http://localhost:8000";
//bases1["office"] = "http://localhost:1368";
//string envBase = args.Length > 0 ? args[0].ToLowerInvariant() : "pmc";
//string workerName = args.Length > 1 ? args[1] : "colab_xml_1";
//int selectApiKey = args.Length > 2 ? int.Parse(args[2]) : 1;

//if (!bases1.ContainsKey(envBase))
//{
//    Console.ForegroundColor = ConsoleColor.Red;
//    Console.Error.WriteLine($"[FATAL] Invalid envBase '{envBase}'. Valid values: {string.Join(", ", bases1.Keys)}");
//    Console.Error.WriteLine("Usage: dotnet run -- <envBase> <workerName>");
//    Console.ResetColor();
//    return;
//}


//Dictionary<int, string> apikeys= new Dictionary<int, string>();
//apikeys[1] = "adc039160e11aca97d1d65e0a2c3ff051708"; // behzad
//apikeys[2] = "44ff531b462b4b3b4d6df81ec2fa71a0a809"; // navid
//apikeys[3] = "30d301581c6419236c6d83ce614e24d53f08"; // hamid
//apikeys[4] = "d7253ccdfd26fe9b1794958b92c7b641c908"; // hamid
//apikeys[5] = "aa6e87f6bc1fb31e81ecbced3d1dd44d1109"; // hamid
//apikeys[6] = "37552ed91e1df90c87d29caeae81e5878e09"; // hamid
//apikeys[7] = "c076a802f9d39de06af03f4f531076a1ec08"; // hamid
//apikeys[8] = "7076420f8af7e56a27f86d07ba313e966408"; // hamid

//string apiKey = apikeys[selectApiKey];


//async Task Status(string currentEnvBase)
//{
//    using (var apiCall = new ArticleApiCall(bases1[currentEnvBase]))
//    {
//        var health = await apiCall.HealthCheckAsync();
//        Console.WriteLine($"Health: {health.Status}");
//        ArticleStaticsDto articleStatics = await apiCall.GetArticleStatisticsAsync();
//        Console.ForegroundColor = ConsoleColor.Red;
//        Console.WriteLine("||||||||||||||||||||||||");
//        Console.ForegroundColor= ConsoleColor.White;
//        Console.WriteLine($"All:\t{articleStatics.TotalList}");
//        Console.WriteLine($"Scraped:\t{articleStatics.TotalScraped}");
//        Console.WriteLine($"FullTxt:\t{articleStatics.TotalFullText}");
//        Console.WriteLine($"Error:\n{articleStatics.TotalError}");
//        Console.WriteLine($"\nWorker: {workerName}\nEnv Base: {envBase}\nSelect {selectApiKey} from apikeys: {apiKey}\n");
//    }
//}


//async Task<int> BatchXML(string currentEnvBase, string apiKey)
//{
//    List<int> ids = new List<int>();
//    int processCount = 0;
//    using (var apiCall = new ArticleApiCall(bases1[currentEnvBase]))
//    {
//        var health = await apiCall.HealthCheckAsync();
//        Console.WriteLine($"Health: {health.Status}");
//        var freeArticles = await apiCall.ClaimFreeArticlesAsync(new GetFreeArticleRequestDto { User = workerName, BatchSize = 50 });
//        ids = freeArticles.Select(x => x.PmcId).ToList();
//        Console.WriteLine(string.Join("\t", ids));
//        Console.WriteLine($"Claimed {ids.Count} free articles.");
//    }
//    using var xmlExtractor = new ArticleExtractorXml(
//    apiKey: apiKey,
//    timeoutMs: 30_000,
//    maxAttempts: 5,
//    retryAfterMs: 110);

//    var sw = System.Diagnostics.Stopwatch.StartNew();
//    var uniqueIds = ids.Distinct().ToList();
//    List<ArticleDTO> articles = await xmlExtractor.GetArticlesAsync(
//        uniqueIds,
//        splitCount: 8,
//        restTimeMs: 0);
//    sw.Stop();

//    // One entry per claimed PMC id: duplicates in `ids` would make ToDictionary throw on errors.
//    var claimedIdSet = uniqueIds.ToHashSet();
//    var successIds = articles
//        .Select(a => a.PmcId)
//        .Where(claimedIdSet.Contains)
//        .ToHashSet();
//    var successDict = successIds.ToDictionary(id => id, _ => true);
//    var errorDict = claimedIdSet
//        .Where(id => !successIds.Contains(id))
//        .ToDictionary(id => id, _ => "Extraction failed");

//    processCount = articles.Count;

//    Console.WriteLine(
//        $"Extracted {articles.Count} / {uniqueIds.Count} articles in {sw.Elapsed.TotalSeconds:F1}s (errors: {errorDict.Count}).");

//    var dedupedArticles = articles
//        .GroupBy(a => a.PmcId)
//        .Select(g =>
//            g.OrderByDescending(a => a.Sections?.Count ?? 0)
//             .ThenByDescending(a => a.AbstractText?.Length ?? 0)
//             .First())
//        .ToList();

//    var fullTextIds = dedupedArticles
//        .Where(x => x.Sections != null && x.Sections.Count > 0)
//        .Select(y => y.PmcId)
//        .Where(claimedIdSet.Contains)
//        .Distinct()
//        .ToList();
//    var updateItems = dedupedArticles.Select(ArticleUpdateItemDto.FromArticleDTO).ToList();

//    using (var apiCall = new ArticleApiCall(bases1[currentEnvBase]))
//    {
//        var health1 = await apiCall.HealthCheckAsync();
//        Console.WriteLine($"Health: {health1.Status}");
//        Console.WriteLine($"Submitting {updateItems.Count} unique articles...");
//        var response = await apiCall.SubmitScrapeResultsAsync(new ScrapeArticleRequestDto
//        {
//            User = workerName,
//            Articles = updateItems,
//            SuccessDict = successDict,
//            ErrorDict = errorDict,
//            FullTextIds = fullTextIds.Count > 0 ? fullTextIds : null
//        });
//        Console.WriteLine($"Submit result - success: {response.Success} - Full-text: {fullTextIds.Count}" +
//            (response.Error != null ? $", error: {response.Error}" : ""));
//    }
//    return processCount;
//}


//long totalProcessedCount = 0;
//DateTime overallStartTime = DateTime.Now;
//for (int k = 0; k < 1500; k++)
//{
//    await Status(envBase);
//    for (var i = 0; i < 10; i++)
//    {
//        DateTime batchStartTime = DateTime.Now;
//        var processedCount = await BatchXML(envBase, apiKey);
//        totalProcessedCount += processedCount;
//        var lastBatchDurationSeconds = (DateTime.Now - batchStartTime).TotalSeconds;
//        var totalDurationMinutes = (DateTime.Now - overallStartTime).TotalMinutes;
//        Console.ForegroundColor = ConsoleColor.Green;
//        Console.WriteLine("\n\n██████████████████████\n");
//        Console.ForegroundColor = ConsoleColor.White;
//        Console.WriteLine($"Batch completed in: {lastBatchDurationSeconds:F2} seconds");
//        Console.WriteLine($"Processed this batch: {processedCount}");
//        Console.WriteLine($"Total processed: {totalProcessedCount} (elapsed: {totalDurationMinutes:F2} minutes)");
//        Console.ForegroundColor = ConsoleColor.Red;
//        Console.WriteLine("\n██████████████████████\n\n");
//        Console.ForegroundColor = ConsoleColor.White;
//        await Task.Delay(Random.Shared.Next(1000, 3000));
//    }
//    await Task.Delay(1000);
//    Console.Clear();
//}