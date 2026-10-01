using EmptyEngine.Core.RuntimeLink;
using Xunit;

namespace EmptyEngine.Tests;

public sealed class RuntimeModeEndpointTests
{
    [Theory]
    [InlineData("ws://127.0.0.1:5003/runtime")]
    [InlineData("wss://example.test/runtime")]
    public void Editor_endpoint_accepts_websocket_urls(string value)
    {
        Assert.Equal(value, RuntimeMode.ParseEndpoint(value)?.AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("127.0.0.1:5003")]
    [InlineData("http://127.0.0.1:5003/runtime")]
    public void Editor_endpoint_rejects_missing_or_non_websocket_values(string? value)
    {
        Assert.Null(RuntimeMode.ParseEndpoint(value));
    }
}
