using HtmlAgilityPack;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalSearchEngine.Core.Crawling;

/// <summary>
/// Extracts structured elements (title, headings, text, robots directives, canonical aliases, and outlinks) from raw HTML, PDF, or DOCX document bodies.
/// </summary>
public static class ContentExtractor
{
    private static readonly char[] WordSeparators = { ' ', '\n', '\r', '\t' };

    private static readonly Regex CharsetRegex = new(
        "charset\\s*=\\s*[\"']?([a-zA-Z0-9_\\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
    /// <param name="allowedHosts">The set of hostnames that are in-scope for the crawl.</param>
    /// <param name="robotsCache">A dictionary cache of robots.txt rules indexed by hostname.</param>
    /// <param name="userAgentToken">The lowercase user agent token of this crawler.</param>
    /// <returns>An <see cref="HtmlAnalysis"/> object containing the extracted components.</returns>
    public static HtmlAnalysis AnalyzeHtml(
        byte[] body, string? httpCharset, string? xRobotsTag, string currentUrl,
        IReadOnlySet<string> allowedHosts, IReadOnlyDictionary<string, RobotsRules> robotsCache,
        string userAgentToken)
    {
        var html = DecodeHtml(body, httpCharset);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

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
        // template (inert DOM), form controls, and aside (related links/ads) are chrome too.
        var nodesToRemove = doc.DocumentNode.SelectNodes("//script|//style|//nav|//footer|//header|//svg|//noscript|//template|//form|//aside");
        if (nodesToRemove != null)
        {
            foreach (var node in nodesToRemove) node.Remove();
        }

        analysis.Headings = ExtractHeadings(doc);
        analysis.Text = ExtractVisibleText(doc.DocumentNode);

        if (!analysis.NoFollow)
        {
            analysis.Outlinks = ExtractInScopeLinks(doc, currentUrl, allowedHosts, robotsCache);
        }

        return analysis;
    }

    /// <summary>
    /// Extracts all crawlable in-scope links from an HTML document.
    /// </summary>
    /// <param name="doc">The HTML document to extract links from.</param>
    /// <param name="currentUrl">The base URL of the document for resolving relative links.</param>
    /// <param name="allowedHosts">The set of hostnames allowed within the crawl scope.</param>
    /// <param name="robotsCache">The cached robots rules for check restrictions.</param>
    /// <returns>A list of resolved and normalized absolute URLs.</returns>
    private static List<string> ExtractInScopeLinks(
        HtmlDocument doc, string currentUrl,
        IReadOnlySet<string> allowedHosts, IReadOnlyDictionary<string, RobotsRules> robotsCache)
    {
        var result = new List<string>();
        var linkNodes = doc.DocumentNode.SelectNodes("//a[@href]");
        if (linkNodes is null) return result;

        var baseForLinks = new Uri(currentUrl);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in linkNodes)
        {
            var rel = link.GetAttributeValue("rel", string.Empty);
            if (rel.Contains("nofollow", StringComparison.OrdinalIgnoreCase)) continue;

            var href = link.GetAttributeValue("href", string.Empty);
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(baseForLinks, href, out var absoluteUri)) continue;
            if (!allowedHosts.Contains(absoluteUri.Host)) continue;

            var normalizedUrl = UrlNormalizer.Normalize(absoluteUri);
            if (!CrawlPolicy.IsIndexableExtension(normalizedUrl)) continue;

            var linkRobots = robotsCache.TryGetValue(absoluteUri.Host, out var lr) ? lr : RobotsRules.AllowAll;
            if (!CrawlPolicy.IsAllowedByRobots(normalizedUrl, linkRobots)) continue;

            if (seen.Add(normalizedUrl)) result.Add(normalizedUrl);
        }

        return result;
    }

    /// <summary>
    /// Parses the HTML meta tags and HTTP headers for robots directives.
    /// </summary>
    /// <param name="doc">The HTML document to parse.</param>
    /// <param name="xRobotsTag">The X-Robots-Tag HTTP header value, if any.</param>
    /// <param name="userAgentToken">The lowercase user agent token of this crawler.</param>
    /// <returns>A tuple indicating if noindex and nofollow directives are set.</returns>
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
                var name = meta.GetAttributeValue("name", string.Empty).Trim().ToLowerInvariant();
                if (name == "robots" || name == userAgentToken)
                {
                    Apply(HtmlEntity.DeEntitize(meta.GetAttributeValue("content", string.Empty)));
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
    /// <param name="value">The X-Robots-Tag header value.</param>
    /// <param name="userAgentToken">The lowercase user agent token of this crawler.</param>
    /// <returns>The directive part of the tag if applicable, or an empty string.</returns>
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
    /// <param name="doc">The HTML document to parse.</param>
    /// <param name="currentUrl">The current URL of the page.</param>
    /// <param name="allowedHosts">The set of hosts in-scope for crawling.</param>
    /// <returns>The absolute normalized canonical URL if present and valid; otherwise, <c>null</c>.</returns>
    private static string? ResolveCanonicalAlias(HtmlDocument doc, string currentUrl, IReadOnlySet<string> allowedHosts)
    {
        var links = doc.DocumentNode.SelectNodes("//link[@rel]");
        if (links is null) return null;

        foreach (var link in links)
        {
            if (!string.Equals(link.GetAttributeValue("rel", string.Empty).Trim(), "canonical", StringComparison.OrdinalIgnoreCase))
                continue;

            var href = link.GetAttributeValue("href", string.Empty);
            if (string.IsNullOrWhiteSpace(href)) return null;
            if (!Uri.TryCreate(new Uri(currentUrl), href, out var canonicalUri)) return null;

            var normalized = UrlNormalizer.Normalize(canonicalUri);
            if (string.Equals(normalized, currentUrl, StringComparison.OrdinalIgnoreCase)) return null; // self-canonical
            if (!allowedHosts.Contains(canonicalUri.Host)) return null;                                  // out of scope
            if (!CrawlPolicy.IsIndexableExtension(normalized)) return null;
            return normalized;
        }

        return null;
    }

    /// <summary>
    /// Extracts document title and content text from a PDF document body.
    /// </summary>
    /// <param name="body">The raw bytes of the PDF document.</param>
    /// <returns>A tuple containing the metadata title (if found) and the extracted text content.</returns>
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
    /// <param name="body">The raw bytes of the DOCX document.</param>
    /// <returns>A tuple containing the core metadata title (if found) and the extracted text content.</returns>
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
    /// <param name="title">The raw title string to clean.</param>
    /// <returns>The cleaned title, or <c>null</c> if it is null or empty.</returns>
    private static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var cleaned = CollapseWhitespace(title);
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    /// <summary>
    /// Extracts the page title from the HTML document's &lt;title&gt; tag.
    /// </summary>
    /// <param name="doc">The HTML document.</param>
    /// <returns>The cleaned title string, or <c>null</c> if not present.</returns>
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
    /// <param name="doc">The HTML document.</param>
    /// <returns>A single space-separated string containing all heading text.</returns>
    private static string ExtractHeadings(HtmlDocument doc)
    {
        var headingTexts = new List<string>();
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        if (titleNode != null && !string.IsNullOrWhiteSpace(titleNode.InnerText))
        {
            headingTexts.Add(HtmlEntity.DeEntitize(titleNode.InnerText).Trim());
        }

        var hNodes = doc.DocumentNode.SelectNodes("//h1|//h2|//h3|//h4|//h5|//h6");
        if (hNodes != null)
        {
            foreach (var node in hNodes)
            {
                if (!string.IsNullOrWhiteSpace(node.InnerText))
                {
                    headingTexts.Add(HtmlEntity.DeEntitize(node.InnerText).Trim());
                }
            }
        }

        return CollapseWhitespace(string.Join(" ", headingTexts));
    }

    /// <summary>
    /// Recursively walks the text nodes of an HTML element to gather visible text.
    /// </summary>
    /// <param name="root">The root HTML node to process.</param>
    /// <returns>A space-separated string containing the visible text.</returns>
    private static string ExtractVisibleText(HtmlNode root)
    {
        var sb = new StringBuilder();
        foreach (var node in root.DescendantsAndSelf())
        {
            if (node.NodeType != HtmlNodeType.Text) continue;
            var decoded = HtmlEntity.DeEntitize(node.InnerText);
            if (string.IsNullOrWhiteSpace(decoded)) continue;
            sb.Append(decoded.Trim()).Append(' ');
        }
        return CollapseWhitespace(sb.ToString());
    }

    /// <summary>
    /// Collapses multiple consecutive whitespace characters into a single space.
    /// </summary>
    /// <param name="text">The raw text string.</param>
    /// <returns>The cleaned text with collapsed whitespace.</returns>
    private static string CollapseWhitespace(string text) =>
        string.Join(" ", text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Decodes the HTML bytes into a string, resolving encoding using http headers or sniffing metadata.
    /// </summary>
    /// <param name="bytes">The raw HTML page bytes.</param>
    /// <param name="charset">The charset specified in HTTP headers, if any.</param>
    /// <returns>The decoded HTML string.</returns>
    private static string DecodeHtml(byte[] bytes, string? charset)
    {
        var encoding = ResolveEncoding(charset) ?? ResolveEncoding(SniffCharset(bytes));
        return (encoding ?? Encoding.UTF8).GetString(bytes);
    }

    /// <summary>
    /// Attempts to resolve a character encoding name into an <see cref="Encoding"/> instance.
    /// </summary>
    /// <param name="charset">The name of the encoding character set.</param>
    /// <returns>An <see cref="Encoding"/> instance if resolved; otherwise, <c>null</c>.</returns>
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

    /// <summary>
    /// Sniffs the first 4096 bytes of the page body for a &lt;meta charset&gt; tag.
    /// </summary>
    /// <param name="bytes">The raw page bytes.</param>
    /// <returns>The detected charset name, or <c>null</c> if not found.</returns>
    private static string? SniffCharset(byte[] bytes)
    {
        int len = Math.Min(bytes.Length, 4096);
        var head = Encoding.Latin1.GetString(bytes, 0, len);
        int headEnd = head.IndexOf("</head", StringComparison.OrdinalIgnoreCase);
        if (headEnd >= 0) head = head[..headEnd];
        var match = CharsetRegex.Match(head);
        return match.Success ? match.Groups[1].Value : null;
    }
}
