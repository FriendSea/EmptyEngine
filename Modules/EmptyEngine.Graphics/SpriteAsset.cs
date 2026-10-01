using System.Numerics;
using EmptyEngine.Core;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.Graphics;

/// <summary>テクスチャの一部を 1 枚のスプライトとして切り出すアセット</summary>
[Asset]
public sealed class SpriteAsset : IAssetResolutionHook
{
    /// <summary>切り出し元の <see cref="TextureAsset"/> への参照</summary>
    public AssetReference<TextureAsset> Texture = new();

    private TextureAsset? _resolvedTexture;

    /// <summary><see cref="Texture"/> を解決した実体</summary>
    public TextureAsset? ResolvedTexture => _resolvedTexture;

    /// <summary>実体化直後の内包する <see cref="Texture"/> 参照の解決</summary>
    public async ValueTask OnResolveAssetsAsync(IAssetResolver resolver)
        => _resolvedTexture = await resolver.ResolveAsync(Texture);

    /// <summary>テクスチャ内で使用する矩形領域（正規化 UV）</summary>
    public Rect Region = Rect.Full;

    /// <summary>スプライトの中心（ピボット）</summary>
    public Vector2 Pivot = new(0.5f, 0.5f);

    /// <summary>表示スケール（等倍 = 1）</summary>
    public float Scale = 1f;

    /// <summary>コマ送り速度（1 秒あたりに進むフレーム数）</summary>
    public float AnimationSpeed = 0f;

    /// <summary>アニメーションの総フレーム数</summary>
    public int FrameCount = 1;
}
