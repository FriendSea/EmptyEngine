using System.Diagnostics;
using System.Globalization;

namespace EmptyEngine.Host;

/// <summary>起こした子プロセスの控え。Host が後始末なしに死んだとき、次の起動で残りを片付けるために使う</summary>
/// <remarks>
/// ジョブ（<see cref="ChildProcessJob"/>）で縛れなかった子のためのもの。
/// ジョブに当たる仕組みが OS に無い環境と、Windows でジョブ経由の起動に失敗したときが当たる。
/// PID は使い回されるので、開始時刻も一致したものだけを同じプロセスとみなす。
/// </remarks>
internal sealed class ChildProcessLedger
{
    /// <summary>開始時刻の読み取りごとの揺れ（Linux は起動時刻からの換算になる）を許す幅</summary>
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    private readonly string _path;

    public ChildProcessLedger(string path) => _path = path;

    /// <summary>子プロセスを控えに足す</summary>
    public void Record(Process process)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.AppendAllText(_path, FormatLine(process.Id, process.StartTime.ToUniversalTime()) + "\n");
    }

    /// <summary>控えにあって今も生きているプロセスをツリーごと落とし、控えを空にする</summary>
    /// <returns>落とした PID。</returns>
    public IReadOnlyList<int> KillSurvivors()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        List<int> survivors = SelectSurvivors(File.ReadAllLines(_path), StartTimeOf);
        foreach (int pid in survivors)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch
            {
            }
        }

        File.Delete(_path);
        return survivors;
    }

    internal static string FormatLine(int pid, DateTime startedUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"{pid} {startedUtc.Ticks}");

    /// <summary>控えの各行のうち、PID と開始時刻の両方が今のプロセスと一致するものの選び出し</summary>
    /// <param name="startTimeOf">PID から今の開始時刻（UTC）を引く。そのプロセスが居なければ null。</param>
    internal static List<int> SelectSurvivors(IEnumerable<string> lines, Func<int, DateTime?> startTimeOf)
    {
        var survivors = new List<int>();
        foreach (string line in lines)
        {
            if (!TryParseLine(line, out int pid, out DateTime recordedUtc)
                || pid == Environment.ProcessId
                || survivors.Contains(pid))
            {
                continue;
            }

            if (startTimeOf(pid) is DateTime actualUtc && (actualUtc - recordedUtc).Duration() <= StartTimeTolerance)
            {
                survivors.Add(pid);
            }
        }

        return survivors;
    }

    internal static bool TryParseLine(string line, out int pid, out DateTime startedUtc)
    {
        pid = 0;
        startedUtc = default;

        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out pid)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
            || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        startedUtc = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    private static DateTime? StartTimeOf(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.HasExited ? null : process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }
}
