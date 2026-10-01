using System.Runtime.InteropServices;

namespace EmptyEngine.Audio;

/// <summary>Web Audio 側でデコードと再生予約を行う再生口</summary>
internal sealed class WebAudioVoice : IAudioVoice
{
    [DllImport("ee_audio")] internal static extern int ee_audio_voice_create(int layerCount);
    [DllImport("ee_audio")] internal static extern unsafe void ee_audio_voice_set_source(
        int id, byte* data, int len, int* lengths, int layerCount, double loopStartSeconds, double loopEndSeconds);
    [DllImport("ee_audio")] private static extern void ee_audio_voice_set_volume(int id, float volume);
    [DllImport("ee_audio")] private static extern double ee_audio_voice_time(int id);
    [DllImport("ee_audio")] private static extern int ee_audio_voice_is_playing(int id);
    [DllImport("ee_audio")] private static extern void ee_audio_voice_set_layer_volume(int id, int layer, float volume);
    [DllImport("ee_audio")] private static extern void ee_audio_voice_play(int id, double startSeconds, int loop, float volume);
    [DllImport("ee_audio")] private static extern void ee_audio_voice_destroy(int id);

    private readonly int _id;
    private readonly float[] _layerVolumes;
    private float _volume = 1f;
    private bool _playing;
    private bool _disposed;

    public WebAudioVoice(int id, int layerCount)
    {
        _id = id;
        _layerVolumes = new float[layerCount];
        _layerVolumes[0] = 1f;
    }

    public int LayerCount => _layerVolumes.Length;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = value;
            if (!_disposed)
                ee_audio_voice_set_volume(_id, value);
        }
    }

    public double Time => _playing && !_disposed ? ee_audio_voice_time(_id) : 0d;

    public bool IsPlaying => _playing && !_disposed && ee_audio_voice_is_playing(_id) != 0;

    public float GetLayerVolume(int layer)
        => (uint)layer < (uint)_layerVolumes.Length ? _layerVolumes[layer] : 0f;

    public void SetLayerVolume(int layer, float volume)
    {
        if ((uint)layer >= (uint)_layerVolumes.Length)
            return;

        _layerVolumes[layer] = volume;
        if (!_disposed)
            ee_audio_voice_set_layer_volume(_id, layer, volume);
    }

    public void Play(double startSeconds = 0d, bool loop = false, float volume = 1f)
    {
        if (_playing || _disposed)
            return;

        _playing = true;
        Volume = volume;
        ee_audio_voice_play(_id, startSeconds, loop ? 1 : 0, volume);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ee_audio_voice_destroy(_id);
    }
}
