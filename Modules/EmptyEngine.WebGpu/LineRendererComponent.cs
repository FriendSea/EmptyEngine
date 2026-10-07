using System.Numerics;
using System.Runtime.InteropServices;
using EmptyEngine.Core;
using EmptyEngine.Generators;
using EmptyEngine.Graphics;
using EmptyEngine.ObjectModel;
using WgpuBuffer = EmptyEngine.WebGpu.Buffer;
using GraphicsColor = EmptyEngine.Graphics.Color;

namespace EmptyEngine.WebGpu;

/// <summary>折れ線（ポリライン）を 1 本描くコンポーネント</summary>
public sealed unsafe partial class LineRendererComponent : ISpriteRenderer, IShaderParamsHost
{
    private readonly RenderWorld _renderWorld;
    private const int UniformSize = 160;

    private const int VertexFloats = 5;

    private const float MiterLimit = 4f;

    private IObject? _owner;

    /// <summary>頂点シェーダに使う WGSL シェーダの解決済み実体</summary>
    [ResolveAsset("Shader")]
    private ShaderAsset? _resolvedShader;

    /// <summary>描画に使うマテリアルの解決済み実体</summary>
    [ResolveAsset("Material")]
    private MaterialAsset? _resolvedMaterial;

    /// <summary>メインテクスチャの解決済み実体（空なら白）</summary>
    [ResolveAsset("MainTexture")]
    private TextureAsset? _resolvedMainTexture;

    /// <summary>描画に使うメインテクスチャ（自分の指定が無ければマテリアルのもの）</summary>
    private TextureAsset? EffectiveMainTexture => _resolvedMainTexture ?? _resolvedMaterial?.ResolvedMainTexture;

    private ShaderAsset? _builtinShader;
    private ShaderAsset? _vertexShader;
    private GpuResourcePool? _pool;
    private WgpuBuffer* _vertexBuffer;
    private int _vertexCapacity;
    private float[] _scratch = [];
    private readonly MaterialBinding _binding = new();

    public LineRendererComponent(RenderWorld renderWorld) => _renderWorld = renderWorld;

    /// <summary>結ぶ点列（ローカル座標）</summary>
    public Vector3[] Points { get; set; } = [];

    /// <summary>線の幅（world 単位）</summary>
    public float Width { get; set; } = 0.1f;

    /// <summary>両端を接線方向へ余分に延長する長さ（world 単位）</summary>
    public float CapExtension { get; set; }

    /// <summary>線のカラー（RGBA 各成分 0..1）</summary>
    public GraphicsColor Color { get; set; } = GraphicsColor.White;

    /// <summary>頂点シェーダが宣言するパラメータの値</summary>
    public float[] Params { get; set; } = [];

    /// <inheritdoc/>
    public IObject? Owner => _owner;

    /// <inheritdoc/>
    public bool IsTransparent => RenderQueue >= ShaderRenderQueue.Transparent;

    /// <inheritdoc/>
    public int RenderQueue => _resolvedMaterial?.Queue ?? ShaderRenderQueue.Transparent;

    /// <inheritdoc/>
    public Vector3 SortPosition
    {
        get
        {
            Vector3[] points = Points ?? [];
            if (points.Length == 0)
            {
                return SpriteRenderSupport.ReadTransform(_owner).Translation;
            }

            Vector3 min = points[0];
            Vector3 max = points[0];
            for (int i = 1; i < points.Length; i++)
            {
                min = Vector3.Min(min, points[i]);
                max = Vector3.Max(max, points[i]);
            }

            return Vector3.Transform((min + max) * 0.5f, SpriteRenderSupport.ReadTransform(_owner));
        }
    }

    public void OnCreated(IObject owner)
    {
    }

    public void OnDeserialized(IObject owner)
    {
        _owner = owner;
        _renderWorld.RegisterRenderer(this);

        Points ??= [];
        if (Width == 0f) Width = 0.1f;
        if (Color == default) Color = GraphicsColor.White;
        _scratch ??= [];
        ApplyShaders();
    }

    /// <summary>頂点シェーダの決定と、それに合わせた <see cref="Params"/> の枠の更新</summary>
    private void ApplyShaders()
    {
        _vertexShader = VertexShaderSelection.Select(
            MaterialVertexKind.Line, _resolvedShader, _resolvedMaterial?.ResolvedShader, _builtinShader);
        Params = ShaderParamsHost.Reconcile(_vertexShader?.VertexParams, Params);
    }

    public void OnDestroy(IObject owner)
    {
        _renderWorld.UnregisterRenderer(this);
        _pool?.ReleaseAll(this);
        _vertexCapacity = 0;
        _binding.Release();
        _owner = null;
        _resolvedShader = null;
        _resolvedMaterial = null;
        _resolvedMainTexture = null;
        _vertexShader = null;
    }

    public void Render(in RenderContext context)
    {
        Vector3[] points = Points ?? [];
        if (points.Length < 2 || Width <= 0f) return;

        if (!_binding.Matches(_vertexShader, _resolvedMaterial, EffectiveMainTexture, Params?.Length ?? 0))
        {
            _binding.Build(
                in context, MaterialVertexKind.Line, UniformSize, _vertexShader, _resolvedMaterial,
                EffectiveMainTexture, context.DefaultSampler, Params?.Length ?? 0);
        }

        if (!_binding.IsReady) return;

        int vertexCount = BuildStrip(points, Width * 0.5f, MathF.Max(0f, CapExtension), out float totalLength);
        if (vertexCount < 3) return;
        EnsureVertexCapacity(in context, vertexCount);
        fixed (float* v = _scratch)
        {
            WGPU.wgpuQueueWriteBuffer(context.Queue, _vertexBuffer, 0, v, (nuint)(vertexCount * VertexFloats * sizeof(float)));
        }

        Matrix4x4 modelView = SpriteRenderSupport.ReadTransform(_owner) * context.ViewMatrix;
        Matrix4x4 projection = context.ProjectionMatrix;
        Span<float> uniform = stackalloc float[UniformSize / sizeof(float)];
        Span<byte> uniformBytes = MemoryMarshal.AsBytes(uniform);
        MemoryMarshal.Write(uniformBytes, in modelView);
        MemoryMarshal.Write(uniformBytes[64..], in projection);
        uniform[32] = Color.R;
        uniform[33] = Color.G;
        uniform[34] = Color.B;
        uniform[35] = Color.A;
        uniform[36] = 0f;
        uniform[37] = totalLength;
        uniform[38] = 0f;
        uniform[39] = 0f;
        fixed (float* u = uniform)
        {
            WGPU.wgpuQueueWriteBuffer(context.Queue, _binding.UniformBuffer, 0, u, UniformSize);
        }

        _binding.WriteParams(in context, Params);
        _binding.Bind(in context);
        WGPU.wgpuRenderPassEncoderSetVertexBuffer(
            context.Pass, 0, _vertexBuffer, 0, (ulong)(vertexCount * VertexFloats * sizeof(float)));
        WGPU.wgpuRenderPassEncoderDraw(context.Pass, (uint)vertexCount, 1, 0, 0);
    }

    /// <summary>点列の幅付き triangle strip 頂点への展開</summary>
    private int BuildStrip(Vector3[] points, float halfWidth, float capExtension, out float totalLength)
    {
        int required = points.Length * 2 * VertexFloats;
        if (_scratch.Length < required) _scratch = new float[required];

        totalLength = capExtension * 2f;
        for (int i = 1; i < points.Length; i++)
        {
            totalLength += new Vector2(points[i].X - points[i - 1].X, points[i].Y - points[i - 1].Y).Length();
        }

        int write = 0;
        int last = points.Length - 1;
        Vector2 lastTangent = Vector2.UnitX;
        Vector2 previousCenter = default;
        float arc = 0f;
        for (int i = 0; i < points.Length; i++)
        {
            Vector2 directionPrev = i > 0 ? NormalizeXY(points[i] - points[i - 1]) : Vector2.Zero;
            Vector2 directionNext = i < last ? NormalizeXY(points[i + 1] - points[i]) : Vector2.Zero;

            Vector2 tangent = directionPrev + directionNext;
            if (tangent.LengthSquared() > 1e-12f) tangent = Vector2.Normalize(tangent);
            else if (directionPrev != Vector2.Zero) tangent = directionPrev;
            else tangent = lastTangent;
            lastTangent = tangent;

            Vector2 normal = new(-tangent.Y, tangent.X);

            float scale = 1f;
            if (directionPrev != Vector2.Zero && directionNext != Vector2.Zero)
            {
                float cosHalf = Vector2.Dot(normal, new Vector2(-directionPrev.Y, directionPrev.X));
                scale = 1f / MathF.Max(cosHalf, 1f / MiterLimit);
            }

            var center = new Vector2(points[i].X, points[i].Y);
            if (i == 0) center -= tangent * capExtension;
            if (i == last) center += tangent * capExtension;

            if (i > 0) arc += (center - previousCenter).Length();
            previousCenter = center;
            float u = totalLength > 1e-6f ? arc / totalLength : 0f;

            Vector2 offset = normal * (halfWidth * scale);
            _scratch[write++] = center.X + offset.X;
            _scratch[write++] = center.Y + offset.Y;
            _scratch[write++] = points[i].Z;
            _scratch[write++] = u;
            _scratch[write++] = 0f;
            _scratch[write++] = center.X - offset.X;
            _scratch[write++] = center.Y - offset.Y;
            _scratch[write++] = points[i].Z;
            _scratch[write++] = u;
            _scratch[write++] = 1f;
        }

        return write / VertexFloats;
    }

    private static Vector2 NormalizeXY(Vector3 v)
    {
        var xy = new Vector2(v.X, v.Y);
        return xy.LengthSquared() > 1e-12f ? Vector2.Normalize(xy) : Vector2.Zero;
    }

    private void EnsureVertexCapacity(in RenderContext context, int vertexCount)
    {
        if (_vertexBuffer is not null && _vertexCapacity >= vertexCount) return;
        if (_vertexBuffer is not null) { WGPU.wgpuBufferRelease(_vertexBuffer); _vertexBuffer = null; }

        var descriptor = new BufferDescriptor
        {
            Usage = BufferUsage.Vertex | BufferUsage.CopyDst,
            Size = (ulong)(vertexCount * VertexFloats * sizeof(float)),
        };
        _vertexBuffer = WGPU.wgpuDeviceCreateBuffer(context.Device, &descriptor);
        _vertexCapacity = vertexCount;
        _pool = context.Resources;
        context.Resources.Track(this, () => { if (_vertexBuffer is not null) { WGPU.wgpuBufferRelease(_vertexBuffer); _vertexBuffer = null; } });
    }
}

public sealed partial class LineRendererComponent
{
    /// <summary>フィールド値が入るたびの参照の解決（空のマテリアルは同梱の既定へ）</summary>
    public async ValueTask OnResolveAssetsAsync(IAssetResolver resolver)
    {
        await ResolveAssetFieldsAsync(resolver);
        _resolvedMaterial ??= await resolver.ResolveAsync(new AssetReference<MaterialAsset>(BuiltinMaterials.Transparent));
        _builtinShader = await resolver.ResolveAsync(new AssetReference<ShaderAsset>(BuiltinShaders.Line));
        ApplyShaders();
    }
}
