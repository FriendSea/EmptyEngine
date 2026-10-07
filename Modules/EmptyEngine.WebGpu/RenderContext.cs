using System.Numerics;
using WgpuBuffer = EmptyEngine.WebGpu.Buffer;

namespace EmptyEngine.WebGpu;

/// <summary>1 フレームの描画に必要な wgpu ハンドルと共有リソース</summary>
public unsafe ref struct RenderContext
{
    public Device* Device;
    public Queue* Queue;

    /// <summary>確保した wgpu ハンドルの持ち主別追跡（取りこぼし時の安全網）</summary>
    public GpuResourcePool Resources;

    /// <summary>アセット単位で共有するパイプラインとテクスチャ</summary>
    public GpuAssetCache Assets;
    public RenderPassEncoder* Pass;
    public WgpuBuffer* QuadVertexBuffer;
    public uint QuadVertexCount;

    /// <summary>テクスチャ未設定スプライト用の 1x1 白テクスチャビュー</summary>
    public TextureView* DefaultTextureView;

    /// <summary>全スプライト共用のサンプラ（線形補間・端クランプ）</summary>
    public Sampler* DefaultSampler;

    /// <summary>全メッシュ共用のサンプラ（線形補間・繰り返し）</summary>
    public Sampler* RepeatSampler;

    /// <summary>不透明の描画が終わった時点の画面</summary>
    /// <remarks>Presenter が残していない間は 1x1 の白。</remarks>
    public TextureView* OpaqueTextureView;

    /// <summary>画面のアスペクト比を補正する射影行列</summary>
    public Matrix4x4 Projection;

    /// <summary>ワールド座標からカメラのビュー空間へ変換する行列</summary>
    public Matrix4x4 ViewMatrix;

    /// <summary>ビュー空間からクリップ空間へ変換する投影行列</summary>
    public Matrix4x4 ProjectionMatrix;

    /// <summary><see cref="RenderWorld.AdvanceTime"/> で進めた、この世界のアニメーション時刻（秒）</summary>
    public float Time;

    /// <summary><c>group(1)</c>（世界で共有する値）のレイアウト。自前でパイプラインを組むコンポーネントは、これをパイプラインレイアウトの 1 番目へ置く</summary>
    /// <remarks>bind group はレンダラーがパスの頭で結ぶ。</remarks>
    public BindGroupLayout* GlobalsLayout;

    /// <summary><c>group(3)</c>（メインテクスチャとサンプラー）のレイアウト</summary>
    public BindGroupLayout* MainTextureLayout;

    /// <summary>描画先（swapchain）のテクスチャフォーマット</summary>
    public TextureFormat TargetFormat;

    /// <summary>レンダーパスの深度アタッチメントのフォーマット</summary>
    public TextureFormat DepthFormat;
}
