using EmptyEngine.Core;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.WebGpu;

/// <summary>インポート済みシェーダ</summary>
/// <remarks>描画状態には各プロパティの値を使う。<see cref="Source"/> はシェーダモジュールのソースを保持する。</remarks>
[Asset]
public sealed class ShaderAsset
{
    /// <summary>WGSL ソース本体（UTF-8 バイト列）</summary>
    public IAssetBinary Source = null!;

    /// <summary>シェーダが公開するユーザ定義パラメータの記述</summary>
    public ShaderParam[] Params { get; set; } = [];

    /// <summary>シェーダが公開する追加テクスチャスロットの記述</summary>
    public ShaderTextureSlot[] TextureSlots { get; set; } = [];

    /// <summary>シェーダが <c>group(1)</c> で拾う、世界の共有値の記述</summary>
    /// <remarks>値は <see cref="ShaderGlobals"/> から名前で対応付ける。</remarks>
    public ShaderParam[] Globals { get; set; } = [];

    /// <summary><c>//! blend:</c> の指定</summary>
    public ShaderBlendMode Blend { get; set; }

    /// <summary><c>//! render:</c> の指定</summary>
    public ShaderRenderMode Render { get; set; }

    /// <summary><c>//! queue:</c> の解決済み描画キュー番号</summary>
    /// <remarks>0 は未指定を表す（利用側が自分の既定を採る）。<see cref="ShaderRenderQueue"/> が名前付きの値を持つ</remarks>
    public int Queue { get; set; }

    /// <summary><c>//! zwrite:</c> の指定</summary>
    public ShaderDepthWrite DepthWrite { get; set; }

    /// <summary><c>//! ztest:</c> の指定</summary>
    public ShaderDepthCompare DepthCompare { get; set; }
}

/// <summary>シェーダ未指定のコンポーネントが使う、同梱の既定シェーダのアセットキー</summary>
/// <remarks>実体は EmptyEngine.WebGpu.Editor が同梱するソースアセット（<c>assets/*.wgsl</c>）。その <c>.meta</c> の guid と、props の <c>DistributionRoot</c> に同じ値を書く。</remarks>
public static class BuiltinShaders
{
    public const string Sprite = "d73a34965e56402c81ae33d70b969657";
    public const string Mesh = "84e640fe39354467b69a0ac4fa6d9e4b";
    public const string Line = "7709483a8f3f41f99e7760f4c56f7777";
}

/// <summary>シェーダが要求するブレンド</summary>
/// <remarks><c>Unspecified</c> はブレンド方法を指定しないことを表す。値はアセットの保存形式に含まれる。</remarks>
public enum ShaderBlendMode
{
    /// <summary>指定なし</summary>
    Unspecified = 0,

    /// <summary>通常のアルファ合成</summary>
    Alpha = 1,

    /// <summary>画面の色を反転する合成</summary>
    Invert = 2,

    /// <summary>描画先のアルファが 0 の場所にだけ出る合成</summary>
    DstAlphaMask = 3,
}

/// <summary>シェーダが不透明・半透明のどちらとして描かれることを想定しているか</summary>
/// <remarks>値はアセットの保存形式に含まれる。</remarks>
public enum ShaderRenderMode
{
    /// <summary>指定なし</summary>
    Unspecified = 0,

    /// <summary>不透明</summary>
    Opaque = 1,

    /// <summary>半透明</summary>
    Transparent = 2,
}

/// <summary>シェーダが要求する深度バッファへの書き込み</summary>
/// <remarks>値はアセットの保存形式に含まれる。</remarks>
public enum ShaderDepthWrite
{
    /// <summary>指定なし</summary>
    Unspecified = 0,

    /// <summary>書き込む</summary>
    On = 1,

    /// <summary>書き込まない</summary>
    Off = 2,
}

/// <summary>シェーダが要求する深度比較</summary>
/// <remarks>プラットフォームに依存しない深度比較の指定。</remarks>
public enum ShaderDepthCompare
{
    /// <summary>指定なし</summary>
    Unspecified = 0,

    /// <summary>手前と同じ深度まで通す</summary>
    LessEqual = 1,

    /// <summary>手前だけ通す</summary>
    Less = 2,

    /// <summary>深度によらず通す</summary>
    Always = 3,
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

    /// <summary>WGSL 宣言の binding 番号（4 以降）</summary>
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
