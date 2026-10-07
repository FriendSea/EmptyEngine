using System.Runtime.InteropServices;
using EmptyEngine.Graphics;
using EmptyEngine.ObjectModel;
using WgpuBuffer = EmptyEngine.WebGpu.Buffer;

namespace EmptyEngine.WebGpu;

/// <summary>マテリアルで描くコンポーネントが共有する、シェーダの選択とパイプラインの生成</summary>
internal static unsafe class MaterialRenderSupport
{
    /// <summary><c>group(0)</c>：コンポーネント固有の uniform</summary>
    public const uint UniformBinding = 0;

    /// <summary><c>group(0)</c>：コンポーネントが値を持つ Params</summary>
    public const uint ParamsBinding = 3;

    /// <summary><c>group(0)</c>：Effect のエミッター軌跡</summary>
    public const uint HistoryBinding = 31;

    /// <summary><c>group(2)</c>：マテリアルが値を持つ Params</summary>
    public const uint MaterialParamsBinding = 0;

    /// <summary><c>group(2)</c>：追加テクスチャ用のサンプラー</summary>
    public const uint MaterialSamplerBinding = 1;

    /// <summary><c>group(3)</c>：メインテクスチャ</summary>
    public const uint MainTextureBinding = 0;

    /// <summary><c>group(3)</c>：メインテクスチャのサンプラー</summary>
    public const uint MainSamplerBinding = 1;

    public const int HistoryCapacity = 128;
    public const int HistoryHeaderFloatCount = 4;
    public const int HistorySampleFloatCount = 20;
    public const int HistoryBufferFloatCount = HistoryHeaderFloatCount + HistoryCapacity * HistorySampleFloatCount;
    public const int HistoryBufferSize = HistoryBufferFloatCount * sizeof(float);

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

    /// <summary>float の数から決まる Params 用 uniform buffer の大きさ</summary>
    public static int ParamBufferSize(int valueCount) => Math.Max(16, ((valueCount * sizeof(float)) + 15) & ~15);

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
    public static ShaderAsset? SelectVertexShader(
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

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ShaderAsset, object> Warned = new();

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

    /// <summary>頂点・フラグメントを別モジュールから取るレンダーパイプラインの生成</summary>
    public static RenderPipeline* CreatePipeline(in RenderContext context, in MaterialPipelineKey key, PipelineLayout* layout)
    {
        nint vsEntry = Marshal.StringToCoTaskMemUTF8("vs_main");
        nint fsEntry = Marshal.StringToCoTaskMemUTF8("fs_main");
        ShaderModule* vertexModule = null;
        ShaderModule* fragmentModule = null;
        try
        {
            vertexModule = CreateModule(in context, key.Vertex);
            fragmentModule = ReferenceEquals(key.Vertex, key.Fragment) ? vertexModule : CreateModule(in context, key.Fragment);

            var attributes = stackalloc VertexAttribute[3];
            var bufferLayout = new VertexBufferLayout { StepMode = VertexStepMode.Vertex, Attributes = attributes };
            var primitive = new PrimitiveState
            {
                Topology = PrimitiveTopology.TriangleList,
                StripIndexFormat = IndexFormat.Undefined,
                FrontFace = FrontFace.Ccw,
                CullMode = CullMode.None,
            };
            switch (key.Kind)
            {
                case MaterialVertexKind.Quad:
                    attributes[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 };
                    attributes[1] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = sizeof(float) * 2, ShaderLocation = 1 };
                    bufferLayout.ArrayStride = sizeof(float) * 4;
                    bufferLayout.AttributeCount = 2;
                    break;

                case MaterialVertexKind.Mesh:
                    attributes[0] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 };
                    attributes[1] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = sizeof(float) * 3, ShaderLocation = 1 };
                    attributes[2] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = sizeof(float) * 6, ShaderLocation = 2 };
                    bufferLayout.ArrayStride = sizeof(float) * 8;
                    bufferLayout.AttributeCount = 3;
                    primitive.FrontFace = CoordinateConvention.IsLeftHanded ? FrontFace.CW : FrontFace.Ccw;
                    primitive.CullMode = CullMode.Back;
                    break;

                case MaterialVertexKind.Line:
                    attributes[0] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 };
                    attributes[1] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = sizeof(float) * 3, ShaderLocation = 1 };
                    bufferLayout.ArrayStride = sizeof(float) * 5;
                    bufferLayout.AttributeCount = 2;
                    primitive.Topology = PrimitiveTopology.TriangleStrip;
                    break;
            }

            bool hasBuffer = key.Kind != MaterialVertexKind.Effect;
            var vertexState = new VertexState
            {
                Module = vertexModule,
                EntryPoint = (byte*)vsEntry,
                BufferCount = (nuint)(hasBuffer ? 1 : 0),
                Buffers = hasBuffer ? &bufferLayout : null,
            };

            var blendState = default(BlendState);
            var colorTargetState = new ColorTargetState
            {
                Format = context.TargetFormat,
                Blend = null,
                WriteMask = ColorWriteMask.All,
            };
            if (ShaderBlend.Resolve(key.Blend) is { } blend)
            {
                blendState = new BlendState { Color = blend.Color, Alpha = blend.Alpha };
                colorTargetState.Blend = &blendState;
            }

            var fragmentState = new FragmentState
            {
                Module = fragmentModule,
                EntryPoint = (byte*)fsEntry,
                TargetCount = 1,
                Targets = &colorTargetState,
            };

            DepthStencilState depthState = SpriteRenderSupport.DepthTest(
                context.DepthFormat, key.DepthWrite, ShaderBlend.Resolve(key.DepthCompare));
            var pipelineDescriptor = new RenderPipelineDescriptor
            {
                Layout = layout,
                Vertex = vertexState,
                Fragment = &fragmentState,
                Primitive = primitive,
                DepthStencil = &depthState,
                Multisample = new MultisampleState { Count = 1, Mask = ~0u, AlphaToCoverageEnabled = 0 },
            };
            return WGPU.wgpuDeviceCreateRenderPipeline(context.Device, &pipelineDescriptor);
        }
        finally
        {
            if (fragmentModule is not null && fragmentModule != vertexModule) WGPU.wgpuShaderModuleRelease(fragmentModule);
            if (vertexModule is not null) WGPU.wgpuShaderModuleRelease(vertexModule);
            Marshal.FreeCoTaskMem(vsEntry);
            Marshal.FreeCoTaskMem(fsEntry);
        }
    }

    private static ShaderModule* CreateModule(in RenderContext context, ShaderAsset shader)
    {
        nint code = Marshal.StringToCoTaskMemUTF8(SpriteRenderSupport.ReadShaderSource(shader));
        try
        {
            var wgslDescriptor = new ShaderModuleWGSLDescriptor
            {
                Chain = new ChainedStruct { SType = SType.ShaderModuleWGSLDescriptor },
                Code = (byte*)code,
            };
            var descriptor = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgslDescriptor };
            return WGPU.wgpuDeviceCreateShaderModule(context.Device, &descriptor);
        }
        finally
        {
            Marshal.FreeCoTaskMem(code);
        }
    }
}

/// <summary>1 つのコンポーネントが描画に使う、パイプラインと 3 つの bind group の束</summary>
/// <remarks>
/// <c>group(0)</c> の buffer と bind group は自分で持ち、パイプライン・マテリアル（<c>group(2)</c>）・
/// メインテクスチャ（<c>group(3)</c>）は <see cref="GpuAssetCache"/> から借りる。<c>group(1)</c> は
/// レンダラーがパスごとに結ぶ。
/// </remarks>
internal sealed unsafe class MaterialBinding
{
    private GpuAssetCache? _cache;
    private GpuResourcePool? _pool;
    private RenderPipeline* _pipeline;
    private BindGroup* _instanceGroup;
    private BindGroup* _materialGroup;
    private BindGroup* _mainTextureGroup;
    private WgpuBuffer* _uniformBuffer;
    private WgpuBuffer* _paramBuffer;
    private WgpuBuffer* _historyBuffer;

    private ShaderAsset? _vertex;
    private MaterialAsset? _material;
    private ShaderAsset? _fragment;
    private TextureAsset? _mainTexture;
    private Sampler* _mainSampler;
    private int _paramSize;
    private bool _built;

    /// <summary>コンポーネント固有の uniform の書き込み先</summary>
    public WgpuBuffer* UniformBuffer => _uniformBuffer;

    /// <summary>Effect のエミッター軌跡の書き込み先（使わないシェーダでは <c>null</c>）</summary>
    public WgpuBuffer* HistoryBuffer => _historyBuffer;

    /// <summary>描ける状態か（シェーダが揃わなければ <c>false</c>）</summary>
    public bool IsReady => _built && _pipeline is not null;

    /// <summary>今の束が、この組み合わせのために作られたものか</summary>
    public bool Matches(ShaderAsset? vertex, MaterialAsset? material, TextureAsset? mainTexture, int paramValueCount)
    {
        return _built
            && ReferenceEquals(_vertex, vertex)
            && ReferenceEquals(_material, material)
            && ReferenceEquals(_fragment, material?.ResolvedShader)
            && ReferenceEquals(_mainTexture, mainTexture)
            && _paramSize == MaterialRenderSupport.ParamBufferSize(paramValueCount);
    }

    /// <summary>束の作り直し</summary>
    /// <param name="uniformSize">コンポーネント固有の uniform の大きさ</param>
    /// <param name="paramValueCount">コンポーネントが持つ Params の float の数</param>
    /// <param name="history">Effect のエミッター軌跡を結ぶか</param>
    public void Build(
        in RenderContext context,
        MaterialVertexKind kind,
        int uniformSize,
        ShaderAsset? vertex,
        MaterialAsset? material,
        TextureAsset? mainTexture,
        Sampler* mainSampler,
        int paramValueCount,
        bool history = false)
    {
        Release();

        _cache = context.Assets;
        _pool = context.Resources;
        _vertex = vertex;
        _material = material;
        _fragment = material?.ResolvedShader;
        _mainTexture = mainTexture;
        _mainSampler = mainSampler;
        _paramSize = MaterialRenderSupport.ParamBufferSize(paramValueCount);
        _built = true;

        if (vertex is null || material is null || _fragment is not { HasFragment: true } fragment) return;

        _pipeline = context.Assets.GetPipeline(in context, new MaterialPipelineKey(
            vertex, fragment,
            material.Blend, material.DepthWrite, material.DepthCompare,
            kind, uniformSize, _paramSize, history));
        _materialGroup = context.Assets.AcquireMaterial(in context, material, fragment);
        _mainTextureGroup = context.Assets.AcquireMainTexture(in context, mainTexture, mainSampler);

        var uniformDescriptor = new BufferDescriptor { Usage = BufferUsage.Uniform | BufferUsage.CopyDst, Size = (ulong)uniformSize };
        _uniformBuffer = WGPU.wgpuDeviceCreateBuffer(context.Device, &uniformDescriptor);
        var paramDescriptor = new BufferDescriptor { Usage = BufferUsage.Uniform | BufferUsage.CopyDst, Size = (ulong)_paramSize };
        _paramBuffer = WGPU.wgpuDeviceCreateBuffer(context.Device, &paramDescriptor);
        if (history)
        {
            var historyDescriptor = new BufferDescriptor { Usage = BufferUsage.Storage | BufferUsage.CopyDst, Size = MaterialRenderSupport.HistoryBufferSize };
            _historyBuffer = WGPU.wgpuDeviceCreateBuffer(context.Device, &historyDescriptor);
        }

        var entries = stackalloc BindGroupEntry[3];
        entries[0] = new BindGroupEntry { Binding = MaterialRenderSupport.UniformBinding, Buffer = _uniformBuffer, Offset = 0, Size = (ulong)uniformSize };
        entries[1] = new BindGroupEntry { Binding = MaterialRenderSupport.ParamsBinding, Buffer = _paramBuffer, Offset = 0, Size = (ulong)_paramSize };
        entries[2] = new BindGroupEntry { Binding = MaterialRenderSupport.HistoryBinding, Buffer = _historyBuffer, Offset = 0, Size = MaterialRenderSupport.HistoryBufferSize };
        var descriptor = new BindGroupDescriptor
        {
            Layout = context.Assets.GetInstanceLayout(in context, uniformSize, _paramSize, history),
            EntryCount = (nuint)(history ? 3 : 2),
            Entries = entries,
        };
        _instanceGroup = WGPU.wgpuDeviceCreateBindGroup(context.Device, &descriptor);

        // 持ち主が OnDestroy を通らずに消えたときの安全網。
        context.Resources.Track(this, ReleaseOwned);
    }

    /// <summary>コンポーネントが持つ Params の値の書き込み</summary>
    public void WriteParams(in RenderContext context, float[]? values)
    {
        if (_paramBuffer is null || values is not { Length: > 0 }) return;

        int count = Math.Min(values.Length, _paramSize / sizeof(float));
        fixed (float* p = values)
            WGPU.wgpuQueueWriteBuffer(context.Queue, _paramBuffer, 0, p, (nuint)(count * sizeof(float)));
    }

    /// <summary>パイプラインと、<c>group(0)</c>・<c>group(2)</c>・<c>group(3)</c> の接続</summary>
    public void Bind(in RenderContext context)
    {
        WGPU.wgpuRenderPassEncoderSetPipeline(context.Pass, _pipeline);
        WGPU.wgpuRenderPassEncoderSetBindGroup(context.Pass, 0, _instanceGroup, 0, null);
        WGPU.wgpuRenderPassEncoderSetBindGroup(context.Pass, 2, _materialGroup, 0, null);
        WGPU.wgpuRenderPassEncoderSetBindGroup(context.Pass, 3, _mainTextureGroup, 0, null);
    }

    /// <summary>自分の分の解放と、借りていた分の返却</summary>
    public void Release()
    {
        _pool?.ReleaseAll(this);
        ReleaseOwned();

        if (_materialGroup is not null && _material is not null) _cache?.ReleaseMaterial(_material);
        if (_mainTextureGroup is not null) _cache?.ReleaseMainTexture(_mainTexture, _mainSampler);

        _materialGroup = null;
        _mainTextureGroup = null;
        _pipeline = null;
        _material = null;
        _fragment = null;
        _vertex = null;
        _mainTexture = null;
        _mainSampler = null;
        _built = false;
    }

    private void ReleaseOwned()
    {
        if (_instanceGroup is not null) { WGPU.wgpuBindGroupRelease(_instanceGroup); _instanceGroup = null; }
        if (_uniformBuffer is not null) { WGPU.wgpuBufferRelease(_uniformBuffer); _uniformBuffer = null; }
        if (_paramBuffer is not null) { WGPU.wgpuBufferRelease(_paramBuffer); _paramBuffer = null; }
        if (_historyBuffer is not null) { WGPU.wgpuBufferRelease(_historyBuffer); _historyBuffer = null; }
    }
}
