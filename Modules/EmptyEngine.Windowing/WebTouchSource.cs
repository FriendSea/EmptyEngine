using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EmptyEngine.Windowing;

/// <summary>web 向けのタッチ供給元</summary>
internal static class WebTouchSource
{
    private static TouchDevice? _target;

    [DllImport("ee_windowing")]
    private static extern unsafe void ee_install_touch_callback(
        delegate* unmanaged[Cdecl]<TouchPoint*, int, float, void> cb);

    /// <summary>canvas のタッチイベント購読の開始</summary>
    public static unsafe void Attach(TouchDevice target)
    {
        _target = target;
        ee_install_touch_callback(&OnTouchNative);
    }

    // シムと TouchPoint のメモリレイアウトを一致させる。
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnTouchNative(TouchPoint* points, int count, float aspect)
        => _target?.Set(new ReadOnlySpan<TouchPoint>(points, count), aspect);
}
