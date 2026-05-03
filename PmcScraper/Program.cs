using Newtonsoft.Json;
using PmcScraper;
using PmcScraper.DTOs;

if (args.Length > 0 && args[0] == "--tables-sample")
{
    Environment.ExitCode = ArticleExtractorXmlTest.RunExtractTablesFromSample() ? 0 : 1;
    return;
}

string apiKey = "adc039160e11aca97d1d65e0a2c3ff051708";
string filePath = @"E:\PmcStore\biotin_hair.txt";
string jsonPath = @"E:\Artiles_xml_dotnet.json";

if (!File.Exists(filePath))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"[FATAL] Input file not found: {filePath}");
    Console.ResetColor();
    Environment.ExitCode = 1;
    return;
}

List<int> ids = (await File.ReadAllLinesAsync(filePath))
    .Select(line => line.Trim())
    .Where(line => !string.IsNullOrEmpty(line))
    .Select(line => int.TryParse(line.ToLower().Replace("pmc",""), out var id) ? id : (int?)null)
    .Where(id => id.HasValue)
    .Select(id => id!.Value)
    .Take(50)
    .ToList();

if (ids.Count == 0)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"[WARN] No PMC ids found in {filePath}.");
    Console.ResetColor();
    return;
}

Console.WriteLine($"Loaded {ids.Count} PMC ids from {filePath}.");

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

Console.WriteLine($"Extracted {articles.Count} / {ids.Count} articles in {sw.Elapsed.TotalSeconds:F1}s.");

string? outDir = Path.GetDirectoryName(jsonPath);
if (!string.IsNullOrEmpty(outDir))
    Directory.CreateDirectory(outDir);

string jsonContent = JsonConvert.SerializeObject(articles, Formatting.Indented);
await File.WriteAllTextAsync(jsonPath, jsonContent);

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"Saved JSON to {jsonPath} ({new FileInfo(jsonPath).Length:N0} bytes).");
Console.ResetColor();

// Quick spot-check: find the molecules-27-05136 article (the one with the
// "2. Results and Discussion" / "2.1. Scalp Microbiome ..." structure) and print
// the first 500 chars of its Results section so we can eyeball the fix.
var spot = articles.FirstOrDefault(a =>
    a.Sections != null &&
    a.Sections.Keys.Any(k => k.Contains("Results and Discussion", StringComparison.OrdinalIgnoreCase)));

if (spot != null && spot.Sections != null)
{
    var key = spot.Sections.Keys.First(k => k.Contains("Results and Discussion", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Spot-check PMC{spot.PmcId}: section '{key}' ({spot.Sections[key].Length} chars)");
    Console.ResetColor();
    string preview = spot.Sections[key];
    Console.WriteLine(preview.Length > 600 ? preview.Substring(0, 600) + "…" : preview);
}
