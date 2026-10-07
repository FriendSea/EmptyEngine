namespace EmptyEngine.WebGpu;

/// <summary>コンポーネント種別ごとの頂点入力（頂点バッファの並び・トポロジ・カリング）</summary>
public enum MaterialVertexKind
{
    /// <summary>共有 quad 頂点（位置 2 + UV 2）</summary>
    Quad = 0,

    /// <summary>メッシュ頂点（位置 3 + 法線 3 + UV 2）</summary>
    Mesh = 1,

    /// <summary>折れ線の triangle strip（位置 3 + UV 2）</summary>
    Line = 2,

    /// <summary>頂点バッファ無し（<c>vertex_index</c> から組み立てる）</summary>
    Effect = 3,
}

/// <summary>コンポーネントが使う頂点シェーダの決め方</summary>
/// <remarks>描画とインスペクタが同じ判定を使う。</remarks>
public static class VertexShaderSelection
{
    private static readonly ShaderVertexInput[] QuadAttributes =
    [
        new() { Location = 0, Type = "vec2<f32>" },
        new() { Location = 1, Type = "vec2<f32>" },
    ];

    private static readonly ShaderVertexInput[] MeshAttributes =
    [
        new() { Location = 0, Type = "vec3<f32>" },
        new() { Location = 1, Type = "vec3<f32>" },
        new() { Location = 2, Type = "vec2<f32>" },
    ];

    private static readonly ShaderVertexInput[] LineAttributes =
    [
        new() { Location = 0, Type = "vec3<f32>" },
        new() { Location = 1, Type = "vec2<f32>" },
    ];

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ShaderAsset, object> Warned = new();

    /// <summary>この頂点入力のコンポーネントが、そのシェーダの <c>vs_main</c> を使えるか</summary>
    /// <remarks>頂点バッファを持つ種別は、シェーダが受け取る属性がすべて自分の頂点に同じ型であること。Effect は属性を 1 つも受け取らないこと。</remarks>
    public static bool Fits(MaterialVertexKind kind, ShaderAsset shader)
    {
        if (!shader.HasVertex) return false;

        ShaderVertexInput[] inputs = shader.VertexInputs ?? [];
        if (kind == MaterialVertexKind.Effect) return inputs.Length == 0;
        if (inputs.Length == 0) return false;

        ShaderVertexInput[] attributes = kind switch
        {
            MaterialVertexKind.Mesh => MeshAttributes,
            MaterialVertexKind.Line => LineAttributes,
            _ => QuadAttributes,
        };
        foreach (ShaderVertexInput input in inputs)
        {
            if (!Array.Exists(attributes, a => a.Location == input.Location && a.Type == input.Type))
                return false;
        }

        return true;
    }

    /// <summary>頂点シェーダの決定</summary>
    /// <remarks>コンポーネントの指定、マテリアルのシェーダ、同梱の順。頂点入力の合わないものは飛ばして次へ落とす。</remarks>
    /// <param name="warn">飛ばしたシェーダの通知先（<c>null</c> なら、シェーダごとに 1 回だけ標準エラーへ）</param>
    public static ShaderAsset? Select(
        MaterialVertexKind kind,
        ShaderAsset? component,
        ShaderAsset? material,
        ShaderAsset? builtin,
        Action<string>? warn = null)
    {
        if (component is not null)
        {
            if (Fits(kind, component)) return component;
            Warn(warn, component, $"The component's shader has no vs_main that fits a {kind} vertex layout; falling back.");
        }

        if (material is { HasVertex: true })
        {
            if (Fits(kind, material)) return material;
            Warn(warn, material, $"The material shader's vs_main does not fit a {kind} vertex layout; falling back to the built-in vertex shader.");
        }

        return builtin is not null && Fits(kind, builtin) ? builtin : null;
    }

    private static void Warn(Action<string>? warn, ShaderAsset shader, string message)
    {
        if (warn is not null)
        {
            warn(message);
            return;
        }

        if (Warned.TryAdd(shader, message))
            Console.Error.WriteLine($"[WebGpu] {message}");
    }
}
