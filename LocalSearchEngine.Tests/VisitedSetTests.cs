using LocalSearchEngine.Core.Crawling.Pipeline;
using Xunit;

namespace LocalSearchEngine.Tests;

public sealed class VisitedSetTests
{
    [Fact]
    public void First_claim_wins_and_repeats_lose()
    {
        var set = new VisitedSet();
        Assert.True(set.TryMarkSeen("http://a.local/page"));
        Assert.False(set.TryMarkSeen("http://a.local/page"));
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void Keys_compare_case_insensitively()
    {
        var set = new VisitedSet();
        Assert.True(set.TryMarkSeen("http://a.local/Page"));
        Assert.False(set.TryMarkSeen("http://a.local/page"));
    }

    [Fact]
    public async Task Concurrent_storm_yields_exactly_one_winner_per_key()
    {
        var set = new VisitedSet();
        const int keys = 50;
        const int claimersPerKey = 16;
        int[] wins = new int[keys];

        var tasks = new List<Task>();
        foreach (var key in Enumerable.Range(0, keys))
        {
            foreach (var _ in Enumerable.Range(0, claimersPerKey))
            {
                tasks.Add(Task.Run(() =>
                {
                    if (set.TryMarkSeen($"http://a.local/{key}"))
                    {
                        Interlocked.Increment(ref wins[key]);
                    }
                }));
            }
        }
        await Task.WhenAll(tasks);

        Assert.All(wins, w => Assert.Equal(1, w));
        Assert.Equal(keys, set.Count);
    }
}
