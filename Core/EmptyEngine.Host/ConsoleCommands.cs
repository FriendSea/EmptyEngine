using System.Runtime.InteropServices;

namespace EmptyEngine.Host;

/// <summary>キー 1 打での Host の操作</summary>
internal sealed class ConsoleCommands
{
    private const char CtrlC = '\u0003';

    private readonly HostSession _session;
    private readonly TaskCompletionSource _quit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();

    private readonly ManualResetEventSlim _stopped = new();
    private readonly List<PosixSignalRegistration> _terminations = [];

    private bool _confirming;

    public ConsoleCommands(HostSession session) => _session = session;

    /// <summary>終了が確定するまでの待ち受け</summary>
    public async Task RunUntilQuitAsync()
    {
        Console.CancelKeyPress += OnCancelKeyPress;
        // 端末が閉じられた（SIGHUP／Windows のコンソールを閉じる通知）・止められた（SIGTERM）ときは確認なしで畳む。
        _terminations.Add(PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnTermination));
        _terminations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTermination));

        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "host-console-input" };
        reader.Start();

        try
        {
            await _quit.Task;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }
    }

    /// <summary>子プロセスの後始末が済んだことの知らせ</summary>
    public void NotifyStopped()
    {
        _stopped.Set();
        foreach (PosixSignalRegistration registration in _terminations)
        {
            registration.Dispose();
        }

        _terminations.Clear();
    }

    private void OnTermination(PosixSignalContext context)
    {
        context.Cancel = true;
        _quit.TrySetResult();
        // Windows はこのハンドラが返った時点でプロセスを落とすので、後始末が済むまで返さない。
        _stopped.Wait(TimeSpan.FromSeconds(30));
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        RequestQuit();
    }

    private void ReadLoop()
    {
        try
        {
            if (Console.IsInputRedirected)
            {
                using Stream stdin = Console.OpenStandardInput();
                var buffer = new byte[64];
                while (true)
                {
                    int read = stdin.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        return;
                    }

                    for (int i = 0; i < read; i++)
                    {
                        Handle((char)buffer[i]);
                    }
                }
            }

            while (true)
            {
                Handle(Console.ReadKey(intercept: true).KeyChar);
            }
        }
        catch (Exception)
        {
        }
    }

    private void Handle(char key)
    {
        lock (_gate)
        {
            if (_confirming)
            {
                _confirming = false;
                if (key is 'y' or 'Y' or CtrlC)
                {
                    _quit.TrySetResult();
                    return;
                }

                _session.Console.Answered();
                return;
            }
        }

        switch (key)
        {
            case CtrlC:
                RequestQuit();
                break;

            case 'r' or 'R':
                _ = _session.RestartAsync(RestartScope.Runtime);
                break;

            case 'e' or 'E':
                _ = _session.RestartAsync(RestartScope.Editor);
                break;

            case 'a' or 'A':
                _ = _session.RestartAsync(RestartScope.All);
                break;
        }
    }

    private void RequestQuit()
    {
        lock (_gate)
        {
            if (_confirming)
            {
                _quit.TrySetResult();
                return;
            }

            _confirming = true;
        }

        _session.Console.Ask("Quit? The runtime and the editor will shut down too  [y/N]");
    }
}
