using System.Diagnostics;
using LocalSearchEngine.Core.Crawling.Pipeline;
using LocalSearchEngine.Core.Crawling.Policies;
using Xunit;

namespace LocalSearchEngine.Tests;

public sealed class HostGateTests
{
    [Fact]
    public async Task Same_host_entries_are_spaced_by_the_gap()
    {
        var gate = new HostGate();
        var gap = TimeSpan.FromMilliseconds(150);
        var sw = Stopwatch.StartNew();

        (await gate.EnterAsync("a.local", gap)).Dispose(); // first entry pays no gap
        (await gate.EnterAsync("a.local", gap)).Dispose();
        (await gate.EnterAsync("a.local", gap)).Dispose();

        // Two gaps between three entries; generous lower bound to stay timer-tolerant.
        Assert.True(sw.ElapsedMilliseconds >= 250,
            $"Three same-host entries took {sw.ElapsedMilliseconds}ms; expected at least two 150ms gaps.");
    }

    [Fact]
    public async Task Different_hosts_do_not_wait_on_each_other()
    {
        var gate = new HostGate();
        var gap = TimeSpan.FromSeconds(10);

        // Hold host A's lane; host B must still enter immediately.
        using var holdA = await gate.EnterAsync("a.local", gap);
        var sw = Stopwatch.StartNew();
        (await gate.EnterAsync("b.local", gap)).Dispose();
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"Entering an unrelated host took {sw.ElapsedMilliseconds}ms while another host's lane was held.");
    }

    [Fact]
    public async Task Same_host_lane_is_exclusive_while_held()
    {
        var gate = new HostGate();
        var releaser = await gate.EnterAsync("a.local", TimeSpan.Zero);
        var second = gate.EnterAsync("a.local", TimeSpan.Zero).AsTask();

        await Task.Delay(100);
        Assert.False(second.IsCompleted);

        releaser.Dispose();
        (await second).Dispose();
    }

    [Fact]
    public void Crawl_delay_is_honored_and_capped()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), HostGate.ResolveDelay(RobotsRules.AllowAll, 250));
        Assert.Equal(TimeSpan.FromSeconds(5), HostGate.ResolveDelay(RobotsRules.Parse("User-agent: *\nCrawl-delay: 5", "bot"), 250));
        Assert.Equal(TimeSpan.FromSeconds(30), HostGate.ResolveDelay(RobotsRules.Parse("User-agent: *\nCrawl-delay: 900", "bot"), 250));
    }
}
