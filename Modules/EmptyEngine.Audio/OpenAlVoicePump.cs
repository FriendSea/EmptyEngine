namespace EmptyEngine.Audio;

/// <summary>鳴っている再生口すべてへ PCM を継ぎ足し続ける 1 本のスレッド</summary>
/// <remarks>回す相手が居ない間は眠る</remarks>
internal sealed class OpenAlVoicePump : IDisposable
{
    private const int IntervalMilliseconds = 10;

    private readonly object _sync = new();
    private readonly List<OpenAlAudioVoice> _voices = new();
    private Thread? _thread;
    private bool _stopping;

    public void Add(OpenAlAudioVoice voice)
    {
        lock (_sync)
        {
            if (_stopping)
                return;

            _voices.Add(voice);
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "EmptyEngine audio" };
                _thread.Start();
            }
            Monitor.Pulse(_sync);
        }
    }

    /// <summary>回す相手からの除外</summary>
    /// <remarks>継ぎ足しの最中なら、その 1 周が終わるまで待つ</remarks>
    public void Remove(OpenAlAudioVoice voice)
    {
        lock (_sync)
            _voices.Remove(voice);
    }

    private void Run()
    {
        while (true)
        {
            lock (_sync)
            {
                while (!_stopping && _voices.Count == 0)
                    Monitor.Wait(_sync);
                if (_stopping)
                    return;

                for (int i = _voices.Count - 1; i >= 0; i--)
                {
                    if (!_voices[i].PumpOnce())
                        _voices.RemoveAt(i);
                }
            }

            Thread.Sleep(IntervalMilliseconds);
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_sync)
        {
            _stopping = true;
            _voices.Clear();
            Monitor.Pulse(_sync);
            thread = _thread;
        }
        thread?.Join();
    }
}
