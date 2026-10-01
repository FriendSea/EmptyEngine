using EmptyEngine.Core;
using NVorbis;

namespace EmptyEngine.Audio;

/// <summary>クリップの層を並べて読み進め、ステレオへ混ぜる音源</summary>
internal sealed class LayeredAudioStream : IDisposable
{
    private readonly VorbisReader[] _readers;
    private readonly float[] _targetVolumes;
    private readonly float[] _appliedVolumes;
    private readonly float[] _snapshot;
    private readonly object _volumeSync = new();
    private float[] _decoded = [];
    private bool _started;

    private LayeredAudioStream(VorbisReader[] readers, int sampleRate, long frames, long loopStartFrame, long loopEndFrame)
    {
        _readers = readers;
        SampleRate = sampleRate;
        Frames = frames;
        LoopStartFrame = loopStartFrame;
        LoopEndFrame = loopEndFrame;
        _targetVolumes = new float[readers.Length];
        _targetVolumes[0] = 1f;
        _appliedVolumes = new float[readers.Length];
        _snapshot = new float[readers.Length];
    }

    public int SampleRate { get; }

    public int LayerCount => _readers.Length;

    /// <summary>クリップ全体のフレーム数</summary>
    public long Frames { get; }

    /// <summary>繰り返すか</summary>
    /// <remarks>繰り返さないときはクリップの末尾まで読んで <see cref="Ended"/> になる</remarks>
    public bool Loop { get; set; } = true;

    /// <summary>繰り返さない読み出しがクリップの末尾に届いたか</summary>
    public bool Ended { get; private set; }

    /// <summary>繰り返しの開始位置（ここより前は初回だけ鳴らす）</summary>
    public long LoopStartFrame { get; }

    /// <summary>繰り返しの終端（このフレームは含まない）</summary>
    public long LoopEndFrame { get; }

    /// <summary>1 周のフレーム数</summary>
    public long LoopFrames => LoopEndFrame - LoopStartFrame;

    /// <summary>次に読み出すフレームの位置</summary>
    public long Position { get; private set; }

    public static LayeredAudioStream? TryOpen(AudioClipAsset clip)
    {
        var readers = new List<VorbisReader>();
        try
        {
            byte[] file = ReadAll(clip.Source);
            int[] lengths = clip.LayerLengths is { Length: > 0 } layers ? layers : [file.Length];
            int offset = 0;
            foreach (int length in lengths)
            {
                if (length <= 0 || offset + length > file.Length)
                    return Fail();

                readers.Add(new VorbisReader(new MemoryStream(file, offset, length, writable: false), closeOnDispose: true));
                offset += length;
            }

            int sampleRate = readers[0].SampleRate;
            long loopFrames = long.MaxValue;
            foreach (VorbisReader reader in readers)
            {
                if (reader.Channels is < 1 or > 2 || reader.SampleRate != sampleRate)
                    return Fail();

                loopFrames = Math.Min(loopFrames, reader.TotalSamples);
            }

            if (sampleRate <= 0 || loopFrames <= 0)
                return Fail();

            long loopStart = 0;
            long loopEnd = loopFrames;
            if (clip.LoopStartFrame >= 0 && clip.LoopStartFrame < clip.LoopEndFrame && clip.LoopEndFrame <= loopFrames)
            {
                loopStart = clip.LoopStartFrame;
                loopEnd = clip.LoopEndFrame;
            }

            return new LayeredAudioStream([.. readers], sampleRate, loopFrames, loopStart, loopEnd);
        }
        catch
        {
            return Fail();
        }

        LayeredAudioStream? Fail()
        {
            foreach (VorbisReader reader in readers)
                reader.Dispose();
            return null;
        }
    }

    public float GetLayerVolume(int layer)
    {
        lock (_volumeSync)
            return (uint)layer < (uint)_targetVolumes.Length ? _targetVolumes[layer] : 0f;
    }

    public void SetLayerVolume(int layer, float volume)
    {
        lock (_volumeSync)
        {
            if ((uint)layer < (uint)_targetVolumes.Length)
                _targetVolumes[layer] = volume;
        }
    }

    public void Seek(double seconds)
    {
        long frame = WrapFrame(Math.Max(0L, (long)(seconds * SampleRate)));
        Ended = frame >= Frames;
        if (Ended)
            frame = Frames;

        foreach (VorbisReader reader in _readers)
            reader.SamplePosition = Math.Min(frame, Frames - 1);
        Position = frame;
    }

    /// <summary>イントロとループ区間を考慮したクリップ上のフレーム位置</summary>
    /// <remarks>繰り返さないときは末尾で止まる</remarks>
    public long WrapFrame(long frame)
    {
        if (!Loop)
            return Math.Clamp(frame, 0L, Frames);

        if (frame >= 0 && frame < LoopEndFrame)
            return frame;

        long offset = (frame - LoopStartFrame) % LoopFrames;
        return LoopStartFrame + (offset < 0 ? offset + LoopFrames : offset);
    }

    /// <summary>ステレオでの読み出し</summary>
    /// <param name="stereo">L/R を交互に並べた書き込み先。長さの半分のフレームを埋め、音の無い分は 0 にする</param>
    /// <returns>クリップから読んだフレーム数（繰り返すときは常に長さの半分）</returns>
    /// <remarks>層の音量は、前回の読み出しで使った値からいまの値へ、この範囲の中で直線に移す</remarks>
    public int Read(Span<float> stereo)
    {
        int frames = stereo.Length / 2;
        stereo.Clear();

        lock (_volumeSync)
            _targetVolumes.CopyTo(_snapshot, 0);
        if (!_started)
        {
            _snapshot.CopyTo(_appliedVolumes, 0);
            _started = true;
        }

        int done = 0;
        while (done < frames && !Ended)
        {
            long end = Loop ? LoopEndFrame : Frames;
            int chunk = (int)Math.Min(frames - done, end - Position);
            for (int layer = 0; layer < _readers.Length; layer++)
                MixLayer(layer, stereo, done, chunk, frames);

            done += chunk;
            Position += chunk;
            if (Position < end)
                continue;

            if (!Loop)
            {
                Ended = true;
                break;
            }

            Position = LoopStartFrame;
            foreach (VorbisReader reader in _readers)
                reader.SamplePosition = LoopStartFrame;
        }

        _snapshot.CopyTo(_appliedVolumes, 0);

        for (int i = 0; i < stereo.Length; i++)
            stereo[i] = Math.Clamp(stereo[i], -1f, 1f);
        return done;
    }

    private void MixLayer(int layer, Span<float> stereo, int done, int chunk, int frames)
    {
        VorbisReader reader = _readers[layer];
        int channels = reader.Channels;
        int wanted = chunk * channels;
        if (_decoded.Length < wanted)
            _decoded = new float[wanted];

        int got = 0;
        int read;
        while (got < wanted && (read = reader.ReadSamples(_decoded, got, wanted - got)) > 0)
            got += read;

        float from = _appliedVolumes[layer];
        float to = _snapshot[layer];
        if (from == 0f && to == 0f)
            return;

        for (int i = 0; i < got / channels; i++)
        {
            float volume = from + ((to - from) * (done + i) / frames);
            float left = _decoded[i * channels];
            float right = channels > 1 ? _decoded[(i * channels) + 1] : left;
            stereo[2 * (done + i)] += left * volume;
            stereo[(2 * (done + i)) + 1] += right * volume;
        }
    }

    private static byte[] ReadAll(IAssetBinary source)
    {
        using Stream stream = source.OpenRead();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public void Dispose()
    {
        foreach (VorbisReader reader in _readers)
            reader.Dispose();
    }
}
