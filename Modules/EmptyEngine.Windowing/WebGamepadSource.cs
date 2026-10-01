using System.Runtime.InteropServices;

namespace EmptyEngine.Windowing;

internal sealed class WebGamepadSource
{
    [DllImport("ee_windowing")]
    private static extern int ee_sample_gamepads();

    [DllImport("ee_windowing")]
    private static extern int ee_get_gamepad(int index, out GamepadState state);

    private GamepadState[] _buffer = [];

    public void Update(GamepadDevice device)
    {
        int slots = Math.Max(0, ee_sample_gamepads());
        if (_buffer.Length < slots) _buffer = new GamepadState[slots];
        int count = 0;
        for (int i = 0; i < slots; i++)
            if (ee_get_gamepad(i, out GamepadState state) != 0) _buffer[count++] = state;
        device.Set(_buffer.AsSpan(0, count));
    }
}
