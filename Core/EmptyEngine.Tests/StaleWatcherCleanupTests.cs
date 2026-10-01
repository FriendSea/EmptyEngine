using EmptyEngine.Host;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>取り残された watch の選び出しの検証</summary>
public class StaleWatcherCleanupTests
{
    private const string EditorProject =
        "/Users/dev/Game/.artifacts/Game.Editor/Game.Editor.csproj";

    private const string ProcessTable = """
          501 /sbin/launchd
         2241 dotnet watch --no-hot-reload --project /Users/dev/Game/.artifacts/Game.Editor/Game.Editor.csproj run --configuration Debug -- --ui-port 5170
         2243 /usr/local/share/dotnet/dotnet /usr/local/share/dotnet/sdk/10.0.302/DotnetTools/dotnet-watch/10.0.302/tools/net10.0/any/dotnet-watch.dll --no-hot-reload --project /Users/dev/Game/.artifacts/Game.Editor/Game.Editor.csproj run --configuration Debug
         2297 /usr/local/share/dotnet/dotnet run --project /Users/dev/Game/.artifacts/Game.Editor/Game.Editor.csproj --configuration Debug
         2299 /Users/dev/Game/bin/Debug/net10.0/Game
         2301 dotnet watch --non-interactive run
         2400 /Applications/Some.app/Contents/MacOS/Some --project /Users/dev/Game/.artifacts/Game.Editor/Game.Editor.csproj
        """;

    /// <summary>エディタを指す dotnet プロセスの全取得</summary>
    [Fact]
    public void PicksEveryDotnetPointingAtTheEditorProject()
    {
        List<int> pids = HostSession.SelectWatcherPids(ProcessTable, EditorProject);

        Assert.Equal(new[] { 2241, 2243, 2297 }, pids);
    }

    /// <summary>ランタイムを対象にしないこと</summary>
    /// <summary>別プロジェクトの watch を対象にしないこと</summary>
    [Fact]
    public void IgnoresWatchersOfAnotherProject()
    {
        List<int> pids = HostSession.SelectWatcherPids(
            ProcessTable, "/Users/dev/Other/.artifacts/Other.Editor/Other.Editor.csproj");

        Assert.Empty(pids);
    }

    /// <summary>自分自身を対象にしないこと</summary>
    [Fact]
    public void NeverSelectsItself()
    {
        string table = $"{Environment.ProcessId} dotnet watch --project {EditorProject} run";

        Assert.Empty(HostSession.SelectWatcherPids(table, EditorProject));
    }

    /// <summary>PID だけの行の読み取り</summary>
    [Fact]
    public void ReadsPidOnlyLinesFromWindows()
    {
        List<int> pids = HostSession.ParsePids("1234\r\n5678\r\n\r\n", _ => true);

        Assert.Equal(new[] { 1234, 5678 }, pids);
    }

    /// <summary>台帳の行からの PID・開始時刻・ラベルの取り出し</summary>
    [Fact]
    public void ReadsPidStartTimeAndLabelFromTheLedger()
    {
        Assert.True(HostSession.TryParseLedgerLine("2301 638912345678901234 Runtime", out int pid, out long ticks, out string label));

        Assert.Equal(2301, pid);
        Assert.Equal(638912345678901234, ticks);
        Assert.Equal("Runtime", label);
    }

    /// <summary>形の合わない行を読み飛ばすこと</summary>
    [Theory]
    [InlineData("")]
    [InlineData("2301")]
    [InlineData("Runtime 638912345678901234")]
    [InlineData("2301 not-a-timestamp Runtime")]
    public void SkipsLedgerLinesItCannotRead(string line)
    {
        Assert.False(HostSession.TryParseLedgerLine(line, out _, out _, out _));
    }

    /// <summary>自分自身を台帳から拾わないこと</summary>
    [Fact]
    public void NeverPicksItselfFromTheLedger()
    {
        Assert.False(HostSession.TryParseLedgerLine(
            $"{Environment.ProcessId} 638912345678901234 Runtime", out _, out _, out _));
    }
}
