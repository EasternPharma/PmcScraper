using Newtonsoft.Json;
using PmcScraper;
using PmcScraper.DTOs;

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

// With an NCBI api_key the efetch limit rises from 3 to 10 req/s per IP.
// 110 ms (~9 req/s) stays just under the cap; splitCount 8 lets the rate gate
// fully saturate it without bursting.
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
