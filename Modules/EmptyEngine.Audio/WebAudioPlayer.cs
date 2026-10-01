using System.Runtime.InteropServices;
using EmptyEngine.Core;
using NVorbis;

namespace EmptyEngine.Audio;

/// <summary>web 向けの音再生供給元</summary>
/// <remarks>ブラウザ向け。デスクトップでの再生には対応しない。</remarks>
internal sealed class WebAudioPlayer : IAudioPlayer
{
    [DllImport("ee_audio")] private static extern void ee_audio_setup();
    [DllImport("ee_audio")] private static extern unsafe void ee_audio_register(int id, byte* data, int len);
    [DllImport("ee_audio")] private static extern int ee_audio_play(int id, float volume);
    [DllImport("ee_audio")] private static extern int ee_audio_playback_is_playing(int playback);
    [DllImport("ee_audio")] private static extern double ee_audio_playback_time(int playback);
    [DllImport("ee_audio")] private static extern void ee_audio_playback_stop(int playback);

    private readonly Dictionary<AudioClipAsset, int> _ids = new();
    private int _next;

    public WebAudioPlayer() => ee_audio_setup();

    public unsafe IAudioPlayback? Play(AudioClipAsset clip, float volume = 1f)
    {
        if (!_ids.TryGetValue(clip, out int id))
        {
            id = _next++;
            _ids[clip] = id;
            byte[] bytes = ReadAll(clip.Source);
            fixed (byte* p = bytes)
            {
                ee_audio_register(id, p, bytes.Length);
            }
        }

        int playback = ee_audio_play(id, volume);
        return playback < 0 ? null : new Playback(playback);
    }

    /// <summary>JS 側の再生番号に結びついた一発再生の手</summary>
    private sealed class Playback(int id) : IAudioPlayback
    {
        public bool IsPlaying => ee_audio_playback_is_playing(id) != 0;

        public double Time => ee_audio_playback_time(id);

        public void Dispose() => ee_audio_playback_stop(id);
    }

    public IAudioVoice? CreateVoice(AudioClipAsset clip)
    {
        byte[] file;
        try
        {
            file = ReadAll(clip.Source);
        }
        catch (IOException)
        {
            return null;
        }

        int[] lengths = clip.LayerLengths is { Length: > 0 } layers ? layers : [file.Length];
        int offset = 0;
        foreach (int length in lengths)
        {
            if (length <= 0 || offset > file.Length - length)
                return null;
            offset += length;
        }

        int sampleRate;
        try
        {
            using var reader = new VorbisReader(new MemoryStream(file, 0, lengths[0], writable: false), closeOnDispose: true);
            sampleRate = reader.SampleRate;
        }
        catch (Exception)
        {
            return null;
        }

        if (sampleRate <= 0)
            return null;

        int id = WebAudioVoice.ee_audio_voice_create(lengths.Length);
        if (id < 0)
            return null;

        var voice = new WebAudioVoice(id, lengths.Length);
        unsafe
        {
            fixed (byte* pFile = file)
            fixed (int* pLengths = lengths)
            {
                WebAudioVoice.ee_audio_voice_set_source(
                    id,
                    pFile,
                    file.Length,
                    pLengths,
                    lengths.Length,
                    (double)clip.LoopStartFrame / sampleRate,
                    (double)clip.LoopEndFrame / sampleRate);
            }
        }

        return voice;
    }

    private static byte[] ReadAll(IAssetBinary source)
    {
        using Stream stream = source.OpenRead();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
