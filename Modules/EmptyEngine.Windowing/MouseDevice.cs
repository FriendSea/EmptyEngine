using System.Numerics;

namespace EmptyEngine.Windowing;

[Flags]
public enum MouseButtons
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 4,
}

/// <summary>表示面の左上を原点とするマウス状態。位置と表示面サイズは同じ単位。</summary>
/// <param name="Scroll">上向きを正とするホイールの累積値。差分は利用側で計算する。</param>
/// <param name="IsActive">この表示面に対する入力を受け付けられるか。</param>
public readonly record struct MouseState(
    Vector2 Position, Vector2 SurfaceSize, MouseButtons Buttons, float Scroll, bool IsActive);

public interface IMouseSource
{
    MouseState State { get; }
}

/// <summary>ウィンドウと同じ寿命を持つマウス。未接続時は非アクティブ。</summary>
public sealed class MouseDevice : IMouseSource
{
    private IMouseSource? _source;

    public MouseState State => _source?.State ?? default;

    public void Attach(IMouseSource? source) => _source = source;
}
