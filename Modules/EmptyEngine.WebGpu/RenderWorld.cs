namespace EmptyEngine.WebGpu;

/// <summary>1 つの runtime world に属する描画対象・カメラ・Canvas の集合</summary>
/// <remarks>シーン世界ごとに独立したインスタンスを使う。</remarks>
public sealed class RenderWorld
{
    private readonly Registered<ISpriteRenderer> _renderers = new();
    private readonly Registered<CameraComponent> _cameras = new();
    private readonly Registered<CanvasScalerComponent> _canvasScalers = new();

    /// <summary>スプライトとシェーダのアニメーションに使う、この世界の累積時間（秒）</summary>
    public double Time { get; private set; }

    /// <summary>この世界のシェーダが名前で拾う共有値</summary>
    public ShaderGlobals Globals { get; } = new();

    /// <summary>描画とは独立してアニメーションの時刻を指定秒数だけ進める</summary>
    /// <remarks>ゲームの更新時に呼ぶ。ポーズ中は呼ばないか 0 を渡すと、描画を続けたまま時刻を止められる。</remarks>
    public void AdvanceTime(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds), "Time step must be finite and non-negative.");

        Time += deltaSeconds;
    }

    public void RegisterRenderer(ISpriteRenderer renderer) => _renderers.Add(renderer);

    public void UnregisterRenderer(ISpriteRenderer renderer) => _renderers.Remove(renderer);

    /// <summary>登録順のままの描画対象</summary>
    /// <remarks>描画中の登録・解除に耐える必要がある呼び出し側は、走査の前に自分の入れ物へ写す</remarks>
    public IReadOnlyList<ISpriteRenderer> Renderers => _renderers.Items;

    public void RegisterCamera(CameraComponent camera) => _cameras.Add(camera);

    public void UnregisterCamera(CameraComponent camera) => _cameras.Remove(camera);

    public CameraComponent? ActiveCamera
    {
        get
        {
            IReadOnlyList<CameraComponent> cameras = _cameras.Items;
            return cameras.Count > 0 ? cameras[0] : null;
        }
    }

    /// <summary>一時的な描画視点。シーンのカメラ登録順や保存データには影響しない。</summary>
    public IRenderCamera? CameraOverride { get; set; }

    /// <summary>現在の描画とワールドのヒットテストで使うカメラ。</summary>
    public IRenderCamera? ViewCamera => CameraOverride ?? ActiveCamera;

    public void RegisterCanvasScaler(CanvasScalerComponent scaler) => _canvasScalers.Add(scaler);

    public void UnregisterCanvasScaler(CanvasScalerComponent scaler) => _canvasScalers.Remove(scaler);

    /// <summary>直近の描画で使った表示面の縦横比（幅÷高さ）。</summary>
    public float Aspect { get; private set; } = 1f;

    public void RecomputeCanvases(float aspect)
    {
        Aspect = aspect;
        foreach (CanvasScalerComponent scaler in _canvasScalers.Items) scaler.Recompute(aspect);
    }

    /// <summary>登録順を保ったまま、登録も解除も定数時間で行う集合</summary>
    /// <remarks>登録解除してから同じ要素を再登録すると、その要素は末尾になる。</remarks>
    private sealed class Registered<T> where T : class
    {
        private readonly List<T> _order = [];
        private readonly HashSet<T> _live = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<T> _vacated = new(ReferenceEqualityComparer.Instance);

        /// <summary>空席を詰めたうえでの登録順の一覧</summary>
        public IReadOnlyList<T> Items
        {
            get
            {
                if (_vacated.Count > 0) Compact();
                return _order;
            }
        }

        public void Add(T item)
        {
            if (_live.Contains(item)) return;

            // 再登録時に空席が残らないよう、登録前に詰める。
            if (_vacated.Remove(item)) Compact();

            _live.Add(item);
            _order.Add(item);
        }

        public void Remove(T item)
        {
            if (_live.Remove(item)) _vacated.Add(item);
        }

        private void Compact()
        {
            _order.RemoveAll(item => !_live.Contains(item));
            _vacated.Clear();
        }
    }
}
