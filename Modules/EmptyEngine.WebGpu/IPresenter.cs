namespace EmptyEngine.WebGpu;

/// <summary>提示（present）の platform 非依存な口</summary>
public interface IPresenter : IDisposable
{
    /// <summary>不透明の描画が終わった時点の画面を、以降のシェーダが読める形で残すか</summary>
    /// <remarks>残した画面は <see cref="RenderContext.OpaqueTextureView"/> から拾う。</remarks>
    bool CaptureOpaqueTexture { get; set; }

    /// <summary>フレームバッファのリサイズへの追従</summary>
    void Resize(int width, int height);

    /// <summary>世界の時刻を指定秒数だけ進めてから、1 フレームを描画して present する</summary>
    /// <remarks><see cref="RenderWorld.AdvanceTime"/> を自前で呼ぶ場合は <see cref="RenderFrame(RenderWorld)"/> を使う。</remarks>
    void RenderFrame(double delta, RenderWorld world);

    /// <summary>世界の現在時刻を使って 1 フレームを描画して present する。時刻は進めない。</summary>
    void RenderFrame(RenderWorld world);
}
