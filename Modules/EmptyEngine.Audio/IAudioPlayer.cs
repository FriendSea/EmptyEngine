namespace EmptyEngine.Audio;

/// <summary>音の再生供給元</summary>
public interface IAudioPlayer
{
    /// <summary>クリップの一発再生（多重再生可）</summary>
    /// <param name="volume">音量</param>
    /// <returns>止めるための手。鳴らせないクリップや再生装置が無いときは <c>null</c></returns>
    IAudioPlayback? Play(AudioClipAsset clip, float volume = 1f);

    /// <summary>止めたり音量を変えたりできる再生口の用意</summary>
    /// <returns>鳴らせないクリップや再生装置が無いときは <c>null</c></returns>
    /// <remarks>鳴り始めるのは <see cref="IAudioVoice.Play"/> を呼んでから</remarks>
    IAudioVoice? CreateVoice(AudioClipAsset clip);
}
