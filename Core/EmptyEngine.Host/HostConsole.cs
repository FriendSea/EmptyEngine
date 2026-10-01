using System.Text;

namespace EmptyEngine.Host;

/// <summary>Host の画面</summary>
/// <remarks>状態行と装飾を出すのは端末に向かって喋っているときだけで、パイプ／リダイレクトでは素の行を流す。色だけは <c>NO_COLOR</c> で別に落とせる</remarks>
internal sealed class HostConsole
{
    private const string Reset = "\u001b[0m";
    private const string Red = "\u001b[31;1m";
    private const string Yellow = "\u001b[33;1m";
    private const string Green = "\u001b[32;1m";
    private const string Grey = "\u001b[90m";
    private const string Dim = "\u001b[2m";

    private const string ClearBar = "\r\u001b[2K";
    private const string NoWrap = "\u001b[?7l";
    private const string Wrap = "\u001b[?7h";
    private const string NewLine = "\r\n";

    private readonly object _sync = new();

    private readonly bool _terminal;
    private readonly bool _color;

    private StreamWriter? _logWriter;

    private string _status;
    private string? _prompt;

    private bool _closed;

    public HostConsole()
    {
        _terminal = !Console.IsOutputRedirected;
        _color = _terminal && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        _status = Compose(HostLinkState.Stopped, HostLinkState.Stopped);

        if (!_terminal)
        {
            return;
        }

        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        DrawBar();
    }

    private bool ShowBar => _terminal && !_closed;

    private string BarText => _prompt ?? _status;

    /// <summary>Host 自身が書いたログの 1 行の出力</summary>
    public void Write(HostLogSeverity severity, string message)
    {
        string stamp = Stamp();
        WriteLine(
            Compose(stamp, message, ColorFor(severity), _color),
            Compose(stamp, message, color: null, useColor: false));
    }

    /// <summary>子プロセスが書いたログの 1 行の出力</summary>
    public void WriteFromChild(string message)
    {
        string stamp = Stamp();
        WriteLine(
            Compose(stamp, message, color: null, _color),
            Compose(stamp, message, color: null, useColor: false));
    }

    /// <summary>画面へ流す全ログをプレーンテキストへも書き出す</summary>
    public void StartFileLogging(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Cannot resolve log directory: {fullPath}"));

        var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };

        lock (_sync)
        {
            _logWriter?.Dispose();
            _logWriter = writer;
            _logWriter.WriteLine($"{DateTimeOffset.Now:O} EmptyEngine Host session log started.");
        }
    }

    /// <summary>ログ 1 行の見た目の組み立て</summary>
    internal static string Compose(string stamp, string message, string? color, bool useColor) =>
        useColor
            ? $"{Dim}{stamp}{Reset} {color}{message}{Reset}"
            : $"{stamp} {HostLog.StripAnsi(message)}";

    private void WriteLine(string line, string fileLine)
    {
        lock (_sync)
        {
            try { _logWriter?.WriteLine(fileLine); } catch { }

            if (!ShowBar)
            {
                Emit(line + Environment.NewLine);
                return;
            }

            Emit(ClearBar + line + NewLine + NoWrap + BarText + Wrap);
        }
    }

    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss");

    /// <summary>状態の書き換え</summary>
    public void SetStatus(HostLinkState runtime, HostLinkState editor)
    {
        lock (_sync)
        {
            _status = Compose(runtime, editor);
            if (_prompt is null)
            {
                DrawBar();
            }
        }
    }

    /// <summary>状態行の確認の問いへの差し替え</summary>
    public void Ask(string question)
    {
        lock (_sync)
        {
            _prompt = _color ? $"{Yellow}{question}{Reset}" : question;
            if (ShowBar)
            {
                DrawBar();
            }
            else
            {
                Emit(question + Environment.NewLine);
            }
        }
    }

    /// <summary>問いが済んだあとの状態表示への復帰</summary>
    public void Answered()
    {
        lock (_sync)
        {
            _prompt = null;
            DrawBar();
        }
    }

    /// <summary>状態行を剥がしての端末の返却</summary>
    public void Release()
    {
        lock (_sync)
        {
            if (ShowBar)
            {
                _closed = true;
                _prompt = null;
                Emit(ClearBar);
            }

            try { _logWriter?.Dispose(); } catch { }
            _logWriter = null;
        }
    }

    private string Compose(HostLinkState runtime, HostLinkState editor) =>
        $"{Dot(runtime)} Runtime  {Dot(editor)} Editor" +
        (_color ? $"   {Dim}r/e/a = restart Runtime/Editor/All   Ctrl+C = quit{Reset}"
                : "   r/e/a = restart Runtime/Editor/All   Ctrl+C = quit");

    private string Dot(HostLinkState state)
    {
        (string glyph, string color) = state switch
        {
            HostLinkState.Running => ("●", Green),
            HostLinkState.BuildingOrRestarting => ("◐", Yellow),
            HostLinkState.ConnectedWatcherStopped => ("◐", Yellow),
            _ => ("○", Grey)
        };

        return _color ? $"{color}{glyph}{Reset}" : glyph;
    }

    private void DrawBar()
    {
        if (!ShowBar)
        {
            return;
        }

        Emit(ClearBar + NoWrap + BarText + Wrap);
    }

    private static void Emit(string text)
    {
        try
        {
            Console.Out.Write(text);
            Console.Out.Flush();
        }
        catch
        {
        }
    }

    private static string? ColorFor(HostLogSeverity severity) => severity switch
    {
        HostLogSeverity.Error => Red,
        HostLogSeverity.Warning => Yellow,
        _ => null,
    };
}
