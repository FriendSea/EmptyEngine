using EmptyEngine.Graphics;

namespace EmptyEngine.WebGpu;

/// <summary>アセット単位で共有する GPU 資源のキャッシュ</summary>
/// <remarks>
/// テクスチャは借用者が 0 件になった時点で解放する。
/// パイプラインは生成コストが高く、一時的な Effect/Sprite の生成・破棄でキャッシュが
/// 空になると再生成が頻発するため、renderer の寿命中は保持し <see cref="Dispose"/> で解放する。
/// </remarks>
public sealed unsafe class GpuAssetCache : IDisposable
{
    private sealed class TextureEntry
    {
        public Texture* Texture;
        public TextureView* View;
        public int Count;
    }

    private sealed class PipelineEntry
    {
        public RenderPipeline* Pipeline;
        public PipelineLayout* Layout;
        public BindGroupLayout* BindGroupLayout;
        public int Borrowers;
    }

    private readonly Dictionary<TextureAsset, TextureEntry> _textures = new();
    private readonly Dictionary<(ShaderAsset Shader, int Variant, int LayoutKey), PipelineEntry> _pipelines = new();

    /// <summary>共有テクスチャの借用</summary>
    /// <returns>まだ誰も作っていなければ <c>false</c>（呼び出し側が作って <see cref="AddTexture"/> する）</returns>
    public bool TryAcquireTexture(TextureAsset asset, out TextureView* view)
    {
        if (_textures.TryGetValue(asset, out TextureEntry? entry))
        {
            entry.Count++;
            view = entry.View;
            return true;
        }

        view = null;
        return false;
    }

    /// <summary>作ったばかりのテクスチャの共有への登録（借用 1 件を含む）</summary>
    public void AddTexture(TextureAsset asset, Texture* texture, TextureView* view)
        => _textures[asset] = new TextureEntry { Texture = texture, View = view, Count = 1 };

    /// <summary>借りていたテクスチャの返却</summary>
    public void ReleaseTexture(TextureAsset asset)
    {
        if (!_textures.TryGetValue(asset, out TextureEntry? entry)) return;
        if (--entry.Count > 0) return;

        _textures.Remove(asset);
        Release(entry);
    }

    /// <summary>共有パイプライン一式の借用</summary>
    /// <param name="variant">同じシェーダから作られるパイプライン種別</param>
    /// <param name="layoutKey">uniform サイズなど、shader identity だけでは決まらない layout 差分</param>
    /// <remarks>返却後もキャッシュ自体が所有権を保持し、renderer の破棄まで GPU ハンドルを残す。</remarks>
    public bool TryAcquirePipeline(
        ShaderAsset shader,
        int variant,
        int layoutKey,
        out RenderPipeline* pipeline,
        out PipelineLayout* layout,
        out BindGroupLayout* bindGroupLayout)
    {
        if (_pipelines.TryGetValue((shader, variant, layoutKey), out PipelineEntry? entry))
        {
            entry.Borrowers++;
            pipeline = entry.Pipeline;
            layout = entry.Layout;
            bindGroupLayout = entry.BindGroupLayout;
            return true;
        }

        pipeline = null;
        layout = null;
        bindGroupLayout = null;
        return false;
    }

    /// <summary>作ったパイプライン一式のキャッシュ登録（借用 1 件を含む）</summary>
    public void AddPipeline(
        ShaderAsset shader,
        int variant,
        int layoutKey,
        RenderPipeline* pipeline,
        PipelineLayout* layout,
        BindGroupLayout* bindGroupLayout)
        => _pipelines[(shader, variant, layoutKey)] = new PipelineEntry
        {
            Pipeline = pipeline,
            Layout = layout,
            BindGroupLayout = bindGroupLayout,
            Borrowers = 1,
        };

    /// <summary>借用終了の通知。パイプライン本体は再利用のため cache に残す。</summary>
    public void ReleasePipeline(ShaderAsset shader, int variant, int layoutKey)
    {
        if (!_pipelines.TryGetValue((shader, variant, layoutKey), out PipelineEntry? entry)) return;
        if (entry.Borrowers > 0) entry.Borrowers--;
    }

    /// <summary>返し忘れも含めた全部の解放</summary>
    public void Dispose()
    {
        foreach (TextureEntry entry in _textures.Values) Release(entry);
        _textures.Clear();

        foreach (PipelineEntry entry in _pipelines.Values) Release(entry);
        _pipelines.Clear();
    }

    private static void Release(TextureEntry entry)
    {
        if (entry.View is not null) WGPU.wgpuTextureViewRelease(entry.View);
        if (entry.Texture is not null) WGPU.wgpuTextureRelease(entry.Texture);
        entry.View = null;
        entry.Texture = null;
    }

    private static void Release(PipelineEntry entry)
    {
        if (entry.Pipeline is not null) WGPU.wgpuRenderPipelineRelease(entry.Pipeline);
        if (entry.Layout is not null) WGPU.wgpuPipelineLayoutRelease(entry.Layout);
        if (entry.BindGroupLayout is not null) WGPU.wgpuBindGroupLayoutRelease(entry.BindGroupLayout);
        entry.Pipeline = null;
        entry.Layout = null;
        entry.BindGroupLayout = null;
    }
}

/// <summary>同じシェーダから作られるパイプラインの種別</summary>
internal static class GpuPipelineVariant
{
    /// <summary>共有 quad 頂点＋uniform/texture/sampler の 3 点 bind group</summary>
    public const int SpriteQuad = 0;

    /// <summary>SpriteComponent の custom shader pipeline</summary>
    public const int SpriteCustom = 1;

    /// <summary>EffectComponent の procedural quad pipeline</summary>
    public const int Effect = 2;
}
