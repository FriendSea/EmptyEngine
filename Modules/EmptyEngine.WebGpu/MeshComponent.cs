using System.Numerics;
using System.Runtime.InteropServices;
using EmptyEngine.Core;
using EmptyEngine.Generators;
using EmptyEngine.Graphics;
using EmptyEngine.Mesh;
using EmptyEngine.ObjectModel;
using WgpuBuffer = EmptyEngine.WebGpu.Buffer;
using GraphicsColor = EmptyEngine.Graphics.Color;

namespace EmptyEngine.WebGpu;

/// <summary>3D メッシュを 1 つ描くコンポーネント</summary>
public sealed unsafe partial class MeshComponent : ISpriteRenderer, IShaderParamsHost
{
    private readonly RenderWorld _renderWorld;
    private const int UniformSize = 160;

    private const int MeshRenderLayer = -100;

    private IObject? _owner;

    /// <summary>描画するメッシュの解決済み実体</summary>
    [ResolveAsset("Asset")]
    private MeshAsset? _resolvedMesh;

    /// <summary>描画に使う WGSL シェーダの解決済み実体</summary>
    [ResolveAsset("Shader")]
    private ShaderAsset? _resolvedShader;

    private MeshAsset? _realizedMesh;
    private ShaderAsset? _realizedShader;
    private TextureAsset?[] _resolvedSlotTextures = [];
    private TextureAsset?[] _realizedSlotTextures = [];
    private bool _resourcesBuilt;
    private GpuResourcePool? _pool;
    private WgpuBuffer* _vertexBuffer;
    private WgpuBuffer* _indexBuffer;
    private WgpuBuffer* _uniformBuffer;
    private WgpuBuffer* _paramBuffer;
    private RenderPipeline* _pipeline;
    private PipelineLayout* _pipelineLayout;
    private BindGroupLayout* _bindGroupLayout;
    private Texture*[] _slotTextures = [];
    private TextureView*[] _slotTextureViews = [];
    private BindGroup* _bindGroup;
    private ShaderDepthState _shaderDepth = ShaderDepthState.Opaque;
    private int _renderQueue = ShaderRenderQueue.Geometry;
    private readonly ShaderGlobalsBinding _globals = new();

    public MeshComponent(RenderWorld renderWorld) => _renderWorld = renderWorld;

    /// <summary>メッシュ全体へ乗算するカラー（RGBA 各成分 0..1）</summary>
    public GraphicsColor Tint { get; set; } = GraphicsColor.White;

    /// <summary>シェーダのユーザ定義パラメータの値</summary>
    public float[] Params { get; set; } = [];

    /// <summary>シェーダが宣言したテクスチャスロットへ貼るテクスチャ参照</summary>
    public AssetReference<TextureAsset>[] Textures { get; set; } = [];

    /// <summary>現在解決済みのシェーダ</summary>
    public ShaderAsset? ResolvedShader => _resolvedShader;

    /// <inheritdoc/>
    public IObject? Owner => _owner;

    private int ParamBufferSize => Math.Max(16, (((Params?.Length ?? 0) * sizeof(float)) + 15) & ~15);

    /// <inheritdoc/>
    public int RenderLayer => SpriteRenderSupport.FindCanvas(_owner) is { } canvas
        ? SpriteRenderSupport.CanvasRenderLayer + canvas.SortOrder
        : MeshRenderLayer;

    /// <inheritdoc/>
    public bool IsTransparent => _renderQueue >= ShaderRenderQueue.Transparent;

    /// <inheritdoc/>
    public int RenderQueue => _renderQueue;

    public void OnCreated(IObject owner)
    {
    }

    public void OnDeserialized(IObject owner)
    {
        _owner = owner;
        _renderWorld.RegisterRenderer(this);

        if (Tint == default) Tint = GraphicsColor.White;
        Textures ??= [];
        _slotTextures ??= [];
        _slotTextureViews ??= [];
        if (_resolvedShader is not null)
        {
            bool hasBlend = ShaderBlend.TryResolveDirective(_resolvedShader, out _, out _);
            ShaderDepthState fallback = hasBlend
                ? ShaderDepthState.Transparent
                : ShaderDepthState.Opaque;
            _shaderDepth = ShaderBlend.ResolveDepth(_resolvedShader, fallback);
            _renderQueue = ShaderBlend.ResolveRenderQueue(
                _resolvedShader,
                hasBlend ? ShaderRenderQueue.Transparent : ShaderRenderQueue.Geometry);
        }
        else
        {
            _shaderDepth = ShaderDepthState.Opaque;
            _renderQueue = ShaderRenderQueue.Geometry;
        }

        if (_resolvedShader is not null) Params = ShaderParamsHost.Reconcile(_resolvedShader.Params, Params);
    }

    public void OnDestroy(IObject owner)
    {
        _renderWorld.UnregisterRenderer(this);
        ReleaseResources();
        _owner = null;
        _resolvedMesh = null;
        _resolvedShader = null;
        _resolvedSlotTextures = [];
        _realizedSlotTextures = [];
        _shaderDepth = ShaderDepthState.Opaque;
        _renderQueue = ShaderRenderQueue.Geometry;
    }

    public void Render(in RenderContext context)
    {
        EnsureResources(in context);
        MeshAsset? mesh = _realizedMesh;
        if (mesh is null || _vertexBuffer is null)
        {
            return;
        }

        Matrix4x4 world = SpriteRenderSupport.ReadTransform(_owner);
        Matrix4x4 mvp = world * context.Projection;
        Matrix4x4 normalMatrix = Matrix4x4.Invert(world, out Matrix4x4 inverse)
            ? Matrix4x4.Transpose(inverse)
            : world;

        Vector3 lightDirection = Vector3.Normalize(new Vector3(0.4f, -0.8f, -0.45f));
        const float Ambient = 0.35f;

        Span<float> uniform = stackalloc float[UniformSize / sizeof(float)];
        MemoryMarshal.Write(MemoryMarshal.AsBytes(uniform), in mvp);
        MemoryMarshal.Write(MemoryMarshal.AsBytes(uniform)[64..], in normalMatrix);
        uniform[32] = Tint.R;
        uniform[33] = Tint.G;
        uniform[34] = Tint.B;
        uniform[35] = Tint.A;
        uniform[36] = lightDirection.X;
        uniform[37] = lightDirection.Y;
        uniform[38] = lightDirection.Z;
        uniform[39] = Ambient;
        fixed (float* u = uniform)
        {
            WGPU.wgpuQueueWriteBuffer(context.Queue, _uniformBuffer, 0, u, UniformSize);
        }

        if (_paramBuffer is not null && Params is { Length: > 0 })
        {
            fixed (float* p = Params)
                WGPU.wgpuQueueWriteBuffer(context.Queue, _paramBuffer, 0, p, (nuint)(Params.Length * sizeof(float)));
        }

        if (_bindGroup is null) return;

        WGPU.wgpuRenderPassEncoderSetPipeline(context.Pass, _pipeline);
        WGPU.wgpuRenderPassEncoderSetVertexBuffer(context.Pass, 0, _vertexBuffer, 0, (ulong)mesh.Vertices.Length);
        WGPU.wgpuRenderPassEncoderSetIndexBuffer(context.Pass, _indexBuffer, IndexFormat.Uint32, 0, (ulong)mesh.Indices.Length);
        WGPU.wgpuRenderPassEncoderSetBindGroup(context.Pass, 0, _bindGroup, 0, null);
        _globals.Bind(in context);
        foreach (MeshSubset subset in mesh.Subsets)
        {
            WGPU.wgpuRenderPassEncoderDrawIndexed(context.Pass, (uint)subset.IndexCount, 1, (uint)subset.IndexOffset, 0, 0);
        }
    }

    private void EnsureResources(in RenderContext context)
    {
        TextureAsset?[] resolvedSlots = _resolvedSlotTextures ?? [];
        if (_resourcesBuilt
            && ReferenceEquals(_realizedMesh, _resolvedMesh)
            && ReferenceEquals(_realizedShader, _resolvedShader)
            && SlotTexturesMatch(_realizedSlotTextures, resolvedSlots)) return;

        if (_resourcesBuilt) ReleaseResources();

        _pool = context.Resources;

        MeshAsset? mesh = _resolvedMesh;
        ShaderAsset? shader = _resolvedShader;
        if (shader is null || mesh is null || mesh.Vertices is null || mesh.Indices is null || mesh.IndexCount == 0)
        {
            _realizedMesh = null;
            _realizedShader = _resolvedShader;
            _realizedSlotTextures = resolvedSlots;
            _resourcesBuilt = true;
            return;
        }

        ShaderTextureSlot[] slots = shader.TextureSlots ?? [];

        string wgsl = SpriteRenderSupport.ReadShaderSource(shader);
        _bindGroupLayout = MeshRenderSupport.CreateShaderParamLayout(
            in context, UniformSize, (ulong)ParamBufferSize, slots);
        context.Resources.Track(this, () => { if (_bindGroupLayout is not null) { WGPU.wgpuBindGroupLayoutRelease(_bindGroupLayout); _bindGroupLayout = null; } });
        (BlendComponent Color, BlendComponent Alpha)? blend =
            ShaderBlend.TryResolveDirective(shader, out BlendComponent blendColor, out BlendComponent blendAlpha)
                ? (blendColor, blendAlpha)
                : null;
        _shaderDepth = ShaderBlend.ResolveDepth(
            shader,
            blend is null ? ShaderDepthState.Opaque : ShaderDepthState.Transparent);
        _renderQueue = ShaderBlend.ResolveRenderQueue(
            shader,
            blend is not null ? ShaderRenderQueue.Transparent : ShaderRenderQueue.Geometry);
        _globals.Build(in context, shader, this);
        _pipeline = MeshRenderSupport.CreateMeshPipeline(
            in context, wgsl, _bindGroupLayout, blend,
            _shaderDepth.WriteEnabled, _shaderDepth.Compare, out _pipelineLayout,
            globalsLayout: _globals.Layout);
        context.Resources.Track(this, () => { if (_pipeline is not null) { WGPU.wgpuRenderPipelineRelease(_pipeline); _pipeline = null; } });
        context.Resources.Track(this, () => { if (_pipelineLayout is not null) { WGPU.wgpuPipelineLayoutRelease(_pipelineLayout); _pipelineLayout = null; } });

        var paramDescriptor = new BufferDescriptor { Usage = BufferUsage.Uniform | BufferUsage.CopyDst, Size = (ulong)ParamBufferSize };
        _paramBuffer = WGPU.wgpuDeviceCreateBuffer(context.Device, &paramDescriptor);
        context.Resources.Track(this, () => { if (_paramBuffer is not null) { WGPU.wgpuBufferRelease(_paramBuffer); _paramBuffer = null; } });

        _vertexBuffer = MeshRenderSupport.CreateBufferFromBinary(in context, mesh.Vertices, BufferUsage.Vertex);
        context.Resources.Track(this, () => { if (_vertexBuffer is not null) { WGPU.wgpuBufferRelease(_vertexBuffer); _vertexBuffer = null; } });
        _indexBuffer = MeshRenderSupport.CreateBufferFromBinary(in context, mesh.Indices, BufferUsage.Index);
        context.Resources.Track(this, () => { if (_indexBuffer is not null) { WGPU.wgpuBufferRelease(_indexBuffer); _indexBuffer = null; } });

        var uniformDescriptor = new BufferDescriptor
        {
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
            Size = UniformSize,
        };
        _uniformBuffer = WGPU.wgpuDeviceCreateBuffer(context.Device, &uniformDescriptor);
        context.Resources.Track(this, () => { if (_uniformBuffer is not null) { WGPU.wgpuBufferRelease(_uniformBuffer); _uniformBuffer = null; } });

        _slotTextures = new Texture*[slots.Length];
        _slotTextureViews = new TextureView*[slots.Length];
        Span<int> slotBindings = slots.Length > 0 ? new int[slots.Length] : default;
        Span<nint> slotViews = slots.Length > 0 ? new nint[slots.Length] : default;
        for (int i = 0; i < slots.Length; i++)
        {
            TextureView* slotView = context.DefaultTextureView;
            if (i < resolvedSlots.Length && resolvedSlots[i] is { } slotTexture)
            {
                _slotTextures[i] = SpriteRenderSupport.CreateTexture(in context, slotTexture);
                _slotTextureViews[i] = WGPU.wgpuTextureCreateView(_slotTextures[i], null);
                slotView = _slotTextureViews[i];
            }

            slotBindings[i] = slots[i].Binding;
            slotViews[i] = (nint)slotView;
        }
        context.Resources.Track(this, () =>
        {
            foreach (TextureView* view in _slotTextureViews) if (view is not null) WGPU.wgpuTextureViewRelease(view);
            foreach (Texture* texture in _slotTextures) if (texture is not null) WGPU.wgpuTextureRelease(texture);
            _slotTextureViews = [];
            _slotTextures = [];
        });

        _bindGroup = MeshRenderSupport.CreateShaderParamBindGroup(
            in context, _bindGroupLayout, _uniformBuffer, UniformSize, context.RepeatSampler,
            _paramBuffer, (ulong)ParamBufferSize, slotBindings, slotViews);
        context.Resources.Track(this, () => { if (_bindGroup is not null) { WGPU.wgpuBindGroupRelease(_bindGroup); _bindGroup = null; } });

        _realizedMesh = mesh;
        _realizedShader = _resolvedShader;
        _realizedSlotTextures = resolvedSlots;
        _resourcesBuilt = true;
    }

    private static bool SlotTexturesMatch(TextureAsset?[]? realized, TextureAsset?[] resolved)
    {
        if (realized is null || realized.Length != resolved.Length) return false;
        for (int i = 0; i < resolved.Length; i++)
        {
            if (!ReferenceEquals(realized[i], resolved[i])) return false;
        }

        return true;
    }

    private void ReleaseResources()
    {
        _pool?.ReleaseAll(this);
        _globals.Release();
        _realizedMesh = null;
        _realizedShader = null;
        _realizedSlotTextures = [];
        _resourcesBuilt = false;
    }
}

public sealed partial class MeshComponent
{
    /// <summary>フィールド値が入るたびの参照の解決（シェーダの枠に合わせた <see cref="Textures"/> の枠まで）</summary>
    public async ValueTask OnResolveAssetsAsync(IAssetResolver resolver)
    {
        await ResolveAssetFieldsAsync(resolver);
        _resolvedShader ??= await resolver.ResolveAsync(new AssetReference<ShaderAsset>(BuiltinShaders.Mesh));
        Textures ??= [];
        if (_resolvedShader is not null)
            Textures = ShaderParamsHost.ReconcileTextures(_resolvedShader.TextureSlots, Textures);
        _resolvedSlotTextures = await ShaderParamsHost.ResolveTexturesAsync(Textures, resolver);
    }
}
