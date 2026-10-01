using System.Numerics;
using EmptyEngine.ObjectModel;

namespace EmptyEngine.WebGpu;

/// <summary>指定した design 解像度の矩形を画面へどう収めるか</summary>
public enum CanvasScaleMode
{
    /// <summary>短辺基準で design 全体を画面内へ収める（レターボックス）</summary>
    Fit,

    /// <summary>長辺基準で画面を埋める（はみ出しはクリップ）</summary>
    Fill,

    /// <summary>横を画面幅へ合わせる等倍</summary>
    MatchWidth,

    /// <summary>縦を画面高さへ合わせる等倍</summary>
    MatchHeight,

    /// <summary>縦横を独立に引き伸ばして画面ぴったりに埋める</summary>
    Stretch,
}

/// <summary>スクリーン空間オーバーレイの Canvas ルート</summary>
/// <remarks>カメラには依存せず、配下のスプライトやメッシュはこの Canvas の <see cref="Projection"/> で描かれる。どのシーンにも複数置ける</remarks>
public sealed class CanvasScalerComponent : ILifecycleAttachable
{
    private readonly RenderWorld _renderWorld;

    public CanvasScalerComponent(RenderWorld renderWorld) => _renderWorld = renderWorld;

    /// <summary>基準となる design 解像度の幅（design 単位）</summary>
    public float ReferenceWidth { get; set; } = 1920f;

    /// <summary>基準となる design 解像度の高さ（design 単位）</summary>
    public float ReferenceHeight { get; set; } = 1080f;

    /// <summary>描画する最近距離（design 単位。負値で Z=0 より手前も含める）</summary>
    public float NearPlane { get; set; } = -1000f;

    /// <summary>描画する最遠距離（design 単位）</summary>
    public float FarPlane { get; set; } = 1000f;

    /// <summary>design 矩形を画面へどう収めるか</summary>
    public CanvasScaleMode Mode { get; set; } = CanvasScaleMode.Fit;

    /// <summary>複数 Canvas を重ねたときの描画優先度</summary>
    public int SortOrder { get; set; } = 0;

    private Matrix4x4 _projection = Matrix4x4.Identity;

    /// <summary>design 空間 → クリップ空間の射影行列</summary>
    internal Matrix4x4 Projection => _projection;

    public void OnCreated(IObject owner)
    {
    }

    public void OnDeserialized(IObject owner)
    {
        _renderWorld.RegisterCanvasScaler(this);
    }

    public void OnDestroy(IObject owner)
    {
        _renderWorld.UnregisterCanvasScaler(this);
    }

    /// <summary>現在のアスペクト比からの射影の組み立て</summary>
    internal void Recompute(float aspect)
    {
        float a = MathF.Max(aspect, 1e-4f);
        float dw = MathF.Max(ReferenceWidth, 1e-4f);
        float dh = MathF.Max(ReferenceHeight, 1e-4f);

        float pW = a / dw;
        float pH = 1f / dh;

        float sx, sy;
        if (Mode == CanvasScaleMode.Stretch)
        {
            sx = 2f / dw;
            sy = 2f / dh;
        }
        else
        {
            float p = Mode switch
            {
                CanvasScaleMode.MatchWidth => pW,
                CanvasScaleMode.MatchHeight => pH,
                CanvasScaleMode.Fill => MathF.Max(pW, pH),
                _ => MathF.Min(pW, pH),
            };
            sx = 2f * p / a;
            sy = 2f * p;
        }

        float near = float.IsFinite(NearPlane) ? NearPlane : -1000f;
        float far = float.IsFinite(FarPlane) && FarPlane > near ? FarPlane : near + 1f;
        // XY のスケーリングを保ったまま、前後関係を WebGPU の深度範囲 0..1 に写す。
        _projection = CoordinateConvention.CreateOrthographic(2f / sx, 2f / sy, near, far);
    }
}
