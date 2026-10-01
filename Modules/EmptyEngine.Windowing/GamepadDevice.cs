using System.Numerics;
using System.Runtime.InteropServices;

namespace EmptyEngine.Windowing;

/// <summary>標準ゲームパッドのボタン。表面の文字ではなく物理位置で識別する。</summary>
[Flags]
public enum GamepadButtons : uint
{
    None = 0,
    South = 1 << 0, East = 1 << 1, West = 1 << 2, North = 1 << 3,
    LeftBumper = 1 << 4, RightBumper = 1 << 5,
    Back = 1 << 6, Start = 1 << 7, Home = 1 << 8,
    LeftStick = 1 << 9, RightStick = 1 << 10,
    DPadUp = 1 << 11, DPadRight = 1 << 12, DPadDown = 1 << 13, DPadLeft = 1 << 14,
}

/// <summary>接続中のゲームパッドのフレーム状態。Id は接続中の機器の識別子。</summary>
/// <remarks>スティックは -1..1（右・上が正）、トリガーは 0..1。押下履歴とデッドゾーンは利用側で管理する。</remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct GamepadState(
    int Id, GamepadButtons Buttons, Vector2 LeftStick, Vector2 RightStick,
    float LeftTrigger, float RightTrigger)
{
    /// <summary>指定したすべてのボタンが押されているか。</summary>
    public bool IsButtonDown(GamepadButtons buttons) => buttons != GamepadButtons.None && (Buttons & buttons) == buttons;
}

public interface IGamepadSource
{
    /// <summary>接続中の標準ゲームパッドの全状態。</summary>
    ReadOnlySpan<GamepadState> Gamepads { get; }
}

/// <summary>ウィンドウが持つゲームパッド入力。ロード前と接続機器がないときは空。</summary>
public sealed class GamepadDevice : IGamepadSource
{
    private GamepadState[] _states = [];
    private int _count;

    /// <remarks>次の更新まで有効。フレームをまたいで保持する場合はコピーすること。</remarks>
    public ReadOnlySpan<GamepadState> Gamepads => _states.AsSpan(0, _count);

    /// <summary>プラットフォーム側から接続中の全状態をコピーする。切断した機器は含めない。</summary>
    public void Set(ReadOnlySpan<GamepadState> states)
    {
        if (_states.Length < states.Length) _states = new GamepadState[states.Length];
        states.CopyTo(_states);
        _count = states.Length;
    }
}
