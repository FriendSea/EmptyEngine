using EmptyEngine.Core;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.WebGpu;

/// <summary>インポート済みシェーダ</summary>
/// <remarks>描画状態は持たない（<see cref="MaterialAsset"/> が持つ）。ここにあるのは WGSL が宣言するインターフェースだけ。</remarks>
[Asset]
public sealed class ShaderAsset
{
    /// <summary>WGSL ソース本体（UTF-8 バイト列）</summary>
    public IAssetBinary Source = null!;

    /// <summary><c>vs_main</c> を持つか</summary>
    public bool HasVertex { get; set; }

    /// <summary><c>fs_main</c> を持つか</summary>
    public bool HasFragment { get; set; }

    /// <summary><c>vs_main</c> が受け取る頂点属性</summary>
    public ShaderVertexInput[] VertexInputs { get; set; } = [];

    /// <summary><c>group(0) binding(3)</c> のパラメータの記述（値はコンポーネントが持つ）</summary>
    public ShaderParam[] VertexParams { get; set; } = [];

    /// <summary><c>group(2) binding(0)</c> のパラメータの記述（値はマテリアルが持つ）</summary>
    public ShaderParam[] FragmentParams { get; set; } = [];

    /// <summary><c>group(2)</c> の追加テクスチャスロットの記述</summary>
    public ShaderTextureSlot[] TextureSlots { get; set; } = [];
}

/// <summary>同梱の既定シェーダのアセットキー</summary>
/// <remarks>実体は EmptyEngine.WebGpu.Editor が同梱するソースアセット（<c>assets/*.wgsl</c>）。その <c>.meta</c> の guid と、props の <c>DistributionRoot</c> に同じ値を書く。</remarks>
public static class BuiltinShaders
{
    public const string Sprite = "d73a34965e56402c81ae33d70b969657";
    public const string Mesh = "84e640fe39354467b69a0ac4fa6d9e4b";
    public const string Line = "7709483a8f3f41f99e7760f4c56f7777";
    public const string Effect = "796e4bab36074477a12773b3be36a84f";
}

/// <summary><c>vs_main</c> が受け取る頂点属性 1 つ分の記述</summary>
public sealed class ShaderVertexInput
{
    /// <summary><c>@location</c> の番号</summary>
    public int Location { get; set; }

    /// <summary>空白を除いた WGSL の型名（<c>vec3&lt;f32&gt;</c> など）</summary>
    public string Type { get; set; } = string.Empty;
}

/// <summary>シェーダのユーザ定義パラメータ 1 つ分の記述</summary>
public sealed class ShaderParam
{
    /// <summary>パラメータ名</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>未指定時の初期値</summary>
    public float Default { get; set; }

    /// <summary>パラメータの表示・格納形式</summary>
    public ShaderParamKind Kind { get; set; }

    /// <summary>Color の G 成分の初期値</summary>
    public float DefaultG { get; set; }

    /// <summary>Color の B 成分の初期値</summary>
    public float DefaultB { get; set; }

    /// <summary>Color の A 成分の初期値</summary>
    public float DefaultA { get; set; } = 1f;
}

/// <summary>シェーダパラメータの論理型</summary>
/// <remarks>値はアセットの保存形式に含まれる。</remarks>
public enum ShaderParamKind
{
    /// <summary>WGSL の <c>f32</c></summary>
    Float = 0,

    /// <summary>インスペクタで Color として表示する WGSL の <c>vec4&lt;f32&gt;</c></summary>
    Color = 1,
}

/// <summary>シェーダの追加テクスチャスロット 1 つ分の記述</summary>
public sealed class ShaderTextureSlot
{
    /// <summary>スロット名</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>WGSL 宣言の binding 番号（2 以降）</summary>
    public int Binding { get; set; }
}

/// <summary>描画順を指定する標準のキュー番号</summary>
public static class ShaderRenderQueue
{
    public const int Background = 1000;
    public const int Geometry = 2000;
    public const int AlphaTest = 2450;
    public const int Transparent = 3000;
    public const int Overlay = 4000;
}
