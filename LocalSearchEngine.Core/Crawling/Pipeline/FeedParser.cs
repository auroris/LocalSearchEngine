using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>One feed entry, in the feed's own (newest-first) order.</summary>
/// <param name="Location">The item's resolved link target.</param>
/// <param name="PublishedUtc">When the feed says the item changed (RSS <c>pubDate</c>, Atom
/// <c>updated</c>/<c>published</c>), or <c>null</c> when the entry carries no parseable date —
/// which incremental planning must treat as "can't prove it's covered".</param>
internal readonly record struct FeedItem(Uri Location, DateTime? PublishedUtc);

/// <summary>
/// Parses RSS 2.0 and Atom documents into their item lists, preserving document order because that
/// order is load-bearing: a feed is a newest-first change journal, and incremental planning walks it
/// top-down looking for the first already-covered entry. Hardened like the sitemap parser (no DTDs,
/// no external resolution) so a hostile feed can't trigger entity expansion or out-of-band fetches.
/// </summary>
internal static class FeedParser
{
    /// <summary>
    /// Parses a feed body.
    /// </summary>
    /// <param name="body">The fetched feed bytes.</param>
    /// <param name="feedUri">The feed's own location; relative Atom hrefs resolve against it.</param>
    /// <param name="items">The entries in document order; empty when parsing failed.</param>
    /// <returns><c>false</c> when the body is not parseable XML (the caller logs; a bad feed never throws).</returns>
    public static bool TryParse(byte[] body, Uri feedUri, out List<FeedItem> items)
    {
        items = new List<FeedItem>();
        XmlDocument doc;
        try
        {
            var readerSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            doc = new XmlDocument { XmlResolver = null };
            using var byteStream = new MemoryStream(body);
            using var xmlReader = XmlReader.Create(byteStream, readerSettings);
            doc.Load(xmlReader);
        }
        catch
        {
            return false;
        }

        // RSS 2.0: <item><link>text</link><pubDate>RFC-1123</pubDate></item>. The flat tag scan
        // mirrors the sitemap parser — feeds in the wild are too messy for a strict path match, and
        // a <link> outside an item (the channel's own) points at the site root, which scope
        // checking already handles. The two-argument lookup matches by local name in any namespace,
        // because real feeds are served both bare and namespace-prefixed.
        foreach (XmlNode item in doc.GetElementsByTagName("item", "*"))
        {
            Uri? location = null;
            DateTime? published = null;
            foreach (XmlNode child in item.ChildNodes)
            {
                if (location is null && string.Equals(child.LocalName, "link", StringComparison.OrdinalIgnoreCase))
                {
                    location = ResolveLink(feedUri, child.InnerText);
                }
                else if (published is null && string.Equals(child.LocalName, "pubDate", StringComparison.OrdinalIgnoreCase))
                {
                    published = ParseDate(child.InnerText);
                }
            }
            if (location is not null)
            {
                items.Add(new FeedItem(location, published));
            }
        }

        // Atom: <entry><link href="..."/>, preferring rel="alternate" (or no rel, which means the
        // same); the change date is <updated>, falling back to <published>.
        foreach (XmlNode entry in doc.GetElementsByTagName("entry", "*"))
        {
            Uri? location = null;
            DateTime? updated = null;
            DateTime? published = null;
            foreach (XmlNode child in entry.ChildNodes)
            {
                if (location is null && string.Equals(child.LocalName, "link", StringComparison.OrdinalIgnoreCase))
                {
                    var rel = child.Attributes?["rel"]?.Value;
                    if (rel is null || string.Equals(rel, "alternate", StringComparison.OrdinalIgnoreCase))
                    {
                        location = ResolveLink(feedUri, child.Attributes?["href"]?.Value);
                    }
                }
                else if (string.Equals(child.LocalName, "updated", StringComparison.OrdinalIgnoreCase))
                {
                    updated = ParseDate(child.InnerText);
                }
                else if (string.Equals(child.LocalName, "published", StringComparison.OrdinalIgnoreCase))
                {
                    published = ParseDate(child.InnerText);
                }
            }
            if (location is not null)
            {
                items.Add(new FeedItem(location, updated ?? published));
            }
        }

        return true;
    }

    private static Uri? ResolveLink(Uri feedUri, string? link)
    {
        var value = link?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (!Uri.TryCreate(feedUri, value, out var itemUri)) return null;
        if (itemUri.Scheme != Uri.UriSchemeHttp && itemUri.Scheme != Uri.UriSchemeHttps) return null;
        return itemUri;
    }

    private static DateTime? ParseDate(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        // Handles both RFC-1123 with zone (RSS) and ISO-8601 with offset (Atom); everything is
        // compared in UTC because CrawlState.LastCrawled is stamped from DateTime.UtcNow.
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.UtcDateTime
            : null;
    }
}
