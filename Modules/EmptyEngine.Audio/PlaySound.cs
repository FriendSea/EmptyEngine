using EmptyEngine.Generators;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.Audio;

/// <summary>生成された瞬間に効果音を一発鳴らすコンポーネント</summary>
/// <remarks>鳴るのはプレイ時の生成でのみ。エディット時は無音</remarks>
public sealed partial class PlaySound
{
    /// <summary>生成時に鳴らす効果音クリップの解決済み実体</summary>
    [ResolveAsset]
    private AudioClipAsset? _clip;

    public float Volume { get; set; } = 1f;

    public void OnCreated(IObject owner) => GameAudio.Play(_clip, Volume);
}
