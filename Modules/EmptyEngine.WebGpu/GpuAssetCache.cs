using EmptyEngine.Graphics;
using WgpuBuffer = EmptyEngine.WebGpu.Buffer;

namespace EmptyEngine.WebGpu;

/// <summary>共有する GPU 資源のキャッシュ</summary>
/// <remarks>
/// テクスチャと bind group は借用者が 0 件になった時点で解放する。
/// パイプラインとレイアウトは生成コストが高く、一時的な Effect/Sprite の生成・破棄でキャッシュが
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
    }

    private sealed class MaterialEntry
    {
        public BindGroup* BindGroup;
        public WgpuBuffer* ParamBuffer;
        public TextureAsset[] Textures = [];
        public int Count;
    }

    private sealed class MainTextureEntry
    {
        public BindGroup* BindGroup;
        public int Count;
    }

    private readonly Dictionary<TextureAsset, TextureEntry> _textures = new();
    private readonly Dictionary<MaterialPipelineKey, PipelineEntry> _pipelines = new();
    private readonly Dictionary<(int UniformSize, int ParamSize, bool History), nint> _instanceLayouts = new();
    private readonly Dictionary<ShaderAsset, nint> _materialLayouts = new();
    private readonly Dictionary<MaterialAsset, MaterialEntry> _materials = new();
    private readonly Dictionary<(TextureAsset? Texture, nint Sampler), MainTextureEntry> _mainTextures = new();

    /// <summary>共有テクスチャの借用（まだ誰も作っていなければ作る）</summary>
    public TextureView* AcquireTexture(in RenderContext context, TextureAsset asset)
    {
        if (_textures.TryGetValue(asset, out TextureEntry? entry))
        {
            entry.Count++;
            return entry.View;
        }

        Texture* texture = SpriteRenderSupport.CreateTexture(in context, asset);
        TextureView* view = WGPU.wgpuTextureCreateView(texture, null);
        _textures[asset] = new TextureEntry { Texture = texture, View = view, Count = 1 };
        return view;
    }

    /// <summary>借りていたテクスチャの返却</summary>
    public void ReleaseTexture(TextureAsset asset)
    {
        if (!_textures.TryGetValue(asset, out TextureEntry? entry)) return;
        if (--entry.Count > 0) return;

        _textures.Remove(asset);
        Release(entry);
    }

    /// <summary><c>group(0)</c>（コンポーネント uniform・Params・Effect の履歴）のレイアウト</summary>
    internal BindGroupLayout* GetInstanceLayout(in RenderContext context, int uniformSize, int paramSize, bool history)
    {
        if (_instanceLayouts.TryGetValue((uniformSize, paramSize, history), out nint cached))
            return (BindGroupLayout*)cached;

        var entries = stackalloc BindGroupLayoutEntry[3];
        entries[0] = new BindGroupLayoutEntry
        {
            Binding = MaterialRenderSupport.UniformBinding,
            Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, MinBindingSize = (ulong)uniformSize },
        };
        entries[1] = new BindGroupLayoutEntry
        {
            Binding = MaterialRenderSupport.ParamsBinding,
            Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, MinBindingSize = (ulong)paramSize },
        };
        entries[2] = new BindGroupLayoutEntry
        {
            Binding = MaterialRenderSupport.HistoryBinding,
            Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage, MinBindingSize = MaterialRenderSupport.HistoryBufferSize },
        };
        var descriptor = new BindGroupLayoutDescriptor { EntryCount = (nuint)(history ? 3 : 2), Entries = entries };
        BindGroupLayout* layout = WGPU.wgpuDeviceCreateBindGroupLayout(context.Device, &descriptor);
        _instanceLayouts[(uniformSize, paramSize, history)] = (nint)layout;
        return layout;
    }

    /// <summary><c>group(2)</c>（マテリアルの Params・サンプラー・追加テクスチャ）のレイアウト</summary>
    internal BindGroupLayout* GetMaterialLayout(in RenderContext context, ShaderAsset fragment)
    {
        if (_materialLayouts.TryGetValue(fragment, out nint cached))
            return (BindGroupLayout*)cached;

        ShaderTextureSlot[] slots = fragment.TextureSlots ?? [];
        var entries = stackalloc BindGroupLayoutEntry[2 + slots.Length];
        entries[0] = new BindGroupLayoutEntry
        {
            Binding = MaterialRenderSupport.MaterialParamsBinding,
            Visibility = ShaderStage.Fragment,
            Buffer = new BufferBindingLayout
            {
                Type = BufferBindingType.Uniform,
                MinBindingSize = (ulong)MaterialRenderSupport.ParamBufferSize(ShaderParamsHost.ValueCount(fragment.FragmentParams)),
            },
        };
        entries[1] = new BindGroupLayoutEntry
        {
            Binding = MaterialRenderSupport.MaterialSamplerBinding,
            Visibility = ShaderStage.Fragment,
            Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering },
        };
        for (int i = 0; i < slots.Length; i++)
        {
            entries[2 + i] = new BindGroupLayoutEntry
            {
                Binding = (uint)slots[i].Binding,
                Visibility = ShaderStage.Fragment,
                Texture = new TextureBindingLayout
                {
                    SampleType = TextureSampleType.Float,
                    ViewDimension = TextureViewDimension.Dimension2D,
                    Multisampled = 0,
                },
            };
        }

        var descriptor = new BindGroupLayoutDescriptor { EntryCount = (nuint)(2 + slots.Length), Entries = entries };
        BindGroupLayout* layout = WGPU.wgpuDeviceCreateBindGroupLayout(context.Device, &descriptor);
        _materialLayouts[fragment] = (nint)layout;
        return layout;
    }

    /// <summary>マテリアルの <c>group(2)</c> bind group の借用（同じマテリアルは 1 個を共有する）</summary>
    internal BindGroup* AcquireMaterial(in RenderContext context, MaterialAsset material, ShaderAsset fragment)
    {
        if (_materials.TryGetValue(material, out MaterialEntry? entry))
        {
            entry.Count++;
            return entry.BindGroup;
        }

        ShaderTextureSlot[] slots = fragment.TextureSlots ?? [];

        int paramSize = MaterialRenderSupport.ParamBufferSize(ShaderParamsHost.ValueCount(fragment.FragmentParams));
        var bufferDescriptor = new BufferDescriptor { Usage = BufferUsage.Uniform | BufferUsage.CopyDst, Size = (ulong)paramSize };
        WgpuBuffer* paramBuffer = WGPU.wgpuDeviceCreateBuffer(context.Device, &bufferDescriptor);

        float[] values = new float[paramSize / sizeof(float)];
        float[] source = material.Params ?? [];
        Array.Copy(source, values, Math.Min(source.Length, values.Length));
        fixed (float* p = values)
            WGPU.wgpuQueueWriteBuffer(context.Queue, paramBuffer, 0, p, (nuint)paramSize);

        var borrowed = new List<TextureAsset>();
        var entries = stackalloc BindGroupEntry[2 + slots.Length];
        entries[0] = new BindGroupEntry { Binding = MaterialRenderSupport.MaterialParamsBinding, Buffer = paramBuffer, Offset = 0, Size = (ulong)paramSize };
        entries[1] = new BindGroupEntry { Binding = MaterialRenderSupport.MaterialSamplerBinding, Sampler = context.DefaultSampler };
        IReadOnlyList<TextureAsset?> textures = material.ResolvedTextures;
        for (int i = 0; i < slots.Length; i++)
        {
            TextureView* view = context.DefaultTextureView;
            if (i < textures.Count && textures[i] is { } texture)
            {
                view = AcquireTexture(in context, texture);
                borrowed.Add(texture);
            }

            entries[2 + i] = new BindGroupEntry { Binding = (uint)slots[i].Binding, TextureView = view };
        }

        var descriptor = new BindGroupDescriptor
        {
            Layout = GetMaterialLayout(in context, fragment),
            EntryCount = (nuint)(2 + slots.Length),
            Entries = entries,
        };
        BindGroup* bindGroup = WGPU.wgpuDeviceCreateBindGroup(context.Device, &descriptor);
        _materials[material] = new MaterialEntry { BindGroup = bindGroup, ParamBuffer = paramBuffer, Textures = borrowed.ToArray(), Count = 1 };
        return bindGroup;
    }

    /// <summary>借りていたマテリアルの bind group の返却</summary>
    internal void ReleaseMaterial(MaterialAsset material)
    {
        if (!_materials.TryGetValue(material, out MaterialEntry? entry)) return;
        if (--entry.Count > 0) return;

        _materials.Remove(material);
        Release(entry);
        foreach (TextureAsset texture in entry.Textures) ReleaseTexture(texture);
    }

    /// <summary>メインテクスチャの <c>group(3)</c> bind group の借用（テクスチャとサンプラーの組ごとに 1 個）</summary>
    /// <param name="texture"><c>null</c> なら 1x1 の白</param>
    internal BindGroup* AcquireMainTexture(in RenderContext context, TextureAsset? texture, Sampler* sampler)
    {
        if (_mainTextures.TryGetValue((texture, (nint)sampler), out MainTextureEntry? entry))
        {
            entry.Count++;
            return entry.BindGroup;
        }

        TextureView* view = texture is null ? context.DefaultTextureView : AcquireTexture(in context, texture);
        var entries = stackalloc BindGroupEntry[2];
        entries[0] = new BindGroupEntry { Binding = MaterialRenderSupport.MainTextureBinding, TextureView = view };
        entries[1] = new BindGroupEntry { Binding = MaterialRenderSupport.MainSamplerBinding, Sampler = sampler };
        var descriptor = new BindGroupDescriptor { Layout = context.MainTextureLayout, EntryCount = 2, Entries = entries };
        BindGroup* bindGroup = WGPU.wgpuDeviceCreateBindGroup(context.Device, &descriptor);
        _mainTextures[(texture, (nint)sampler)] = new MainTextureEntry { BindGroup = bindGroup, Count = 1 };
        return bindGroup;
    }

    /// <summary>借りていたメインテクスチャの bind group の返却</summary>
    internal void ReleaseMainTexture(TextureAsset? texture, Sampler* sampler)
    {
        if (!_mainTextures.TryGetValue((texture, (nint)sampler), out MainTextureEntry? entry)) return;
        if (--entry.Count > 0) return;

        _mainTextures.Remove((texture, (nint)sampler));
        if (entry.BindGroup is not null) WGPU.wgpuBindGroupRelease(entry.BindGroup);
        if (texture is not null) ReleaseTexture(texture);
    }

    /// <summary>共有パイプラインの取得（無ければ作る）</summary>
    /// <remarks>キャッシュ自体が所有権を保持し、renderer の破棄まで GPU ハンドルを残す。</remarks>
    internal RenderPipeline* GetPipeline(in RenderContext context, in MaterialPipelineKey key)
    {
        if (_pipelines.TryGetValue(key, out PipelineEntry? entry)) return entry.Pipeline;

        BindGroupLayout** layouts = stackalloc BindGroupLayout*[4];
        layouts[0] = GetInstanceLayout(in context, key.UniformSize, key.ParamSize, key.History);
        layouts[1] = context.GlobalsLayout;
        layouts[2] = GetMaterialLayout(in context, key.Fragment);
        layouts[3] = context.MainTextureLayout;
        var layoutDescriptor = new PipelineLayoutDescriptor { BindGroupLayoutCount = 4, BindGroupLayouts = layouts };
        PipelineLayout* layout = WGPU.wgpuDeviceCreatePipelineLayout(context.Device, &layoutDescriptor);

        RenderPipeline* pipeline = MaterialRenderSupport.CreatePipeline(in context, in key, layout);
        _pipelines[key] = new PipelineEntry { Pipeline = pipeline, Layout = layout };
        return pipeline;
    }

    /// <summary>返し忘れも含めた全部の解放</summary>
    public void Dispose()
    {
        foreach (MainTextureEntry entry in _mainTextures.Values)
            if (entry.BindGroup is not null) WGPU.wgpuBindGroupRelease(entry.BindGroup);
        _mainTextures.Clear();

        foreach (MaterialEntry entry in _materials.Values) Release(entry);
        _materials.Clear();

        foreach (TextureEntry entry in _textures.Values) Release(entry);
        _textures.Clear();

        foreach (PipelineEntry entry in _pipelines.Values)
        {
            if (entry.Pipeline is not null) WGPU.wgpuRenderPipelineRelease(entry.Pipeline);
            if (entry.Layout is not null) WGPU.wgpuPipelineLayoutRelease(entry.Layout);
        }
        _pipelines.Clear();

        foreach (nint layout in _instanceLayouts.Values) WGPU.wgpuBindGroupLayoutRelease((BindGroupLayout*)layout);
        _instanceLayouts.Clear();
        foreach (nint layout in _materialLayouts.Values) WGPU.wgpuBindGroupLayoutRelease((BindGroupLayout*)layout);
        _materialLayouts.Clear();
    }

    private static void Release(TextureEntry entry)
    {
        if (entry.View is not null) WGPU.wgpuTextureViewRelease(entry.View);
        if (entry.Texture is not null) WGPU.wgpuTextureRelease(entry.Texture);
        entry.View = null;
        entry.Texture = null;
    }

    private static void Release(MaterialEntry entry)
    {
        if (entry.BindGroup is not null) WGPU.wgpuBindGroupRelease(entry.BindGroup);
        if (entry.ParamBuffer is not null) WGPU.wgpuBufferRelease(entry.ParamBuffer);
        entry.BindGroup = null;
        entry.ParamBuffer = null;
    }
}

/// <summary>パイプラインを 1 つに決める条件</summary>
/// <remarks>マテリアルそのものは含めない。同じシェーダと描画状態のマテリアルは、パイプラインを共有する。</remarks>
internal readonly record struct MaterialPipelineKey(
    ShaderAsset Vertex,
    ShaderAsset Fragment,
    MaterialBlend Blend,
    bool DepthWrite,
    MaterialDepthCompare DepthCompare,
    MaterialVertexKind Kind,
    int UniformSize,
    int ParamSize,
    bool History);
