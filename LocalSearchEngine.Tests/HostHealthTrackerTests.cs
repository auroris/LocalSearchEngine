using LocalSearchEngine.Core.Crawling.Policies;
using Xunit;

namespace LocalSearchEngine.Tests;

public sealed class HostHealthTrackerTests
{
    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)] // no DNS entry
    [InlineData(HttpRequestError.ConnectionError)]     // resolves, but nothing answers / refuses / connect timeout
    public void Failures_before_any_server_answers_are_connection_failures(HttpRequestError error)
    {
        var ex = new HttpRequestException(error, "unreachable");
        Assert.True(HostHealthTracker.IsConnectionFailure(ex));
        // The same failures also count toward the broader unreachable write-off.
        Assert.True(HostHealthTracker.IsTransportFailure(ex));
    }

    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError)] // TLS handshake broke: something answered
    [InlineData(HttpRequestError.InvalidResponse)]
    [InlineData(HttpRequestError.ResponseEnded)]
    [InlineData(HttpRequestError.Unknown)]
    public void Failures_after_a_server_answered_keep_their_retries(HttpRequestError error)
    {
        Assert.False(HostHealthTracker.IsConnectionFailure(new HttpRequestException(error, "mid-exchange")));
    }

    [Fact]
    public void Tls_failure_is_not_a_connection_failure_but_still_writes_off_a_silent_host()
    {
        // The two classifications deliberately split on TLS: retries are wasted only on hosts that
        // never answered, while the first-contact write-off treats a broken handshake as unreachable.
        var tls = new HttpRequestException(HttpRequestError.SecureConnectionError, "handshake failed");
        Assert.False(HostHealthTracker.IsConnectionFailure(tls));
        Assert.True(HostHealthTracker.IsTransportFailure(tls));
    }
}
