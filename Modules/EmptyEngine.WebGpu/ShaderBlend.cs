namespace EmptyEngine.WebGpu;

/// <summary>アセットに焼かれたシェーダ指定の、wgpu の描画状態への解決</summary>
/// <remarks>指定が <c>Unspecified</c> の項目は呼び出し側の fallback がそのまま残る＝描画コンポーネントごとの既定を潰さない</remarks>
internal static class ShaderBlend
{
    /// <summary>シェーダが不透明パスを明示しているか</summary>
    public static bool IsOpaque(ShaderAsset shader) => shader.Render == ShaderRenderMode.Opaque;

    /// <summary>シェーダの描画キューの解決</summary>
    public static int ResolveRenderQueue(ShaderAsset shader, int fallback)
    {
        if (shader.Queue != 0) return shader.Queue;

        return shader.Render switch
        {
            ShaderRenderMode.Opaque => ShaderRenderQueue.Geometry,
            ShaderRenderMode.Transparent => ShaderRenderQueue.Transparent,
            _ => fallback,
        };
    }

    /// <summary>シェーダの ZTest / ZWrite 設定の解決</summary>
    public static ShaderDepthState ResolveDepth(ShaderAsset shader, ShaderDepthState fallback)
    {
        bool writeEnabled = shader.DepthWrite switch
        {
            ShaderDepthWrite.On => true,
            ShaderDepthWrite.Off => false,
            _ => fallback.WriteEnabled,
        };

        CompareFunction compare = shader.DepthCompare switch
        {
            ShaderDepthCompare.LessEqual => CompareFunction.LessEqual,
            ShaderDepthCompare.Less => CompareFunction.Less,
            ShaderDepthCompare.Always => CompareFunction.Always,
            _ => fallback.Compare,
        };

        return new ShaderDepthState(writeEnabled, compare);
    }

    /// <summary>シェーダのブレンド成分の決定（指定が無ければアルファ合成）</summary>
    public static (BlendComponent Color, BlendComponent Alpha) Resolve(ShaderAsset shader)
        => TryResolveDirective(shader, out BlendComponent color, out BlendComponent alpha)
            ? (color, alpha)
            : AlphaComponents();

    /// <summary>シェーダがブレンドを明示している場合の、その成分の取り出し</summary>
    public static bool TryResolveDirective(ShaderAsset shader, out BlendComponent color, out BlendComponent alpha)
    {
        switch (shader.Blend)
        {
            case ShaderBlendMode.Invert:
                color = new BlendComponent { SrcFactor = BlendFactor.OneMinusDst, DstFactor = BlendFactor.Zero, Operation = BlendOperation.Add };
                alpha = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.Zero, Operation = BlendOperation.Add };
                return true;

            case ShaderBlendMode.DstAlphaMask:
                // 描画先のアルファ 0 を塗り替え、1 は保持する。
                color = new BlendComponent { SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.DstAlpha, Operation = BlendOperation.Add };
                alpha = color;
                return true;

            case ShaderBlendMode.Alpha:
                (color, alpha) = AlphaComponents();
                return true;

            default:
                color = default;
                alpha = default;
                return false;
        }
    }

    private static (BlendComponent Color, BlendComponent Alpha) AlphaComponents()
    {
        var color = new BlendComponent { SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add };
        var alpha = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add };
        return (color, alpha);
    }
}

internal readonly record struct ShaderDepthState(bool WriteEnabled, CompareFunction Compare)
{
    public static ShaderDepthState Opaque => new(true, CompareFunction.LessEqual);

    public static ShaderDepthState Transparent => new(false, CompareFunction.LessEqual);

    public static ShaderDepthState Overlay => new(false, CompareFunction.Always);
}
