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

    private const string ShaderSource = """
        struct Uniforms {
            model_view: mat4x4<f32>,
            projection: mat4x4<f32>,
            color: vec4<f32>,
            misc: vec4<f32>,
        };
        @group(0) @binding(0) var<uniform> u: Uniforms;

        @vertex
        fn vs_main(@location(0) position: vec3<f32>) -> @builtin(position) vec4<f32> {
            return u.projection * u.model_view * vec4<f32>(position, 1.0);
        }

        @fragment
        fn fs_main() -> @location(0) vec4<f32> {
            return u.color;
        }
        """;

    private const float MiterLimit = 4f;

    private IObject? _owner;
    /// <summary>描画に使う WGSL シェーダの解決済み実体</summary>
    [ResolveAsset("Shader")]
    private ShaderAsset? _resolvedShader;
    private ShaderAsset? _realizedShader;
    private TextureAsset?[] _resolvedSlotTextures = [];
    private TextureAsset?[] _realizedSlotTextures = [];
    private bool _resourcesBuilt;
    private GpuResourcePool? _pool;
    private WgpuBuffer* _uniformBuffer;
    private WgpuBuffer* _paramBuffer;
    private WgpuBuffer* _vertexBuffer;
    private int _vertexCapacity;
    private RenderPipeline* _opaquePipeline;
    private RenderPipeline* _transparentPipeline;
    private PipelineLayout* _opaquePipelineLayout;
    private PipelineLayout* _transparentPipelineLayout;
    private BindGroupLayout* _bindGroupLayout;
    private BindGroup* _bindGroup;
    private Texture*[] _slotTextures = [];
    private TextureView*[] _slotTextureViews = [];
    private float[] _scratch = [];
    private ShaderDepthState _shaderDepth = ShaderDepthState.Opaque;
    private int _renderQueue = ShaderRenderQueue.Geometry;
    private readonly ShaderGlobalsBinding _globals = new();

    public LineRendererComponent(RenderWorld renderWorld) => _renderWorld = renderWorld;

    /// <summary>結ぶ点列（ローカル座標）</summary>
    public Vector3[] Points { get; set; } = [];

    /// <summary>線の幅（world 単位）</summary>
    public float Width { get; set; } = 0.1f;

    /// <summary>両端を接線方向へ余分に延長する長さ（world 単位）</summary>
    public float CapExtension { get; set; }

    /// <summary>線のカラー（RGBA 各成分 0..1）</summary>
    public GraphicsColor Color { get; set; } = GraphicsColor.White;

    /// <summary>シェーダのユーザ定義パラメータの値</summary>
    public float[] Params { get; set; } = [];

    /// <summary>シェーダが宣言したテクスチャスロットへ貼るテクスチャ参照</summary>
    public AssetReference<TextureAsset>[] Textures { get; set; } = [];

    /// <summary>現在解決済みのシェーダ</summary>
    public ShaderAsset? ResolvedShader => _resolvedShader;

    /// <inheritdoc/>
    public IObject? Owner => _owner;

    /// <inheritdoc/>
    public bool IsTransparent => _resolvedShader is null
        ? Color.A < 1f
        : _renderQueue >= ShaderRenderQueue.Transparent;

    /// <inheritdoc/>
    public int RenderQueue => _resolvedShader is null
        ? (Color.A < 1f ? ShaderRenderQueue.Transparent : ShaderRenderQueue.Geometry)
        : _renderQueue;

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

    private int ParamBufferSize => Math.Max(16, (((Params?.Length ?? 0) * sizeof(float)) + 15) & ~15);

    private ShaderTextureSlot[] EffectiveTextureSlots => _resolvedShader?.TextureSlots ?? [];

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
        Textures ??= [];
        _scratch ??= [];
        _slotTextures ??= [];
        _slotTextureViews ??= [];
        if (_resolvedShader is not null)
        {
            ShaderDepthState fallback = ShaderBlend.IsOpaque(_resolvedShader)
                ? ShaderDepthState.Opaque
                : ShaderDepthState.Transparent;
            _shaderDepth = ShaderBlend.ResolveDepth(_resolvedShader, fallback);
            _renderQueue = ShaderBlend.ResolveRenderQueue(
                _resolvedShader,
                ShaderBlend.IsOpaque(_resolvedShader) ? ShaderRenderQueue.Geometry : ShaderRenderQueue.Transparent);
        }
        else
        {
            _shaderDepth = ShaderDepthState.Opaque;
            _renderQueue = ShaderRenderQueue.Geometry;
        }

        if (_resolvedShader is not null)
            Params = ShaderParamsHost.Reconcile(_resolvedShader.Params, Params);
    }

    public void OnDestroy(IObject owner)
    {
        _renderWorld.UnregisterRenderer(this);
        ReleaseResources();
        _owner = null;
        _resolvedShader = null;
        _resolvedSlotTextures = [];
        _realizedSlotTextures = [];
        _shaderDepth = ShaderDepthState.Opaque;
        _renderQueue = ShaderRenderQueue.Geometry;
    }

    public void Render(in RenderContext context)
    {
        Vector3[] points = Points ?? [];
        if (points.Length < 2 || Width <= 0f) return;

        EnsureResources(in context);

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
        uniform[36] = context.Time;
        uniform[37] = totalLength;
        uniform[38] = 0f;
        uniform[39] = 0f;
        fixed (float* u = uniform)
        {
            WGPU.wgpuQueueWriteBuffer(context.Queue, _uniformBuffer, 0, u, UniformSize);
        }

        if (_paramBuffer is not null && Params is { Length: > 0 })
        {
            fixed (float* p = Params)
                WGPU.wgpuQueueWriteBuffer(context.Queue, _paramBuffer, 0, p, (nuint)(Params.Length * sizeof(float)));
        }

        RenderPipeline* pipeline = IsTransparent ? _transparentPipeline : _opaquePipeline;
        WGPU.wgpuRenderPassEncoderSetPipeline(context.Pass, pipeline);
        WGPU.wgpuRenderPassEncoderSetBindGroup(context.Pass, 0, _bindGroup, 0, null);
        _globals.Bind(in context);
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
        context.Resources.Track(this, () => { if (_vertexBuffer is not null) { WGPU.wgpuBufferRelease(_vertexBuffer); _vertexBuffer = null; } });
    }

    private void EnsureResources(in RenderContext context)
    {
        TextureAsset?[] resolvedSlots = _resolvedSlotTextures ?? [];
        if (_resourcesBuilt
            && ReferenceEquals(_realizedShader, _resolvedShader)
            && SlotTexturesMatch(_realizedSlotTextures, resolvedSlots)) return;

        if (_resourcesBuilt) ReleaseResources();

        _pool = context.Resources;

        ShaderTextureSlot[] slots = EffectiveTextureSlots;
        bool custom = _resolvedShader is not null;

        if (custom)
        {
            ShaderAsset shader = _resolvedShader!;
            string wgsl = SpriteRenderSupport.ReadShaderSource(shader);
            ShaderDepthState fallback = ShaderBlend.IsOpaque(shader)
                ? ShaderDepthState.Opaque
                : ShaderDepthState.Transparent;
            _shaderDepth = ShaderBlend.ResolveDepth(shader, fallback);
            _renderQueue = ShaderBlend.ResolveRenderQueue(
                shader,
                ShaderBlend.IsOpaque(shader) ? ShaderRenderQueue.Geometry : ShaderRenderQueue.Transparent);
            _bindGroupLayout = MeshRenderSupport.CreateShaderParamLayout(
                in context, UniformSize, (ulong)ParamBufferSize, slots);
            context.Resources.Track(this, () => { if (_bindGroupLayout is not null) { WGPU.wgpuBindGroupLayoutRelease(_bindGroupLayout); _bindGroupLayout = null; } });
            _globals.Build(in context, shader, this);
            var (blendColor, blendAlpha) = ShaderBlend.Resolve(shader);
            BuildDepthPipelines(in context, wgsl, blendColor, blendAlpha);

            var paramDescriptor = new BufferDescriptor { Usage = BufferUsage.Uniform | BufferUsage.CopyDst, Size = (ulong)ParamBufferSize };
            _paramBuffer = WGPU.wgpuDeviceCreateBuffer(context.Device, &paramDescriptor);
            context.Resources.Track(this, () => { if (_paramBuffer is not null) { WGPU.wgpuBufferRelease(_paramBuffer); _paramBuffer = null; } });
        }
        else
        {
            _shaderDepth = ShaderDepthState.Opaque;
            _renderQueue = ShaderRenderQueue.Geometry;
            var layoutEntry = new BindGroupLayoutEntry
            {
                Binding = 0,
                Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
                Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, MinBindingSize = UniformSize },
            };
            var layoutDescriptor = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &layoutEntry };
            _bindGroupLayout = WGPU.wgpuDeviceCreateBindGroupLayout(context.Device, &layoutDescriptor);
            context.Resources.Track(this, () => { if (_bindGroupLayout is not null) { WGPU.wgpuBindGroupLayoutRelease(_bindGroupLayout); _bindGroupLayout = null; } });

            var (blendColor, blendAlpha) = SpriteRenderSupport.AlphaBlend;
            BuildDepthPipelines(in context, ShaderSource, blendColor, blendAlpha);
        }

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

        if (custom)
        {
            _bindGroup = MeshRenderSupport.CreateShaderParamBindGroup(
                in context, _bindGroupLayout, _uniformBuffer, UniformSize, context.DefaultSampler,
                _paramBuffer, (ulong)ParamBufferSize, slotBindings, slotViews);
        }
        else
        {
            var bindGroupEntry = new BindGroupEntry { Binding = 0, Buffer = _uniformBuffer, Offset = 0, Size = UniformSize };
            var bindGroupDescriptor = new BindGroupDescriptor { Layout = _bindGroupLayout, EntryCount = 1, Entries = &bindGroupEntry };
            _bindGroup = WGPU.wgpuDeviceCreateBindGroup(context.Device, &bindGroupDescriptor);
        }
        context.Resources.Track(this, () => { if (_bindGroup is not null) { WGPU.wgpuBindGroupRelease(_bindGroup); _bindGroup = null; } });

        _realizedShader = _resolvedShader;
        _realizedSlotTextures = resolvedSlots;
        _resourcesBuilt = true;
    }

    private void BuildDepthPipelines(
        in RenderContext context,
        string wgsl,
        in BlendComponent blendColor,
        in BlendComponent blendAlpha)
    {
        // Honor custom ZWrite so transparent masks can still occlude geometry.
        bool customDepthWrite = _resolvedShader is not null && _shaderDepth.WriteEnabled;
        _opaquePipeline = CreateLinePipeline(
            in context, wgsl, _bindGroupLayout, blendColor, blendAlpha,
            out _opaquePipelineLayout,
            depthWrite: _resolvedShader is null || customDepthWrite,
            depthCompare: _shaderDepth.Compare,
            globalsLayout: _globals.Layout);
        context.Resources.Track(this, () =>
        {
            if (_opaquePipeline is not null)
            {
                WGPU.wgpuRenderPipelineRelease(_opaquePipeline);
                _opaquePipeline = null;
            }
        });
        context.Resources.Track(this, () =>
        {
            if (_opaquePipelineLayout is not null)
            {
                WGPU.wgpuPipelineLayoutRelease(_opaquePipelineLayout);
                _opaquePipelineLayout = null;
            }
        });

        _transparentPipeline = CreateLinePipeline(
            in context, wgsl, _bindGroupLayout, blendColor, blendAlpha,
            out _transparentPipelineLayout,
            depthWrite: customDepthWrite,
            depthCompare: _shaderDepth.Compare,
            globalsLayout: _globals.Layout);
        context.Resources.Track(this, () =>
        {
            if (_transparentPipeline is not null)
            {
                WGPU.wgpuRenderPipelineRelease(_transparentPipeline);
                _transparentPipeline = null;
            }
        });
        context.Resources.Track(this, () =>
        {
            if (_transparentPipelineLayout is not null)
            {
                WGPU.wgpuPipelineLayoutRelease(_transparentPipelineLayout);
                _transparentPipelineLayout = null;
            }
        });
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

    /// <summary>線用レンダーパイプライン</summary>
    private static RenderPipeline* CreateLinePipeline(
        in RenderContext context,
        string wgsl,
        BindGroupLayout* bindGroupLayout,
        in BlendComponent colorBlend,
        in BlendComponent alphaBlend,
        out PipelineLayout* pipelineLayout,
        bool depthWrite,
        CompareFunction depthCompare,
        BindGroupLayout* globalsLayout)
    {
        nint codePtr = Marshal.StringToCoTaskMemUTF8(wgsl);
        nint vsEntry = Marshal.StringToCoTaskMemUTF8("vs_main");
        nint fsEntry = Marshal.StringToCoTaskMemUTF8("fs_main");
        try
        {
            var wgslDescriptor = new ShaderModuleWGSLDescriptor
            {
                Chain = new ChainedStruct { SType = SType.ShaderModuleWGSLDescriptor },
                Code = (byte*)codePtr,
            };
            var shaderModuleDescriptor = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgslDescriptor };
            ShaderModule* module = WGPU.wgpuDeviceCreateShaderModule(context.Device, &shaderModuleDescriptor);

            pipelineLayout = SpriteRenderSupport.CreatePipelineLayout(in context, bindGroupLayout, globalsLayout);

            var vertexAttributes = stackalloc VertexAttribute[2];
            vertexAttributes[0] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 };
            vertexAttributes[1] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = sizeof(float) * 3, ShaderLocation = 1 };
            var vertexBufferLayout = new VertexBufferLayout
            {
                ArrayStride = sizeof(float) * VertexFloats,
                StepMode = VertexStepMode.Vertex,
                AttributeCount = 2,
                Attributes = vertexAttributes,
            };
            var vertexState = new VertexState
            {
                Module = module,
                EntryPoint = (byte*)vsEntry,
                BufferCount = 1,
                Buffers = &vertexBufferLayout,
            };

            var blendState = new BlendState { Color = colorBlend, Alpha = alphaBlend };
            var colorTargetState = new ColorTargetState
            {
                Format = context.TargetFormat,
                Blend = &blendState,
                WriteMask = ColorWriteMask.All,
            };
            var fragmentState = new FragmentState
            {
                Module = module,
                EntryPoint = (byte*)fsEntry,
                TargetCount = 1,
                Targets = &colorTargetState,
            };

            DepthStencilState depthState = SpriteRenderSupport.DepthTest(
                context.DepthFormat, depthWrite, depthCompare);
            var pipelineDescriptor = new RenderPipelineDescriptor
            {
                Layout = pipelineLayout,
                Vertex = vertexState,
                Fragment = &fragmentState,
                Primitive = new PrimitiveState
                {
                    Topology = PrimitiveTopology.TriangleStrip,
                    StripIndexFormat = IndexFormat.Undefined,
                    FrontFace = FrontFace.Ccw,
                    CullMode = CullMode.None,
                },
                DepthStencil = &depthState,
                Multisample = new MultisampleState { Count = 1, Mask = ~0u, AlphaToCoverageEnabled = 0 },
            };
            RenderPipeline* pipeline = WGPU.wgpuDeviceCreateRenderPipeline(context.Device, &pipelineDescriptor);

            WGPU.wgpuShaderModuleRelease(module);
            return pipeline;
        }
        finally
        {
            Marshal.FreeCoTaskMem(codePtr);
            Marshal.FreeCoTaskMem(vsEntry);
            Marshal.FreeCoTaskMem(fsEntry);
        }
    }

    private void ReleaseResources()
    {
        _pool?.ReleaseAll(this);
        _globals.Release();
        _vertexCapacity = 0;
        _realizedShader = null;
        _realizedSlotTextures = [];
        _resourcesBuilt = false;
    }
}

public sealed partial class LineRendererComponent
{
    /// <summary>フィールド値が入るたびの参照の解決（シェーダの枠に合わせた <see cref="Textures"/> の枠まで）</summary>
    public async ValueTask OnResolveAssetsAsync(IAssetResolver resolver)
    {
        await ResolveAssetFieldsAsync(resolver);
        Textures ??= [];
        if (_resolvedShader is not null)
            Textures = ShaderParamsHost.ReconcileTextures(_resolvedShader.TextureSlots, Textures);
        _resolvedSlotTextures = await ShaderParamsHost.ResolveTexturesAsync(Textures, resolver);
    }
}
