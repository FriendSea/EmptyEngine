namespace EmptyEngine.WebGpu;

internal enum TextureCompressionFamily
{
    None,
    Bc,
    Astc,
    Etc2,
}

/// <summary>Texture compression features selected from the active WebGPU adapter.</summary>
internal static unsafe class GpuCapabilities
{
    public static bool SupportsBc7 { get; private set; }
    public static bool SupportsAstc { get; private set; }
    public static bool SupportsEtc2 { get; private set; }

    public static TextureCompressionFamily PreferredTextureCompression { get; private set; }

    /// <summary>Selects one device feature and returns whether it must be requested.</summary>
    public static bool Configure(Adapter* adapter, out FeatureName requestedFeature)
    {
        SupportsBc7 = WGPU.wgpuAdapterHasFeature(adapter, FeatureName.TextureCompressionBC) != 0;
        SupportsAstc = WGPU.wgpuAdapterHasFeature(adapter, FeatureName.TextureCompressionASTC) != 0;
        SupportsEtc2 = WGPU.wgpuAdapterHasFeature(adapter, FeatureName.TextureCompressionETC2) != 0;

        if (SupportsBc7)
        {
            PreferredTextureCompression = TextureCompressionFamily.Bc;
            requestedFeature = FeatureName.TextureCompressionBC;
            return true;
        }
        if (SupportsAstc)
        {
            PreferredTextureCompression = TextureCompressionFamily.Astc;
            requestedFeature = FeatureName.TextureCompressionASTC;
            return true;
        }
        if (SupportsEtc2)
        {
            PreferredTextureCompression = TextureCompressionFamily.Etc2;
            requestedFeature = FeatureName.TextureCompressionETC2;
            return true;
        }

        PreferredTextureCompression = TextureCompressionFamily.None;
        requestedFeature = FeatureName.Undefined;
        return false;
    }
}
