using HtmlAgilityPack;
using Newtonsoft.Json;
using PmcScraper.DTOs;
using System.Net;
using System.Text;

namespace PmcScraper;

struct SectionExtractionResult
{
    public Dictionary<string, string>? Sections { get; set; }
    public short SectionCount { get; set; }
}

public class ArticleExtractor : IPmcScraper
{
    #region Properties and Fields
    private bool disposedValue;
    private Dictionary<string, string>? _headers;
    private Dictionary<string, string>? _cookies;
    private HttpClient? _httpClient;
    private HttpClientHandler? _httpHandler;
    private CookieContainer? _cookieContainer;

    // Limits how many PMC fetches run truly in parallel. PMC tolerates 2-3 concurrent
    // requests per IP comfortably; more triggers soft bans / empty HTML.
    private static readonly SemaphoreSlim _concurrencyGate = new SemaphoreSlim(3, 3);
    #endregion

    #region Static Initializer — Real-time Console
    static ArticleExtractor()
    {
        // Ensure stdout and stderr are flushed after every write so logs appear
        // immediately in Google Colab, Docker, and other piped environments.
        if (Console.Out is not System.IO.StreamWriter { AutoFlush: true })
        {
            var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true };
            Console.SetOut(stdout);
        }
        if (Console.Error is not System.IO.StreamWriter { AutoFlush: true })
        {
            var stderr = new System.IO.StreamWriter(Console.OpenStandardError(), Console.OutputEncoding) { AutoFlush = true };
            Console.SetError(stderr);
        }
    }
    #endregion

    #region Constructors Methods 
    public ArticleExtractor()
    {
    }

    public ArticleExtractor(Dictionary<string, string>? headers, Dictionary<string, string>? cookies)
    {
        _headers = headers;
        _cookies = cookies;
    }
    #endregion

    #region Enum and Constants
    enum XPath
    {
        // Direct XPath queries
        Title,
        Abstract,
        Keywords,
        Section,
        Author,

        // Meta tag name attributes (ncbi_*)
        Domain,
        Type,
        PcId,

        // Meta tag name attributes (citation_*)
        JournalTitle,
        CitationTitle,
        PublicationDate,
        Volume,
        Issue,
        FirstPage,
        Doi,
        PmId,
        FulltextUrl,
        PdfUrl,

        // Meta tag name attributes (og:*)
        OgTitle,
        OgType,
    }

    readonly Dictionary<XPath, string> _XPathMap = new()
    {
        // Direct XPath queries
        { XPath.Title,           "//h1" },
        { XPath.Abstract,        "//section[contains(@class,'abstract')]" },
        { XPath.Keywords,        "//section[contains(@class,'kwd')]" },
        { XPath.Section,         "//section[@class='body main-article-body']" },
        { XPath.Author,          "citation_author" },

        // ncbi_* meta tags
        { XPath.Domain,          "ncbi_domain" },
        { XPath.Type,            "ncbi_type" },
        { XPath.PcId,            "ncbi_pcid" },

        // citation_* meta tags
        { XPath.JournalTitle,    "citation_journal_title" },
        { XPath.CitationTitle,   "citation_title" },
        { XPath.PublicationDate, "citation_publication_date" },
        { XPath.Volume,          "citation_volume" },
        { XPath.Issue,           "citation_issue" },
        { XPath.FirstPage,       "citation_firstpage" },
        { XPath.Doi,             "citation_doi" },
        { XPath.PmId,            "citation_pmid" },
        { XPath.FulltextUrl,     "citation_fulltext_html_url" },
        { XPath.PdfUrl,          "citation_pdf_url" },

        // og:* meta tags
        { XPath.OgTitle,         "og:title" },
        { XPath.OgType,          "og:type" },
    };

    public string EnvironmentName { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public string WorkerName { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    #endregion

    #region Set Header Logic
    public void SetHeader(Dictionary<string, string> headers)
    {
        _headers = headers;
    }
    #endregion

    #region Get MetaTag Logic
    /// <summary>
    /// Returns the <c>content</c> attribute of the first &lt;meta&gt; tag
    /// whose <c>name</c> attribute matches <paramref name="name"/>, or <c>null</c> if not found.
    /// </summary>
    private string? GetMeta(HtmlDocument doc, string name)
    {
        var node = doc.DocumentNode.SelectSingleNode($"//meta[@name='{name}']");
        if (node != null)
        {
            return node.GetAttributeValue("content", "");
        }
        return null;
    }
    #endregion

    #region Get Authors Logic
    /// <summary>
    /// Collects all <c>citation_author</c> meta tag values and returns them as a list.
    /// Returns <c>null</c> if no author tags are present.
    /// </summary>
    private List<string>? GetAuthors(HtmlDocument doc)
    {
        var authorNodes = doc.DocumentNode.SelectNodes($"//meta[@name='{_XPathMap[XPath.Author]}']");
        if (authorNodes != null && authorNodes.Count > 0)
        {
            var authors = new List<string>();
            foreach (var node in authorNodes)
            {
                string? author = node.GetAttributeValue("content", "").Trim();
                if (!string.IsNullOrEmpty(author))
                    authors.Add(author);
            }
            return authors;
        }
        return null;
    }
    #endregion

    #region Debug Printing Logic
    /// <summary>
    /// Prints a labeled metadata field to the console:
    /// the caption in yellow and the value in white.
    /// </summary>
    private static void PrintMeta(string caption, string? data)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"{caption}:\t");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"{data}");
        Console.ResetColor();
    }
    #endregion

    #region Publication Date Parsing Logic
    /// <summary>
    /// Parses a raw PMC publication date string against multiple known formats
    /// (full date, month-year, numeric, year-only). Returns <c>null</c> if none match.
    /// </summary>
    private DateTime? ParsePublishDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();

        string[] fullDateFormats = ["yyyy MMM dd", "yyyy MMMM dd", "yyyy MMM d", "yyyy MMMM d"];
        if (DateTime.TryParseExact(raw, fullDateFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var result))
            return result;

        string[] monthYearFormats = ["yyyy MMM", "yyyy MMMM"];
        if (DateTime.TryParseExact(raw, monthYearFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out result))
            return result;

        if (DateTime.TryParseExact(raw, ["yyyy MM dd", "yyyy M d", "yyyy MM d", "yyyy M dd"],
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out result))
            return result;

        if (DateTime.TryParseExact(raw, "yyyy",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out result))
            return result;

        return null;
    }
    #endregion

    #region Abstract Extraction Logic
    /// <summary>
    /// Locates the abstract section (by id <c>abstract1</c> or class <c>abstract</c>),
    /// collects its paragraphs — excluding keyword and footnote sub-sections —
    /// and returns the cleaned, joined text. Returns <c>null</c> if not found.
    /// </summary>
    private string? ExtractAbstract(HtmlDocument doc)
    {
        var section = doc.GetElementbyId("abstract1")
            ?? doc.DocumentNode.SelectSingleNode(_XPathMap[XPath.Abstract]);

        if (section == null)
            return null;

        var paragraphs = section.SelectNodes(
            ".//p[not(ancestor::section[contains(@class,'kwd')]) " +
                 "and not(ancestor::section[contains(@class,'fn')])]");

        if (paragraphs == null || paragraphs.Count == 0)
            return null;

        var lines = paragraphs
            .Select(p => System.Net.WebUtility.HtmlDecode(p.InnerText))
            .Select(t => System.Text.RegularExpressions.Regex.Replace(t.Trim(), @"\s+", " "))
            .Where(t => !string.IsNullOrWhiteSpace(t));

        return string.Join(" ", lines);
    }
    #endregion

    #region Keyword Extraction Logic
    /// <summary>
    /// Finds all sections with class <c>kwd</c>, splits their paragraph text
    /// on commas and semicolons, and returns a flat list of individual keywords.
    /// Returns <c>null</c> if no keyword sections exist.
    /// </summary>
    private List<string>? GetKeywords(HtmlDocument doc)
    {
        var kwdSections = doc.DocumentNode.SelectNodes(_XPathMap[XPath.Keywords]);
        if (kwdSections == null)
            return null;

        var keywords = new List<string>();
        foreach (var section in kwdSections)
        {
            var paraNodes = section.SelectNodes(".//p");
            if (paraNodes != null)
            {
                foreach (var para in paraNodes)
                {
                    string kwdText = System.Net.WebUtility.HtmlDecode(para.InnerText).Trim()
                        .Replace("Keywords:", "")
                        .Replace("Abbreviations:", "");
                    var parts = kwdText.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(k => k.Trim())
                        .Where(k => !string.IsNullOrEmpty(k))
                        .ToList();
                    if (parts.Count > 0)
                        keywords.AddRange(parts);
                }
            }
        }
        return keywords;
    }
    #endregion

    #region Section Extraction Logic
    /// <summary>
    /// Walks <paramref name="nodes"/> and builds a title→text map by grouping body content
    /// under the nearest preceding header. Recurses one level into child sections;
    /// at <c>depth 0</c>, skips <c>abstract</c> and <c>ref-list</c> sections.
    /// </summary>
    private SectionExtractionResult ExtractSectionsFromNodes(HtmlNodeCollection nodes, int depth = 0, short _sectionCount = 1)
    {
        var sections = new Dictionary<string, string>();
        var header = new StringBuilder();
        var body = new StringBuilder();

        void Flush()
        {
            string text = body.ToString().Trim();
            if (string.IsNullOrWhiteSpace(text)) { header.Clear(); body.Clear(); return; }

            string sectionKey = header.ToString().Trim();
            if (string.IsNullOrWhiteSpace(sectionKey)) sectionKey = "section_" + _sectionCount++;

            while (sections.ContainsKey(sectionKey)) sectionKey = $"{sectionKey}_{_sectionCount++}";

            string sectionText = System.Text.RegularExpressions.Regex.Replace(text, @"\.([A-Z])", ". $1");
            sectionText = sectionText.Replace("Open in a new tab", "").Trim();
            sections[sectionKey] = sectionText;
            header.Clear();
            body.Clear();
        }

        foreach (HtmlNode node in nodes)
        {
            string nodeName = node.Name.ToLower();

            if (node.NodeType != HtmlNodeType.Element) continue;

            if (depth == 0 && nodeName == "section")
            {
                var classes = node.GetAttributeValue("class", "").Split(' ');
                if (classes.Contains("abstract") || classes.Contains("ref-list"))
                    continue;
            }

            bool isHeader = nodeName is "title" or "b" or "strong" or "bold" ||
                            (nodeName.Length >= 2 && nodeName[0] == 'h' && char.IsDigit(nodeName[1]));
            if (isHeader)
            {
                if (body.Length > 0)
                    Flush();
                else
                    header.Clear();
                header.AppendLine(System.Net.WebUtility.HtmlDecode(node.InnerText.Trim()));
            }
            else if (nodeName != "section")
            {
                body.AppendLine(System.Net.WebUtility.HtmlDecode(node.InnerText.Trim()));
            }
            else if (depth < 1)
            {
                Flush();
                SectionExtractionResult extractResult = ExtractSectionsFromNodes(node.ChildNodes, 1, _sectionCount);
                _sectionCount = extractResult.SectionCount;
                if (extractResult.Sections != null)
                    foreach (var kvp in extractResult.Sections)
                        sections[kvp.Key] = kvp.Value;
            }
            else
            {
                // depth >= 1: flatten all children of nested section into body
                Flush();
                foreach (HtmlNode subNode in node.ChildNodes)
                {
                    if (subNode.NodeType != HtmlNodeType.Element) continue;
                    string? subText = subNode.InnerText?.Trim();
                    if (!string.IsNullOrEmpty(subText))
                        body.AppendLine(System.Net.WebUtility.HtmlDecode(subText));
                }
                Flush();
            }
        }

        Flush();
        return new SectionExtractionResult { Sections = sections, SectionCount = _sectionCount };
    }

    /// <summary>
    /// Locates the main article body (<c>section.body.main-article-body</c>) and
    /// delegates section extraction to <see cref="ExtractSectionsFromNodes"/>.
    /// Returns <c>null</c> if the body node is not found.
    /// </summary>
    private Dictionary<string, string>? ExtractSections(HtmlDocument doc)
    {
        var bodyNode = doc.DocumentNode.SelectSingleNode(_XPathMap[XPath.Section]);
        if (bodyNode == null)
            return null;

        var sections = new Dictionary<string, string>();
        var childNodes = bodyNode.ChildNodes;

        if (childNodes.Count == 0)
        {
            sections["full_text"] = System.Net.WebUtility.HtmlDecode(bodyNode.InnerText.Trim());
            return sections;
        }

        var extractResult = ExtractSectionsFromNodes(childNodes, 0);
        if (extractResult.Sections != null)
            foreach (var kvp in extractResult.Sections)
                sections[kvp.Key] = kvp.Value;

        return sections;
    }
    #endregion

    #region Extract Data
    /// <summary>
    /// Parses <paramref name="doc"/> and returns article metadata and body sections.
    /// DOM work runs synchronously; the task completes immediately.
    /// </summary>
    public Task<ArticleDTO> ExtractDataAsync(int pmcId, HtmlDocument doc)
        => Task.FromResult(ExtractArticleFromDocument(pmcId, doc));

    private ArticleDTO ExtractArticleFromDocument(int pmcId, HtmlDocument doc)
    {
        var title = doc.DocumentNode.SelectSingleNode(_XPathMap[XPath.Title])?.InnerText?.Trim();

        string? ncbiDomain = GetMeta(doc, _XPathMap[XPath.Domain]);
        string? ncbiType = GetMeta(doc, _XPathMap[XPath.Type]);
        string? ncbiPcid = GetMeta(doc, _XPathMap[XPath.PcId]);
        string? journalTitle = GetMeta(doc, _XPathMap[XPath.JournalTitle]);
        string? citationTitle = GetMeta(doc, _XPathMap[XPath.CitationTitle]);
        string? pubDateRaw = GetMeta(doc, _XPathMap[XPath.PublicationDate]);
        string? volume = GetMeta(doc, _XPathMap[XPath.Volume]);
        string? issue = GetMeta(doc, _XPathMap[XPath.Issue]);
        string? firstPage = GetMeta(doc, _XPathMap[XPath.FirstPage]);
        string? doi = GetMeta(doc, _XPathMap[XPath.Doi]);
        string? pmid = GetMeta(doc, _XPathMap[XPath.PmId]);
        string? fulltextUrl = GetMeta(doc, _XPathMap[XPath.FulltextUrl]);
        string? pdfUrl = GetMeta(doc, _XPathMap[XPath.PdfUrl]);
        string? ogTitle = GetMeta(doc, _XPathMap[XPath.OgTitle]);
        string? ogType = GetMeta(doc, _XPathMap[XPath.OgType]);

        string? abstractText = ExtractAbstract(doc);
        List<string>? authors = GetAuthors(doc);
        List<string>? keywords = GetKeywords(doc);

        bool isFullText = !string.IsNullOrEmpty(ncbiType) && !ncbiType.StartsWith("scan", StringComparison.OrdinalIgnoreCase);
        Dictionary<string, string>? sections = isFullText ? ExtractSections(doc) : null;

        var article = new ArticleDTO
        {
            Title = title,
            Doi = doi,
            PmId = int.TryParse(pmid, out var pmIdParsed) ? pmIdParsed : null,
            PmcId = pmcId,
            Journal = journalTitle,
            Volume = volume,
            FPage = firstPage,
            PublishDate = ParsePublishDate(pubDateRaw),
            Authors = authors,
            AbstractText = abstractText,
            Category = null,
            Keywords = keywords,
            ISSN = null,
            Issue = issue,
            LPage = null,
            Publisher = null,
            Sections = sections
        };

        //Console.WriteLine($"\n\n____________________________\n");
        //PrintMeta("title", title);
        //PrintMeta("ncbi_domain", ncbiDomain);
        //PrintMeta("ncbi_type", ncbiType);
        //PrintMeta("ncbi_pcid", ncbiPcid);
        //PrintMeta("citation_journal_title", journalTitle);
        //PrintMeta("citation_title", citationTitle);
        //if (authors != null)
        //    PrintMeta("citation_author", string.Join(", ", authors));
        //if (article.Keywords != null)
        //    PrintMeta("keywords", string.Join(", ", article.Keywords));
        //PrintMeta("citation_publication_date", article.PublishDate.ToString());
        //PrintMeta("citation_volume", volume);
        //PrintMeta("citation_firstpage", firstPage);
        //PrintMeta("citation_doi", doi);
        //PrintMeta("citation_pmid", pmid);
        //PrintMeta("citation_fulltext_html_url", fulltextUrl);
        //PrintMeta("citation_pdf_url", pdfUrl);
        //PrintMeta("og_title", ogTitle);
        //PrintMeta("og_type", ogType);
        //PrintMeta("abstract", abstractText);

        //Console.WriteLine("\n_____\nSections:");
        //if (sections != null)
        //{
        //    foreach (var section in sections)
        //    {
        //        Console.WriteLine($"{section.Key}: {section.Value}");
        //        Console.WriteLine("--------------------------------");
        //    }
        //}
        //else
        //{
        //    Console.WriteLine("Null");
        //}
        //Console.WriteLine("____________________________\n");

        return article;
    }
    #endregion

    #region Extract Data From File
    /// <summary>
    /// Parses a PMC HTML file at <paramref name="filePath"/>, extracts all article metadata
    /// (title, authors, DOI, dates, sections, keywords, abstract), prints a debug summary
    /// to the console, and returns the result as an <see cref="ArticleDTO"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="filePath"/> is null or empty.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist at the given path.</exception>
    /// <exception cref="IOException">Thrown when the file cannot be read due to an I/O error.</exception>
    public async Task<ArticleDTO> ExtractDataFromFileAsync(FileInfo fileInfo, CancellationToken cancellationToken = default)
    {
        int pmcId = int.TryParse(fileInfo.Name.Replace("pmc", "").Replace(".html", ""), out var pmcIdParsed) ? pmcIdParsed : 0;

        string html;
        try
        {
            await using var stream = new FileStream(
                fileInfo.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            html = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Could not read file: {fileInfo.FullName}", ex);
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return await ExtractDataAsync(pmcId, doc).ConfigureAwait(false);
    }
    #endregion

    #region Extract Data From URL
    /// <summary>
    /// Lazily builds an HttpClient configured for PMC scraping:
    /// - automatic gzip/deflate/brotli decompression (PMC always serves compressed HTML)
    /// - shared CookieContainer so anti-bot cookies persist between requests
    /// - automatic redirect handling
    /// Seeds the cookie jar with whatever was passed via the constructor.
    /// </summary>
    private HttpClient GetOrCreateHttpClient()
    {
        if (_httpClient != null) return _httpClient;

        _cookieContainer = new CookieContainer();
        if (_cookies != null && _cookies.Count > 0)
        {
            foreach (var kv in _cookies)
            {
                try { _cookieContainer.Add(new Uri("https://pmc.ncbi.nlm.nih.gov"), new Cookie(kv.Key, kv.Value)); }
                catch { /* malformed cookie names are silently dropped */ }
            }
        }

        _httpHandler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            CookieContainer        = _cookieContainer,
            UseCookies             = true,
            AllowAutoRedirect      = true,
            MaxAutomaticRedirections = 10,
        };

        _httpClient = new HttpClient(_httpHandler) { Timeout = TimeSpan.FromSeconds(30) };
        return _httpClient;
    }

    /// <summary>
    /// Adds a full set of headers that match a real Chrome browser navigating to a PMC article.
    /// Without Sec-Fetch-*, Sec-Ch-Ua, Accept-Encoding and Referer, PMC's anti-bot layer
    /// returns either 429 or a stripped HTML page (empty title), which is what triggers
    /// the "Empty" retries.
    /// </summary>
    private void ApplyBrowserHeaders(HttpRequestMessage request, string url)
    {
        // User-Agent / Accept / Accept-Language come from the constructor-provided dictionary
        // so we keep whatever was harvested at warmup time. Everything else is fixed.
        if (_headers != null)
        {
            foreach (var h in _headers)
                request.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        // Standard browser navigation headers
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
        request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        request.Headers.TryAddWithoutValidation("DNT", "1");
        request.Headers.TryAddWithoutValidation("Connection", "keep-alive");

        // Client hints — required by Cloudflare/Akamai for Chrome UA fingerprint match
        request.Headers.TryAddWithoutValidation("sec-ch-ua",
            "\"Chromium\";v=\"124\", \"Google Chrome\";v=\"124\", \"Not-A.Brand\";v=\"99\"");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");

        // Fetch metadata — we say we navigated to this article from the PMC home
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");

        // Referer significantly reduces empty/banned responses
        request.Headers.TryAddWithoutValidation("Referer", "https://pmc.ncbi.nlm.nih.gov/");
    }

    public async Task<ArticleDTO?> ExtractDataFromUrlAsync(int pmcId, string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL must not be null or empty.", nameof(url));

        ArticleDTO? result = null;

        var http = GetOrCreateHttpClient();

        // Total attempts is now bounded and small. PMC will not magically start serving us
        // after 15 retries; if 5 fail we move on so we don't waste the batch's time budget.
        const int MaxAttempts = 5;
        const int MaxEmptyAttempts = 2;   // empty HTML is a soft-ban; bail fast
        int emptyCount = 0;

        try
        {
            for (int i = 0; i < MaxAttempts; i++)
            {
                await TicketManager.WaitForTicketAsync(cancellationToken).ConfigureAwait(false);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyBrowserHeaders(request, url);

                var doc = new HtmlDocument();
                try
                {
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Try {i + 1}\t-\tPMC{pmcId}\t [429 Ban]\t- Delay: {TicketManager._delay} - Best: {TicketManager._bestDelay}");
                        Console.ForegroundColor = ConsoleColor.White;
                        TicketManager.RecordOutcome(RequestOutcome.HttpBan);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Try {i + 1}\t-\tPMC{pmcId}\t [HTTP {(int)response.StatusCode}]\t- Delay: {TicketManager._delay} - Best: {TicketManager._bestDelay}");
                        TicketManager.RecordOutcome(RequestOutcome.HttpError);
                        continue;
                    }

                    var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    doc.LoadHtml(html);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (TaskCanceledException)
                {
                    Console.WriteLine($"Try {i + 1}\t-\tPMC{pmcId}\t [Timeout]\t- Delay: {TicketManager._delay} - Best: {TicketManager._bestDelay}");
                    TicketManager.RecordOutcome(RequestOutcome.Timeout);
                    continue;
                }
                catch (HttpRequestException)
                {
                    Console.WriteLine($"Try {i + 1}\t-\tPMC{pmcId}\t [HttpError]\t- Delay: {TicketManager._delay} - Best: {TicketManager._bestDelay}");
                    TicketManager.RecordOutcome(RequestOutcome.HttpError);
                    continue;
                }
                catch (Exception)
                {
                    Console.WriteLine($"Try {i + 1}\t-\tPMC{pmcId}\t [Error]\t- Delay: {TicketManager._delay} - Best: {TicketManager._bestDelay}");
                    TicketManager.RecordOutcome(RequestOutcome.OtherError);
                    continue;
                }

                var parsed = await ExtractDataAsync(pmcId, doc).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(parsed.Title))
                {
                    TicketManager.RecordOutcome(RequestOutcome.Success);
                    return parsed;
                }
                result = parsed;

                emptyCount++;
                Console.ForegroundColor = emptyCount == 1 ? ConsoleColor.Yellow : ConsoleColor.Red;
                Console.WriteLine($"Try {i + 1}\t-\tPMC{pmcId}\t [Empty]\t- Delay: {TicketManager._delay} - Best: {TicketManager._bestDelay}");
                Console.ForegroundColor = ConsoleColor.White;
                TicketManager.RecordOutcome(RequestOutcome.EmptyResponse);

                // Soft ban: hammering the same URL only makes it worse. Skip after a couple of tries.
                if (emptyCount >= MaxEmptyAttempts)
                {
                    Console.WriteLine($"Skip\t-\tPMC{pmcId}\t [Empty x{emptyCount}] - https://pmc.ncbi.nlm.nih.gov/articles/PMC{pmcId}/");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("█░█░█░█░█░█░█░█░█░█░█░█░█░█░");
            Console.WriteLine($"Error in PMC{pmcId}");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(ex.Message + "\n\n");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("█░█░█░█░█░█░█░█░█░█░█░█░█░█░");
            Console.ForegroundColor = ConsoleColor.White;
        }

        // After all retries we still don't have a Title — surface the failure as null
        // so the caller (batch submit) can place this PmcId into errorDict instead of
        // silently submitting a half-empty ArticleDTO.
        if (result == null || string.IsNullOrEmpty(result.Title))
            return null;
        return result;
    }

    public async Task<ArticleDTO?> GetArticleAsync(int pmcId, CancellationToken cancellationToken = default)
    {
        string url = $"https://pmc.ncbi.nlm.nih.gov/articles/PMC{pmcId}/";
        return await ExtractDataFromUrlAsync(pmcId, url, cancellationToken).ConfigureAwait(false);
    }
    #endregion

    #region Extract Data Batch
    /// <summary>
    /// Fetches each ID with a hard concurrency cap (see <see cref="_concurrencyGate"/>).
    /// Replaces the previous index*delay staggering, which collapsed back to a thundering herd
    /// once all delays expired and which behaved badly under Colab's network jitter.
    /// </summary>
    public async Task<IEnumerable<ArticleDTO>> GetArticlesAsync(int[] pmcIds, int splitCount = 5, int restTimeMs = 0, bool showProgressBar = true, CancellationToken cancellationToken = default)
    {
        async Task<ArticleDTO?> RunGatedAsync(int pmcId)
        {
            await _concurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await GetArticleAsync(pmcId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _concurrencyGate.Release();
            }
        }

        var tasks = pmcIds.Select(RunGatedAsync).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var articles = results.Where(a => a != null).Select(a => a!).ToList();

        foreach (var article in articles)
        {
            string title = string.IsNullOrEmpty(article.Title)
                ? ""
                : article.Title.Length > 50
                    ? article.Title.Substring(0, 49) + " ..."
                    : article.Title;

            Console.WriteLine($"{title} (PMC{article.PmcId}) \tDelay: {TicketManager._delay} - Best: {TicketManager._bestDelay}");
        }
        return articles;
    }
    #endregion

    #region Save to Json
    public async Task SaveToJsonAsync(List<ArticleDTO> articles, FileInfo file)
    {
        string content = JsonConvert.SerializeObject(
            articles,
            Newtonsoft.Json.Formatting.Indented
        );

        await File.WriteAllTextAsync(file.FullName, content);
    }
    #endregion


    
    #region IDisposable Support
    /// <summary>
    /// Releases managed resources. Called by <see cref="IDisposable.Dispose"/>.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                _httpClient?.Dispose();
                _httpClient = null;
                _httpHandler?.Dispose();
                _httpHandler = null;
            }
            disposedValue = true;
        }
    }

    void IDisposable.Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    #endregion
}