using EmptyEngine.Editor.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmptyEngine.Tests;

public sealed class EditorDispatcherTests
{
    [Fact]
    public async Task ResultInvocationRunsOnceAndReturnsItsValue()
    {
        using var dispatcher = new EditorDispatcher(NullLogger<EditorDispatcher>.Instance);
        int calls = 0;

        int result = await dispatcher.InvokeAsync(() =>
        {
            calls++;
            return 42;
        });

        Assert.Equal(42, result);
        Assert.Equal(1, calls);
    }
}
