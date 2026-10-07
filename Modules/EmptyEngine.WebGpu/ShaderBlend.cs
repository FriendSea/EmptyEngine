namespace EmptyEngine.WebGpu;

/// <summary>マテリアルの描画状態の、wgpu の描画状態への解決</summary>
internal static class ShaderBlend
{
    /// <summary>マテリアルの深度比較の解決</summary>
    public static CompareFunction Resolve(MaterialDepthCompare compare) => compare switch
    {
        MaterialDepthCompare.Less => CompareFunction.Less,
        MaterialDepthCompare.Always => CompareFunction.Always,
        _ => CompareFunction.LessEqual,
    };

    /// <summary>マテリアルのブレンド成分（合成しないなら <c>null</c>）</summary>
    public static (BlendComponent Color, BlendComponent Alpha)? Resolve(MaterialBlend blend)
    {
        switch (blend)
        {
            case MaterialBlend.Invert:
                return (
                    new BlendComponent { SrcFactor = BlendFactor.OneMinusDst, DstFactor = BlendFactor.Zero, Operation = BlendOperation.Add },
                    new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.Zero, Operation = BlendOperation.Add });

            case MaterialBlend.DstAlphaMask:
                // 描画先のアルファ 0 を塗り替え、1 は保持する。
                var mask = new BlendComponent { SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.DstAlpha, Operation = BlendOperation.Add };
                return (mask, mask);

            case MaterialBlend.Alpha:
                return (
                    new BlendComponent { SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add },
                    new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add });

            default:
                return null;
        }
    }
}
