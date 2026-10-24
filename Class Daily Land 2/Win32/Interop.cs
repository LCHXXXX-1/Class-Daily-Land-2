using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace ClassDailyLand.App.Win32;

/// <summary>
/// 全部 Win32 互操作集中在此：置顶、WS_EX_NOACTIVATE、前台窗口/全屏检测、Office 前台检测。
/// 其它文件不得再出现 DllImport。
/// </summary>
internal static class Interop
{
    // ————————————————————— 常量 —————————————————————

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOZORDER = 0x0004;

    const int GWL_EXSTYLE = -20;
    const int WS_EX_DLGMODALFRAME = 0x0001;
    const int WS_EX_NOPARENTNOTIFY = 0x0004;
    const int WS_EX_TRANSPARENT = 0x0020;
    const int WS_EX_TOOLWINDOW = 0x0080;
    const int WS_EX_WINDOWEDGE = 0x0100;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_APPWINDOW = 0x40000;

    const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    /// <summary>Office / WPS 前台进程名（小写、含 .exe）。</summary>
    public static readonly HashSet<string> OfficeProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "wps.exe", "et.exe", "wpp.exe", "wppnt.exe", "wpsoffice.exe",
        "winword.exe", "excel.exe", "powerpnt.exe", "powerpoint.exe",
    };

    // ————————————————————— P/Invoke —————————————————————

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // ————————————————————— 扩展样式 —————————————————————

    static IntPtr GetExStyle(IntPtr hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, GWL_EXSTYLE) : new IntPtr(GetWindowLong(hwnd, GWL_EXSTYLE));

    static void SetExStyle(IntPtr hwnd, IntPtr style)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr(hwnd, GWL_EXSTYLE, style);
        else SetWindowLong(hwnd, GWL_EXSTYLE, style.ToInt32());
    }

    /// <summary>
    /// 让窗口不抢焦点、不出现在 Alt-Tab（灵动岛 / 副岛必须）。
    /// 需在窗口 SourceInitialized 之后调用。
    /// </summary>
    public static void MakeToolWindowNoActivate(Window window, bool hideFromAltTab = true)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetExStyle(hwnd).ToInt64();
        style |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
        style &= ~WS_EX_APPWINDOW;
        if (hideFromAltTab) style |= WS_EX_DLGMODALFRAME;   // 双保险：不显示任务栏按钮
        style &= ~WS_EX_WINDOWEDGE;
        SetExStyle(hwnd, new IntPtr(style));
        // 样式改变后需要重新定位一次才生效
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>强制置顶（灵动岛每秒调一次，避免被其它 TOPMOST 窗口压住）。</summary>
    public static void KeepTopmost(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public static void SetTopmost(Window window, bool topmost)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // ————————————————————— 前台窗口检测 —————————————————————

    static string _cachedProcName = "";
    static uint _cachedPid;
    static DateTime _cachedAt;

    /// <summary>前台窗口所属进程名（小写，含 .exe）；失败返回 ""。带 200ms 缓存。</summary>
    public static string ForegroundProcessName()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "";
        if (Math.Abs((DateTime.UtcNow - _cachedAt).TotalMilliseconds) < 200) return _cachedProcName;

        GetWindowThreadProcessId(hwnd, out var pid);
        var name = "";
        try
        {
            if (pid != 0)
            {
                using var p = Process.GetProcessById((int)pid);
                name = p.ProcessName + ".exe";
            }
        }
        catch
        {
            // 无权访问（其它用户进程）时用类名兜底判断 Office
            name = ClassNameOf(hwnd);
        }
        _cachedProcName = name.ToLowerInvariant();
        _cachedPid = pid;
        _cachedAt = DateTime.UtcNow;
        return _cachedProcName;
    }

    static string ClassNameOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    public static bool IsOfficeForeground() => OfficeProcesses.Contains(ForegroundProcessName());

    static RECT? ForegroundRect(IntPtr hwnd)
    {
        // DWM 扩展边框更准确（不含不可见边框）。DWMWA_EXTENDED_FRAME_BOUNDS = 9
        if (DwmGetWindowAttribute(hwnd, 9, out var rect, Marshal.SizeOf<RECT>()) == 0)
            return rect;
        return GetWindowRect(hwnd, out var r) ? r : null;
    }

    /// <summary>
    /// 前台窗口是否全屏覆盖其所在显示器（PPT 放映、全屏视频、全屏游戏）。
    /// 排除桌面窗口与自身的窗口（主窗/岛都是 TOPMOST，但面积远小于整屏）。
    /// </summary>
    public static bool IsForegroundFullscreen()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        if (hwnd == GetShellWindow()) return false;
        if (!IsWindowVisible(hwnd)) return false;
        if (!TryMonitorRect(hwnd, out var monitor)) return false;
        var rect = ForegroundRect(hwnd) ?? default;
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        var mw = monitor.Right - monitor.Left;
        var mh = monitor.Bottom - monitor.Top;
        if (w <= 0 || h <= 0) return false;

        // 允许 2px 误差（DWM 边框）
        bool covers = rect.Left <= monitor.Left + 2 && rect.Top <= monitor.Top + 2
            && rect.Right >= monitor.Right - 2 && rect.Bottom >= monitor.Bottom - 2;
        if (!covers) return false;

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId) return false;   // 自己的窗口不算
        return true;
    }

    static bool TryMonitorRect(IntPtr hwnd, out RECT monitor)
    {
        monitor = default;
        var hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMon, ref mi)) return false;
        monitor = mi.rcMonitor;
        return true;
    }

    /// <summary>前台窗口是否属于本进程（用于面板“点外面就收起”的判断）。</summary>
    public static bool IsForegroundOurs()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    // ————————————————————— 显示器几何（物理像素） —————————————————————

    public static RECT PrimaryMonitorRect()
    {
        var desktop = GetShellWindow();
        if (TryMonitorRect(desktop, out var r)) return r;
        return new RECT { Left = 0, Top = 0, Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) };
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
}
