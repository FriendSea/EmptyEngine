using EmptyEngine.Host;
using Xunit;

namespace EmptyEngine.Tests;

/// <summary>Host のログ 1 行の組み立ての検証</summary>
public class HostConsoleLineTests
{
    private const string Esc = "\u001b";

    [Fact]
    public void ChildLinesKeepTheirOwnColors()
    {
        string message = $"[Editor] Program.cs(4,9): {Esc}[31;1merror{Esc}[m CS0103";

        string line = HostConsole.Compose("12:00:00", message, color: null, useColor: true);

        Assert.Contains(message, line, System.StringComparison.Ordinal);
    }

    [Fact]
    public void HostOwnLinesCarryTheColorTheWriterChose()
    {
        string line = HostConsole.Compose("12:00:00", "[Host] Hub error: address in use", $"{Esc}[31;1m", useColor: true);

        Assert.Contains($"{Esc}[31;1m[Host] Hub error: address in use", line, System.StringComparison.Ordinal);
    }

    [Fact]
    public void PipedOutputCarriesNoEscapes()
    {
        string message = $"[Editor] Program.cs(4,9): {Esc}[31;1merror{Esc}[m CS0103";

        string line = HostConsole.Compose("12:00:00", message, color: null, useColor: false);

        Assert.Equal("12:00:00 [Editor] Program.cs(4,9): error CS0103", line);
    }

    [Fact]
    public void StripsHyperlinksAndProgressNotJustColors()
    {
        string line =
            $"{Esc}]9;4;3;{Esc}\\  colortest {Esc}[36;1mnet10.0{Esc}[m {Esc}[33;1msucceeded{Esc}[m " +
            $"{Esc}]8;;file:///C:/bin/Debug/net10.0{Esc}\\bin\\Debug\\net10.0\\colortest.dll{Esc}]8;;{Esc}\\";

        Assert.Equal("  colortest net10.0 succeeded bin\\Debug\\net10.0\\colortest.dll", HostLog.StripAnsi(line));
    }

    [Fact]
    public void FileLogCapturesHostAndChildLinesWithoutAnsi()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ee-host-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "host.log");
        var console = new HostConsole();

        try
        {
            console.StartFileLogging(path);
            console.Write(HostLogSeverity.Warning, "[Host] warning");
            console.WriteFromChild($"[Editor] {Esc}[31;1mstack trace{Esc}[m");
            const string plain = "[Editor] dotnet watch ⏳ Waiting for a file to change before restarting ...";
            console.WriteFromChild(plain);
            console.Release();

            string log = File.ReadAllText(path);
            Assert.Contains("[Host] warning", log, StringComparison.Ordinal);
            Assert.Contains("[Editor] stack trace", log, StringComparison.Ordinal);
            Assert.DoesNotContain(Esc, log, StringComparison.Ordinal);
            Assert.Contains(plain, log, StringComparison.Ordinal);
        }
        finally
        {
            console.Release();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
