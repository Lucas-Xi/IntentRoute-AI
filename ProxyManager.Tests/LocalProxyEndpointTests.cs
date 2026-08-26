using ProxyManager.Standalone;
using Xunit;

namespace ProxyManager.Tests;

public sealed class LocalProxyEndpointTests
{
    [Theory]
    [InlineData("127.0.0.1", 10808, "127.0.0.1")]
    [InlineData("127.0.0.2", 1, "127.0.0.2")]
    [InlineData("::1", 65535, "::1")]
    public void TryNormalize_AcceptsLiteralLoopback(string host, int port, string expected)
    {
        var success = LocalProxyEndpoint.TryNormalize(host, port, out var normalized, out var error);

        Assert.True(success, error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("localhost", 10808)]
    [InlineData("192.168.1.10", 10808)]
    [InlineData("8.8.8.8", 53)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", 65536)]
    [InlineData("::ffff:127.0.0.1", 10808)]
    public void TryNormalize_RejectsUnsupportedEndpoint(string host, int port)
    {
        var success = LocalProxyEndpoint.TryNormalize(host, port, out _, out var error);

        Assert.False(success);
        Assert.NotEmpty(error);
    }
}
