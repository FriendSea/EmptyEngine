using System.Runtime.InteropServices;

namespace EmptyEngine.Windowing;

/// <summary>実行ファイルに焼かれたアイコンのウィンドウへの適用</summary>
/// <remarks>実行ファイルとウィンドウで同じアイコンを表示する。アイコンがない場合は既定の表示を維持する。</remarks>
internal static class ExecutableIcon
{
    /// <summary>.NET SDK が <c>&lt;ApplicationIcon&gt;</c> を焼く RT_GROUP_ICON の序数</summary>
    private const nint ApplicationIconResourceId = 32512;

    private const uint ImageIcon = 1;
    private const uint LoadShared = 0x8000;
    private const uint SetIconMessage = 0x0080;

    private const nint IconSmall = 0;
    private const nint IconBig = 1;

    private const int MetricIconWidth = 11;
    private const int MetricIconHeight = 12;
    private const int MetricSmallIconWidth = 49;
    private const int MetricSmallIconHeight = 50;

    /// <summary>Win32 ウィンドウへの適用</summary>
    /// <param name="windowHandle">対象ウィンドウの HWND。</param>
    /// <remarks>Windows 以外、またはアイコンを持たない実行ファイルでは何もしない</remarks>
    public static void Apply(nint windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == 0)
        {
            return;
        }

        nint module = GetModuleHandleW(null);
        Apply(windowHandle, module, IconBig, MetricIconWidth, MetricIconHeight);
        Apply(windowHandle, module, IconSmall, MetricSmallIconWidth, MetricSmallIconHeight);
    }

    private static void Apply(nint windowHandle, nint module, nint which, int widthMetric, int heightMetric)
    {
        // LR_SHARED はシステムがハンドルを持つ（破棄不要）代わりに標準サイズでしか使えないので、
        // 大きさは決め打ちにせず GetSystemMetrics から採る。
        nint icon = LoadImageW(
            module,
            ApplicationIconResourceId,
            ImageIcon,
            GetSystemMetrics(widthMetric),
            GetSystemMetrics(heightMetric),
            LoadShared);

        if (icon != 0)
        {
            SendMessageW(windowHandle, SetIconMessage, which, icon);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImageW(nint instance, nint name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
}
