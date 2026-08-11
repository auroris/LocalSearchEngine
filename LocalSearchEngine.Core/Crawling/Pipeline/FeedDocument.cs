using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// An RSS/Atom feed fetch — the entry point for update crawls, where the feed is the site's change
/// journal: every listed item is offered to the frontier and nothing else is touched. The cheapness
/// guarantee comes from the items themselves, not from trusting feed dates: each enqueued item is
/// fetched with a conditional request (an unchanged article answers 304 for pennies) and only a
/// changed content hash re-embeds. Parsed with the same hardened settings as sitemaps (no DTDs, no
/// external resolution). Item links go through <see cref="ICrawlContext.Enqueue"/> — feed items are
/// seed material, exempt from the FollowLinks gate that keeps an update run from becoming a crawl.
/// </summary>
internal sealed class FeedDocument : Document
{
    public FeedDocument(Uri fetchUri) : base(fetchUri) { }

    public override bool IsPage => false;

    public override Document WithLocation(Uri fetchUri) => new FeedDocument(fetchUri);

    public override Task ProcessAsync(FetchResult fetch, ICrawlContext ctx, CancellationToken ct)
    {
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
            using var byteStream = new MemoryStream(fetch.Body);
            using var xmlReader = XmlReader.Create(byteStream, readerSettings);
            doc.Load(xmlReader);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to parse feed {Url}", DedupKey);
            return Task.CompletedTask;
        }

        int added = 0;

        // RSS 2.0: <item><link>text</link></item>. The flat tag scan mirrors the sitemap parser —
        // feeds in the wild are too messy for a strict path match, and a <link> outside an item
        // (the channel's own) points at the site root, which scope-checking already handles.
        foreach (var item in ElementsByLocalName(doc, "item"))
        {
            foreach (XmlNode child in item.ChildNodes)
            {
                if (!string.Equals(child.LocalName, "link", StringComparison.OrdinalIgnoreCase)) continue;
                if (TryEnqueueItem(ctx, child.InnerText)) added++;
                break;
            }
        }

        // Atom: <entry><link href="..."/>, preferring rel="alternate" (or no rel, which means the same).
        foreach (var entry in ElementsByLocalName(doc, "entry"))
        {
            foreach (XmlNode child in entry.ChildNodes)
            {
                if (!string.Equals(child.LocalName, "link", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = child.Attributes?["rel"]?.Value;
                if (rel is not null && !string.Equals(rel, "alternate", StringComparison.OrdinalIgnoreCase)) continue;
                if (TryEnqueueItem(ctx, child.Attributes?["href"]?.Value)) added++;
                break;
            }
        }

        ctx.Logger.LogInformation("Feed {Url}: enqueued {Count} item(s).", DedupKey, added);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Enumerates elements by local name so namespace prefixes do not change feed semantics.
    /// Both a default-namespace <c>&lt;entry&gt;</c> and a prefixed <c>&lt;atom:entry&gt;</c>
    /// are the same Atom element.
    /// </summary>
    private static IEnumerable<XmlNode> ElementsByLocalName(XmlDocument doc, string localName)
    {
        foreach (XmlNode node in doc.GetElementsByTagName("*"))
        {
            if (string.Equals(node.LocalName, localName, StringComparison.OrdinalIgnoreCase))
            {
                yield return node;
            }
        }
    }

    private bool TryEnqueueItem(ICrawlContext ctx, string? link)
    {
        var value = link?.Trim();
        if (string.IsNullOrEmpty(value)) return false;

        // Item links may be relative (Atom commonly is); resolve against the feed's own location.
        if (!Uri.TryCreate(FetchUri, value, out var itemUri)) return false;
        if (itemUri.Scheme != Uri.UriSchemeHttp && itemUri.Scheme != Uri.UriSchemeHttps) return false;

        return ctx.Enqueue(new PageDocument(itemUri));
    }
}
