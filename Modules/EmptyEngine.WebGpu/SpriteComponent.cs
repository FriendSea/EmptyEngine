using System.Numerics;
using System.Runtime.InteropServices;
using EmptyEngine.Core;
using EmptyEngine.Generators;
using EmptyEngine.Graphics;
using EmptyEngine.ObjectModel;
using WgpuBuffer = EmptyEngine.WebGpu.Buffer;
using GraphicsColor = EmptyEngine.Graphics.Color;

namespace EmptyEngine.WebGpu;

/// <summary>テクスチャ付き quad を 1 枚描くスプライト</summary>
public sealed unsafe partial class SpriteComponent : ISpriteRenderer, IShaderParamsHost
{
    private readonly RenderWorld _renderWorld;
    private const int UniformSize = 112;
    private static readonly ShaderTextureSlot BaseTextureSlot = new() { Name = "Base Texture", Binding = 1 };

    /// <summary>単位 quad の四隅（周回順）</summary>
    private static readonly Vector2[] QuadCorners =
        [new(-0.5f, -0.5f), new(0.5f, -0.5f), new(0.5f, 0.5f), new(-0.5f, 0.5f)];

    /// <summary>描画するスプライト（<see cref="SpriteAsset"/>）の解決済み実体</summary>
    [ResolveAsset("Asset")]
    private SpriteAsset? _resolvedSprite;

    /// <summary>描画に使う WGSL シェーダの解決済み実体</summary>
    [ResolveAsset("Shader")]
    private ShaderAsset? _resolvedShader;

    private TextureAsset? _resolvedTexture;
    private TextureAsset?[] _resolvedSlotTextures = [];
    private TextureAsset?[] _realizedSlotTextures = [];
    private TextureAsset? _realizedTexture;
    private ShaderAsset? _realizedShader;
    private Rect _region = Rect.Full;
    private Vector2 _pivot = new(0.5f, 0.5f);
    private float _scale = 1f;
    private float _animationSpeed;
    private int _frameCount = 1;
    private bool _resourcesBuilt;
    private GpuResourcePool? _pool;
    private IObject? _owner;
    private SpriteAsset? _appliedSprite;
    private WgpuBuffer* _uniformBuffer;
    private WgpuBuffer* _paramBuffer;
    private BindGroup* _bindGroup;
    private RenderPipeline* _pipeline;
    private PipelineLayout* _pipelineLayout;
    private BindGroupLayout* _bindGroupLayout;
    private Texture*[] _slotTextures = [];
    private TextureView*[] _slotTextureViews = [];
    private GpuAssetCache? _cache;
    private TextureAsset? _borrowedTexture;
    private ShaderAsset? _borrowedPipelineShader;
    private int _borrowedPipelineLayoutKey;
    private ShaderDepthState _shaderDepth = ShaderDepthState.Transparent;
    private int _renderQueue = ShaderRenderQueue.Transparent;
    private readonly ShaderGlobalsBinding _globals = new();

    public SpriteComponent(RenderWorld renderWorld) => _renderWorld = renderWorld;

    /// <summary>スプライトへ乗算する頂点カラー（RGBA 各成分 0..1）</summary>
    public GraphicsColor Color { get; set; } = GraphicsColor.White;

    /// <summary>シェーダのユーザ定義パラメータの値</summary>
    public float[] Params { get; set; } = [];

    /// <summary>シェーダが宣言した追加テクスチャスロットへ貼るテクスチャ参照</summary>
    public AssetReference<TextureAsset>[] Textures { get; set; } = [];

    /// <summary>現在解決済みのシェーダ</summary>
    public ShaderAsset? ResolvedShader => _resolvedShader;

    /// <inheritdoc/>
    public ShaderTextureSlot[] ResolveTextureSlots(ShaderAsset? resolvedShader) =>
        resolvedShader?.TextureSlots?.Where(slot => slot.Binding != BaseTextureSlot.Binding).ToArray() ?? [];

    /// <inheritdoc/>
    public IObject? Owner => _owner;

    /// <inheritdoc/>
    public bool IsTransparent => _renderQueue >= ShaderRenderQueue.Transparent;

    /// <inheritdoc/>
    public int RenderQueue => _renderQueue;

    private int ParamBufferSize => Math.Max(16, (((Params?.Length ?? 0) * sizeof(float)) + 15) & ~15);

    /// <summary>実行時に生成したテクスチャの直貼り</summary>
    public void SetTexture(TextureAsset? texture)
    {
        _appliedSprite = null;
        _resolvedTexture = texture;
        _region = Rect.Full;
        _pivot = new Vector2(0.5f, 0.5f);
        _animationSpeed = 0f;
        _frameCount = 1;
        _scale = texture is { Height: > 0 } ? 1f / texture.Height : 1f;
    }

    /// <summary>解決済み <see cref="SpriteAsset"/> の直貼り</summary>
    public void SetSprite(SpriteAsset? sprite)
    {
        if (ReferenceEquals(sprite, _appliedSprite)) return;
        ApplySprite(sprite);
    }

    public void OnCreated(IObject owner)
    {
    }

    public void OnDeserialized(IObject owner)
    {
        _owner = owner;

        _renderWorld.RegisterRenderer(this);

        ApplySprite(_resolvedSprite);

        Params ??= [];
        Textures ??= [];
        if (_resolvedShader is not null)
            Params = ShaderParamsHost.Reconcile(_resolvedShader.Params, Params);

        _shaderDepth = _resolvedShader is null
            ? ShaderDepthState.Transparent
            : ShaderBlend.ResolveDepth(_resolvedShader, ShaderDepthState.Transparent);
        _renderQueue = _resolvedShader is null
            ? ShaderRenderQueue.Transparent
            : ShaderBlend.ResolveRenderQueue(_resolvedShader, ShaderRenderQueue.Transparent);

        if (_resolvedShader is null && Color == default) Color = GraphicsColor.White;
    }

    /// <summary>解決済み <see cref="SpriteAsset"/> からの矩形・スケール・アニメの取り込み</summary>
    private void ApplySprite(SpriteAsset? sprite)
    {
        _appliedSprite = sprite;
        if (sprite is not null)
        {
            _region = sprite.Region;
            _pivot = sprite.Pivot;
            _scale = sprite.Scale;
            _animationSpeed = sprite.AnimationSpeed;
            _frameCount = sprite.FrameCount;
            _resolvedTexture = sprite.ResolvedTexture;
        }
        else
        {
            _region = Rect.Full;
            _pivot = new Vector2(0.5f, 0.5f);
            _scale = 1f;
            _animationSpeed = 0f;
            _frameCount = 1;
            _resolvedTexture = null;
        }
    }

    public void OnDestroy(IObject owner)
    {
        _renderWorld.UnregisterRenderer(this);
        ReleaseResources();
        _owner = null;
        _appliedSprite = null;
        _resolvedSprite = null;
        _resolvedTexture = null;
        _realizedTexture = null;
        _resolvedShader = null;
        _realizedShader = null;
        _resolvedSlotTextures = [];
        _realizedSlotTextures = [];
        _shaderDepth = ShaderDepthState.Transparent;
        _renderQueue = ShaderRenderQueue.Transparent;
    }

    public void Render(in RenderContext context)
    {
        EnsureResources(in context);

        Matrix4x4 transform = BuildLocalMatrix() * SpriteRenderSupport.ReadTransform(_owner) * context.Projection;

        Span<float> uniform = stackalloc float[UniformSize / sizeof(float)];
        MemoryMarshal.Write(MemoryMarshal.AsBytes(uniform), in transform);
        uniform[16] = _region.X;
        uniform[17] = _region.Y;
        uniform[18] = _region.Width;
        uniform[19] = _region.Height;
        uniform[20] = context.Time;
        uniform[21] = _animationSpeed;
        uniform[22] = _frameCount;
        uniform[23] = 0f;
        uniform[24] = Color.R;
        uniform[25] = Color.G;
        uniform[26] = Color.B;
        uniform[27] = Color.A;
        fixed (float* u = uniform)
        {
            WGPU.wgpuQueueWriteBuffer(context.Queue, _uniformBuffer, 0, u, UniformSize);
        }

        if (_paramBuffer is not null && Params is { Length: > 0 })
        {
            fixed (float* p = Params)
                WGPU.wgpuQueueWriteBuffer(context.Queue, _paramBuffer, 0, p, (nuint)(Params.Length * sizeof(float)));
        }

        RenderPipeline* pipeline = _pipeline is not null ? _pipeline : context.QuadPipeline;
        SpriteRenderSupport.DrawQuad(in context, pipeline, _bindGroup, _globals);
    }

    /// <summary>単位 quad（±0.5）をこのスプライトの大きさとピボットへ合わせるローカル変換</summary>
    private Matrix4x4 BuildLocalMatrix()
    {
        float sx = _scale * _region.Width * (_resolvedTexture?.Width ?? 1f);
        float sy = _scale * _region.Height * (_resolvedTexture?.Height ?? 1f);
        float ox = (0.5f - _pivot.X) * sx;
        float oy = (_pivot.Y - 0.5f) * sy;
        return Matrix4x4.CreateScale(sx, sy, 1f) * Matrix4x4.CreateTranslation(ox, oy, 0f);
    }

    /// <summary>正規化スクリーン座標（左上原点・y 下向き）がこのスプライトの上にあるか</summary>
    /// <remarks>直近の描画に使った射影に基づいて判定する。最初の描画前は有効な判定結果を保証しない。</remarks>
    public bool HitTest(Vector2 screen)
    {
        Matrix4x4 projection = SpriteRenderSupport.FindCanvas(_owner) is { } canvas
            ? canvas.Projection
            : _renderWorld.ViewCamera?.BuildViewProjection(_renderWorld.Aspect) ?? Matrix4x4.Identity;
        Matrix4x4 transform = BuildLocalMatrix() * SpriteRenderSupport.ReadTransform(_owner) * projection;

        Span<Vector2> corners = stackalloc Vector2[QuadCorners.Length];
        for (int i = 0; i < QuadCorners.Length; i++)
        {
            Vector4 clip = Vector4.Transform(new Vector4(QuadCorners[i], 0f, 1f), transform);
            if (clip.W <= 0f) return false;
            corners[i] = new Vector2(((clip.X / clip.W) + 1f) * 0.5f, (1f - (clip.Y / clip.W)) * 0.5f);
        }

        // 反転した矩形でも判定できるよう、辺に対する符号の一致を見る。
        float sign = 0f;
        for (int i = 0; i < corners.Length; i++)
        {
            Vector2 edge = corners[(i + 1) % corners.Length] - corners[i];
            Vector2 offset = screen - corners[i];
            float cross = (edge.X * offset.Y) - (edge.Y * offset.X);
            if (cross == 0f) continue;
            if (sign * cross < 0f) return false;
            sign = cross;
        }
        return true;
    }

    private void EnsureResources(in RenderContext context)
    {
        TextureAsset?[] resolvedSlots = _resolvedSlotTextures ?? [];
        if (_resourcesBuilt
            && ReferenceEquals(_realizedTexture, _resolvedTexture)
            && ReferenceEquals(_realizedShader, _resolvedShader)
            && SlotTexturesMatch(_realizedSlotTextures, resolvedSlots)) return;

        if (_resourcesBuilt) ReleaseResources();

        _pool = context.Resources;
        _cache = context.Assets;

        var bufferDescriptor = new BufferDescriptor
        {
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
            Size = UniformSize,
        };
        _uniformBuffer = WGPU.wgpuDeviceCreateBuffer(context.Device, &bufferDescriptor);
        context.Resources.Track(this, () => { if (_uniformBuffer is not null) { WGPU.wgpuBufferRelease(_uniformBuffer); _uniformBuffer = null; } });

        TextureView* textureView = _resolvedTexture is not null
            ? AcquireTexture(in context, _resolvedTexture)
            : context.DefaultTextureView;

        if (_resolvedShader is not null)
        {
            BuildCustomPipeline(in context, _resolvedShader);
            ShaderTextureSlot[] extraSlots = ResolveTextureSlots(_resolvedShader);
            ShaderTextureSlot[] allSlots = [BaseTextureSlot, .. extraSlots];
            Span<int> slotBindings = new int[allSlots.Length];
            Span<nint> slotViews = new nint[allSlots.Length];
            slotBindings[0] = BaseTextureSlot.Binding;
            slotViews[0] = (nint)textureView;

            _slotTextures = new Texture*[extraSlots.Length];
            _slotTextureViews = new TextureView*[extraSlots.Length];
            for (int i = 0; i < extraSlots.Length; i++)
            {
                TextureView* view = context.DefaultTextureView;
                if (i < resolvedSlots.Length && resolvedSlots[i] is { } texture)
                {
                    _slotTextures[i] = SpriteRenderSupport.CreateTexture(in context, texture);
                    _slotTextureViews[i] = WGPU.wgpuTextureCreateView(_slotTextures[i], null);
                    view = _slotTextureViews[i];
                }

                slotBindings[i + 1] = extraSlots[i].Binding;
                slotViews[i + 1] = (nint)view;
            }
            context.Resources.Track(this, () =>
            {
                foreach (TextureView* view in _slotTextureViews) if (view is not null) WGPU.wgpuTextureViewRelease(view);
                foreach (Texture* texture in _slotTextures) if (texture is not null) WGPU.wgpuTextureRelease(texture);
                _slotTextureViews = [];
                _slotTextures = [];
            });

            _bindGroup = MeshRenderSupport.CreateShaderParamBindGroup(
                in context, _bindGroupLayout, _uniformBuffer, UniformSize, context.DefaultSampler,
                _paramBuffer, (ulong)ParamBufferSize, slotBindings, slotViews);
        }
        else
        {
            _shaderDepth = ShaderDepthState.Transparent;
            _renderQueue = ShaderRenderQueue.Transparent;
            _bindGroup = SpriteRenderSupport.CreateTexturedBindGroup(
                in context, context.SpriteLayout, _uniformBuffer, UniformSize, textureView, context.DefaultSampler);
        }
        context.Resources.Track(this, () => { if (_bindGroup is not null) { WGPU.wgpuBindGroupRelease(_bindGroup); _bindGroup = null; } });

        _realizedTexture = _resolvedTexture;
        _realizedShader = _resolvedShader;
        _realizedSlotTextures = resolvedSlots;
        _resourcesBuilt = true;
    }

    private void BuildCustomPipeline(in RenderContext context, ShaderAsset shader)
    {
        _shaderDepth = ShaderBlend.ResolveDepth(shader, ShaderDepthState.Transparent);
        _renderQueue = ShaderBlend.ResolveRenderQueue(shader, ShaderRenderQueue.Transparent);
        _globals.Build(in context, shader, this);

        int layoutKey = ParamBufferSize;
        if (context.Assets.TryAcquirePipeline(
            shader, GpuPipelineVariant.SpriteCustom, layoutKey,
            out _pipeline, out _pipelineLayout, out _bindGroupLayout))
        {
            _borrowedPipelineShader = shader;
            _borrowedPipelineLayoutKey = layoutKey;
        }
        else
        {
            ShaderTextureSlot[] allSlots = [BaseTextureSlot, .. ResolveTextureSlots(shader)];
            BindGroupLayout* bindGroupLayout = null;
            PipelineLayout* pipelineLayout = null;
            RenderPipeline* pipeline = null;
            try
            {
                bindGroupLayout = MeshRenderSupport.CreateShaderParamLayout(
                    in context, UniformSize, (ulong)ParamBufferSize, allSlots);

                var (blendColor, blendAlpha) = ShaderBlend.Resolve(shader);
                pipeline = SpriteRenderSupport.CreateQuadPipeline(
                    in context, SpriteRenderSupport.ReadShaderSource(shader), bindGroupLayout,
                    blendColor, blendAlpha, out pipelineLayout,
                    depthWrite: _shaderDepth.WriteEnabled, depthCompare: _shaderDepth.Compare,
                    globalsLayout: _globals.Layout);

                context.Assets.AddPipeline(
                    shader, GpuPipelineVariant.SpriteCustom, layoutKey, pipeline, pipelineLayout, bindGroupLayout);

                _pipeline = pipeline;
                _pipelineLayout = pipelineLayout;
                _bindGroupLayout = bindGroupLayout;
                _borrowedPipelineShader = shader;
                _borrowedPipelineLayoutKey = layoutKey;

                // ここから先は cache が所有する。
                pipeline = null;
                pipelineLayout = null;
                bindGroupLayout = null;
            }
            finally
            {
                if (pipeline is not null) WGPU.wgpuRenderPipelineRelease(pipeline);
                if (pipelineLayout is not null) WGPU.wgpuPipelineLayoutRelease(pipelineLayout);
                if (bindGroupLayout is not null) WGPU.wgpuBindGroupLayoutRelease(bindGroupLayout);
            }
        }

        // uniform/params buffer と bind group は instance ごとの値を持つので共有しない。
        var paramDescriptor = new BufferDescriptor
        {
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
            Size = (ulong)ParamBufferSize,
        };
        _paramBuffer = WGPU.wgpuDeviceCreateBuffer(context.Device, &paramDescriptor);
        context.Resources.Track(this, () => { if (_paramBuffer is not null) { WGPU.wgpuBufferRelease(_paramBuffer); _paramBuffer = null; } });
    }

    private static bool SlotTexturesMatch(TextureAsset?[]? realized, TextureAsset?[] resolved)
    {
        if (realized is null || realized.Length != resolved.Length) return false;
        for (int i = 0; i < resolved.Length; i++)
            if (!ReferenceEquals(realized[i], resolved[i])) return false;
        return true;
    }

    /// <summary>テクスチャの借用（同じテクスチャのスプライトは 1 枚を共有する）</summary>
    private TextureView* AcquireTexture(in RenderContext context, TextureAsset asset)
    {
        if (!context.Assets.TryAcquireTexture(asset, out TextureView* view))
        {
            Texture* texture = SpriteRenderSupport.CreateTexture(in context, asset);
            view = WGPU.wgpuTextureCreateView(texture, null);
            context.Assets.AddTexture(asset, texture, view);
        }

        _borrowedTexture = asset;
        return view;
    }

    /// <summary>自分の分の解放と、共有資源の返却</summary>
    private void ReleaseResources()
    {
        _pool?.ReleaseAll(this);
        _globals.Release();

        if (_borrowedTexture is not null) _cache?.ReleaseTexture(_borrowedTexture);
        if (_borrowedPipelineShader is not null)
            _cache?.ReleasePipeline(_borrowedPipelineShader, GpuPipelineVariant.SpriteCustom, _borrowedPipelineLayoutKey);

        _borrowedTexture = null;
        _borrowedPipelineShader = null;
        _borrowedPipelineLayoutKey = 0;
        _pipeline = null;
        _pipelineLayout = null;
        _bindGroupLayout = null;
        _realizedTexture = null;
        _realizedShader = null;
        _realizedSlotTextures = [];
        _resourcesBuilt = false;
    }
}

public sealed partial class SpriteComponent
{
    /// <summary>フィールド値が入るたびの参照の解決（シェーダの枠に合わせた <see cref="Textures"/> の枠まで）</summary>
    public async ValueTask OnResolveAssetsAsync(IAssetResolver resolver)
    {
        await ResolveAssetFieldsAsync(resolver);
        Textures ??= [];
        if (_resolvedShader is not null)
            Textures = ShaderParamsHost.ReconcileTextures(ResolveTextureSlots(_resolvedShader), Textures);
        _resolvedSlotTextures = await ShaderParamsHost.ResolveTexturesAsync(Textures, resolver);
    }
}
