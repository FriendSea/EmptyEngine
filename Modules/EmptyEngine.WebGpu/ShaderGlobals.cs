using System.Numerics;
using GraphicsColor = EmptyEngine.Graphics.Color;

namespace EmptyEngine.WebGpu;

/// <summary>シェーダが名前で拾う、世界で共有する値</summary>
/// <remarks>
/// シェーダ側は <c>@group(1) @binding(0)</c> の uniform 構造体に、必要なメンバだけを好きな順で宣言する。
/// 値はメンバ名で対応付けるので、宣言の並びを世界側と揃える必要は無い。設定の無い名前は WGSL の
/// <c>// default</c> 値になる。
/// </remarks>
public sealed class ShaderGlobals
{
    private readonly Dictionary<string, Vector4> _values = new(StringComparer.Ordinal);

    /// <summary>いずれかの値が変わるたびに進む版数</summary>
    public int Version { get; private set; }

    /// <summary><c>f32</c> メンバへ渡す値の設定</summary>
    public void SetFloat(string name, float value) => Set(name, new Vector4(value, 0f, 0f, 0f));

    /// <summary><c>vec4&lt;f32&gt;</c> メンバへ渡す色の設定</summary>
    public void SetColor(string name, GraphicsColor value) => Set(name, value);

    /// <summary>設定済みの <c>f32</c> 値</summary>
    public bool TryGetFloat(string name, out float value)
    {
        bool found = _values.TryGetValue(name, out Vector4 stored);
        value = found ? stored.X : 0f;
        return found;
    }

    /// <summary>設定済みの色</summary>
    public bool TryGetColor(string name, out GraphicsColor value)
    {
        bool found = _values.TryGetValue(name, out Vector4 stored);
        value = found ? stored : default;
        return found;
    }

    /// <summary>設定の取り消し（シェーダ宣言の既定値へ戻る）</summary>
    public bool Remove(string name)
    {
        if (!_values.Remove(name)) return false;
        Version++;
        return true;
    }

    private void Set(string name, Vector4 value)
    {
        if (_values.TryGetValue(name, out Vector4 current) && current == value) return;
        _values[name] = value;
        Version++;
    }

    /// <summary>シェーダの宣言に合わせた値の詰め直し</summary>
    /// <remarks>設定の無い名前は宣言の既定値で埋める。</remarks>
    internal void Pack(ShaderParam[] declarations, Span<float> destination)
    {
        destination.Clear();
        Span<float> components = stackalloc float[4];
        for (int i = 0; i < declarations.Length; i++)
        {
            ShaderParam declaration = declarations[i];
            int count = ShaderParamsHost.ComponentCount(declaration);
            int offset = ShaderParamsHost.ValueOffset(declarations, i);
            if (offset + count > destination.Length) break;

            if (_values.TryGetValue(declaration.Name, out Vector4 value))
            {
                value.CopyTo(components);
            }
            else
            {
                for (int component = 0; component < count; component++)
                    components[component] = ShaderParamsHost.DefaultAt(declaration, component);
            }

            components[..count].CopyTo(destination.Slice(offset, count));
        }
    }
}
