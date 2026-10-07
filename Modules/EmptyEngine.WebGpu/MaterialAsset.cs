using EmptyEngine.Core;
using EmptyEngine.Graphics;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.WebGpu;

/// <summary>フラグメントシェーダと、その描画状態・パラメータ・追加テクスチャの組</summary>
/// <remarks>
/// <see cref="Shader"/> は <c>fs_main</c> を必ず持つ。<c>vs_main</c> も持っていれば、コンポーネントが
/// 自分の <c>Shader</c> を指定していないときの頂点シェーダになる。
/// </remarks>
[Asset]
public sealed class MaterialAsset : IAssetResolutionHook
{
    /// <summary>描画に使う WGSL シェーダ（<c>.wgsl</c>）への参照</summary>
    public AssetReference<ShaderAsset> Shader = new();

    /// <summary>描画先との合成</summary>
    public MaterialBlend Blend;

    /// <summary>深度バッファへ書き込むか</summary>
    public bool DepthWrite = true;

    /// <summary>深度比較</summary>
    public MaterialDepthCompare DepthCompare;

    /// <summary>描画キュー番号（<see cref="ShaderRenderQueue"/> が名前付きの値を持つ）</summary>
    public int Queue = ShaderRenderQueue.Geometry;

    /// <summary><c>group(2) binding(0)</c> のパラメータの値</summary>
    public float[] Params = [];

    /// <summary><c>group(2)</c> の追加テクスチャスロットへ貼るテクスチャ参照</summary>
    public AssetReference<TextureAsset>[] Textures = [];

    private ShaderAsset? _resolvedShader;
    private TextureAsset?[] _resolvedTextures = [];

    /// <summary><see cref="Shader"/> を解決した実体</summary>
    public ShaderAsset? ResolvedShader => _resolvedShader;

    /// <summary><see cref="Textures"/> を解決した実体（空の枠は <c>null</c>）</summary>
    public IReadOnlyList<TextureAsset?> ResolvedTextures => _resolvedTextures;

    /// <summary>実体化直後の、シェーダと追加テクスチャの解決（値の枠はシェーダの宣言に合わせる）</summary>
    public async ValueTask OnResolveAssetsAsync(IAssetResolver resolver)
    {
        _resolvedShader = await resolver.ResolveAsync(Shader);
        if (_resolvedShader is not null)
        {
            Params = ShaderParamsHost.Reconcile(_resolvedShader.FragmentParams, Params);
            Textures = ShaderParamsHost.ReconcileTextures(_resolvedShader.TextureSlots, Textures);
        }

        Textures ??= [];
        _resolvedTextures = await ShaderParamsHost.ResolveTexturesAsync(Textures, resolver);
    }
}

/// <summary>マテリアル未指定のコンポーネントが使う、同梱の既定マテリアルのアセットキー</summary>
/// <remarks>
/// 2 つは描画状態だけが違い、同じフラグメントシェーダ（テクスチャ × 色）を使う。
/// 実体は EmptyEngine.WebGpu.Editor が同梱するソースアセット（<c>assets/Opaque.asset</c>、<c>assets/Transparent.asset</c>）。その <c>.meta</c> の guid と、props の <c>DistributionRoot</c> に同じ値を書く。
/// </remarks>
public static class BuiltinMaterials
{
    /// <summary>不透明（合成なし、深度を書く）。Mesh の既定</summary>
    public const string Opaque = "656bbdeb09f144b3a145fa75abcf7736";

    /// <summary>半透明（アルファ合成、深度は書かない）。Sprite・LineRenderer・Effect の既定</summary>
    public const string Transparent = "da3bdabebb974e38adeea904f3ddb830";
}

/// <summary>描画先との合成</summary>
/// <remarks>値はアセットの保存形式に含まれる。</remarks>
public enum MaterialBlend
{
    /// <summary>合成しない（上書き）</summary>
    Opaque = 0,

    /// <summary>通常のアルファ合成</summary>
    Alpha = 1,

    /// <summary>画面の色を反転する合成</summary>
    Invert = 2,

    /// <summary>描画先のアルファが 0 の場所にだけ出る合成</summary>
    DstAlphaMask = 3,
}

/// <summary>深度比較</summary>
/// <remarks>値はアセットの保存形式に含まれる。</remarks>
public enum MaterialDepthCompare
{
    /// <summary>手前と同じ深度まで通す</summary>
    LessEqual = 0,

    /// <summary>手前だけ通す</summary>
    Less = 1,

    /// <summary>深度によらず通す</summary>
    Always = 2,
}
