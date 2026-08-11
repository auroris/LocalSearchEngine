using HtmlAgilityPack;
using System.Text;
using LocalSearchEngine.Core.Crawling.Policies;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace LocalSearchEngine.Core.Crawling.Extraction;

/// <summary>
/// Turns a fetched document body into the structured pieces the index needs. For HTML it decodes the
/// bytes with the right charset, strips scripts, nav, footers, and other chrome so they never reach the
/// index, then harvests the title, headings, and visible text, reads the page's robots directives and
/// canonical link, and collects its links — split into in-scope outlinks (crawlable) and off-site links
/// (kept only for optional verification). PDF and DOCX bodies are routed to their own extractors for
/// title and text. Stateless: it reads bytes in and returns values, contacting nothing.
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
        /// <summary>Gets or sets the list of absolute in-scope outlinks discovered on the page.</summary>
        public List<string> Outlinks = new();
        /// <summary>
        /// Gets or sets the exact resolved URIs of <see cref="Outlinks"/>, index-aligned 1:1.
        /// The normalized string is the link's identity (dedup, storage); this is what actually
        /// goes on the wire — an href's original percent-escaping survives here, where the
        /// display-form string would re-encode it on re-parse.
        /// </summary>
        public List<Uri> OutlinkUris = new();
        /// <summary>Gets or sets the absolute off-site (out-of-scope) http(s) links on the page, kept for optional link verification.</summary>
        public List<string> OffsiteLinks = new();
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
            ExtractLinks(doc, currentUrl, allowedHosts, robotsCache, analysis);
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
    /// Reads a node's href as a parseable URL string. HtmlAgilityPack's GetAttributeValue returns the
    /// attribute's raw text, so an href that (correctly) HTML-escapes its ampersands — the only valid
    /// way to write one in markup — arrives with the entities intact. They must be decoded before the
    /// value is parsed as a URL, or the escaped ampersand travels into the stored URL and corrupts the
    /// query string, 404ing the link.
    /// </summary>
    private static string GetHref(HtmlNode node) =>
        HtmlEntity.DeEntitize(node.GetAttributeValue("href", "")) ?? string.Empty;

    /// <summary>
    /// Extracts the document's links into <see cref="HtmlAnalysis.Outlinks"/> (in-scope, crawlable)
    /// and <see cref="HtmlAnalysis.OffsiteLinks"/> (out-of-scope http(s) targets, kept only so an
    /// optional end-of-crawl pass can verify they still resolve — they are never crawled).
    /// </summary>
    private static void ExtractLinks(
        HtmlDocument doc, string currentUrl,
        AllowedHosts allowedHosts, IReadOnlyDictionary<string, RobotsRules> robotsCache,
        HtmlAnalysis analysis)
    {
        var linkNodes = doc.DocumentNode.SelectNodes("//a[@href]");
        if (linkNodes is null) return;

        var baseForLinks = new Uri(currentUrl);
        var seenInScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenOffsite = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in linkNodes)
        {
            var rel = link.GetAttributeValue("rel", "");
            if (rel.Contains("nofollow", StringComparison.OrdinalIgnoreCase)) continue;

            var href = GetHref(link);
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(baseForLinks, href, out var absoluteUri)) continue;

            // Only http(s) targets are crawlable or checkable; mailto:, tel:, javascript:, etc. are not links to a page.
            if (absoluteUri.Scheme != Uri.UriSchemeHttp && absoluteUri.Scheme != Uri.UriSchemeHttps) continue;

            // No extension filtering here: whether a fetched body is indexable is decided by
            // its Content-Type (or sniffed bytes), never by how its URL looks.
            var normalizedUrl = UrlNormalizer.Normalize(absoluteUri);

            if (!allowedHosts.IsAllowed(absoluteUri))
            {
                // Off-site: not crawled. Recorded (deduplicated) so the optional link-check pass can probe it.
                if (seenOffsite.Add(normalizedUrl)) analysis.OffsiteLinks.Add(normalizedUrl);
                continue;
            }

            var linkRobots = robotsCache.TryGetValue(UrlOrigin.Key(absoluteUri), out var lr) ? lr : RobotsRules.AllowAll;
            if (!CrawlPolicy.IsAllowedByRobots(normalizedUrl, linkRobots)) continue;

            if (seenInScope.Add(normalizedUrl))
            {
                analysis.Outlinks.Add(normalizedUrl);
                analysis.OutlinkUris.Add(absoluteUri);
            }
        }
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
                // Match "robots" (all crawlers) or our own user-agent token, case-insensitively —
                // the same way the X-Robots-Tag user-agent prefix is matched below.
                var name = meta.GetAttributeValue("name", "").Trim();
                if (string.Equals(name, "robots", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, userAgentToken, StringComparison.OrdinalIgnoreCase))
                {
                    Apply(HtmlEntity.DeEntitize(meta.GetAttributeValue("content", "")));
                }
            }
        }

        if (!string.IsNullOrEmpty(xRobotsTag))
        {
            var standardDirectives = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "noindex", "nofollow", "none", "all", "index", "follow", "noarchive", "nosnippet",
                "unavailable_after", "max-snippet", "max-image-preview", "max-video-preview", "notranslate"
            };

            string? activeUserAgent = null;

            foreach (var part in xRobotsTag.Split(','))
            {
                var trimmedPart = part.Trim();
                int colon = trimmedPart.IndexOf(':');

                string directiveValue = trimmedPart;

                if (colon >= 0)
                {
                    var prefix = trimmedPart[..colon].Trim().ToLowerInvariant();
                    if (!standardDirectives.Contains(prefix))
                    {
                        activeUserAgent = prefix;
                        directiveValue = trimmedPart[(colon + 1)..].Trim();
                    }
                }

                if (activeUserAgent == null || string.Equals(activeUserAgent, userAgentToken, StringComparison.OrdinalIgnoreCase))
                {
                    Apply(directiveValue);
                }
            }
        }

        return (noIndex, noFollow);
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

            var href = GetHref(link);
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
    /// The result of extracting a PDF: its metadata title, the concatenated page text, and a measure of
    /// how much of that text is actually recoverable as Unicode rather than font-encoding garbage.
    /// </summary>
    /// <param name="Title">The cleaned document-metadata title, or <c>null</c> when absent.</param>
    /// <param name="Text">The whitespace-collapsed text harvested from every page.</param>
    /// <param name="TotalGlyphs">Visible (non-space) glyphs drawn across all pages.</param>
    /// <param name="MappableGlyphs">How many of those glyphs were drawn with a font that can be reversed to Unicode.</param>
    public readonly record struct PdfExtraction(string? Title, string Text, long TotalGlyphs, long MappableGlyphs)
    {
        // A PDF where most glyphs can't be reversed to Unicode extracts as cipher-like garbage (e.g.
        // an XPS-printed form whose subset fonts carry no ToUnicode CMap); indexing it just poisons the
        // index with junk tokens. 0.80 leaves headroom for documents that legitimately mix a little
        // unmappable decoration into otherwise-clean text, while still catching the mostly-garbled ones.
        private const double MinMappableFraction = 0.80;

        /// <summary>The share of drawn glyphs that map to Unicode, in [0,1]; 0 when the PDF carries no text.</summary>
        public double MappableFraction => TotalGlyphs == 0 ? 0.0 : (double)MappableGlyphs / TotalGlyphs;

        /// <summary>
        /// True when the extracted text isn't worth indexing: either the PDF has no text layer at all
        /// (<see cref="TotalGlyphs"/> is 0 — typically a scanned image), or too few of its glyphs are
        /// Unicode-mappable (a broken/subset font encoding). Both are candidates for an OCR fallback.
        /// </summary>
        public bool IsLowQualityText => TotalGlyphs == 0 || MappableFraction < MinMappableFraction;
    }

    /// <summary>
    /// Extracts a PDF's title and page text, and measures how much of that text is genuinely recoverable
    /// (see <see cref="PdfExtraction.IsLowQualityText"/>). The text is gathered exactly as before; the
    /// quality measure rides along on the same single parse.
    /// </summary>
    public static PdfExtraction ExtractPdf(byte[] body)
    {
        using var stream = new MemoryStream(body);
        using var pdfReader = new PdfReader(stream);
        using var pdfDoc = new PdfDocument(pdfReader);
        var title = CleanTitle(pdfDoc.GetDocumentInfo()?.GetTitle());

        var sb = new StringBuilder();
        long totalGlyphs = 0, mappableGlyphs = 0;
        for (int i = 1; i <= pdfDoc.GetNumberOfPages(); i++)
        {
            // A fresh strategy per page: GetResultantText() accumulates, so reusing one would re-emit
            // earlier pages. The tallies are summed across pages instead.
            var strategy = new TextQualityExtractionStrategy();
            sb.Append(PdfTextExtractor.GetTextFromPage(pdfDoc.GetPage(i), strategy));
            sb.Append(' ');
            totalGlyphs += strategy.TotalGlyphs;
            mappableGlyphs += strategy.MappableGlyphs;
        }
        return new PdfExtraction(title, CollapseWhitespace(sb.ToString()), totalGlyphs, mappableGlyphs);
    }

    /// <summary>
    /// A text-extraction strategy that returns the same text as <see cref="SimpleTextExtractionStrategy"/>
    /// (to which it delegates) while also counting, per drawn glyph, whether the glyph's font can be
    /// reversed to Unicode. That ratio is what tells a readable PDF apart from one that extracts as
    /// garbage because its fonts lack the mapping iText needs.
    /// </summary>
    private sealed class TextQualityExtractionStrategy : ITextExtractionStrategy
    {
        private readonly SimpleTextExtractionStrategy _text = new();

        /// <summary>Visible (non-space) glyphs seen so far.</summary>
        public long TotalGlyphs { get; private set; }
        /// <summary>How many of <see cref="TotalGlyphs"/> were drawn with a Unicode-mappable font.</summary>
        public long MappableGlyphs { get; private set; }

        public void EventOccurred(IEventData data, EventType type)
        {
            _text.EventOccurred(data, type);
            if (type != EventType.RENDER_TEXT || data is not TextRenderInfo render) return;

            bool mappable = IsFontUnicodeMappable(render.GetFont());
            foreach (var glyph in render.GetCharacterRenderInfos())
            {
                // Skip spacing the font synthesizes between words: it isn't indexable text and would
                // dilute the ratio. A glyph from a broken font draws a visible (non-space) character,
                // so it still counts against the denominator.
                if (string.IsNullOrWhiteSpace(glyph.GetText())) continue;
                TotalGlyphs++;
                if (mappable) MappableGlyphs++;
            }
        }

        public ICollection<EventType> GetSupportedEvents() => _text.GetSupportedEvents();
        public string GetResultantText() => _text.GetResultantText();

        /// <summary>
        /// Decides whether text drawn with <paramref name="font"/> can be recovered as Unicode. A
        /// ToUnicode CMap is the authoritative answer when present. Without one, only simple fonts
        /// (Type1/TrueType) still reverse reliably through their glyph-name encoding; composite (Type0)
        /// and Type3 fonts then expose only glyph IDs or private encodings — the garbage case.
        /// </summary>
        private static bool IsFontUnicodeMappable(PdfFont font)
        {
            var dict = font.GetPdfObject();
            if (dict.ContainsKey(PdfName.ToUnicode)) return true;
            var subtype = dict.GetAsName(PdfName.Subtype);
            return !PdfName.Type0.Equals(subtype) && !PdfName.Type3.Equals(subtype);
        }
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
