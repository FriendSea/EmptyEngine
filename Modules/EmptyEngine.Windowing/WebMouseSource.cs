using System.Numerics;
using System.Runtime.InteropServices;

namespace EmptyEngine.Windowing;

internal sealed class WebMouseSource : IMouseSource
{
    [DllImport("ee_windowing")]
    private static extern unsafe void ee_read_mouse(float* values);

    public unsafe MouseState State
    {
        get
        {
            float* values = stackalloc float[7];
            ee_read_mouse(values);
            return new MouseState(new Vector2(values[0], values[1]), new Vector2(values[2], values[3]),
                (MouseButtons)(int)values[4], values[5], values[6] != 0);
        }
    }
}
