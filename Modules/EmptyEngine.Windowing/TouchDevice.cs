using System.Runtime.InteropServices;

namespace EmptyEngine.Windowing;

/// <summary>表示面の左上を原点とする接触点（座標は 0..1 の正規化値で、y は下向き）</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct TouchPoint(int Id, float X, float Y);

/// <summary>接触点の供給元</summary>
public interface ITouchSource
{
    /// <summary>現在触れている点の全体</summary>
    ReadOnlySpan<TouchPoint> Points { get; }

    /// <summary>正規化に使った表示面の縦横比（幅÷高さ）</summary>
    /// <remarks>x 座標にこの値を掛けると、x と y を表示面の高さを基準とする同じ尺度で扱える。</remarks>
    float Aspect { get; }
}

/// <summary>ウィンドウが持つタッチ面</summary>
/// <remarks>ウィンドウと同じ寿命を持つ。接触点をどの操作として扱うかは利用側が決める。タッチ非対応のプラットフォームでは <see cref="Points"/> は空になる。</remarks>
public sealed class TouchDevice : ITouchSource
{
    private TouchPoint[] _points = [];
    private int _count;

    /// <summary>現在触れている点の全体</summary>
    /// <remarks>次の <see cref="Set"/> で内容が入れ替わる＝フレームをまたいで持ち越さないこと。</remarks>
    public ReadOnlySpan<TouchPoint> Points => _points.AsSpan(0, _count);

    /// <summary>正規化に使った表示面の縦横比（幅÷高さ）</summary>
    public float Aspect { get; private set; } = 1f;

    /// <summary>プラットフォーム固有のウィンドウが接触点の全集合を差し込む口</summary>
    /// <remarks>現在のすべての接触点を渡すこと。</remarks>
    public void Set(ReadOnlySpan<TouchPoint> points, float aspect)
    {
        if (_points.Length < points.Length) _points = new TouchPoint[points.Length];
        points.CopyTo(_points);
        _count = points.Length;
        Aspect = aspect > 0f ? aspect : 1f;
    }
}
