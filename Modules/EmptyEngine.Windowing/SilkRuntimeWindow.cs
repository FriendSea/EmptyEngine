using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace EmptyEngine.Windowing;

/// <summary>デスクトップ用のウィンドウ。</summary>
internal sealed class SilkRuntimeWindow : IRuntimeWindow
{
    private const int PlacementSaveDelayMilliseconds = 250;

    private readonly WindowSettings _settings;
    private readonly WindowPlacementStore _placementStore;
    private readonly object _placementGate = new();
    private readonly Timer _placementSaveTimer;
    private IWindow? _window;
    private IInputContext? _input;
    private SilkGamepadSource? _gamepads;
    private SilkMouseSource? _mouse;
    private WindowPlacement? _normalPlacement;
    private bool _restoredPlacement;

    /// <summary>ゲーム毎に異なるウィンドウ設定の受け取り</summary>
    public SilkRuntimeWindow(WindowSettings settings)
    {
        _settings = settings;
        _placementStore = WindowPlacementStore.ForWindow(settings.PlacementPath);
        _placementSaveTimer = new Timer(
            _ => FlushPlacement(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public KeyboardDevice Keyboard { get; } = new();

    public MouseDevice Mouse { get; } = new();

    public GamepadDevice Gamepad { get; } = new();

    public TouchDevice Touch { get; } = new();

    public event Action? Loaded;

    public event Action<int, int>? Resized;

    public event Action<double>? Update;

    public event Action<double>? Render;

    public event Action? Closed;

    public (nint Window, nint Instance) NativeHandle
    {
        get
        {
            (nint Hwnd, nint HDC, nint HInstance)? win32 = _window?.Native?.Win32;
            if (win32 is not null)
            {
                return (win32.Value.Hwnd, win32.Value.HInstance);
            }

            nint? cocoa = _window?.Native?.Cocoa;
            if (cocoa is not null)
            {
                return (cocoa.Value, 0);
            }

            throw new PlatformNotSupportedException(
                "The window's native handle is neither Win32 (Windows) nor Cocoa (macOS); X11/Wayland are not implemented.");
        }
    }

    public (int Width, int Height) FramebufferSize
    {
        get
        {
            Vector2D<int> size = _window?.FramebufferSize ?? new Vector2D<int>(1, 1);
            return (size.X, size.Y);
        }
    }

    /// <remarks>ウィンドウを所有するプロセスから呼び出すこと。</remarks>
    public unsafe void Focus()
    {
        nint? glfwWindow = _window?.Native?.Glfw;
        if (glfwWindow is null)
        {
            return;
        }

        Silk.NET.GLFW.Glfw glfw = Silk.NET.GLFW.Glfw.GetApi();
        glfw.FocusWindow((Silk.NET.GLFW.WindowHandle*)glfwWindow.Value);
    }

    public void Run()
    {
        Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();

        WindowPlacement? placement = _placementStore.Read();
        _restoredPlacement = placement is not null;
        var options = WindowOptions.Default with
        {
            Size = placement is { } restored
                ? new Vector2D<int>(restored.Width, restored.Height)
                : new Vector2D<int>(_settings.Width, _settings.Height),
            Title = _settings.Title,
            API = GraphicsAPI.None,
        };
        if (placement is { } positioned)
        {
            options = options with { Position = new Vector2D<int>(positioned.X, positioned.Y) };
        }

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Move += _ => CaptureNormalPlacement();
        _window.Resize += _ => CaptureNormalPlacement();
        _window.Closing += SavePlacement;
        _window.FramebufferResize += size => Resized?.Invoke(size.X, size.Y);
        _window.Update += d =>
        {
            _gamepads?.Update(Gamepad);
            Update?.Invoke(d);
        };
        _window.Render += d => Render?.Invoke(d);
        try
        {
            _window.Run();
        }
        finally
        {
            SavePlacement();
        }

        Closed?.Invoke();
    }

    private void OnLoad()
    {
        EnsureRestoredWindowIsVisible();
        ExecutableIcon.Apply(_window?.Native?.Win32?.Hwnd ?? 0);
        Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();
        _input = _window!.CreateInput();
        Keyboard.Attach(new SilkKeyboardSource(_input));
        _mouse = new SilkMouseSource(_window!, _input);
        Mouse.Attach(_mouse);
        _gamepads = new SilkGamepadSource(_input);
        _gamepads.Update(Gamepad);
        if (_settings.LockAspectRatio)
        {
            LockAspectRatio();
        }
        CaptureNormalPlacement();
        Loaded?.Invoke();
    }

    private void CaptureNormalPlacement()
    {
        if (_window is not { WindowState: WindowState.Normal } window)
        {
            return;
        }

        Vector2D<int> position = window.Position;
        Vector2D<int> size = window.Size;
        var placement = new WindowPlacement(position.X, position.Y, size.X, size.Y);
        if (placement.IsValid)
        {
            lock (_placementGate)
            {
                _normalPlacement = placement;
            }
            _placementSaveTimer.Change(PlacementSaveDelayMilliseconds, Timeout.Infinite);
        }
    }

    private void SavePlacement()
    {
        CaptureNormalPlacement();
        FlushPlacement();
    }

    private void FlushPlacement()
    {
        lock (_placementGate)
        {
            if (_normalPlacement is { } placement)
            {
                _placementStore.Write(placement);
            }
        }
    }

    private void EnsureRestoredWindowIsVisible()
    {
        if (_window is null || !_restoredPlacement)
        {
            return;
        }

        Vector2D<int> position = _window.Position;
        Vector2D<int> size = _window.Size;
        var placement = new WindowPlacement(position.X, position.Y, size.X, size.Y);

        bool visible = Silk.NET.Windowing.Monitor.GetMonitors(_window).Any(monitor =>
        {
            Rectangle<int> bounds = monitor.Bounds;
            return placement.HasVisibleArea(
                bounds.Origin.X,
                bounds.Origin.Y,
                bounds.Size.X,
                bounds.Size.Y);
        });

        if (!visible)
        {
            _window.Center(Silk.NET.Windowing.Monitor.GetMainMonitor(_window));
        }
    }

    private unsafe void LockAspectRatio()
    {
        nint? glfwWindow = _window?.Native?.Glfw;
        if (glfwWindow is null)
        {
            return;
        }

        Silk.NET.GLFW.Glfw glfw = Silk.NET.GLFW.Glfw.GetApi();
        glfw.SetWindowAspectRatio(
            (Silk.NET.GLFW.WindowHandle*)glfwWindow.Value,
            _settings.Width,
            _settings.Height);
    }

    public void Dispose()
    {
        _placementSaveTimer.Dispose();
        FlushPlacement();
        Gamepad.Set([]);
        Mouse.Attach(null);
        _mouse?.Dispose();
        _mouse = null;
        _input?.Dispose();
        _input = null;
        _gamepads = null;
        _window?.Dispose();
    }
}
