using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Crawling.Engine;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Reporting;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// The per-worker face of the pipeline: everything shared delegates to <see cref="CrawlPipeline"/>;
/// only the read connection is private, because a <see cref="SqliteConnection"/> cannot run
/// concurrent commands.
/// </summary>
internal sealed class PipelineContext : ICrawlContext
{
    private readonly CrawlPipeline _pipeline;

    public PipelineContext(CrawlPipeline pipeline, SqliteConnection read)
    {
        _pipeline = pipeline;
        Read = read;
    }

    public bool FollowLinks => _pipeline.Plan.FollowLinks;
    public AllowedHosts Scope => _pipeline.Plan.Scope;
    public NoIndexRules NoIndexRules => _pipeline.Plan.NoIndexRules;
    public IReadOnlyDictionary<string, RobotsRules> RobotsRules => _pipeline.Robots.Rules;
    public ICrawlObserver Observer => _pipeline.Observer;
    public ILogger Logger => _pipeline.Logger;
    public SqliteConnection Read { get; }

    public bool Discover(Uri fetchUri) => FollowLinks && _pipeline.Enqueue(new PageDocument(fetchUri));

    public bool Enqueue(Document document) => _pipeline.Enqueue(document);

    public void Submit(CrawlJob job) => _pipeline.Submit(job);

    public bool TryAcceptIndex(string contentHash, string url, out string? duplicateOf) =>
        _pipeline.TryAcceptIndex(contentHash, url, out duplicateOf);
}
