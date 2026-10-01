using Silk.NET.OpenAL;

namespace EmptyEngine.Audio;

/// <summary>desktop 向けの音声の再生口</summary>
/// <remarks>混ぜた PCM を、<see cref="OpenAlVoicePump"/> が OpenAL のソースのキューへ継ぎ足し続ける</remarks>
internal sealed unsafe class OpenAlAudioVoice : IAudioVoice
{
    private const int FramesPerBuffer = 2048;
    private const int BufferCount = 8;

    private readonly AL _al;
    private readonly LayeredAudioStream _stream;
    private readonly OpenAlVoicePump _pump;
    private readonly object _sync = new();
    private readonly uint _source;
    private readonly uint[] _buffers;
    private readonly Stack<uint> _free;
    private readonly Queue<(long Start, int Frames)> _queued = new();
    private readonly float[] _mix = new float[FramesPerBuffer * 2];
    private readonly short[] _pcm = new short[FramesPerBuffer * 2];
    private volatile bool _stopping;
    private float _volume = 1f;
    private long _idleFrame;
    private bool _started;
    private bool _ended;
    private bool _disposed;

    public OpenAlAudioVoice(AL al, LayeredAudioStream stream, OpenAlVoicePump pump)
    {
        _al = al;
        _stream = stream;
        _pump = pump;
        _source = al.GenSource();
        _buffers = al.GenBuffers(BufferCount);
        _free = new Stack<uint>(_buffers);
    }

    public int LayerCount => _stream.LayerCount;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = value;
            lock (_sync)
            {
                if (!_disposed)
                    _al.SetSourceProperty(_source, SourceFloat.Gain, value);
            }
        }
    }

    public double Time
    {
        get
        {
            lock (_sync)
            {
                if (_disposed || _queued.Count == 0)
                    return _idleFrame / (double)_stream.SampleRate;

                _al.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out int offset);
                return _stream.WrapFrame(_queued.Peek().Start + offset) / (double)_stream.SampleRate;
            }
        }
    }

    public bool IsPlaying
    {
        get
        {
            lock (_sync)
                return _started && !_disposed && !(_ended && _queued.Count == 0);
        }
    }

    public float GetLayerVolume(int layer) => _stream.GetLayerVolume(layer);

    public void SetLayerVolume(int layer, float volume) => _stream.SetLayerVolume(layer, volume);

    public void Play(double startSeconds = 0d, bool loop = false, float volume = 1f)
    {
        if (_started || _disposed)
            return;

        _started = true;
        Volume = volume;
        _stream.Loop = loop;
        _stream.Seek(startSeconds);
        _idleFrame = _stream.Position;
        Pump();
        _pump.Add(this);
    }

    /// <summary><see cref="OpenAlVoicePump"/> からの 1 回分の継ぎ足し</summary>
    /// <returns>まだ回し続けるか</returns>
    internal bool PumpOnce()
    {
        if (_stopping)
            return false;

        try
        {
            Pump();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[audio] audio stream stopped: {e}");
            return false;
        }

        lock (_sync)
            return !(_ended && _queued.Count == 0);
    }

    private void Pump()
    {
        lock (_sync)
            ReclaimProcessed();

        while (true)
        {
            uint buffer;
            lock (_sync)
            {
                if (_stopping || _ended || _free.Count == 0)
                    break;
                buffer = _free.Pop();
            }

            long start = _stream.Position;
            int frames = _stream.Read(_mix);
            bool ended = _stream.Ended;
            for (int i = 0; i < frames * 2; i++)
                _pcm[i] = (short)(_mix[i] * short.MaxValue);

            lock (_sync)
            {
                _ended = ended;
                if (frames == 0)
                {
                    _free.Push(buffer);
                    break;
                }

                fixed (short* pcm = _pcm)
                    _al.BufferData(buffer, BufferFormat.Stereo16, pcm, frames * 2 * sizeof(short), _stream.SampleRate);
                _al.SourceQueueBuffers(_source, [buffer]);
                _queued.Enqueue((start, frames));
            }
        }

        lock (_sync)
        {
            if (_stopping)
                return;

            _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
            if ((SourceState)state == SourceState.Playing)
                return;

            ReclaimProcessed();
            if (_queued.Count > 0)
                _al.SourcePlay(_source);
        }
    }

    private void ReclaimProcessed()
    {
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
        if (processed <= 0)
            return;

        uint[] finished = new uint[processed];
        _al.SourceUnqueueBuffers(_source, finished);
        foreach (uint buffer in finished)
        {
            _free.Push(buffer);
            (long start, int frames) = _queued.Dequeue();
            _idleFrame = _stream.WrapFrame(start + frames);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _stopping = true;
        _pump.Remove(this);
        lock (_sync)
        {
            _disposed = true;
            _al.SourceStop(_source);
            _al.SetSourceProperty(_source, SourceInteger.Buffer, 0);
            _al.DeleteSource(_source);
            _al.DeleteBuffers(_buffers);
        }

        _stream.Dispose();
    }
}
