using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EmptyEngine.Windowing;

/// <summary>web 向けのキー供給元</summary>
internal sealed class WebKeyboardSource : IKeyboardSource
{
    [DllImport("ee_windowing")]
    private static extern unsafe void ee_install_key_callback(delegate* unmanaged[Cdecl]<byte*, int, int> cb);

    private static WebKeyboardSource? _self;

    private readonly HashSet<KeyboardKey> _down = new();

    /// <summary>ブラウザのキーイベント購読の開始とキー供給元の生成</summary>
    public static unsafe IKeyboardSource Create()
    {
        var source = new WebKeyboardSource();
        _self = source;
        ee_install_key_callback(&OnKeyNative);
        return source;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int OnKeyNative(byte* code, int isDown)
    {
        string? s = Marshal.PtrToStringUTF8((nint)code);
        return s is not null && _self!.SetKey(s, isDown != 0) ? 1 : 0;
    }

    public bool IsKeyDown(KeyboardKey key) => _down.Contains(key);

    /// <summary>ブラウザの KeyboardEvent.code からの押下状態の更新</summary>
    public bool SetKey(string code, bool isDown)
    {
        if (!TryMap(code, out KeyboardKey key))
        {
            return false;
        }

        if (isDown)
        {
            _down.Add(key);
        }
        else
        {
            _down.Remove(key);
        }

        return true;
    }

    private static bool TryMap(string code, out KeyboardKey key)
    {
        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal) && code[3] is >= 'A' and <= 'Z')
        {
            key = KeyboardKey.A + (code[3] - 'A');
            return true;
        }

        if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal) && code[5] is >= '0' and <= '9')
        {
            key = KeyboardKey.Number0 + (code[5] - '0');
            return true;
        }

        switch (code)
        {
            case "ArrowLeft": key = KeyboardKey.Left; return true;
            case "ArrowRight": key = KeyboardKey.Right; return true;
            case "ArrowUp": key = KeyboardKey.Up; return true;
            case "ArrowDown": key = KeyboardKey.Down; return true;
            case "Space": key = KeyboardKey.Space; return true;
            case "Enter": key = KeyboardKey.Enter; return true;
            case "Escape": key = KeyboardKey.Escape; return true;
            case "Tab": key = KeyboardKey.Tab; return true;
            case "Backspace": key = KeyboardKey.Backspace; return true;
            case "ShiftLeft": key = KeyboardKey.ShiftLeft; return true;
            case "ShiftRight": key = KeyboardKey.ShiftRight; return true;
            case "ControlLeft": key = KeyboardKey.ControlLeft; return true;
            case "ControlRight": key = KeyboardKey.ControlRight; return true;
            case "AltLeft": key = KeyboardKey.AltLeft; return true;
            case "AltRight": key = KeyboardKey.AltRight; return true;
            default: key = default; return false;
        }
    }
}
