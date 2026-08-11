using LocalSearchEngine.Core.Crawling.Pipeline;
using Xunit;

namespace LocalSearchEngine.Tests;

public sealed class PendingWorkCounterTests
{
    [Fact]
    public void Zero_strike_fires_exactly_once()
    {
        var counter = new PendingWorkCounter();
        counter.Increment();
        counter.Increment();
        Assert.False(counter.Decrement());
        Assert.True(counter.Decrement());
    }

    [Fact]
    public async Task Concurrent_balanced_traffic_strikes_zero_exactly_once()
    {
        var counter = new PendingWorkCounter();
        int zeroStrikes = 0;

        // The root-token usage pattern: one held token brackets a storm of balanced
        // increment/decrement pairs, so zero can only strike at the very end.
        counter.Increment();
        var tasks = Enumerable.Range(0, 1000).Select(_ => Task.Run(() =>
        {
            counter.Increment();
            if (counter.Decrement())
            {
                Interlocked.Increment(ref zeroStrikes);
            }
        }));
        await Task.WhenAll(tasks);
        if (counter.Decrement())
        {
            Interlocked.Increment(ref zeroStrikes);
        }

        Assert.Equal(1, zeroStrikes);
    }
}
