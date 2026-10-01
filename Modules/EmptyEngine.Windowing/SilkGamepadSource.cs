using System.Numerics;
using Silk.NET.Input;

namespace EmptyEngine.Windowing;

/// <summary>GLFW から標準ゲームパッドの状態を取得する。</summary>
/// <remarks>XInput 系のコントローラーのみ対応。macOS では Switch Pro や PlayStation 系を OS が独自形式のレポートへ切り替えるため、GLFW からは入力を読めない。</remarks>
internal sealed class SilkGamepadSource(IInputContext context)
{
    private GamepadState[] _buffer = [];

    public void Update(GamepadDevice device)
    {
        if (_buffer.Length < context.Gamepads.Count) _buffer = new GamepadState[context.Gamepads.Count];
        int count = 0;
        foreach (IGamepad pad in context.Gamepads)
        {
            if (!pad.IsConnected) continue;
            GamepadButtons buttons = GamepadButtons.None;
            foreach (Button button in pad.Buttons)
                if (button.Pressed) buttons |= Map(button.Name);
            _buffer[count++] = new GamepadState(pad.Index, buttons,
                Stick(pad, 0), Stick(pad, 1), Trigger(pad, 0), Trigger(pad, 1));
        }
        device.Set(_buffer.AsSpan(0, count));
    }

    private static Vector2 Stick(IGamepad pad, int index) => pad.Thumbsticks.Count > index
        ? new Vector2(pad.Thumbsticks[index].X, -pad.Thumbsticks[index].Y) : Vector2.Zero;

    // GLFW のトリガー軸は未押下が -1、全押下が +1。
    private static float Trigger(IGamepad pad, int index) => pad.Triggers.Count > index
        ? Math.Clamp((pad.Triggers[index].Position + 1f) * 0.5f, 0f, 1f) : 0f;

    internal static GamepadButtons Map(ButtonName button) => button switch
    {
        ButtonName.A => GamepadButtons.South,
        ButtonName.B => GamepadButtons.East,
        ButtonName.X => GamepadButtons.West,
        ButtonName.Y => GamepadButtons.North,
        ButtonName.LeftBumper => GamepadButtons.LeftBumper,
        ButtonName.RightBumper => GamepadButtons.RightBumper,
        ButtonName.Back => GamepadButtons.Back,
        ButtonName.Start => GamepadButtons.Start,
        ButtonName.Home => GamepadButtons.Home,
        ButtonName.LeftStick => GamepadButtons.LeftStick,
        ButtonName.RightStick => GamepadButtons.RightStick,
        ButtonName.DPadUp => GamepadButtons.DPadUp,
        ButtonName.DPadRight => GamepadButtons.DPadRight,
        ButtonName.DPadDown => GamepadButtons.DPadDown,
        ButtonName.DPadLeft => GamepadButtons.DPadLeft,
        _ => GamepadButtons.None,
    };
}
