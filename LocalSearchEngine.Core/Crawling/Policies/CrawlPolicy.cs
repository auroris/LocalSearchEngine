using System.IO.Compression;
using System.Text;

namespace LocalSearchEngine.Core.Crawling.Policies;

/// <summary>
/// Specifies the kinds of documents that can be processed and indexed.
/// </summary>
public enum DocKind { Unknown, Html, Pdf, Docx }

/// <summary>
/// Provides policy decisions for crawling and content classification that are independent of network state.
/// </summary>
public static class CrawlPolicy
{
    private static readonly HashSet<string> GenericMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/octet-stream",
        "text/plain",
        "application/zip",
        "application/x-zip-compressed"
    };

    /// <summary>
    /// Determines whether a content type header is directly supported or is a generic type that can be sniffed.
    /// </summary>
    /// <param name="mediaType">The media type string from the Content-Type header.</param>
    /// <returns><c>true</c> if the media type is supported or generic; otherwise, <c>false</c>.</returns>
    public static bool IsSupportedOrGenericContentType(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType)) return true;

        int semicolonIdx = mediaType.IndexOf(';');
        if (semicolonIdx >= 0)
        {
            mediaType = mediaType.Substring(0, semicolonIdx);
        }
        mediaType = mediaType.Trim();

        if (FromContentType(mediaType) != DocKind.Unknown) return true;

        return GenericMimeTypes.Contains(mediaType);
    }

    /// <summary>
    /// Checks if a prefix buffer contains headers or signatures that suggest a supported document kind.
    /// </summary>
    /// <param name="prefix">The initial byte chunk from the document stream.</param>
    /// <param name="contentType">The media type header from the server.</param>
    /// <param name="url">The URL of the document being fetched.</param>
    /// <returns><c>true</c> if the prefix structure is supported or generic; otherwise, <c>false</c>.</returns>
    public static bool IsSupportedPrefix(byte[] prefix, string? contentType, string? url = null)
    {
        var declared = FromContentType(contentType);
        if (declared != DocKind.Unknown) return true;

        if (StartsWith(prefix, PdfMagic)) return true;
        if (StartsWith(prefix, ZipMagic))
        {
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var ext = Path.GetExtension(uri.AbsolutePath);
                if (!string.IsNullOrEmpty(ext) && !string.Equals(ext, ".docx", StringComparison.OrdinalIgnoreCase))
                {
                    return false; // Looks like a ZIP/container, but extension is explicitly not .docx (e.g. .zip, .xlsx)
                }
            }
            return true; // could be Docx, need full download
        }
        if (LooksLikeHtml(prefix)) return true;

        return false;
    }

    /// <summary>
    /// Determines whether the crawl policy allows fetching the specified URL according to robots.txt rules.
    /// </summary>
    /// <param name="url">The URL string to evaluate.</param>
    /// <param name="robots">The robots.txt rules applicable to the host.</param>
    /// <returns><c>true</c> if the path is allowed by robots.txt or fails to parse; otherwise, <c>false</c>.</returns>
    public static bool IsAllowedByRobots(string url, RobotsRules robots)
    {
        return !Uri.TryCreate(url, UriKind.Absolute, out var uri) || robots.IsAllowed(uri.PathAndQuery);
    }

    /// <summary>
    /// Classifies the document format of a fetched content body based on its Content-Type header or sniffed bytes.
    /// </summary>
    /// <param name="contentType">The media type header returned by the server.</param>
    /// <param name="body">The raw byte array of the content body.</param>
    /// <returns>A <see cref="DocKind"/> indicating how the body should be parsed.</returns>
    public static DocKind ClassifyContent(string? contentType, byte[] body)
    {
        var declared = FromContentType(contentType);
        return declared != DocKind.Unknown ? declared : Sniff(body);
    }

    /// <summary>
    /// Classifies a MIME media type into a supported document kind.
    /// </summary>
    /// <param name="mediaType">The media type string to classify.</param>
    /// <returns>A <see cref="DocKind"/> corresponding to the media type, or <see cref="DocKind.Unknown"/> if not recognized.</returns>
    private static DocKind FromContentType(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType)) return DocKind.Unknown;
        mediaType = mediaType.ToLowerInvariant();
        if (mediaType.Contains("pdf")) return DocKind.Pdf;
        if (mediaType.Contains("wordprocessingml")) return DocKind.Docx;
        if (mediaType is "text/html" or "application/xhtml+xml") return DocKind.Html;
        return DocKind.Unknown;
    }

    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 };

    /// <summary>
    /// Sniffs the magic bytes of a content body to detect its document kind.
    /// </summary>
    /// <param name="body">The raw byte array of the content body.</param>
    /// <returns>A <see cref="DocKind"/> detected from the bytes, or <see cref="DocKind.Unknown"/> if no signature matches.</returns>
    private static DocKind Sniff(byte[] body)
    {
        if (StartsWith(body, PdfMagic)) return DocKind.Pdf;
        if (StartsWith(body, ZipMagic) && ZipIsWordDocument(body)) return DocKind.Docx;
        if (LooksLikeHtml(body)) return DocKind.Html;
        return DocKind.Unknown;
    }

    /// <summary>
    /// Checks if the content body starts with the specified signature bytes.
    /// </summary>
    /// <param name="body">The raw content body byte array.</param>
    /// <param name="signature">The signature bytes to match.</param>
    /// <returns><c>true</c> if the body starts with the signature; otherwise, <c>false</c>.</returns>
    private static bool StartsWith(byte[] body, byte[] signature) =>
        body.Length >= signature.Length && body.AsSpan(0, signature.Length).SequenceEqual(signature);

    /// <summary>
    /// Determines whether the zip archive body represents a Word processing document (.docx).
    /// </summary>
    /// <param name="body">The raw zip file byte array.</param>
    /// <returns><c>true</c> if the zip archive contains the main word document entry; otherwise, <c>false</c>.</returns>
    private static bool ZipIsWordDocument(byte[] body)
    {
        try
        {
            using var stream = new MemoryStream(body);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            return archive.GetEntry("word/document.xml") is not null;
        }
        catch
        {
            return false; // truncated or invalid zip
        }
    }

    /// <summary>
    /// Inspects the content body to determine if it resembles HTML markup.
    /// </summary>
    /// <param name="body">The raw content body byte array.</param>
    /// <returns><c>true</c> if the content looks like HTML; otherwise, <c>false</c>.</returns>
    private static bool LooksLikeHtml(byte[] body)
    {
        int i = 0;
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF) i = 3; // UTF-8 BOM
        while (i < body.Length && body[i] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C) i++;
        if (i >= body.Length || body[i] != (byte)'<') return false;

        var head = Encoding.ASCII.GetString(body, i, Math.Min(15, body.Length - i)).ToLowerInvariant();
        return head.StartsWith("<!doctype html")
            || head.StartsWith("<html")
            || head.StartsWith("<head")
            || head.StartsWith("<body")
            || head.StartsWith("<!--");
    }
}
