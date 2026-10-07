using EmptyEngine.Core;
using EmptyEngine.Graphics;

namespace EmptyEngine.WebGpu;

/// <summary>マテリアルで描き、頂点シェーダとそのパラメータを自分で持てるコンポーネントの契約</summary>
public interface IShaderParamsHost
{
    /// <summary>頂点シェーダに使う WGSL シェーダ（<c>.wgsl</c>）への参照。空ならマテリアルのシェーダ、それも頂点を持たなければ同梱のものを使う</summary>
    AssetReference<ShaderAsset> Shader { get; }

    /// <summary>頂点シェーダが宣言する <c>group(0) binding(3)</c> のパラメータの値</summary>
    float[] Params { get; set; }

    /// <summary>描画に使うマテリアルへの参照。空なら同梱の既定マテリアルを使う</summary>
    AssetReference<MaterialAsset> Material { get; }
}

/// <summary>シェーダ宣言に合わせてパラメータ配列を調整する</summary>
public static class ShaderParamsHost
{
    /// <summary>テクスチャ枠の参照の一斉の解決</summary>
    /// <returns>枠ごとの実体（空の枠と行き先の無い枠は <c>null</c>）</returns>
    public static async ValueTask<TextureAsset?[]> ResolveTexturesAsync(
        AssetReference<TextureAsset>[] references, IAssetResolver resolver)
    {
        var resolving = new ValueTask<TextureAsset?>[references.Length];
        for (int i = 0; i < references.Length; i++)
            resolving[i] = resolver.ResolveAsync(references[i]);

        var resolved = new TextureAsset?[references.Length];
        for (int i = 0; i < references.Length; i++)
            resolved[i] = await resolving[i];
        return resolved;
    }

    /// <summary>指定パラメータが flat float 配列内で始まる位置</summary>
    public static int ValueOffset(ShaderParam[]? definitions, int parameterIndex)
    {
        ShaderParam[] defs = definitions ?? [];
        int cursor = 0;
        for (int i = 0; i < parameterIndex && i < defs.Length; i++)
        {
            cursor = Align(cursor, defs[i]);
            cursor += ComponentCount(defs[i]);
        }

        return parameterIndex < defs.Length ? Align(cursor, defs[parameterIndex]) : cursor;
    }

    /// <summary>WGSL の uniform 配置を含めて必要になる float 数</summary>
    public static int ValueCount(ShaderParam[]? definitions)
    {
        ShaderParam[] defs = definitions ?? [];
        if (defs.Length == 0) return 0;

        int last = defs.Length - 1;
        return ValueOffset(defs, last) + ComponentCount(defs[last]);
    }

    /// <summary>論理パラメータの成分数</summary>
    public static int ComponentCount(ShaderParam definition) =>
        definition.Kind == ShaderParamKind.Color ? 4 : 1;

    /// <summary>論理パラメータの指定成分の初期値</summary>
    public static float DefaultAt(ShaderParam definition, int component) => component switch
    {
        0 => definition.Default,
        1 => definition.DefaultG,
        2 => definition.DefaultB,
        3 => definition.DefaultA,
        _ => 0f,
    };

    /// <summary>シェーダのパラメータ記述に合わせた値配列の長さ調整</summary>
    public static float[] Reconcile(ShaderParam[]? definitions, float[]? current)
    {
        ShaderParam[] defs = definitions ?? [];
        float[] values = current ?? [];
        int valueCount = ValueCount(defs);
        if (values.Length == valueCount) return values;

        var next = new float[valueCount];
        for (int i = 0; i < defs.Length; i++)
        {
            int offset = ValueOffset(defs, i);
            for (int component = 0; component < ComponentCount(defs[i]); component++)
                next[offset + component] = DefaultAt(defs[i], component);
        }

        Array.Copy(values, next, Math.Min(values.Length, next.Length));
        return next;
    }

    /// <summary>シェーダのテクスチャスロット記述に合わせた参照配列の長さ調整</summary>
    public static AssetReference<TextureAsset>[] ReconcileTextures(
        ShaderTextureSlot[]? slots, AssetReference<TextureAsset>[]? current)
    {
        ShaderTextureSlot[] defs = slots ?? [];
        AssetReference<TextureAsset>[] values = current ?? [];
        if (values.Length == defs.Length) return values;

        var next = new AssetReference<TextureAsset>[defs.Length];
        for (int i = 0; i < defs.Length; i++)
            next[i] = i < values.Length ? values[i] : default;
        return next;
    }

    private static int Align(int cursor, ShaderParam definition) =>
        definition.Kind == ShaderParamKind.Color ? (cursor + 3) & ~3 : cursor;
}
