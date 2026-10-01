using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EmptyEngine.Windowing;

/// <summary>ブラウザの canvas を面に取る web のウィンドウ</summary>
public sealed class WebRuntimeWindow : IRuntimeWindow
{
    private static WebRuntimeWindow? _self;

    private DateTime _lastFrameUtc;
    private readonly WebGamepadSource _gamepads = new();

    /// <remarks>サイズとタイトルはページに従い、<paramref name="settings"/> は使用しない。</remarks>
    public WebRuntimeWindow(WindowSettings settings) { }

    [DllImport("ee_windowing")]
    private static extern unsafe void ee_set_main_loop(delegate* unmanaged[Cdecl]<void> frame);

    public KeyboardDevice Keyboard { get; } = new();

    public MouseDevice Mouse { get; } = new();

    public GamepadDevice Gamepad { get; } = new();

    public TouchDevice Touch { get; } = new();

    public event Action? Loaded;

    // canvas のリサイズもページの終了も拾っていない＝この 2 つは発火しない（口だけ契約に合わせる）。
#pragma warning disable CS0067
    public event Action<int, int>? Resized;

    public event Action? Closed;
#pragma warning restore CS0067

    public event Action<double>? Update;

    public event Action<double>? Render;

    /// <exception cref="PlatformNotSupportedException">ブラウザでは取得できない。</exception>
    public (nint Window, nint Instance) NativeHandle =>
        throw new PlatformNotSupportedException("The web canvas is not exposed through a native handle.");

    /// <inheritdoc cref="NativeHandle"/>
    public (int Width, int Height) FramebufferSize =>
        throw new PlatformNotSupportedException("The web canvas is not exposed through a native handle.");

    /// <summary>フォーカスを変更しない。</summary>
    public void Focus()
    {
    }

    public unsafe void Run()
    {
        _self = this;
        _lastFrameUtc = DateTime.UtcNow;

        Keyboard.Attach(WebKeyboardSource.Create());
        Mouse.Attach(new WebMouseSource());
        _ = Mouse.State; // 最初のフレームより前に canvas のイベントを購読する。
        WebTouchSource.Attach(Touch);
        _gamepads.Update(Gamepad);
        Loaded?.Invoke();

        ee_set_main_loop(&Frame);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Frame()
    {
        WebRuntimeWindow self = _self!;

        DateTime now = DateTime.UtcNow;
        double delta = (now - self._lastFrameUtc).TotalSeconds;
        self._lastFrameUtc = now;

        self._gamepads.Update(self.Gamepad);
        self.Update?.Invoke(delta);
        self.Render?.Invoke(delta);
    }

    public void Dispose()
    {
        Gamepad.Set([]);
        Mouse.Attach(null);
    }
}
