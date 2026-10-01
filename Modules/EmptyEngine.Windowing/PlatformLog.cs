using System.Runtime.InteropServices;

namespace EmptyEngine.Windowing;

/// <summary>プラットフォームの即時ログ</summary>
/// <remarks>ブラウザでは開発者コンソールへ、デスクトップと iOS では標準出力へ書き込む。</remarks>
public static class PlatformLog
{
    [DllImport("ee_windowing")] private static extern unsafe void ee_windowing_log(byte* s);

    public static unsafe void Write(string message)
    {
        if (!OperatingSystem.IsBrowser())
        {
            Console.WriteLine(message);
            return;
        }

        nint p = Marshal.StringToCoTaskMemUTF8(message);
        ee_windowing_log((byte*)p);
        Marshal.FreeCoTaskMem(p);
    }
}
