using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace EmptyEngine.Windowing;

internal sealed class SilkMouseSource : IMouseSource, IDisposable
{
    private readonly IWindow _window;
    private readonly IMouse? _mouse;
    private float _scroll;
    private bool _focused = true;

    public SilkMouseSource(IWindow window, IInputContext input)
    {
        _window = window;
        _mouse = input.Mice.Count > 0 ? input.Mice[0] : null;
        _window.FocusChanged += OnFocusChanged;
        if (_mouse is not null) _mouse.Scroll += OnScroll;
    }

    public MouseState State
    {
        get
        {
            if (_mouse is null) return default;
            Vector2 position = _mouse.Position;
            var size = new Vector2(_window.Size.X, _window.Size.Y);
            MouseButtons buttons = MouseButtons.None;
            if (_mouse.IsButtonPressed(MouseButton.Left)) buttons |= MouseButtons.Left;
            if (_mouse.IsButtonPressed(MouseButton.Right)) buttons |= MouseButtons.Right;
            if (_mouse.IsButtonPressed(MouseButton.Middle)) buttons |= MouseButtons.Middle;
            bool inside = position.X >= 0 && position.Y >= 0 && position.X < size.X && position.Y < size.Y;
            return new MouseState(position, size, buttons, _scroll,
                _focused && (inside || buttons != MouseButtons.None));
        }
    }

    private void OnScroll(IMouse mouse, ScrollWheel wheel) => _scroll += wheel.Y;

    private void OnFocusChanged(bool focused) => _focused = focused;

    public void Dispose()
    {
        if (_mouse is not null) _mouse.Scroll -= OnScroll;
        _window.FocusChanged -= OnFocusChanged;
    }
}
