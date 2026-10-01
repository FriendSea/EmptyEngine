using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EmptyEngine.Core;
using EmptyEngine.Generators;
using EmptyEngine.Graphics;
using EmptyEngine.ObjectModel;
using WgpuBuffer = EmptyEngine.WebGpu.Buffer;
using GraphicsColor = EmptyEngine.Graphics.Color;

namespace EmptyEngine.WebGpu;

/// <summary>粒子をエミッターに追従させるか、発生時のワールド変換へ残すか</summary>
public enum EffectSimulationSpace
{
    Local = 0,
    World = 1,
}

/// <summary>見た目を WGSL シェーダに丸投げする最小エフェクト</summary>
public sealed unsafe partial class EffectComponent : ISpriteRenderer, IShaderParamsHost
{
    private const int UniformSize = 240;
    private const uint HistoryBinding = 31;
    private const int HistoryCapacity = 128;
    private const int HistoryHeaderFloatCount = 4;
    private const int HistorySampleFloatCount = 20;
    private const int HistoryBufferFloatCount = HistoryHeaderFloatCount + HistoryCapacity * HistorySampleFloatCount;
    private const int HistoryBufferSize = HistoryBufferFloatCount * sizeof(float);
    private static readonly Regex HistoryBindingRegex = new(
        @"@group\s*\(\s*0\s*\)\s*@binding\s*\(\s*31\s*\)", RegexOptions.Compiled);

    private readonly RenderWorld _renderWorld;
    private readonly float[] _historyData = new float[HistoryBufferFloatCount];

    /// <summary>描画に使う WGSL シェーダの解決済み実体</summary>
    [ResolveAsset("Shader")]
    private ShaderAsset? _resolvedShader;

    private ShaderAsset? _realizedShader;
    private TextureAsset?[] _resolvedSlotTextures = [];
    private TextureAsset?[] _realizedSlotTextures = [];
    private int _realizedQuadCount = -1;
    private bool _resourcesBuilt;
    private bool _usesSimulationHistory;
    private GpuResourcePool? _pool;
    private GpuAssetCache? _cache;
    private ShaderAsset? _borrowedPipelineShader;
    private int _borrowedPipelineLayoutKey;
    private float _startTime = -1f;
    private IObject? _owner;

    private RenderPipeline* _pipeline;
    private PipelineLayout* _pipelineLayout;
    private BindGroupLayout* _bindGroupLayout;
    private BindGroup* _bindGroup;
    private WgpuBuffer* _uniformBuffer;
    private WgpuBuffer* _paramBuffer;
    private WgpuBuffer* _historyBuffer;
    private WgpuBuffer* _indexBuffer;
    private Texture*[] _slotTextures = [];
    private TextureView*[] _slotTextureViews = [];
    private uint _indexCount;
    private ShaderDepthState _shaderDepth = ShaderDepthState.Transparent;
    private int _renderQueue = ShaderRenderQueue.Transparent;
    private readonly ShaderGlobalsBinding _globals = new();
    private int _historyCount;
    private int _historyWriteIndex = -1;
    private float _historyInterval;
    private float _lastHistoryObservationTime = -1f;
    private float _nextHistorySampleTime;
    private Matrix4x4 _lastHistoryObservation;
    private bool _lastHistoryEmissionEnabled;

    public EffectComponent(RenderWorld renderWorld) => _renderWorld = renderWorld;

    /// <summary>描く quad の数</summary>
    public int QuadCount { get; set; } = 1;

    /// <summary>描画順レイヤへの加算オフセット</summary>
    public int SortOrder { get; set; }

    /// <summary>粒子をエミッターへ追従させるか、発生時のワールド変換へ残すか</summary>
    public EffectSimulationSpace SimulationSpace { get; set; } = EffectSimulationSpace.Local;

    /// <summary>World シミュレーションでエミッター軌跡を保持する秒数（0 以下ならシェーダの duration パラメータに従う）</summary>
    public float SimulationHistoryDuration { get; set; }

    /// <summary>新しい粒子を発生させるか（<c>false</c> にしても既存の粒子は寿命まで残る）</summary>
    public bool Emit { get; set; } = true;

    /// <summary>シェーダへ渡す乗算カラー（RGBA 各成分 0..1）</summary>
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
    public int RenderLayer => SpriteRenderSupport.GetRenderLayer(_owner) + SortOrder;

    /// <inheritdoc/>
    public bool IsTransparent => _renderQueue >= ShaderRenderQueue.Transparent;

    /// <inheritdoc/>
    public int RenderQueue => _renderQueue;

    private int ParamBufferSize => Math.Max(16, (((Params?.Length ?? 0) * sizeof(float)) + 15) & ~15);

    public void OnCreated(IObject owner)
    {
    }

    public void OnDeserialized(IObject owner)
    {
        _owner = owner;

        _startTime = -1f;
        ResetHistoryState();

        if (Color == default) Color = GraphicsColor.White;

        Textures ??= [];

        _renderWorld.RegisterRenderer(this);

        if (_resolvedShader is not null) Params = ShaderParamsHost.Reconcile(_resolvedShader.Params, Params);

        _shaderDepth = _resolvedShader is null
            ? ShaderDepthState.Transparent
            : ShaderBlend.ResolveDepth(_resolvedShader, ShaderDepthState.Transparent);
        _renderQueue = _resolvedShader is null
            ? ShaderRenderQueue.Transparent
            : ShaderBlend.ResolveRenderQueue(_resolvedShader, ShaderRenderQueue.Transparent);

    }

    public void OnDestroy(IObject owner)
    {
        _renderWorld.UnregisterRenderer(this);
        ReleaseResources();
        _owner = null;
        _resolvedShader = null;
        _realizedShader = null;
        _resolvedSlotTextures = [];
        _realizedSlotTextures = [];
        _shaderDepth = ShaderDepthState.Transparent;
        _renderQueue = ShaderRenderQueue.Transparent;
        ResetHistoryState();
    }

    public void Render(in RenderContext context)
    {
        if (_resolvedShader is null || QuadCount <= 0) return;

        EnsureResources(in context);

        if (_startTime < 0f) _startTime = context.Time;
        float localTime = context.Time - _startTime;

        Matrix4x4 worldTransform = SpriteRenderSupport.ReadTransform(_owner);
        UpdateHistory(localTime, worldTransform);
        UploadHistory(context.Queue);

        Matrix4x4 transform = worldTransform * context.Projection;
        float* u = stackalloc float[UniformSize / sizeof(float)];
        *(Matrix4x4*)u = transform;
        u[16] = localTime;
        u[17] = QuadCount;
        u[18] = (float)SimulationSpace;
        u[19] = 0f;
        u[20] = Color.R;
        u[21] = Color.G;
        u[22] = Color.B;
        u[23] = Color.A;
        *(Matrix4x4*)(u + 24) = context.Projection;
        *(Matrix4x4*)(u + 40) = worldTransform;
        u[56] = Emit ? 1f : 0f;
        u[57] = 0f;
        u[58] = 0f;
        u[59] = 0f;
        WGPU.wgpuQueueWriteBuffer(context.Queue, _uniformBuffer, 0, u, UniformSize);

        if (Params is { Length: > 0 })
        {
            fixed (float* p = Params)
                WGPU.wgpuQueueWriteBuffer(context.Queue, _paramBuffer, 0, p, (nuint)(Params.Length * sizeof(float)));
        }

        WGPU.wgpuRenderPassEncoderSetPipeline(context.Pass, _pipeline);
        WGPU.wgpuRenderPassEncoderSetBindGroup(context.Pass, 0, _bindGroup, 0, null);
        _globals.Bind(in context);
        WGPU.wgpuRenderPassEncoderSetIndexBuffer(context.Pass, _indexBuffer, IndexFormat.Uint32, 0, (ulong)_indexCount * sizeof(uint));
        WGPU.wgpuRenderPassEncoderDrawIndexed(context.Pass, _indexCount, 1, 0, 0, 0);
    }

    private void EnsureResources(in RenderContext context)
    {
        bool shaderChanged = !ReferenceEquals(_realizedShader, _resolvedShader);
        bool countChanged = _realizedQuadCount != QuadCount;
        TextureAsset?[] resolvedSlots = _resolvedSlotTextures ?? [];
        bool slotTexturesChanged = !SlotTexturesMatch(_realizedSlotTextures, resolvedSlots);
        if (_resourcesBuilt && !shaderChanged && !countChanged && !slotTexturesChanged) return;

        if (_resourcesBuilt) ReleaseResources();

        _pool = context.Resources;

        BuildPipeline(in context, _resolvedShader!);
        BuildIndexBuffer(in context, QuadCount);
        BuildUniformAndBindGroup(in context, resolvedSlots);

        _realizedShader = _resolvedShader;
        _realizedSlotTextures = resolvedSlots;
        _realizedQuadCount = QuadCount;
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

    private void BuildPipeline(in RenderContext context, ShaderAsset shader)
    {
        string wgsl = SpriteRenderSupport.ReadShaderSource(shader);
        _usesSimulationHistory = HistoryBindingRegex.IsMatch(wgsl);
        _shaderDepth = ShaderBlend.ResolveDepth(shader, ShaderDepthState.Transparent);
        _renderQueue = ShaderBlend.ResolveRenderQueue(shader, ShaderRenderQueue.Transparent);
        _globals.Build(in context, shader, this);

        // 同じ shader/layout の Effect が大量に生成・破棄されても pipeline を作り直さない。
        // ParamBufferSize は instance の Params 配列から決まるため cache key に含める。
        int layoutKey = ParamBufferSize | (_usesSimulationHistory ? 1 : 0);
        if (context.Assets.TryAcquirePipeline(
            shader, GpuPipelineVariant.Effect, layoutKey,
            out _pipeline, out _pipelineLayout, out _bindGroupLayout))
        {
            _cache = context.Assets;
            _borrowedPipelineShader = shader;
            _borrowedPipelineLayoutKey = layoutKey;
            return;
        }

        ShaderTextureSlot[] slots = shader.TextureSlots ?? [];
        if (_usesSimulationHistory && slots.Any(slot => slot.Binding == HistoryBinding))
            throw new InvalidOperationException($"Effect shader texture binding {HistoryBinding} is reserved for world-space simulation history.");

        nint codePtr = Marshal.StringToCoTaskMemUTF8(wgsl);
        nint vsEntry = Marshal.StringToCoTaskMemUTF8("vs_main");
        nint fsEntry = Marshal.StringToCoTaskMemUTF8("fs_main");
        ShaderModule* module = null;
        BindGroupLayout* bindGroupLayout = null;
        PipelineLayout* pipelineLayout = null;
        RenderPipeline* pipeline = null;
        try
        {
            var wgslDescriptor = new ShaderModuleWGSLDescriptor
            {
                Chain = new ChainedStruct { SType = SType.ShaderModuleWGSLDescriptor },
                Code = (byte*)codePtr,
            };
            var shaderModuleDescriptor = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgslDescriptor };
            module = WGPU.wgpuDeviceCreateShaderModule(context.Device, &shaderModuleDescriptor);

            int fixedEntryCount = _usesSimulationHistory ? 4 : 3;
            var layoutEntries = stackalloc BindGroupLayoutEntry[fixedEntryCount + slots.Length];
            layoutEntries[0] = new BindGroupLayoutEntry
            {
                Binding = 0,
                Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
                Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, MinBindingSize = UniformSize },
            };
            layoutEntries[1] = new BindGroupLayoutEntry
            {
                Binding = 2,
                Visibility = ShaderStage.Fragment,
                Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering },
            };
            layoutEntries[2] = new BindGroupLayoutEntry
            {
                Binding = 3,
                Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
                Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, MinBindingSize = (ulong)ParamBufferSize },
            };
            if (_usesSimulationHistory)
            {
                layoutEntries[3] = new BindGroupLayoutEntry
                {
                    Binding = HistoryBinding,
                    Visibility = ShaderStage.Vertex,
                    Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage, MinBindingSize = HistoryBufferSize },
                };
            }
            for (int i = 0; i < slots.Length; i++)
            {
                layoutEntries[fixedEntryCount + i] = new BindGroupLayoutEntry
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
            var bindGroupLayoutDescriptor = new BindGroupLayoutDescriptor
            {
                EntryCount = (uint)(fixedEntryCount + slots.Length),
                Entries = layoutEntries,
            };
            bindGroupLayout = WGPU.wgpuDeviceCreateBindGroupLayout(context.Device, &bindGroupLayoutDescriptor);

            pipelineLayout = SpriteRenderSupport.CreatePipelineLayout(in context, bindGroupLayout, _globals.Layout);

            var vertexState = new VertexState
            {
                Module = module,
                EntryPoint = (byte*)vsEntry,
                BufferCount = 0,
                Buffers = null,
            };

            var (blendColor, blendAlpha) = ShaderBlend.Resolve(shader);
            var blendState = new BlendState { Color = blendColor, Alpha = blendAlpha };
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
                context.DepthFormat, _shaderDepth.WriteEnabled, _shaderDepth.Compare);
            var pipelineDescriptor = new RenderPipelineDescriptor
            {
                Layout = pipelineLayout,
                Vertex = vertexState,
                Fragment = &fragmentState,
                Primitive = new PrimitiveState
                {
                    Topology = PrimitiveTopology.TriangleList,
                    StripIndexFormat = IndexFormat.Undefined,
                    FrontFace = FrontFace.Ccw,
                    CullMode = CullMode.None,
                },
                DepthStencil = &depthState,
                Multisample = new MultisampleState { Count = 1, Mask = ~0u, AlphaToCoverageEnabled = 0 },
            };
            pipeline = WGPU.wgpuDeviceCreateRenderPipeline(context.Device, &pipelineDescriptor);

            context.Assets.AddPipeline(
                shader, GpuPipelineVariant.Effect, layoutKey, pipeline, pipelineLayout, bindGroupLayout);

            _pipeline = pipeline;
            _pipelineLayout = pipelineLayout;
            _bindGroupLayout = bindGroupLayout;
            _cache = context.Assets;
            _borrowedPipelineShader = shader;
            _borrowedPipelineLayoutKey = layoutKey;

            // ここから先は cache が所有する。失敗時 cleanup の対象から外す。
            pipeline = null;
            pipelineLayout = null;
            bindGroupLayout = null;
        }
        finally
        {
            if (module is not null) WGPU.wgpuShaderModuleRelease(module);
            if (pipeline is not null) WGPU.wgpuRenderPipelineRelease(pipeline);
            if (pipelineLayout is not null) WGPU.wgpuPipelineLayoutRelease(pipelineLayout);
            if (bindGroupLayout is not null) WGPU.wgpuBindGroupLayoutRelease(bindGroupLayout);
            Marshal.FreeCoTaskMem(codePtr);
            Marshal.FreeCoTaskMem(vsEntry);
            Marshal.FreeCoTaskMem(fsEntry);
        }
    }

    private void BuildIndexBuffer(in RenderContext context, int quadCount)
    {
        _indexCount = (uint)(quadCount * 6);
        var indices = new uint[_indexCount];
        for (int q = 0; q < quadCount; q++)
        {
            uint b = (uint)(q * 4);
            int o = q * 6;
            indices[o + 0] = b + 0;
            indices[o + 1] = b + 1;
            indices[o + 2] = b + 2;
            indices[o + 3] = b + 0;
            indices[o + 4] = b + 2;
            indices[o + 5] = b + 3;
        }

        ulong size = (ulong)(_indexCount * sizeof(uint));
        var bufferDescriptor = new BufferDescriptor { Usage = BufferUsage.Index | BufferUsage.CopyDst, Size = size };
        _indexBuffer = WGPU.wgpuDeviceCreateBuffer(context.Device, &bufferDescriptor);
        context.Resources.Track(this, () => { if (_indexBuffer is not null) { WGPU.wgpuBufferRelease(_indexBuffer); _indexBuffer = null; } });
        fixed (uint* p = indices)
        {
            WGPU.wgpuQueueWriteBuffer(context.Queue, _indexBuffer, 0, p, (nuint)size);
        }
    }

    private void BuildUniformAndBindGroup(in RenderContext context, TextureAsset?[] resolvedSlots)
    {
        var bufferDescriptor = new BufferDescriptor { Usage = BufferUsage.Uniform | BufferUsage.CopyDst, Size = UniformSize };
        _uniformBuffer = WGPU.wgpuDeviceCreateBuffer(context.Device, &bufferDescriptor);
        context.Resources.Track(this, () => { if (_uniformBuffer is not null) { WGPU.wgpuBufferRelease(_uniformBuffer); _uniformBuffer = null; } });

        var paramBufferDescriptor = new BufferDescriptor { Usage = BufferUsage.Uniform | BufferUsage.CopyDst, Size = (ulong)ParamBufferSize };
        _paramBuffer = WGPU.wgpuDeviceCreateBuffer(context.Device, &paramBufferDescriptor);
        context.Resources.Track(this, () => { if (_paramBuffer is not null) { WGPU.wgpuBufferRelease(_paramBuffer); _paramBuffer = null; } });

        if (_usesSimulationHistory)
        {
            var historyBufferDescriptor = new BufferDescriptor { Usage = BufferUsage.Storage | BufferUsage.CopyDst, Size = HistoryBufferSize };
            _historyBuffer = WGPU.wgpuDeviceCreateBuffer(context.Device, &historyBufferDescriptor);
            context.Resources.Track(this, () => { if (_historyBuffer is not null) { WGPU.wgpuBufferRelease(_historyBuffer); _historyBuffer = null; } });
        }

        ShaderTextureSlot[] slots = _resolvedShader?.TextureSlots ?? [];
        _slotTextures = new Texture*[slots.Length];
        _slotTextureViews = new TextureView*[slots.Length];

        int fixedEntryCount = _usesSimulationHistory ? 4 : 3;
        var entries = stackalloc BindGroupEntry[fixedEntryCount + slots.Length];
        entries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformBuffer, Offset = 0, Size = UniformSize };
        entries[1] = new BindGroupEntry { Binding = 2, Sampler = context.DefaultSampler };
        entries[2] = new BindGroupEntry { Binding = 3, Buffer = _paramBuffer, Offset = 0, Size = (ulong)ParamBufferSize };
        if (_usesSimulationHistory)
            entries[3] = new BindGroupEntry { Binding = HistoryBinding, Buffer = _historyBuffer, Offset = 0, Size = HistoryBufferSize };
        for (int i = 0; i < slots.Length; i++)
        {
            TextureView* slotView = context.DefaultTextureView;
            if (i < resolvedSlots.Length && resolvedSlots[i] is { } slotTexture)
            {
                _slotTextures[i] = SpriteRenderSupport.CreateTexture(in context, slotTexture);
                _slotTextureViews[i] = WGPU.wgpuTextureCreateView(_slotTextures[i], null);
                slotView = _slotTextureViews[i];
            }

            entries[fixedEntryCount + i] = new BindGroupEntry { Binding = (uint)slots[i].Binding, TextureView = slotView };
        }
        context.Resources.Track(this, () =>
        {
            foreach (TextureView* view in _slotTextureViews) if (view is not null) WGPU.wgpuTextureViewRelease(view);
            foreach (Texture* texture in _slotTextures) if (texture is not null) WGPU.wgpuTextureRelease(texture);
            _slotTextureViews = [];
            _slotTextures = [];
        });
        var bindGroupDescriptor = new BindGroupDescriptor { Layout = _bindGroupLayout, EntryCount = (uint)(fixedEntryCount + slots.Length), Entries = entries };
        _bindGroup = WGPU.wgpuDeviceCreateBindGroup(context.Device, &bindGroupDescriptor);
        context.Resources.Track(this, () => { if (_bindGroup is not null) { WGPU.wgpuBindGroupRelease(_bindGroup); _bindGroup = null; } });
    }

    private void UpdateHistory(float localTime, Matrix4x4 worldTransform)
    {
        if (!_usesSimulationHistory)
        {
            if (_historyCount != 0 || _lastHistoryObservationTime >= 0f)
                ResetHistoryState();
            return;
        }

        float historyDuration = ResolveHistoryDuration();
        // Reserve one interval for the lag of the newest sample so history covers the full particle lifetime.
        float interval = Math.Max(historyDuration / (HistoryCapacity - 2), 1f / 240f);
        if (_historyCount == 0 || _lastHistoryObservationTime < 0f || localTime < _lastHistoryObservationTime ||
            MathF.Abs(interval - _historyInterval) > 0.00001f)
        {
            ResetHistory(localTime, worldTransform, interval);
            return;
        }

        float elapsed = localTime - _lastHistoryObservationTime;
        if (elapsed > 0f && _nextHistorySampleTime <= localTime)
        {
            int sampleCount = (int)MathF.Floor((localTime - _nextHistorySampleTime) / interval) + 1;
            int skippedSamples = Math.Max(0, sampleCount - HistoryCapacity);
            float sampleTime = _nextHistorySampleTime + skippedSamples * interval;
            for (int i = skippedSamples; i < sampleCount; i++, sampleTime += interval)
            {
                float amount = Math.Clamp((sampleTime - _lastHistoryObservationTime) / elapsed, 0f, 1f);
                AppendHistorySample(
                    sampleTime,
                    Matrix4x4.Lerp(_lastHistoryObservation, worldTransform, amount),
                    _lastHistoryEmissionEnabled);
            }

            _nextHistorySampleTime += sampleCount * interval;
        }

        _lastHistoryObservationTime = localTime;
        _lastHistoryObservation = worldTransform;
        _lastHistoryEmissionEnabled = Emit;
        WriteHistoryHeader();
    }

    private float ResolveHistoryDuration()
    {
        if (float.IsFinite(SimulationHistoryDuration) && SimulationHistoryDuration > 0f)
            return SimulationHistoryDuration;

        ShaderParam[] definitions = _resolvedShader?.Params ?? [];
        float[] values = Params ?? [];
        int valueOffset = 0;
        foreach (ShaderParam definition in definitions)
        {
            if (definition.Name.Equals("duration", StringComparison.OrdinalIgnoreCase) &&
                valueOffset < values.Length)
            {
                float duration = values[valueOffset];
                if (float.IsFinite(duration) && duration > 0f)
                    return duration;
            }

            valueOffset += definition.Kind == ShaderParamKind.Color ? 4 : 1;
        }

        return 2f;
    }

    private void ResetHistory(float localTime, Matrix4x4 worldTransform, float interval)
    {
        Array.Clear(_historyData);
        _historyCount = 0;
        _historyWriteIndex = -1;
        _historyInterval = interval;
        _lastHistoryObservationTime = localTime;
        _lastHistoryObservation = worldTransform;
        _lastHistoryEmissionEnabled = Emit;
        _nextHistorySampleTime = localTime + interval;
        AppendHistorySample(localTime, worldTransform, _lastHistoryEmissionEnabled);
        WriteHistoryHeader();
    }

    private void AppendHistorySample(float time, Matrix4x4 transform, bool emissionEnabled)
    {
        _historyWriteIndex = (_historyWriteIndex + 1) % HistoryCapacity;
        _historyCount = Math.Min(_historyCount + 1, HistoryCapacity);

        int offset = HistoryHeaderFloatCount + _historyWriteIndex * HistorySampleFloatCount;
        fixed (float* history = _historyData)
            *(Matrix4x4*)(history + offset) = transform;
        _historyData[offset + 16] = time;
        _historyData[offset + 17] = emissionEnabled ? 1f : 0f;
        _historyData[offset + 18] = 0f;
        _historyData[offset + 19] = 0f;
    }

    private void WriteHistoryHeader()
    {
        _historyData[0] = _historyCount;
        _historyData[1] = Math.Max(_historyWriteIndex, 0);
        _historyData[2] = _historyInterval;
        _historyData[3] = 0f;
    }

    private void UploadHistory(Queue* queue)
    {
        if (_historyBuffer is null) return;
        fixed (float* history = _historyData)
            WGPU.wgpuQueueWriteBuffer(queue, _historyBuffer, 0, history, HistoryBufferSize);
    }

    private void ResetHistoryState()
    {
        Array.Clear(_historyData);
        _historyCount = 0;
        _historyWriteIndex = -1;
        _historyInterval = 0f;
        _lastHistoryObservationTime = -1f;
        _nextHistorySampleTime = 0f;
        _lastHistoryObservation = default;
        _lastHistoryEmissionEnabled = false;
    }

    private void ReleaseResources()
    {
        _pool?.ReleaseAll(this);
        _globals.Release();

        if (_borrowedPipelineShader is not null)
            _cache?.ReleasePipeline(_borrowedPipelineShader, GpuPipelineVariant.Effect, _borrowedPipelineLayoutKey);

        _borrowedPipelineShader = null;
        _borrowedPipelineLayoutKey = 0;
        _pipeline = null;
        _pipelineLayout = null;
        _bindGroupLayout = null;
        _indexCount = 0;
        _realizedShader = null;
        _realizedSlotTextures = [];
        _realizedQuadCount = -1;
        _usesSimulationHistory = false;
        _resourcesBuilt = false;
    }
}

public sealed partial class EffectComponent
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
