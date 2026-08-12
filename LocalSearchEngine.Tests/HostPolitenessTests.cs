using System.Diagnostics;
using LocalSearchEngine.Core.Crawling.Policies;
using Xunit;

namespace LocalSearchEngine.Tests;

public sealed class HostPolitenessTests
{
    [Fact]
    public async Task Same_host_turns_are_spaced_by_the_gap()
    {
        var politeness = new HostPoliteness();
        var gap = TimeSpan.FromMilliseconds(150);
        var sw = Stopwatch.StartNew();

        await politeness.WaitTurnAsync("a.local", gap); // first turn pays no gap
        await politeness.WaitTurnAsync("a.local", gap);
        await politeness.WaitTurnAsync("a.local", gap);

        // Two gaps between three turns; generous lower bound to stay timer-tolerant.
        Assert.True(sw.ElapsedMilliseconds >= 250,
            $"Three same-host turns took {sw.ElapsedMilliseconds}ms; expected at least two 150ms gaps.");
    }

    [Fact]
    public async Task Simultaneous_same_host_turns_space_out_instead_of_bunching()
    {
        var politeness = new HostPoliteness();
        var gap = TimeSpan.FromMilliseconds(150);
        var sw = Stopwatch.StartNew();

        // All claimed at once, as parallel workers would; the claims must still come out one gap apart.
        await Task.WhenAll(
            politeness.WaitTurnAsync("a.local", gap).AsTask(),
            politeness.WaitTurnAsync("a.local", gap).AsTask(),
            politeness.WaitTurnAsync("a.local", gap).AsTask());

        Assert.True(sw.ElapsedMilliseconds >= 250,
            $"Three simultaneous turns completed in {sw.ElapsedMilliseconds}ms; expected the last to wait two 150ms gaps.");
    }

    [Fact]
    public async Task A_host_left_alone_longer_than_the_gap_admits_immediately()
    {
        var politeness = new HostPoliteness();
        var gap = TimeSpan.FromSeconds(2);

        // Take a turn, then spend longer than the gap "processing" — the next turn must not wait:
        // politeness is a clock since the host was last bothered, not a toll on every request.
        await politeness.WaitTurnAsync("a.local", gap);
        await Task.Delay(2300);

        var sw = Stopwatch.StartNew();
        await politeness.WaitTurnAsync("a.local", gap);
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"A turn on a host idle longer than the gap took {sw.ElapsedMilliseconds}ms; expected immediate admission.");
    }

    [Fact]
    public async Task Different_hosts_do_not_wait_on_each_other()
    {
        var politeness = new HostPoliteness();
        var gap = TimeSpan.FromSeconds(10);

        await politeness.WaitTurnAsync("a.local", gap);
        var sw = Stopwatch.StartNew();
        await politeness.WaitTurnAsync("b.local", gap);
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"A first turn on an unrelated host took {sw.ElapsedMilliseconds}ms while another host's clock was running.");
    }

    [Fact]
    public void Crawl_delay_is_honored_and_capped()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), HostPoliteness.ResolveDelay(RobotsRules.AllowAll, 250));
        Assert.Equal(TimeSpan.FromSeconds(5), HostPoliteness.ResolveDelay(RobotsRules.Parse("User-agent: *\nCrawl-delay: 5", "bot"), 250));
        Assert.Equal(TimeSpan.FromSeconds(30), HostPoliteness.ResolveDelay(RobotsRules.Parse("User-agent: *\nCrawl-delay: 900", "bot"), 250));
    }
}
