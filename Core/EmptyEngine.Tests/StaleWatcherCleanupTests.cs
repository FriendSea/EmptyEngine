using System.Diagnostics;
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

    /// <summary>ジョブを閉じると、子が起こした孫まで落ちること</summary>
    [Fact]
    public void ClosingTheJobKillsGrandchildrenToo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 子は起動してすぐ孫を起こし、その PID と渡された環境変数を 1 行ずつ出して居座る
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = "-NoProfile -NonInteractive -Command \"(Start-Process ping -ArgumentList '-n 60 127.0.0.1' -NoNewWindow -RedirectStandardOutput NUL -PassThru).Id; $env:HOST_JOB_TEST; Start-Sleep 60\"",
            WorkingDirectory = Path.GetTempPath(),
        };
        startInfo.Environment["HOST_JOB_TEST"] = "passed through";

        var job = new ChildProcessJob();
        (Process child, StreamReader output, StreamReader _) = job.Start(startInfo);
        try
        {
            int grandchildPid = int.Parse(output.ReadLine()!);
            Assert.Equal("passed through", output.ReadLine());
            using Process grandchild = Process.GetProcessById(grandchildPid);
            Assert.False(child.HasExited);
            Assert.False(grandchild.HasExited);

            job.Dispose();

            Assert.True(child.WaitForExit(5000));
            Assert.True(grandchild.WaitForExit(5000));
            // Host は終了の知らせで終了コードを読むので、読めること自体を見る（値は OS 任せ）
            Assert.Null(Record.Exception(() => child.ExitCode));
        }
        finally
        {
            job.Dispose();
            child.Dispose();
        }
    }

    private static readonly DateTime Started= new(2026, 10, 8, 3, 0, 0, DateTimeKind.Utc);

    /// <summary>PID と開始時刻が一致する控えだけを選ぶこと</summary>
    [Fact]
    public void PicksLedgeredProcessesThatAreStillTheSameProcess()
    {
        string[] ledger =
        [
            ChildProcessLedger.FormatLine(1234, Started),
            ChildProcessLedger.FormatLine(5678, Started),
            ChildProcessLedger.FormatLine(9012, Started),
        ];

        // 1234 はそのまま、5678 は PID を別のプロセスが使い回している、9012 はもう居ない
        List<int> survivors = ChildProcessLedger.SelectSurvivors(ledger, pid => pid switch
        {
            1234 => Started.AddMilliseconds(300),
            5678 => Started.AddMinutes(10),
            _ => null,
        });

        Assert.Equal(new[] { 1234 }, survivors);
    }

    /// <summary>読めない行を飛ばすこと</summary>
    [Fact]
    public void SkipsLedgerLinesItCannotRead()
    {
        string[] ledger = ["", "abc 1", "1234", "1234 -5", $"1234 {Started.Ticks} extra", ChildProcessLedger.FormatLine(42, Started)];

        Assert.Equal(new[] { 42 }, ChildProcessLedger.SelectSurvivors(ledger, _ => Started));
    }

    /// <summary>控えに自分自身が載っていても対象にしないこと</summary>
    [Fact]
    public void NeverPicksItselfFromTheLedger()
    {
        string[] ledger = [ChildProcessLedger.FormatLine(Environment.ProcessId, Started)];

        Assert.Empty(ChildProcessLedger.SelectSurvivors(ledger, _ => Started));
    }

    /// <summary>控えた子を次の起動で落とし、控えを消すこと</summary>
    [Fact]
    public void KillsARecordedChildAndClearsTheLedger()
    {
        string path = Path.Combine(Path.GetTempPath(), $"host-children-{Guid.NewGuid():N}", "ledger.txt");
        using Process child = Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "ping" : "sleep",
            Arguments = OperatingSystem.IsWindows() ? "-n 60 127.0.0.1" : "60",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        try
        {
            new ChildProcessLedger(path).Record(child);

            // 別の Host が起動し直した体で、新しいインスタンスから読む
            IReadOnlyList<int> killed = new ChildProcessLedger(path).KillSurvivors();

            Assert.Equal(new[] { child.Id }, killed);
            Assert.True(child.WaitForExit(5000));
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
            }

            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
