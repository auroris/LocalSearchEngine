using HtmlAgilityPack;
using System.Text;
using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Crawling.Extraction;

/// <summary>
/// Extracts structured elements (title, headings, text, robots directives, canonical aliases, and outlinks) from raw HTML, PDF, or DOCX document bodies.
/// </summary>
public static class ContentExtractor
{
    private static readonly char[] WordSeparators = { ' ', '\n', '\r', '\t' };

    static ContentExtractor()
    {
        // Lets Encoding.GetEncoding resolve legacy labels (windows-1252, etc.) when a page
        // declares one via <meta charset>. Without this provider only a few encodings exist.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Represents the structured components extracted from an HTML page for indexing.
    /// </summary>
    public sealed class HtmlAnalysis
    {
        /// <summary>Gets or sets the title of the HTML page.</summary>
        public string? Title;
        /// <summary>Gets or sets the compiled heading text from the HTML page.</summary>
        public string Headings = string.Empty;
        /// <summary>Gets or sets the main visible text extracted from the HTML page.</summary>
        public string Text = string.Empty;
        /// <summary>Gets or sets a value indicating whether the page has a noindex directive.</summary>
        public bool NoIndex;
        /// <summary>Gets or sets a value indicating whether the page has a nofollow directive.</summary>
        public bool NoFollow;
        /// <summary>Gets or sets the canonical URL alias specified by the page, if any.</summary>
        public string? CanonicalAlias;
        /// <summary>Gets or sets the list of absolute outlinks discovered on the page.</summary>
        public List<string> Outlinks = new();
    }

    /// <summary>
    /// Parses an HTML body to extract indexable page components.
    /// </summary>
    /// <param name="body">The raw byte array containing the HTML content.</param>
    /// <param name="httpCharset">The character set specified in the HTTP Content-Type header, if any.</param>
    /// <param name="xRobotsTag">The X-Robots-Tag HTTP header value, if any.</param>
    /// <param name="currentUrl">The current URL of the page being crawled.</param>
    /// <param name="allowedHosts">The host rules that are in-scope for the crawl.</param>
    /// <param name="robotsCache">A dictionary cache of robots.txt rules keyed by origin (scheme://host:port).</param>
    /// <param name="userAgentToken">The lowercase user agent token of this crawler.</param>
    /// <returns>An <see cref="HtmlAnalysis"/> object containing the extracted components.</returns>
    public static HtmlAnalysis AnalyzeHtml(
        byte[] body, string? httpCharset, string? xRobotsTag, string currentUrl,
        AllowedHosts allowedHosts, IReadOnlyDictionary<string, RobotsRules> robotsCache,
        string userAgentToken)
    {
        var doc = LoadHtml(body, httpCharset);

        var analysis = new HtmlAnalysis
        {
            Title = ExtractTitle(doc),
        };

        var (noIndex, noFollow) = ParseRobotsDirectives(doc, xRobotsTag, userAgentToken);
        analysis.NoIndex = noIndex;
        analysis.NoFollow = noFollow;
        analysis.CanonicalAlias = ResolveCanonicalAlias(doc, currentUrl, allowedHosts);

        // Strip boilerplate BEFORE harvesting headings/text/links so footer "Quick Links"
        // headings and nav chrome don't pollute the index. noscript (enable-JS banners),
        // template (inert DOM), and aside (related links/ads) are chrome too. Form *controls*
        // are stripped individually rather than whole <form> elements: platforms like Oracle
        // APEX and ASP.NET WebForms wrap the entire page body in one form, so removing forms
        // wholesale would throw away the page content.
        var nodesToRemove = doc.DocumentNode.SelectNodes(
            "//script|//style|//nav|//footer|//header|//svg|//noscript|//template|//aside" +
            "|//input|//select|//textarea|//button|//label|//datalist|//output");
        if (nodesToRemove != null)
        {
            foreach (var node in nodesToRemove) node.Remove();
        }

        analysis.Headings = ExtractHeadings(doc);

        var texts = doc.DocumentNode.DescendantsAndSelf()
            .Where(n => n.NodeType == HtmlNodeType.Text)
            .Select(n => HtmlEntity.DeEntitize(n.InnerText))
            .Where(t => !string.IsNullOrWhiteSpace(t));
        analysis.Text = CollapseWhitespace(string.Join(" ", texts));

        if (!analysis.NoFollow)
        {
            analysis.Outlinks = ExtractInScopeLinks(doc, currentUrl, allowedHosts, robotsCache);
        }

        return analysis;
    }

    /// <summary>
    /// Decodes and parses an HTML body. A charset from the HTTP header is authoritative when
    /// present. Otherwise the document is parsed once (BOM-sniffed, defaulting to UTF-8) and,
    /// when the page itself declares a different encoding via <c>&lt;meta charset&gt;</c> or
    /// <c>&lt;meta http-equiv="Content-Type"&gt;</c>, re-parsed with that encoding —
    /// HtmlAgilityPack records the declaration as <see cref="HtmlDocument.DeclaredEncoding"/>
    /// but never re-decodes a stream on its own.
    /// </summary>
    /// <param name="body">The raw bytes of the HTML page.</param>
    /// <param name="httpCharset">The charset from the HTTP Content-Type header, if any.</param>
    /// <returns>The parsed document.</returns>
    private static HtmlDocument LoadHtml(byte[] body, string? httpCharset)
    {
        var doc = new HtmlDocument();
        var headerEncoding = ResolveEncoding(httpCharset);
        using (var stream = new MemoryStream(body))
        {
            if (headerEncoding != null)
            {
                doc.Load(stream, headerEncoding);
                return doc;
            }
            doc.Load(stream, detectEncodingFromByteOrderMarks: true);
        }

        var declared = doc.DeclaredEncoding;
        var used = doc.StreamEncoding ?? Encoding.UTF8;
        // A meta tag claiming UTF-16/32 is wrong by construction (it was just read from
        // ASCII-compatible bytes); per the HTML5 spec such declarations are ignored.
        bool selfContradictory = declared is { CodePage: 1200 or 1201 or 12000 or 12001 };
        if (declared != null && !selfContradictory && declared.CodePage != used.CodePage)
        {
            using var reload = new MemoryStream(body);
            doc.Load(reload, declared);
        }
        return doc;
    }

    /// <summary>
    /// Extracts all crawlable in-scope links from an HTML document.
    /// </summary>
    private static List<string> ExtractInScopeLinks(
        HtmlDocument doc, string currentUrl,
        AllowedHosts allowedHosts, IReadOnlyDictionary<string, RobotsRules> robotsCache)
    {
        var result = new List<string>();
        var linkNodes = doc.DocumentNode.SelectNodes("//a[@href]");
        if (linkNodes is null) return result;

        var baseForLinks = new Uri(currentUrl);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in linkNodes)
        {
            var rel = link.GetAttributeValue("rel", "");
            if (rel.Contains("nofollow", StringComparison.OrdinalIgnoreCase)) continue;

            var href = link.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(baseForLinks, href, out var absoluteUri)) continue;
            if (!allowedHosts.IsAllowed(absoluteUri)) continue;

            // No extension filtering here: whether a fetched body is indexable is decided by
            // its Content-Type (or sniffed bytes), never by how its URL looks.
            var normalizedUrl = UrlNormalizer.Normalize(absoluteUri);

            var linkRobots = robotsCache.TryGetValue(UrlOrigin.Key(absoluteUri), out var lr) ? lr : RobotsRules.AllowAll;
            if (!CrawlPolicy.IsAllowedByRobots(normalizedUrl, linkRobots)) continue;

            if (seen.Add(normalizedUrl)) result.Add(normalizedUrl);
        }

        return result;
    }

    /// <summary>
    /// Parses the HTML meta tags and HTTP headers for robots directives.
    /// </summary>
    private static (bool NoIndex, bool NoFollow) ParseRobotsDirectives(HtmlDocument doc, string? xRobotsTag, string userAgentToken)
    {
        bool noIndex = false, noFollow = false;

        void Apply(string content)
        {
            foreach (var raw in content.Split(','))
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "none": noIndex = true; noFollow = true; break;
                    case "noindex": noIndex = true; break;
                    case "nofollow": noFollow = true; break;
                }
            }
        }

        var metas = doc.DocumentNode.SelectNodes("//meta[@name]");
        if (metas != null)
        {
            foreach (var meta in metas)
            {
                var name = meta.GetAttributeValue("name", "").Trim().ToLowerInvariant();
                if (name == "robots" || name == userAgentToken)
                {
                    Apply(HtmlEntity.DeEntitize(meta.GetAttributeValue("content", "")));
                }
            }
        }

        if (!string.IsNullOrEmpty(xRobotsTag))
        {
            Apply(StripXRobotsAgent(xRobotsTag, userAgentToken));
        }

        return (noIndex, noFollow);
    }

    /// <summary>
    /// Strips any user agent prefix from an X-Robots-Tag HTTP header value, verifying if the rule applies to this bot.
    /// </summary>
    private static string StripXRobotsAgent(string value, string userAgentToken)
    {
        int colon = value.IndexOf(':');
        if (colon < 0) return value;

        var prefix = value[..colon].Trim().ToLowerInvariant();
        if (prefix is "noindex" or "nofollow" or "none" or "all" or "index" or "follow") return value;
        return prefix == userAgentToken ? value[(colon + 1)..] : string.Empty;
    }

    /// <summary>
    /// Resolves the canonical URL specified by a rel='canonical' link tag.
    /// </summary>
    private static string? ResolveCanonicalAlias(HtmlDocument doc, string currentUrl, AllowedHosts allowedHosts)
    {
        var links = doc.DocumentNode.SelectNodes("//link[@rel]");
        if (links is null) return null;

        foreach (var link in links)
        {
            if (!string.Equals(link.GetAttributeValue("rel", "").Trim(), "canonical", StringComparison.OrdinalIgnoreCase))
                continue;

            var href = link.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(href)) return null;
            if (!Uri.TryCreate(new Uri(currentUrl), href, out var canonicalUri)) return null;

            var normalized = UrlNormalizer.Normalize(canonicalUri);
            if (string.Equals(normalized, currentUrl, StringComparison.OrdinalIgnoreCase)) return null; // self-canonical
            if (!allowedHosts.IsAllowed(canonicalUri)) return null;                                      // out of scope
            return normalized;
        }

        return null;
    }

    /// <summary>
    /// Extracts document title and content text from a PDF document body.
    /// </summary>
    public static (string? Title, string Text) ExtractPdf(byte[] body)
    {
        using var stream = new MemoryStream(body);
        using var pdfReader = new iText.Kernel.Pdf.PdfReader(stream);
        using var pdfDoc = new iText.Kernel.Pdf.PdfDocument(pdfReader);
        var title = CleanTitle(pdfDoc.GetDocumentInfo()?.GetTitle());
        var sb = new StringBuilder();
        for (int i = 1; i <= pdfDoc.GetNumberOfPages(); i++)
        {
            var page = pdfDoc.GetPage(i);
            sb.Append(iText.Kernel.Pdf.Canvas.Parser.PdfTextExtractor.GetTextFromPage(page, new iText.Kernel.Pdf.Canvas.Parser.Listener.SimpleTextExtractionStrategy()));
            sb.Append(' ');
        }
        return (title, CollapseWhitespace(sb.ToString()));
    }

    /// <summary>
    /// Extracts document title and content text from a Microsoft Word (DOCX) document body.
    /// </summary>
    public static (string? Title, string Text) ExtractDocx(byte[] body)
    {
        using var stream = new MemoryStream(body);
        var doc = new NPOI.XWPF.UserModel.XWPFDocument(stream);
        var title = CleanTitle(doc.GetProperties()?.CoreProperties?.Title);
        var extractor = new NPOI.XWPF.Extractor.XWPFWordExtractor(doc);
        return (title, CollapseWhitespace(extractor.Text));
    }

    /// <summary>
    /// Cleans and collapses whitespace in a document-metadata title.
    /// </summary>
    private static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var cleaned = CollapseWhitespace(title);
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    /// <summary>
    /// Extracts the page title from the HTML document's &lt;title&gt; tag.
    /// </summary>
    private static string? ExtractTitle(HtmlDocument doc)
    {
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        if (titleNode is null || string.IsNullOrWhiteSpace(titleNode.InnerText)) return null;
        var title = CollapseWhitespace(HtmlEntity.DeEntitize(titleNode.InnerText));
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    /// <summary>
    /// Extracts all heading elements (&lt;h1&gt; through &lt;h6&gt;) and the page title, merging them.
    /// </summary>
    private static string ExtractHeadings(HtmlDocument doc)
    {
        var titleText = doc.DocumentNode.SelectSingleNode("//title")?.InnerText;
        var hNodes = doc.DocumentNode.SelectNodes("//h1|//h2|//h3|//h4|//h5|//h6");
        
        var headings = new List<string>();
        if (!string.IsNullOrWhiteSpace(titleText))
        {
            headings.Add(HtmlEntity.DeEntitize(titleText));
        }
        if (hNodes != null)
        {
            headings.AddRange(hNodes.Select(n => HtmlEntity.DeEntitize(n.InnerText)));
        }

        return CollapseWhitespace(string.Join(" ", headings));
    }

    /// <summary>
    /// Collapses multiple consecutive whitespace characters into a single space.
    /// </summary>
    private static string CollapseWhitespace(string text) =>
        string.Join(" ", text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Attempts to resolve a character encoding name into an <see cref="Encoding"/> instance.
    /// </summary>
    private static Encoding? ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return null;
        try
        {
            return Encoding.GetEncoding(charset.Trim('"', '\''));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
