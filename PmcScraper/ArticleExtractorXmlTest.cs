using PmcScraper.DTOs;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace PmcScraper;

/// <summary>
/// Smoke test for <see cref="ArticleExtractorXml"/>. Fetches a known PMC article over the
/// NCBI E-utilities efetch endpoint, parses it, and asserts the core fields match what we
/// expect for that article. Designed to be invoked from <c>Program.cs</c> (e.g.
/// <c>await ArticleExtractorXmlTest.RunAsync();</c>) so it can run without a separate test
/// project.
/// </summary>
public static class ArticleExtractorXmlTest
{
    private const int TestPmcId = 3874642;

    public static async Task<bool> RunAsync(string? apiKey = null)
    {
        Console.WriteLine($"=== ArticleExtractorXml test — PMC{TestPmcId} ===");

        ArticleDTO? article;
        try
        {
            using var extractor = new ArticleExtractorXml(
                apiKey: apiKey ?? string.Empty,
                timeoutMs: 30_000,
                maxAttempts: 3);

            article = await extractor.GetArticleAsync(TestPmcId);
        }
        catch (Exception ex)
        {
            Fail($"Extractor threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        if (article == null)
        {
            Fail("Extractor returned null.");
            return false;
        }

        bool ok = true;

        ok &= AssertEqual("PmcId", TestPmcId, article.PmcId);
        ok &= AssertEqual("PmId", 24325571, article.PmId);
        ok &= AssertEqual("Doi", "10.1186/1476-511X-12-182", article.Doi);
        ok &= AssertContains("Title", "Ascorbic acid", article.Title);
        ok &= AssertContains("Title", "3T3-L1", article.Title);
        ok &= AssertEqual("Journal", "Lipids in Health and Disease", article.Journal);
        ok &= AssertEqual("Publisher", "BMC", article.Publisher);
        ok &= AssertEqual("ISSN", "1476-511X", article.ISSN);
        ok &= AssertEqual("Volume", "12", article.Volume);
        ok &= AssertEqual("FPage", "182", article.FPage);
        ok &= AssertEqual("LPage", "182", article.LPage);
        ok &= AssertEqual("Category", "Research", article.Category);

        // Publication date — JATS pub-date with @pub-type='epub' is 11 Dec 2013.
        ok &= AssertTrue("PublishDate has value", article.PublishDate.HasValue);
        if (article.PublishDate.HasValue)
        {
            ok &= AssertEqual("PublishDate.Year",  2013, article.PublishDate.Value.Year);
            ok &= AssertEqual("PublishDate.Month", 12,   article.PublishDate.Value.Month);
            ok &= AssertEqual("PublishDate.Day",   11,   article.PublishDate.Value.Day);
        }

        // Authors — exactly 4 listed in the XML.
        ok &= AssertTrue("Authors not null", article.Authors != null);
        if (article.Authors != null)
        {
            ok &= AssertEqual("Authors count", 4, article.Authors.Count);
            ok &= AssertTrue("Authors contains 'Byoungjae Kim'",
                article.Authors.Any(a => a.Contains("Kim") && a.Contains("Byoungjae")));
            ok &= AssertTrue("Authors contains 'Min-Goo Lee'",
                article.Authors.Any(a => a.Contains("Lee") && a.Contains("Min-Goo")));
        }

        // Keywords — 5 in the XML: Ascorbic acid, adipogenesis, 3T3-L1, collagens, differential expression
        ok &= AssertTrue("Keywords not null", article.Keywords != null);
        if (article.Keywords != null)
        {
            ok &= AssertEqual("Keywords count", 5, article.Keywords.Count);
            ok &= AssertTrue("Keywords contains 'Ascorbic acid'",
                article.Keywords.Any(k => k.Equals("Ascorbic acid", StringComparison.OrdinalIgnoreCase)));
            ok &= AssertTrue("Keywords contains '3T3-L1'",
                article.Keywords.Any(k => k.Equals("3T3-L1", StringComparison.OrdinalIgnoreCase)));
        }

        // Abstract — should contain the Background/Methods/Results/Conclusion sub-titles
        // because the XML abstract is structured.
        ok &= AssertTrue("AbstractText not null/empty", !string.IsNullOrWhiteSpace(article.AbstractText));
        if (!string.IsNullOrWhiteSpace(article.AbstractText))
        {
            ok &= AssertContains("AbstractText", "Background",  article.AbstractText);
            ok &= AssertContains("AbstractText", "Methods",     article.AbstractText);
            ok &= AssertContains("AbstractText", "Results",     article.AbstractText);
            ok &= AssertContains("AbstractText", "Conclusion",  article.AbstractText);
            ok &= AssertContains("AbstractText", "adipogenesis", article.AbstractText);
        }

        // Body sections. Each top-level <sec> appears as its own dict entry keyed by
        // its <title>. Subsections are flattened into the parent entry with their titles
        // inlined as sub-headers in the body text — so the parent context is preserved.
        ok &= AssertTrue("Sections not null", article.Sections != null);
        if (article.Sections != null)
        {
            ok &= AssertTrue("Sections contains 'Introduction'",
                article.Sections.Keys.Any(k => k.Equals("Introduction", StringComparison.OrdinalIgnoreCase)));
            ok &= AssertTrue("Sections contains 'Results'",
                article.Sections.Keys.Any(k => k.Equals("Results", StringComparison.OrdinalIgnoreCase)));
            ok &= AssertTrue("Sections contains 'Discussion'",
                article.Sections.Keys.Any(k => k.Equals("Discussion", StringComparison.OrdinalIgnoreCase)));
            ok &= AssertTrue("Sections contains 'Conclusions'",
                article.Sections.Keys.Any(k => k.Equals("Conclusions", StringComparison.OrdinalIgnoreCase)));
            ok &= AssertTrue("Sections contains 'Materials and methods'",
                article.Sections.Keys.Any(k => k.Equals("Materials and methods", StringComparison.OrdinalIgnoreCase)));
            ok &= AssertTrue("Sections contains 'Abbreviations'",
                article.Sections.Keys.Any(k => k.Equals("Abbreviations", StringComparison.OrdinalIgnoreCase)));

            if (article.Sections.TryGetValue("Introduction", out var intro))
            {
                ok &= AssertContains("Sections[Introduction] has 'adipogenesis'", "adipogenesis", intro);
                ok &= AssertTrue("Sections[Introduction] >= 1000 chars", intro.Length >= 1000);
            }

            // Results section has 4 subsections — confirm at least one subsection title
            // is inlined in its body (proves nested-sec flattening is working).
            if (article.Sections.TryGetValue("Results", out var results))
            {
                ok &= AssertContains("Sections[Results] inlines subsection title",
                    "ASC stimulates", results);
            }
        }

        Console.WriteLine();
        DumpArticle(article);

        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"\n=== Result: {(ok ? "PASS" : "FAIL")} ===");
        Console.ResetColor();
        return ok;
    }

    #region Tiny assertion helpers

    private static bool AssertEqual<T>(string name, T expected, T actual)
    {
        bool ok = Equals(expected, actual);
        Report(ok, name, $"expected '{expected}', got '{actual}'");
        return ok;
    }

    private static bool AssertContains(string name, string needle, string? haystack)
    {
        bool ok = haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        Report(ok, name, $"expected to contain '{needle}'");
        return ok;
    }

    private static bool AssertTrue(string name, bool condition)
    {
        Report(condition, name, "condition was false");
        return condition;
    }

    private static void Report(bool ok, string name, string failDetail)
    {
        if (ok)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [PASS] {name}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [FAIL] {name} — {failDetail}");
        }
        Console.ResetColor();
    }

    private static void Fail(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  [FAIL] {message}");
        Console.ResetColor();
    }

    #endregion

    #region Pretty dump

    private static void DumpArticle(ArticleDTO a)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("--- Parsed article ---");
        Console.ResetColor();
        Console.WriteLine($"Title:       {a.Title}");
        Console.WriteLine($"PmcId:       {a.PmcId}");
        Console.WriteLine($"PmId:        {a.PmId}");
        Console.WriteLine($"Doi:         {a.Doi}");
        Console.WriteLine($"Journal:     {a.Journal}");
        Console.WriteLine($"Publisher:   {a.Publisher}");
        Console.WriteLine($"ISSN:        {a.ISSN}");
        Console.WriteLine($"Volume:      {a.Volume}  Issue: {a.Issue}  fpage: {a.FPage}  lpage: {a.LPage}");
        Console.WriteLine($"Category:    {a.Category}");
        Console.WriteLine($"PublishDate: {a.PublishDate:yyyy-MM-dd}");
        Console.WriteLine($"Authors:     {(a.Authors != null ? string.Join(" | ", a.Authors) : "(null)")}");
        Console.WriteLine($"Keywords:    {(a.Keywords != null ? string.Join(", ", a.Keywords) : "(null)")}");

        string abs = a.AbstractText ?? "(null)";
        Console.WriteLine($"Abstract ({abs.Length} chars):");
        Console.WriteLine($"  {Truncate(abs, 400)}");

        Console.WriteLine($"Sections ({a.Sections?.Count ?? 0}):");
        if (a.Sections != null)
        {
            foreach (var kv in a.Sections)
                Console.WriteLine($"  - {kv.Key} ({kv.Value.Length} chars)");
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "…";

    #endregion

    #region Table extraction (sample.xml)

    /// <summary>
    /// Loads <c>sample.xml</c> from the app output directory and validates
    /// <see cref="ArticleExtractorXml.ExtractTablesFromXml"/>.
    /// </summary>
    public static bool RunExtractTablesFromSample()
    {
        Console.WriteLine("=== ArticleExtractorXml table extraction — sample.xml ===");

        string path = Path.Combine(AppContext.BaseDirectory, "sample.xml");
        if (!File.Exists(path))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  [SKIP] sample.xml not found at {path}");
            Console.ResetColor();
            return true;
        }

        string xml = File.ReadAllText(path);
        var tables = ArticleExtractorXml.ExtractTablesFromXml(xml);
        if (tables.Count != 1)
        {
            Fail($"Expected 1 table-wrap, got {tables.Count}");
            return false;
        }

        var t = tables[0];
        bool ok = true;
        ok &= AssertEqual("TableName", "Table 1", t.TableName);
        ok &= AssertTrue("Description mentions follicle",
            t.Description != null && t.Description.Contains("follicle", StringComparison.OrdinalIgnoreCase));
        ok &= AssertEqual("Colmuns count", 4, t.Colmuns.Count);
        ok &= AssertTrue("First column is Group",
            t.Colmuns.Count > 0 && t.Colmuns[0].Contains("Group", StringComparison.OrdinalIgnoreCase));
        ok &= AssertTrue("Data has >= 3 rows", t.Data.Count >= 3);
        ok &= AssertTrue("Row with Adenosine present",
            t.Data.Any(row => row.Any(cell => cell.Contains("Adenosine", StringComparison.OrdinalIgnoreCase))));

        Console.WriteLine(ArticleExtractorXml.SerializeTableToJson(t));

        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"\n=== Table sample: {(ok ? "PASS" : "FAIL")} ===");
        Console.ResetColor();
        return ok;
    }

    #endregion
}
