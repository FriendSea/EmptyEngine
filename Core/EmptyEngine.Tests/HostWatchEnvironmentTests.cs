using EmptyEngine.Host;
using Xunit;

namespace EmptyEngine.Tests;

public class HostWatchEnvironmentTests
{
    [Fact]
    public void DisablesMsBuildNodeReuseForManagedWatchers()
    {
        var environment = new Dictionary<string, string>();

        HostSession.AddWatchEnvironment(environment);

        Assert.Equal("1", environment["MSBUILDDISABLENODEREUSE"]);
    }
}
