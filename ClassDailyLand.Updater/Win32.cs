using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClassDailyLand.Updater;

/// <summary>更新器小窗用到的极简 Win32 互操作（Win11 圆角）。</summary>
static class Win32
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWCP_ROUND = 2;

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>
    /// Win11 圆角窗口。
    ///
    /// ★ **只画圆角，绝不碰背景**。曾经在这里加过"云母背板 + Background=Transparent"，
    ///   结果整窗渲染成纯黑（背板没真正生效时，透明客户区直接露出黑色合成面）——
    ///   背景一律用主题令牌的不透明颜色，见 AGENTS 坑清单「WPF 云母变黑」。
    /// </summary>
    public static void ApplyRoundedCorners(Window window)
    {
        if (!IsWindows11) return;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var value = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
        }
        catch { /* 老系统没有 dwmapi 也不影响 */ }
    }
}
