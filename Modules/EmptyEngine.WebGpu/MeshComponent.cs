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
    private const int UniformSize = 144;

    private const int MeshRenderLayer = -100;

    private IObject? _owner;

    /// <summary>描画するメッシュの解決済み実体</summary>
    [ResolveAsset("Asset")]
    private MeshAsset? _resolvedMesh;

    /// <summary>頂点シェーダに使う WGSL シェーダの解決済み実体</summary>
    [ResolveAsset("Shader")]
    private ShaderAsset? _resolvedShader;

    /// <summary>描画に使うマテリアルの解決済み実体</summary>
    [ResolveAsset("Material")]
    private MaterialAsset? _resolvedMaterial;

    /// <summary>メインテクスチャの解決済み実体（空なら白）</summary>
    [ResolveAsset("MainTexture")]
    private TextureAsset? _resolvedMainTexture;

    private ShaderAsset? _builtinShader;
    private ShaderAsset? _vertexShader;
    private MeshAsset? _realizedMesh;
    private GpuResourcePool? _pool;
    private WgpuBuffer* _vertexBuffer;
    private WgpuBuffer* _indexBuffer;
    private readonly MaterialBinding _binding = new();

    public MeshComponent(RenderWorld renderWorld) => _renderWorld = renderWorld;

    /// <summary>メッシュ全体へ乗算するカラー（RGBA 各成分 0..1）</summary>
    public GraphicsColor Tint { get; set; } = GraphicsColor.White;

    /// <summary>頂点シェーダが宣言するパラメータの値</summary>
    public float[] Params { get; set; } = [];

    /// <inheritdoc/>
    public IObject? Owner => _owner;

    /// <inheritdoc/>
    public int RenderLayer => SpriteRenderSupport.FindCanvas(_owner) is { } canvas
        ? SpriteRenderSupport.CanvasRenderLayer + canvas.SortOrder
        : MeshRenderLayer;

    /// <inheritdoc/>
    public bool IsTransparent => RenderQueue >= ShaderRenderQueue.Transparent;

    /// <inheritdoc/>
    public int RenderQueue => _resolvedMaterial?.Queue ?? ShaderRenderQueue.Geometry;

    public void OnCreated(IObject owner)
    {
    }

    public void OnDeserialized(IObject owner)
    {
        _owner = owner;
        _renderWorld.RegisterRenderer(this);

        if (Tint == default) Tint = GraphicsColor.White;
        ApplyShaders();
    }

    /// <summary>頂点シェーダの決定と、それに合わせた <see cref="Params"/> の枠の更新</summary>
    private void ApplyShaders()
    {
        _vertexShader = MaterialRenderSupport.SelectVertexShader(
            MaterialVertexKind.Mesh, _resolvedShader, _resolvedMaterial?.ResolvedShader, _builtinShader);
        Params = ShaderParamsHost.Reconcile(_vertexShader?.VertexParams, Params);
    }

    public void OnDestroy(IObject owner)
    {
        _renderWorld.UnregisterRenderer(this);
        ReleaseMesh();
        _binding.Release();
        _owner = null;
        _resolvedMesh = null;
        _resolvedShader = null;
        _resolvedMaterial = null;
        _resolvedMainTexture = null;
        _vertexShader = null;
    }

    public void Render(in RenderContext context)
    {
        EnsureMesh(in context);
        MeshAsset? mesh = _realizedMesh;
        if (mesh is null || _vertexBuffer is null) return;

        if (!_binding.Matches(_vertexShader, _resolvedMaterial, _resolvedMainTexture, Params?.Length ?? 0))
        {
            _binding.Build(
                in context, MaterialVertexKind.Mesh, UniformSize, _vertexShader, _resolvedMaterial,
                _resolvedMainTexture, context.RepeatSampler, Params?.Length ?? 0);
        }

        if (!_binding.IsReady) return;

        Matrix4x4 world = SpriteRenderSupport.ReadTransform(_owner);
        Matrix4x4 mvp = world * context.Projection;
        Matrix4x4 normalMatrix = Matrix4x4.Invert(world, out Matrix4x4 inverse)
            ? Matrix4x4.Transpose(inverse)
            : world;

        Span<float> uniform = stackalloc float[UniformSize / sizeof(float)];
        MemoryMarshal.Write(MemoryMarshal.AsBytes(uniform), in mvp);
        MemoryMarshal.Write(MemoryMarshal.AsBytes(uniform)[64..], in normalMatrix);
        uniform[32] = Tint.R;
        uniform[33] = Tint.G;
        uniform[34] = Tint.B;
        uniform[35] = Tint.A;
        fixed (float* u = uniform)
        {
            WGPU.wgpuQueueWriteBuffer(context.Queue, _binding.UniformBuffer, 0, u, UniformSize);
        }

        _binding.WriteParams(in context, Params);
        _binding.Bind(in context);
        WGPU.wgpuRenderPassEncoderSetVertexBuffer(context.Pass, 0, _vertexBuffer, 0, (ulong)mesh.Vertices.Length);
        WGPU.wgpuRenderPassEncoderSetIndexBuffer(context.Pass, _indexBuffer, IndexFormat.Uint32, 0, (ulong)mesh.Indices.Length);
        foreach (MeshSubset subset in mesh.Subsets)
        {
            WGPU.wgpuRenderPassEncoderDrawIndexed(context.Pass, (uint)subset.IndexCount, 1, (uint)subset.IndexOffset, 0, 0);
        }
    }

    /// <summary>メッシュの頂点・インデックスバッファの用意（メッシュが差し替わったら作り直す）</summary>
    private void EnsureMesh(in RenderContext context)
    {
        MeshAsset? mesh = _resolvedMesh;
        if (mesh is null || mesh.Vertices is null || mesh.Indices is null || mesh.IndexCount == 0) mesh = null;
        if (ReferenceEquals(_realizedMesh, mesh)) return;

        ReleaseMesh();
        if (mesh is null) return;

        _pool = context.Resources;
        _vertexBuffer = MeshRenderSupport.CreateBufferFromBinary(in context, mesh.Vertices, BufferUsage.Vertex);
        context.Resources.Track(this, () => { if (_vertexBuffer is not null) { WGPU.wgpuBufferRelease(_vertexBuffer); _vertexBuffer = null; } });
        _indexBuffer = MeshRenderSupport.CreateBufferFromBinary(in context, mesh.Indices, BufferUsage.Index);
        context.Resources.Track(this, () => { if (_indexBuffer is not null) { WGPU.wgpuBufferRelease(_indexBuffer); _indexBuffer = null; } });
        _realizedMesh = mesh;
    }

    private void ReleaseMesh()
    {
        _pool?.ReleaseAll(this);
        _realizedMesh = null;
    }
}

public sealed partial class MeshComponent
{
    /// <summary>フィールド値が入るたびの参照の解決（空のマテリアルは同梱の既定へ）</summary>
    public async ValueTask OnResolveAssetsAsync(IAssetResolver resolver)
    {
        await ResolveAssetFieldsAsync(resolver);
        _resolvedMaterial ??= await resolver.ResolveAsync(new AssetReference<MaterialAsset>(BuiltinMaterials.Mesh));
        _builtinShader = await resolver.ResolveAsync(new AssetReference<ShaderAsset>(BuiltinShaders.Mesh));
        ApplyShaders();
    }
}
