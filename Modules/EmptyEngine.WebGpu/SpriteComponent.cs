using System.Numerics;
using System.Runtime.InteropServices;
using EmptyEngine.Core;
using EmptyEngine.Generators;
using EmptyEngine.Graphics;
using EmptyEngine.ObjectModel;
using GraphicsColor = EmptyEngine.Graphics.Color;

namespace EmptyEngine.WebGpu;

/// <summary>テクスチャ付き quad を 1 枚描くスプライト</summary>
public sealed unsafe partial class SpriteComponent : ISpriteRenderer, IShaderParamsHost
{
    private readonly RenderWorld _renderWorld;
    private const int UniformSize = 112;

    /// <summary>単位 quad の四隅（周回順）</summary>
    private static readonly Vector2[] QuadCorners =
        [new(-0.5f, -0.5f), new(0.5f, -0.5f), new(0.5f, 0.5f), new(-0.5f, 0.5f)];

    /// <summary>描画するスプライト（<see cref="SpriteAsset"/>）の解決済み実体</summary>
    [ResolveAsset("Asset")]
    private SpriteAsset? _resolvedSprite;

    /// <summary>頂点シェーダに使う WGSL シェーダの解決済み実体</summary>
    [ResolveAsset("Shader")]
    private ShaderAsset? _resolvedShader;

    /// <summary>描画に使うマテリアルの解決済み実体</summary>
    [ResolveAsset("Material")]
    private MaterialAsset? _resolvedMaterial;

    private ShaderAsset? _builtinShader;
    private ShaderAsset? _vertexShader;
    private TextureAsset? _resolvedTexture;

    /// <summary>描画に使うメインテクスチャ（スプライトが無ければマテリアルのもの）</summary>
    /// <remarks>大きさはスプライトのテクスチャだけから決まり、マテリアルのテクスチャでは変わらない。</remarks>
    private TextureAsset? EffectiveMainTexture => _resolvedTexture ?? _resolvedMaterial?.ResolvedMainTexture;
    private Rect _region = Rect.Full;
    private Vector2 _pivot = new(0.5f, 0.5f);
    private float _scale = 1f;
    private float _animationSpeed;
    private int _frameCount = 1;
    private float _startTime = -1f;
    private IObject? _owner;
    private SpriteAsset? _appliedSprite;
    private readonly MaterialBinding _binding = new();

    public SpriteComponent(RenderWorld renderWorld) => _renderWorld = renderWorld;

    /// <summary>スプライトへ乗算する頂点カラー（RGBA 各成分 0..1）</summary>
    public GraphicsColor Color { get; set; } = GraphicsColor.White;

    /// <summary>頂点シェーダが宣言するパラメータの値</summary>
    public float[] Params { get; set; } = [];

    /// <inheritdoc/>
    public IObject? Owner => _owner;

    /// <inheritdoc/>
    public bool IsTransparent => RenderQueue >= ShaderRenderQueue.Transparent;

    /// <inheritdoc/>
    public int RenderQueue => _resolvedMaterial?.Queue ?? ShaderRenderQueue.Transparent;

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

        _startTime = -1f;
        ApplyShaders();

        if (Shader.IsEmpty && Material.IsEmpty && Color == default) Color = GraphicsColor.White;
    }

    /// <summary>頂点シェーダの決定と、それに合わせた <see cref="Params"/> の枠の更新</summary>
    private void ApplyShaders()
    {
        _vertexShader = VertexShaderSelection.Select(
            MaterialVertexKind.Quad, _resolvedShader, _resolvedMaterial?.ResolvedShader, _builtinShader);
        Params = ShaderParamsHost.Reconcile(_vertexShader?.VertexParams, Params);
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
        _binding.Release();
        _owner = null;
        _appliedSprite = null;
        _resolvedSprite = null;
        _resolvedTexture = null;
        _resolvedShader = null;
        _resolvedMaterial = null;
        _vertexShader = null;
    }

    public void Render(in RenderContext context)
    {
        if (!_binding.Matches(_vertexShader, _resolvedMaterial, EffectiveMainTexture, Params?.Length ?? 0))
        {
            _binding.Build(
                in context, MaterialVertexKind.Quad, UniformSize, _vertexShader, _resolvedMaterial,
                EffectiveMainTexture, context.DefaultSampler, Params?.Length ?? 0);
        }

        if (!_binding.IsReady) return;

        if (_startTime < 0f) _startTime = context.Time;

        Matrix4x4 transform = BuildLocalMatrix() * SpriteRenderSupport.ReadTransform(_owner) * context.Projection;

        Span<float> uniform = stackalloc float[UniformSize / sizeof(float)];
        MemoryMarshal.Write(MemoryMarshal.AsBytes(uniform), in transform);
        uniform[16] = _region.X;
        uniform[17] = _region.Y;
        uniform[18] = _region.Width;
        uniform[19] = _region.Height;
        uniform[20] = context.Time - _startTime;
        uniform[21] = _animationSpeed;
        uniform[22] = _frameCount;
        uniform[23] = 0f;
        uniform[24] = Color.R;
        uniform[25] = Color.G;
        uniform[26] = Color.B;
        uniform[27] = Color.A;
        fixed (float* u = uniform)
        {
            WGPU.wgpuQueueWriteBuffer(context.Queue, _binding.UniformBuffer, 0, u, UniformSize);
        }

        _binding.WriteParams(in context, Params);
        _binding.Bind(in context);
        SpriteRenderSupport.DrawQuadVertices(in context);
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
}

public sealed partial class SpriteComponent
{
    /// <summary>フィールド値が入るたびの参照の解決（空のマテリアルは同梱の既定へ）</summary>
    public async ValueTask OnResolveAssetsAsync(IAssetResolver resolver)
    {
        await ResolveAssetFieldsAsync(resolver);
        _resolvedMaterial ??= await resolver.ResolveAsync(new AssetReference<MaterialAsset>(BuiltinMaterials.Transparent));
        _builtinShader = await resolver.ResolveAsync(new AssetReference<ShaderAsset>(BuiltinShaders.Sprite));
        ApplyShaders();
    }
}
