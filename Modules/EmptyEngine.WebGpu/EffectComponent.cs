using System.Numerics;
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
    private const int HistoryCapacity = MaterialRenderSupport.HistoryCapacity;
    private const int HistoryHeaderFloatCount = MaterialRenderSupport.HistoryHeaderFloatCount;
    private const int HistorySampleFloatCount = MaterialRenderSupport.HistorySampleFloatCount;
    private const int HistoryBufferFloatCount = MaterialRenderSupport.HistoryBufferFloatCount;
    private const int HistoryBufferSize = MaterialRenderSupport.HistoryBufferSize;
    private static readonly Regex HistoryBindingRegex = new(
        @"@group\s*\(\s*0\s*\)\s*@binding\s*\(\s*31\s*\)", RegexOptions.Compiled);

    private readonly RenderWorld _renderWorld;
    private readonly float[] _historyData = new float[HistoryBufferFloatCount];

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
    private int _realizedQuadCount = -1;
    private bool _usesSimulationHistory;
    private GpuResourcePool? _pool;
    private float _startTime = -1f;
    private IObject? _owner;

    private WgpuBuffer* _indexBuffer;
    private uint _indexCount;
    private readonly MaterialBinding _binding = new();
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

    /// <summary>頂点シェーダが宣言するパラメータの値</summary>
    public float[] Params { get; set; } = [];

    /// <inheritdoc/>
    public IObject? Owner => _owner;

    /// <inheritdoc/>
    public int RenderLayer => SpriteRenderSupport.GetRenderLayer(_owner) + SortOrder;

    /// <inheritdoc/>
    public bool IsTransparent => RenderQueue >= ShaderRenderQueue.Transparent;

    /// <inheritdoc/>
    public int RenderQueue => _resolvedMaterial?.Queue ?? ShaderRenderQueue.Transparent;

    public void OnCreated(IObject owner)
    {
    }

    public void OnDeserialized(IObject owner)
    {
        _owner = owner;

        _startTime = -1f;
        ResetHistoryState();

        if (Color == default) Color = GraphicsColor.White;

        _renderWorld.RegisterRenderer(this);

        ApplyShaders();
    }

    /// <summary>頂点シェーダの決定と、それに合わせた <see cref="Params"/> の枠の更新</summary>
    private void ApplyShaders()
    {
        _vertexShader = VertexShaderSelection.Select(
            MaterialVertexKind.Effect, _resolvedShader, _resolvedMaterial?.ResolvedShader, _builtinShader);
        Params = ShaderParamsHost.Reconcile(_vertexShader?.VertexParams, Params);
    }

    public void OnDestroy(IObject owner)
    {
        _renderWorld.UnregisterRenderer(this);
        ReleaseIndexBuffer();
        _binding.Release();
        _owner = null;
        _resolvedShader = null;
        _resolvedMaterial = null;
        _resolvedMainTexture = null;
        _vertexShader = null;
        ResetHistoryState();
    }

    public void Render(in RenderContext context)
    {
        if (QuadCount <= 0) return;

        if (!_binding.Matches(_vertexShader, _resolvedMaterial, EffectiveMainTexture, Params?.Length ?? 0))
        {
            // 履歴を読むのは頂点・フラグメントのどちらでもよいので、両方のソースを見る。
            _usesSimulationHistory = UsesHistory(_vertexShader) || UsesHistory(_resolvedMaterial?.ResolvedShader);
            _binding.Build(
                in context, MaterialVertexKind.Effect, UniformSize, _vertexShader, _resolvedMaterial,
                EffectiveMainTexture, context.DefaultSampler, Params?.Length ?? 0, _usesSimulationHistory);
        }

        if (!_binding.IsReady) return;

        if (_realizedQuadCount != QuadCount)
        {
            ReleaseIndexBuffer();
            BuildIndexBuffer(in context, QuadCount);
            _realizedQuadCount = QuadCount;
        }

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
        WGPU.wgpuQueueWriteBuffer(context.Queue, _binding.UniformBuffer, 0, u, UniformSize);

        _binding.WriteParams(in context, Params);
        _binding.Bind(in context);
        WGPU.wgpuRenderPassEncoderSetIndexBuffer(context.Pass, _indexBuffer, IndexFormat.Uint32, 0, (ulong)_indexCount * sizeof(uint));
        WGPU.wgpuRenderPassEncoderDrawIndexed(context.Pass, _indexCount, 1, 0, 0, 0);
    }

    private static bool UsesHistory(ShaderAsset? shader)
        => shader is not null && HistoryBindingRegex.IsMatch(SpriteRenderSupport.ReadShaderSource(shader));

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
        _pool = context.Resources;
        context.Resources.Track(this, () => { if (_indexBuffer is not null) { WGPU.wgpuBufferRelease(_indexBuffer); _indexBuffer = null; } });
        fixed (uint* p = indices)
        {
            WGPU.wgpuQueueWriteBuffer(context.Queue, _indexBuffer, 0, p, (nuint)size);
        }
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

        ShaderParam[] definitions = _vertexShader?.VertexParams ?? [];
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
        if (_binding.HistoryBuffer is null) return;
        fixed (float* history = _historyData)
            WGPU.wgpuQueueWriteBuffer(queue, _binding.HistoryBuffer, 0, history, HistoryBufferSize);
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

    private void ReleaseIndexBuffer()
    {
        _pool?.ReleaseAll(this);
        _indexCount = 0;
        _realizedQuadCount = -1;
    }
}

public sealed partial class EffectComponent
{
    /// <summary>フィールド値が入るたびの参照の解決（空のマテリアルは同梱の既定へ）</summary>
    public async ValueTask OnResolveAssetsAsync(IAssetResolver resolver)
    {
        await ResolveAssetFieldsAsync(resolver);
        _resolvedMaterial ??= await resolver.ResolveAsync(new AssetReference<MaterialAsset>(BuiltinMaterials.Transparent));
        _builtinShader = await resolver.ResolveAsync(new AssetReference<ShaderAsset>(BuiltinShaders.Effect));
        ApplyShaders();
    }
}
