namespace EmptyEngine.WebGpu;

/// <summary>フレーム内の描画順を決めるキー。</summary>
/// <remarks>同じキーの要素は <see cref="Order"/> に従う登録順で描画する。</remarks>
internal readonly record struct RenderEntry(
    ISpriteRenderer Renderer,
    CanvasScalerComponent? Canvas,
    int Queue,
    int Layer,
    float Depth,
    int Order);

/// <summary>描画キュー順（同じキューは登録順）</summary>
internal sealed class RenderQueueOrder : IComparer<RenderEntry>
{
    public static readonly RenderQueueOrder Instance = new();

    public int Compare(RenderEntry x, RenderEntry y)
    {
        int result = x.Queue.CompareTo(y.Queue);
        return result != 0 ? result : x.Order.CompareTo(y.Order);
    }
}

/// <summary>描画キュー→レイヤ→奥から手前（すべて同じなら登録順）</summary>
internal sealed class TransparentOrder : IComparer<RenderEntry>
{
    public static readonly TransparentOrder Instance = new();

    public int Compare(RenderEntry x, RenderEntry y)
    {
        int result = x.Queue.CompareTo(y.Queue);
        if (result != 0) return result;

        result = x.Layer.CompareTo(y.Layer);
        if (result != 0) return result;

        result = y.Depth.CompareTo(x.Depth);
        return result != 0 ? result : x.Order.CompareTo(y.Order);
    }
}

/// <summary>レイヤ順（同じレイヤは登録順）</summary>
internal sealed class CanvasOrder : IComparer<RenderEntry>
{
    public static readonly CanvasOrder Instance = new();

    public int Compare(RenderEntry x, RenderEntry y)
    {
        int result = x.Layer.CompareTo(y.Layer);
        return result != 0 ? result : x.Order.CompareTo(y.Order);
    }
}
