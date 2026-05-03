using PmcScraper.DTOs;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace PmcScraper;

public class ArticleExtractorXml : IDisposable
{
    #region Fields

    private readonly CookieContainer _cookieContainer;
    private readonly HttpClientHandler _httpHandler;
    private readonly HttpClient _httpClient;
    private bool _disposed;

    // NCBI E-utilities rate limit: 3 req/s without an api_key, 10 req/s with one.
    // We pace requests across the whole process with a single semaphore + a minimum-interval
    // gate (so even chunked parallel callers cannot exceed the cap).
    private static readonly SemaphoreSlim _rateGate = new SemaphoreSlim(1, 1);
    private static DateTime _lastRequestUtc = DateTime.MinValue;
    private static readonly Random _jitter = new Random();

    #endregion

    #region Properties

    public string ApiKey { get; }
    public int DelayMs { get; set; }
    public int TimeoutMs { get; set; }
    public int MaxAttempts { get; set; }
    public int? RetryAfterMs { get; set; }
    public int RetryCount { get; private set; }

    #endregion

    #region Constructor

    public ArticleExtractorXml(string apiKey, int timeoutMs = 30000, int maxAttempts = 5, int? retryAfterMs = null)
    {
        ApiKey = apiKey;
        TimeoutMs = timeoutMs;
        MaxAttempts = maxAttempts;
        // Base back-off: 750 ms with no api_key (≈ NCBI's 3 req/s window),
        // 150 ms with one (≈ NCBI's 10 req/s window). Caller can override.
        RetryAfterMs = retryAfterMs ?? (string.IsNullOrWhiteSpace(apiKey) ? 750 : 150);
        DelayMs = RetryAfterMs.Value;
        RetryCount = 0;

        _cookieContainer = new CookieContainer();

        _httpHandler = new HttpClientHandler
        {
            CookieContainer = _cookieContainer,
            UseCookies = true
        };

        _httpClient = new HttpClient(_httpHandler)
        {
            Timeout = TimeSpan.FromMilliseconds(TimeoutMs)
        };

        _httpClient.DefaultRequestHeaders.Add("User-Agent", "PmcScraper");
    }

    #endregion

    #region URL Builder

    private string BuildRequestUrl(int pmcId)
    {
        var baseUrl =
            $"https://eutils.ncbi.nlm.nih.gov/entrez/eutils/efetch.fcgi?db=pmc&id=PMC{pmcId}&rettype=xml";

        return string.IsNullOrWhiteSpace(ApiKey)
            ? baseUrl
            : $"{baseUrl}&api_key={ApiKey}";
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Returns the trimmed inner text of the first node matching <paramref name="xpath"/>
    /// relative to <paramref name="context"/>, or <c>null</c> when not found / empty.
    /// </summary>
    private static string? GetText(XmlNode context, string xpath)
    {
        var node = context.SelectSingleNode(xpath);
        if (node == null) return null;
        var text = NormalizeWhitespace(node.InnerText);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Collapses all whitespace runs into single spaces and trims.
    /// </summary>
    private static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Builds a JATS pub-date value (YYYY[-MM[-DD]]) from year/month/day children.
    /// </summary>
    private static DateTime? ParsePubDate(XmlNode? pubDateNode)
    {
        if (pubDateNode == null) return null;

        string? year = pubDateNode.SelectSingleNode("year")?.InnerText?.Trim();
        string? month = pubDateNode.SelectSingleNode("month")?.InnerText?.Trim();
        string? day = pubDateNode.SelectSingleNode("day")?.InnerText?.Trim();

        if (string.IsNullOrEmpty(year)) return null;

        if (!int.TryParse(year, out int y)) return null;
        int m = int.TryParse(month, out var mm) ? mm : 1;
        int d = int.TryParse(day, out var dd) ? dd : 1;

        try
        {
            return new DateTime(y, Math.Clamp(m, 1, 12), Math.Clamp(d, 1, 28), 0, 0, 0, DateTimeKind.Utc);
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Abstract Extraction

    /// <summary>
    /// Extracts and concatenates the text of the article abstract.
    /// JATS abstracts may contain nested <c>&lt;sec&gt;</c> blocks with their own titles
    /// (Background / Methods / Results / Conclusions); titles are inlined inline so the
    /// result is a single readable paragraph, mirroring <see cref="ArticleExtractor"/>.
    /// </summary>
    private static string? ExtractAbstract(XmlNode article)
    {
        var abstractNode = article.SelectSingleNode(".//front//abstract[not(@abstract-type)]")
                          ?? article.SelectSingleNode(".//front//abstract");
        if (abstractNode == null) return null;

        var parts = new List<string>();

        var sections = abstractNode.SelectNodes("./sec");
        if (sections != null && sections.Count > 0)
        {
            foreach (XmlNode sec in sections)
            {
                string? title = GetText(sec, "./title");
                var paragraphs = sec.SelectNodes(".//p");
                var body = new StringBuilder();
                if (paragraphs != null)
                {
                    foreach (XmlNode p in paragraphs)
                    {
                        var t = NormalizeWhitespace(p.InnerText);
                        if (!string.IsNullOrEmpty(t))
                        {
                            if (body.Length > 0) body.Append(' ');
                            body.Append(t);
                        }
                    }
                }

                if (body.Length == 0) continue;

                parts.Add(string.IsNullOrEmpty(title)
                    ? body.ToString()
                    : $"{title}: {body}");
            }
        }
        else
        {
            var paragraphs = abstractNode.SelectNodes(".//p");
            if (paragraphs != null)
            {
                foreach (XmlNode p in paragraphs)
                {
                    var t = NormalizeWhitespace(p.InnerText);
                    if (!string.IsNullOrEmpty(t)) parts.Add(t);
                }
            }
            else
            {
                var t = NormalizeWhitespace(abstractNode.InnerText);
                if (!string.IsNullOrEmpty(t)) parts.Add(t);
            }
        }

        if (parts.Count == 0) return null;
        return string.Join(" ", parts);
    }

    #endregion

    #region Section Extraction

    /// <summary>
    /// Walks the body's top-level <c>&lt;sec&gt;</c> nodes and returns a title→text map.
    /// Nested sections are flattened (their titles inlined into the parent text) so the
    /// resulting dictionary always has one entry per top-level body section, matching the
    /// structure produced by <see cref="ArticleExtractor.ExtractSections(HtmlAgilityPack.HtmlDocument)"/>.
    /// </summary>
    private static Dictionary<string, string>? ExtractSections(XmlNode article)
    {
        var body = article.SelectSingleNode(".//body");
        if (body == null) return null;

        var sections = new Dictionary<string, string>();
        short fallbackCounter = 1;

        var topLevelSecs = body.SelectNodes("./sec");
        if (topLevelSecs == null || topLevelSecs.Count == 0)
        {
            string fullText = NormalizeWhitespace(body.InnerText);
            if (!string.IsNullOrEmpty(fullText))
                sections["full_text"] = fullText;
            return sections.Count > 0 ? sections : null;
        }

        foreach (XmlNode sec in topLevelSecs)
        {
            string? title = GetText(sec, "./title");
            string text = ExtractSectionText(sec);

            if (string.IsNullOrWhiteSpace(text)) continue;

            string key = string.IsNullOrWhiteSpace(title)
                ? "section_" + fallbackCounter++
                : title!;

            string original = key;
            int suffix = 2;
            while (sections.ContainsKey(key))
                key = $"{original}_{suffix++}";

            sections[key] = text;
        }

        return sections.Count > 0 ? sections : null;
    }

    /// <summary>
    /// Builds the readable text for a single body <c>&lt;sec&gt;</c>. Direct paragraphs are
    /// emitted as-is; nested sections contribute their title (as a header line) followed by
    /// their own recursively-collected text.
    /// </summary>
    private static string ExtractSectionText(XmlNode section)
    {
        var sb = new StringBuilder();

        foreach (XmlNode child in section.ChildNodes)
        {
            if (child.NodeType != XmlNodeType.Element) continue;

            string name = child.LocalName.ToLowerInvariant();

            if (name == "title") continue;

            if (name == "sec")
            {
                string? subTitle = GetText(child, "./title");
                if (!string.IsNullOrEmpty(subTitle))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine(subTitle);
                }
                string subText = ExtractSectionText(child);
                if (!string.IsNullOrEmpty(subText))
                    sb.AppendLine(subText);
            }
            else
            {
                string text = NormalizeWhitespace(child.InnerText);
                if (!string.IsNullOrEmpty(text))
                    sb.AppendLine(text);
            }
        }

        return sb.ToString().Trim();
    }

    #endregion

    #region Extract Article Items From XML

    /// <summary>
    /// Parses a PMC efetch XML payload (<c>pmc-articleset</c>) and returns the first article
    /// as an <see cref="ArticleDTO"/>. Returns <c>null</c> when no article element is present.
    /// </summary>
    public ArticleDTO? ExtractArticleItems(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent)) return null;

        var xmlDoc = new XmlDocument();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null
        };
        using (var stringReader = new System.IO.StringReader(xmlContent))
        using (var xmlReader = XmlReader.Create(stringReader, settings))
        {
            xmlDoc.Load(xmlReader);
        }

        var article = xmlDoc.SelectSingleNode("//article");
        if (article == null) return null;

        // All article-level metadata lives under <front>. Restricting our XPaths to the
        // front matter prevents accidentally pulling values (fpage/lpage/volume/issue/...)
        // out of the reference list, where every cited paper has its own <fpage>/<lpage>.
        var front = article.SelectSingleNode("./front") ?? article;
        var articleMeta = front.SelectSingleNode(".//article-meta") ?? front;
        var journalMeta = front.SelectSingleNode(".//journal-meta") ?? front;

        string? title = GetText(articleMeta, ".//article-title");

        string? doi = GetText(articleMeta, ".//article-id[@pub-id-type='doi']");
        string? pmidRaw = GetText(articleMeta, ".//article-id[@pub-id-type='pmid']");
        int? pmid = int.TryParse(pmidRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pmidParsed)
            ? pmidParsed
            : null;

        string? pmcIdRaw = GetText(articleMeta, ".//article-id[@pub-id-type='pmcaid']")
                        ?? GetText(articleMeta, ".//article-id[@pub-id-type='pmcid']");
        int pmcId = 0;
        if (!string.IsNullOrEmpty(pmcIdRaw))
        {
            var digits = new string(pmcIdRaw.Where(char.IsDigit).ToArray());
            int.TryParse(digits, out pmcId);
        }

        var pubDateNode = articleMeta.SelectSingleNode(".//pub-date[@pub-type='epub']")
                       ?? articleMeta.SelectSingleNode(".//pub-date[@pub-type='ppub']")
                       ?? articleMeta.SelectSingleNode(".//pub-date[@pub-type='collection']")
                       ?? articleMeta.SelectSingleNode(".//pub-date");
        DateTime? publishDate = ParsePubDate(pubDateNode);

        string? journal = GetText(journalMeta, ".//journal-title");
        string? publisher = GetText(journalMeta, ".//publisher-name");
        string? issn = GetText(journalMeta, ".//issn[@pub-type='epub']")
                    ?? GetText(journalMeta, ".//issn");
        string? volume = GetText(articleMeta, "./volume");
        string? issue = GetText(articleMeta, "./issue");
        string? fpage = GetText(articleMeta, "./fpage");
        string? lpage = GetText(articleMeta, "./lpage");

        string? category = GetText(articleMeta, ".//article-categories//subj-group[@subj-group-type='heading']/subject")
                        ?? GetText(articleMeta, ".//article-categories//subject");

        var authors = new List<string>();
        var contribs = front.SelectNodes(".//contrib[@contrib-type='author']");
        if (contribs != null)
        {
            foreach (XmlNode contrib in contribs)
            {
                string? given = GetText(contrib, ".//given-names");
                string? surname = GetText(contrib, ".//surname");
                string fullName = string.Join(" ", new[] { given, surname }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                if (!string.IsNullOrWhiteSpace(fullName))
                    authors.Add(fullName);
            }
        }

        var keywords = new List<string>();
        var kwdNodes = front.SelectNodes(".//kwd-group/kwd");
        if (kwdNodes != null)
        {
            foreach (XmlNode kwd in kwdNodes)
            {
                var t = NormalizeWhitespace(kwd.InnerText);
                if (!string.IsNullOrEmpty(t)) keywords.Add(t);
            }
        }

        string? abstractText = ExtractAbstract(article);
        Dictionary<string, string>? sections = ExtractSections(article);

        return new ArticleDTO
        {
            PmcId = pmcId,
            PmId = pmid,
            Doi = doi,
            Title = title,
            Category = category,
            Journal = journal,
            Publisher = publisher,
            Volume = volume,
            Issue = issue,
            ISSN = issn,
            FPage = fpage,
            LPage = lpage,
            Authors = authors.Count > 0 ? authors : null,
            PublishDate = publishDate,
            AbstractText = abstractText,
            Keywords = keywords.Count > 0 ? keywords : null,
            Sections = sections
        };
    }

    #endregion

    #region Public API

    /// <summary>
    /// Process-wide rate gate: serializes the moment of dispatch and ensures at least
    /// <see cref="DelayMs"/> milliseconds elapse between two outbound NCBI requests.
    /// Network round-trip time itself happens after the gate is released, so concurrent
    /// callers still overlap their waits.
    /// </summary>
    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        await _rateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int minInterval = Math.Max(0, DelayMs);
            if (minInterval > 0)
            {
                var elapsed = (DateTime.UtcNow - _lastRequestUtc).TotalMilliseconds;
                if (elapsed < minInterval)
                {
                    int wait = (int)Math.Ceiling(minInterval - elapsed);
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }
            }
            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            _rateGate.Release();
        }
    }

    /// <summary>
    /// Computes a back-off delay for retry attempt <paramref name="attempt"/> (1-based).
    /// Uses exponential growth on top of <see cref="RetryAfterMs"/> with ±25% jitter,
    /// capped at 15 seconds. If the server returned a <c>Retry-After</c> header,
    /// <paramref name="serverHint"/> overrides the computed value.
    /// </summary>
    private int ComputeBackoffMs(int attempt, TimeSpan? serverHint)
    {
        if (serverHint.HasValue && serverHint.Value > TimeSpan.Zero)
            return (int)Math.Min(serverHint.Value.TotalMilliseconds, 30_000);

        int baseMs = RetryAfterMs ?? 750;
        double exp = baseMs * Math.Pow(2, Math.Max(0, attempt - 1));
        double jittered;
        lock (_jitter)
        {
            jittered = exp * (0.75 + _jitter.NextDouble() * 0.5);
        }
        return (int)Math.Min(jittered, 15_000);
    }

    public async Task<string> GetArticleXmlAsync(int pmcId, CancellationToken cancellationToken = default)
    {
        var requestUrl = BuildRequestUrl(pmcId);
        Exception? lastError = null;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PaceAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                using var response = await _httpClient.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);

                // 429 / 503 — honor Retry-After if present, otherwise exponential back-off.
                if (response.StatusCode == (HttpStatusCode)429 ||
                    response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    RetryCount++;
                    if (attempt == MaxAttempts) break;
                    await Task.Delay(
                        ComputeBackoffMs(attempt, response.Headers.RetryAfter?.Delta),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Other 5xx — transient, retry.
                if ((int)response.StatusCode >= 500)
                {
                    RetryCount++;
                    lastError = new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                    if (attempt == MaxAttempts) break;
                    await Task.Delay(ComputeBackoffMs(attempt, null), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                // PMC sometimes returns 200 with an empty body or an error envelope when the
                // record is temporarily unavailable. Treat empty/short responses as transient.
                if (string.IsNullOrWhiteSpace(body) || body.Length < 200)
                {
                    RetryCount++;
                    lastError = new HttpRequestException("Empty or truncated XML body.");
                    if (attempt == MaxAttempts) break;
                    await Task.Delay(ComputeBackoffMs(attempt, null), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return body;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex)
            {
                lastError = ex;
                RetryCount++;
                if (attempt == MaxAttempts) break;
                await Task.Delay(ComputeBackoffMs(attempt, null), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                RetryCount++;
                if (attempt == MaxAttempts) break;
                await Task.Delay(ComputeBackoffMs(attempt, null), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new Exception(
            $"Failed to fetch article XML for PMC{pmcId} after {MaxAttempts} attempts" +
            (lastError != null ? $": {lastError.GetType().Name}: {lastError.Message}" : "."),
            lastError);
    }

    /// <summary>
    /// Convenience method: downloads the article XML for <paramref name="pmcId"/> and parses
    /// it into an <see cref="ArticleDTO"/>.
    /// </summary>
    public async Task<ArticleDTO?> ExtractArticleAsync(int pmcId, CancellationToken cancellationToken = default)
    {
        string xml = await GetArticleXmlAsync(pmcId, cancellationToken).ConfigureAwait(false);
        return ExtractArticleItems(xml);
    }

    #endregion

    #region Batch

    /// <summary>
    /// Fetches and parses an entire list of PMC ids. Ids are processed in chunks of
    /// <paramref name="splitCount"/> in parallel; between chunks the method sleeps
    /// <paramref name="restTimeMs"/> milliseconds to stay under NCBI's per-IP rate limit
    /// (3 req/s without an API key, 10 req/s with one). Articles that fail to fetch or
    /// parse are skipped — the returned list contains only successful, non-null results
    /// and preserves the original id order within each chunk.
    /// </summary>
    public async Task<List<ArticleDTO>> GetArticlesAsync(
        List<int> ids,
        int splitCount = 5,
        int restTimeMs = 0,
        CancellationToken cancellationToken = default)
    {
        if (ids == null) throw new ArgumentNullException(nameof(ids));
        if (splitCount <= 0) throw new ArgumentOutOfRangeException(nameof(splitCount), "splitCount must be > 0");
        if (restTimeMs < 0) throw new ArgumentOutOfRangeException(nameof(restTimeMs), "restTimeMs must be >= 0");

        var articles = new List<ArticleDTO>(ids.Count);
        if (ids.Count == 0) return articles;

        int chunkCount = (int)Math.Ceiling(ids.Count / (double)splitCount);

        for (int i = 0; i < chunkCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = ids.Skip(i * splitCount).Take(splitCount).ToList();

            // Dispatch all ids in this chunk in parallel.
            var tasks = chunk.Select(id => ExtractArticleSafeAsync(id, cancellationToken)).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var article in results)
            {
                if (article != null)
                    articles.Add(article);
            }

            // Don't sleep after the final chunk — there's nothing left to throttle for.
            if (restTimeMs > 0 && i < chunkCount - 1)
                await Task.Delay(restTimeMs, cancellationToken).ConfigureAwait(false);
        }

        return articles;
    }

    /// <summary>
    /// Wraps <see cref="ExtractArticleAsync(int)"/> so a single failed id doesn't cancel
    /// the whole <see cref="Task.WhenAll(Task[])"/>. Errors are logged and the id is dropped.
    /// </summary>
    private async Task<ArticleDTO?> ExtractArticleSafeAsync(int pmcId, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await ExtractArticleAsync(pmcId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[WARN] PMC{pmcId} failed: {ex.GetType().Name}: {ex.Message}");
            Console.ResetColor();
            return null;
        }
    }

    #endregion

    #region Dispose Pattern

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _httpClient?.Dispose();
                _httpHandler?.Dispose();
            }

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    #endregion
}
