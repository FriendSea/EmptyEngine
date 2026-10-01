using EmptyEngine.Core;
using Silk.NET.OpenAL;

namespace EmptyEngine.Audio;

/// <summary>desktop 向けの音再生供給元</summary>
public sealed unsafe class OpenAlAudioPlayer : IAudioPlayer, IDisposable
{
    private readonly AL? _al;
    private readonly ALContext? _alc;
    private readonly Device* _device;
    private readonly Context* _context;
    private readonly bool _ready;

    private readonly Dictionary<AudioClipAsset, uint> _buffers = new();
    private readonly List<uint> _sources = new();
    private readonly Dictionary<uint, int> _generations = new();
    private readonly OpenAlVoicePump _pump = new();

    public OpenAlAudioPlayer()
    {
        try
        {
            _alc = ALContext.GetApi();
            _al = AL.GetApi();
            _device = _alc.OpenDevice("");
            if (_device == null)
            {
                return;
            }

            _context = _alc.CreateContext(_device, null);
            _alc.MakeContextCurrent(_context);
            _ready = _al.GetError() == AudioError.NoError;
        }
        catch
        {
            _ready = false;
        }
    }

    public IAudioPlayback? Play(AudioClipAsset clip, float volume = 1f)
    {
        if (!_ready || _al is null)
        {
            return null;
        }

        uint buffer = GetOrCreateBuffer(clip);
        if (buffer == 0)
        {
            return null;
        }

        uint source = RentSource();
        int generation = _generations[source] = _generations.GetValueOrDefault(source) + 1;
        _al.SetSourceProperty(source, SourceInteger.Buffer, (int)buffer);
        _al.SetSourceProperty(source, SourceFloat.Gain, volume);
        _al.SourcePlay(source);
        return new Playback(this, source, generation);
    }

    public IAudioVoice? CreateVoice(AudioClipAsset clip)
    {
        if (!_ready || _al is null || LayeredAudioStream.TryOpen(clip) is not { } stream)
        {
            return null;
        }

        return new OpenAlAudioVoice(_al, stream, _pump);
    }

    private uint GetOrCreateBuffer(AudioClipAsset clip)
    {
        if (_buffers.TryGetValue(clip, out uint existing))
        {
            return existing;
        }

        byte[] file = ReadAll(clip.Source);
        if (!VorbisDecoder.TryDecode(file, out byte[] pcm, out int channels, out int sampleRate))
        {
            _buffers[clip] = 0;
            return 0;
        }

        BufferFormat format = channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16;

        uint buffer = _al!.GenBuffer();
        _al.BufferData<byte>(buffer, format, pcm, sampleRate);
        _buffers[clip] = buffer;
        return buffer;
    }

    private uint RentSource()
    {
        foreach (uint s in _sources)
        {
            _al!.GetSourceProperty(s, GetSourceInteger.SourceState, out int state);
            if ((SourceState)state != SourceState.Playing)
            {
                return s;
            }
        }

        uint created = _al!.GenSource();
        _sources.Add(created);
        return created;
    }

    private bool IsCurrent(uint source, int generation)
        => _generations.TryGetValue(source, out int current) && current == generation;

    private static byte[] ReadAll(IAssetBinary source)
    {
        using Stream stream = source.OpenRead();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public void Dispose()
    {
        _pump.Dispose();
        if (_al is not null)
        {
            foreach (uint s in _sources) _al.DeleteSource(s);
            foreach (uint b in _buffers.Values) if (b != 0) _al.DeleteBuffer(b);
        }
        if (_alc is not null)
        {
            _alc.MakeContextCurrent(null);
            if (_context != null) _alc.DestroyContext(_context);
            if (_device != null) _alc.CloseDevice(_device);
        }
    }

    /// <summary>使い回しの source に世代で結びついた一発再生の手</summary>
    /// <remarks>source が次の再生へ貸し出されたあとは何も触らない</remarks>
    private sealed class Playback(OpenAlAudioPlayer player, uint source, int generation) : IAudioPlayback
    {
        public bool IsPlaying
        {
            get
            {
                if (!player.IsCurrent(source, generation))
                    return false;

                player._al!.GetSourceProperty(source, GetSourceInteger.SourceState, out int state);
                return (SourceState)state == SourceState.Playing;
            }
        }

        public double Time
        {
            get
            {
                if (!player.IsCurrent(source, generation))
                    return 0d;

                player._al!.GetSourceProperty(source, SourceFloat.SecOffset, out float seconds);
                return seconds;
            }
        }

        public void Dispose()
        {
            if (player.IsCurrent(source, generation))
                player._al!.SourceStop(source);
        }
    }
}
