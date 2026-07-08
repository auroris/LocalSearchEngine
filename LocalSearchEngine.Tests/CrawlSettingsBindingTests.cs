using System.IO;
using System.Text;
using LocalSearchEngine.Crawler;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LocalSearchEngine.Tests;

/// <summary>
/// Guards the appsettings.json → <see cref="CrawlSettings"/> binding. The section's JSON keys are
/// kebab-case, so the options POCO must map them explicitly; without that mapping the binder matches only
/// PascalCase property names and silently leaves allowed-servers, noindex-patterns, and the rest at their
/// defaults — a regression that once shipped and quietly limited a crawl to its seed host. The JSON below
/// mirrors the real appsettings.json shape, including the <c>"max-pages": null</c> "no limit" convention.
/// </summary>
public class CrawlSettingsBindingTests
{
    private const string AppSettingsJson = """
    {
      "CrawlSettings": {
        "max-pages": null,
        "max-pages-per-host": null,
        "max-crawl-size-bytes": 15728640,
        "request-delay-ms": 500,
        "allowed-servers": [ "http://coldlake.mil.ca", "http://documents.coldlake.mil.ca" ],
        "noindex-patterns": [ "http://coldlake.mil.ca/en/Documents/*", "http://coldlake.mil.ca/fr/Documents/*" ],
        "check-external-links": true,
        "no-live-stats": true,
        "log-file": "custom.log",
        "stats-file": "custom-stats",
        "broken-links-file": "custom-broken"
      }
    }
    """;

    private static CrawlSettings Bind()
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(AppSettingsJson)))
            .Build();
        return config.GetSection("CrawlSettings").Get<CrawlSettings>()!;
    }

    [Fact]
    public void Binds_allowed_servers_from_kebab_case_key()
    {
        var settings = Bind();
        Assert.Equal(
            new[] { "http://coldlake.mil.ca", "http://documents.coldlake.mil.ca" },
            settings.AllowedServers);
    }

    [Fact]
    public void Binds_noindex_patterns_from_kebab_case_key()
    {
        var settings = Bind();
        Assert.Equal(
            new[] { "http://coldlake.mil.ca/en/Documents/*", "http://coldlake.mil.ca/fr/Documents/*" },
            settings.NoIndexPatterns);
    }

    [Fact]
    public void Binds_scalar_kebab_case_keys()
    {
        var settings = Bind();
        Assert.Equal(500, settings.RequestDelayMs);
        Assert.Equal(15728640, settings.MaxCrawlSizeBytes);
        Assert.True(settings.CheckExternalLinks);
        Assert.True(settings.NoLiveStats);
        Assert.Equal("custom.log", settings.LogFile);
        Assert.Equal("custom-stats", settings.StatsFile);
        Assert.Equal("custom-broken", settings.BrokenLinksFile);
    }

    [Fact]
    public void Null_max_pages_binds_to_null_not_zero()
    {
        // The JSON "no limit" convention is null; Program resolves that to int.MaxValue. Binding it to a
        // non-nullable int would instead yield 0 — which would cap the crawl at zero pages and index nothing.
        var settings = Bind();
        Assert.Null(settings.MaxPages);
        Assert.Null(settings.MaxPagesPerHost);
    }
}
