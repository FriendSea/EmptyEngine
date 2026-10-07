using System.Numerics;
using GraphicsColor = EmptyEngine.Graphics.Color;

namespace EmptyEngine.WebGpu;

/// <summary>シェーダが番号で拾う、世界で共有する値</summary>
/// <remarks>
/// シェーダ側は <c>@group(1) @binding(番号)</c> に、変数を 1 つずつ宣言する
/// （<c>f32</c>・<c>vec2&lt;f32&gt;</c>・<c>vec3&lt;f32&gt;</c>・<c>vec4&lt;f32&gt;</c> のいずれか）。
/// 対応付けは番号だけで、変数名は問わない。設定の無い番号は 0。
/// </remarks>
public sealed class ShaderGlobals
{
    /// <summary>使える番号の数（0 から <c>SlotCount - 1</c>）</summary>
    /// <remarks>1 ステージが束ねられる uniform buffer は 12 個で、うち 3 個をコンポーネントとマテリアルが使う。</remarks>
    public const int SlotCount = 8;

    private readonly Vector4[] _values = new Vector4[SlotCount];

    /// <summary>いずれかの値が変わるたびに進む版数</summary>
    public int Version { get; private set; }

    /// <summary><c>f32</c> として読む値の設定</summary>
    public void SetFloat(int slot, float value) => SetVector(slot, new Vector4(value, 0f, 0f, 0f));

    /// <summary><c>vec4&lt;f32&gt;</c> として読む色の設定</summary>
    public void SetColor(int slot, GraphicsColor value) => SetVector(slot, value);

    /// <summary>ベクトル値の設定</summary>
    public void SetVector(int slot, Vector4 value)
    {
        if (_values[slot] == value) return;
        _values[slot] = value;
        Version++;
    }

    /// <summary>設定済みの値（未設定は 0）</summary>
    public Vector4 GetVector(int slot) => _values[slot];

    /// <summary>設定の取り消し（0 へ戻る）</summary>
    public void Clear(int slot) => SetVector(slot, Vector4.Zero);

    /// <summary>番号順に並べた全部の値</summary>
    internal ReadOnlySpan<Vector4> Values => _values;
}
