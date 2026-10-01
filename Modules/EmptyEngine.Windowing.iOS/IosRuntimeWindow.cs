using System.Numerics;
using GameController;
using CoreAnimation;
using ObjCRuntime;

namespace EmptyEngine.Windowing.iOS;

/// <summary>画面のリフレッシュに合わせてフレームを更新する iOS 用ウィンドウ。</summary>
/// <remarks>iOS アプリ側で作成する。<see cref="RuntimeWindow.Create"/> では作成できない。</remarks>
public sealed class IosRuntimeWindow : IRuntimeWindow
{
    private static IosRuntimeWindow? _current;
    private CADisplayLink? _displayLink;
    private CAMetalLayer? _metalLayer;
    private DateTime _lastFrameUtc;

    /// <remarks>サイズと向きは画面に従い、<paramref name="settings"/> は使用しない。</remarks>
    public IosRuntimeWindow(WindowSettings settings) { }

    /// <remarks>すべてのキーについて、押されていない状態を返す。</remarks>
    public KeyboardDevice Keyboard { get; } = new();

    /// <summary>マウス未対応のため非アクティブ。</summary>
    public MouseDevice Mouse { get; } = new();

    public GamepadDevice Gamepad { get; } = new();

    private readonly Dictionary<nint, int> _controllerIds = new();
    private readonly HashSet<nint> _connectedControllers = [];
    private readonly List<nint> _disconnectedControllers = [];
    private int _nextControllerId;
    private GamepadState[] _gamepadBuffer = [];

    public TouchDevice Touch { get; } = new();

    public event Action? Loaded;
    public event Action<int, int>? Resized;
    public event Action<double>? Update;
    public event Action<double>? Render;
    public event Action? Closed;

    public (nint Window, nint Instance) NativeHandle =>
        (_metalLayer?.Handle ?? throw new InvalidOperationException("The iOS Metal layer is not loaded yet."), 0);

    public (int Width, int Height) FramebufferSize
    {
        get
        {
            CAMetalLayer layer = _metalLayer ?? throw new InvalidOperationException("The iOS Metal layer is not loaded yet.");
            return ((int)Math.Max(1, layer.DrawableSize.Width), (int)Math.Max(1, layer.DrawableSize.Height));
        }
    }

    public void Focus() { }

    public void Run()
    {
        _current = this;
        UIApplication.Main(Array.Empty<string>(), null, typeof(EmptyEngineAppDelegate));
        Closed?.Invoke();
    }

    internal static IosRuntimeWindow Current => _current
        ?? throw new InvalidOperationException("No iOS RuntimeWindow is active.");

    internal UIViewController CreateViewController()
    {
        var controller = new UIViewController();
        var view = new MetalHostView(this, UIScreen.MainScreen.Bounds)
        {
            BackgroundColor = UIColor.Black,
            MultipleTouchEnabled = true,
        };
        controller.View = view;
        _metalLayer = view.MetalLayer;
        Resize(view.Bounds);
        return controller;
    }

    internal void Start()
    {
        _lastFrameUtc = DateTime.UtcNow;
        UpdateGamepads();
        Loaded?.Invoke();
        _displayLink = CADisplayLink.Create(OnFrame);
        _displayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
    }

    private void OnFrame()
    {
        DateTime now = DateTime.UtcNow;
        double delta = (now - _lastFrameUtc).TotalSeconds;
        _lastFrameUtc = now;
        UpdateGamepads();
        Update?.Invoke(delta);
        Render?.Invoke(delta);
    }

    private void UpdateGamepads()
    {
        GCController[] controllers = GCController.Controllers;
        if (_gamepadBuffer.Length < controllers.Length) _gamepadBuffer = new GamepadState[controllers.Length];
        _connectedControllers.Clear();
        int count = 0;
        foreach (GCController controller in controllers)
        {
            if (controller.ExtendedGamepad is not { } pad) continue;
            nint handle = controller.Handle;
            _connectedControllers.Add(handle);
            if (!_controllerIds.TryGetValue(handle, out int id)) _controllerIds[handle] = id = _nextControllerId++;
            GamepadButtons buttons = GamepadButtons.None;
            Add(pad.ButtonA, GamepadButtons.South);
            Add(pad.ButtonB, GamepadButtons.East);
            Add(pad.ButtonX, GamepadButtons.West);
            Add(pad.ButtonY, GamepadButtons.North);
            Add(pad.LeftShoulder, GamepadButtons.LeftBumper);
            Add(pad.RightShoulder, GamepadButtons.RightBumper);
            Add(pad.ButtonOptions, GamepadButtons.Back);
            Add(pad.ButtonMenu, GamepadButtons.Start);
            Add(pad.ButtonHome, GamepadButtons.Home);
            Add(pad.LeftThumbstickButton, GamepadButtons.LeftStick);
            Add(pad.RightThumbstickButton, GamepadButtons.RightStick);
            Add(pad.DPad.Up, GamepadButtons.DPadUp);
            Add(pad.DPad.Right, GamepadButtons.DPadRight);
            Add(pad.DPad.Down, GamepadButtons.DPadDown);
            Add(pad.DPad.Left, GamepadButtons.DPadLeft);
            _gamepadBuffer[count++] = new GamepadState(id, buttons,
                new Vector2(pad.LeftThumbstick.XAxis.Value, pad.LeftThumbstick.YAxis.Value),
                new Vector2(pad.RightThumbstick.XAxis.Value, pad.RightThumbstick.YAxis.Value),
                pad.LeftTrigger.Value, pad.RightTrigger.Value);

            void Add(GCControllerButtonInput? button, GamepadButtons flag)
            {
                if (button?.IsPressed == true) buttons |= flag;
            }
        }
        _disconnectedControllers.Clear();
        foreach (nint handle in _controllerIds.Keys)
            if (!_connectedControllers.Contains(handle)) _disconnectedControllers.Add(handle);
        foreach (nint handle in _disconnectedControllers) _controllerIds.Remove(handle);
        Gamepad.Set(_gamepadBuffer.AsSpan(0, count));
    }

    private void Resize(CoreGraphics.CGRect bounds)
    {
        if (_metalLayer is null) return;
        nfloat scale = UIScreen.MainScreen.NativeScale;
        _metalLayer.ContentsScale = scale;
        _metalLayer.DrawableSize = new CoreGraphics.CGSize(bounds.Width * scale, bounds.Height * scale);
        Resized?.Invoke((int)_metalLayer.DrawableSize.Width, (int)_metalLayer.DrawableSize.Height);
    }

    public void Dispose()
    {
        Gamepad.Set([]);
        _controllerIds.Clear();
        _displayLink?.Invalidate();
        _displayLink?.Dispose();
        _displayLink = null;
        _metalLayer?.Dispose();
        _metalLayer = null;
        if (ReferenceEquals(_current, this)) _current = null;
    }

    private sealed class MetalHostView : UIView
    {
        private readonly IosRuntimeWindow _owner;

        // UIKit のイベント集合は接触中の指だけを表さないため、接触の開始と終了を追跡する。
        private readonly List<(UITouch Touch, int Id)> _touches = [];
        private int _nextTouchId;
        private TouchPoint[] _buffer = [];

        // Use CAMetalLayer as the backing layer to avoid a UIView layer covering the drawable.
        [Export("layerClass")]
        public static Class LayerClass() => new(typeof(CAMetalLayer));

        public CAMetalLayer MetalLayer => (CAMetalLayer)Layer;

        public MetalHostView(IosRuntimeWindow owner, CoreGraphics.CGRect frame) : base(frame)
        {
            _owner = owner;
            ContentScaleFactor = UIScreen.MainScreen.NativeScale;
            MetalLayer.Opaque = true;
        }

        public override void LayoutSubviews()
        {
            base.LayoutSubviews();
            _owner.Resize(Bounds);
        }

        public override void TouchesBegan(NSSet touches, UIEvent? evt)
        {
            foreach (NSObject item in touches)
            {
                if (item is UITouch touch) _touches.Add((touch, ++_nextTouchId));
            }

            PublishTouches();
        }

        // 座標は保持している UITouch から読み直すので、動いた指を突き合わせる必要はない。
        public override void TouchesMoved(NSSet touches, UIEvent? evt) => PublishTouches();

        public override void TouchesEnded(NSSet touches, UIEvent? evt) => RemoveTouches(touches);
        public override void TouchesCancelled(NSSet touches, UIEvent? evt) => RemoveTouches(touches);

        private void RemoveTouches(NSSet touches)
        {
            foreach (NSObject item in touches)
            {
                if (item is not UITouch touch) continue;
                for (int i = _touches.Count - 1; i >= 0; i--)
                {
                    // ネイティブハンドルで突き合わせる（同じ UITouch が同じマネージドラッパーで来る保証は無い）。
                    if (_touches[i].Touch.Handle == touch.Handle) _touches.RemoveAt(i);
                }
            }

            PublishTouches();
        }

        private void PublishTouches()
        {
            double width = Math.Max((double)Bounds.Width, 1.0);
            double height = Math.Max((double)Bounds.Height, 1.0);
            if (_buffer.Length < _touches.Count) _buffer = new TouchPoint[_touches.Count];

            for (int i = 0; i < _touches.Count; i++)
            {
                (UITouch touch, int id) = _touches[i];
                CoreGraphics.CGPoint point = touch.LocationInView(this);
                _buffer[i] = new TouchPoint(id, (float)(point.X / width), (float)(point.Y / height));
            }

            Current.Touch.Set(_buffer.AsSpan(0, _touches.Count), (float)(width / height));
        }
    }
}

[Register("EmptyEngineAppDelegate")]
internal sealed class EmptyEngineAppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        IosRuntimeWindow runtime = IosRuntimeWindow.Current;
        Window = new UIWindow(UIScreen.MainScreen.Bounds)
        {
            RootViewController = runtime.CreateViewController(),
        };
        Window.MakeKeyAndVisible();
        runtime.Start();
        return true;
    }
}
