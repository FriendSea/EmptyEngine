namespace EmptyEngine.Audio;

/// <summary>音声クリップの再生の窓口</summary>
public static class GameAudio
{
    private static IAudioPlayer? _player;
    private static readonly object Sync = new();

    /// <summary>再生供給元の外部からの差し込み</summary>
    public static void Install(IAudioPlayer player) => _player = player;

    /// <summary>クリップの一発再生</summary>
    /// <param name="volume">音量</param>
    /// <returns>止めるための手。クリップが無い、鳴らせない、再生装置が無いときは <c>null</c></returns>
    /// <remarks>ループしない。ループや層の音量が要るときは <see cref="CreateVoice"/></remarks>
    public static IAudioPlayback? Play(AudioClipAsset? clip, float volume = 1f)
        => clip is null ? null : GetPlayer().Play(clip, volume);

    /// <summary>止めたり音量を変えたりできる再生口の用意</summary>
    /// <returns>クリップが無い、鳴らせない、再生装置が無いときは <c>null</c></returns>
    /// <remarks>鳴り始めるのは <see cref="IAudioVoice.Play"/> を呼んでから。止めるときは Dispose する</remarks>
    public static IAudioVoice? CreateVoice(AudioClipAsset? clip)
        => clip is null ? null : GetPlayer().CreateVoice(clip);

    public static IAudioPlayer GetPlayer()
    {
        if (_player is not null)
        {
            return _player;
        }

        lock (Sync)
        {
            if (_player is not null)
            {
                return _player;
            }

            _player = OperatingSystem.IsBrowser()
                ? new WebAudioPlayer()
                : new OpenAlAudioPlayer();
            return _player;
        }
    }
}
