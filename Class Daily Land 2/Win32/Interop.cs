using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace ClassDailyLand.App.Win32;

/// <summary>
/// 全部 Win32 互操作集中在此：置顶、WS_EX_NOACTIVATE、前台窗口/全屏检测、Office 前台检测。
/// 其它文件不得再出现 DllImport。
/// </summary>
/// <summary>窗口背板实际套上了什么（决定调用方用哪套底色分层）。</summary>
public enum WindowBackdrop
{
    /// <summary>没有背板：用不透明底色，保证可读。</summary>
    None = 0,
    /// <summary>Win10 亚克力/模糊。</summary>
    Acrylic = 1,
    /// <summary>Win11 云母。</summary>
    Mica = 2,
}

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
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    // ——— kernel32：取前台进程名 ———
    // 不用 System.Diagnostics.Process.GetProcessById：它对高权限/受保护进程（svchost、
    // 任务管理器、其它用户的进程）会抛 Win32Exception，外面 try-catch 虽然兜得住，
    // 但调试器每次都会停在异常处，而这个方法一秒调一次 —— 等于没法调试。
    // OpenProcess 失败只是返回 0，QueryFullProcessImageName 失败只是返回 false，都不抛。

    /// <summary>只查询、不读写进程内存：Vista+ 的进程上基本都能拿到，权限要求最低。</summary>
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    struct MARGINS { public int Left; public int Right; public int Top; public int Bottom; }

    // ——— Win10 亚克力（未公开 API，但 1803 起一直可用） ———
    [DllImport("user32.dll")]
    static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;   // AABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

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

    /// <summary>前台窗口所属进程名（小写，含 .exe）；拿不到返回 ""。带 200ms 缓存。**绝不抛异常**。</summary>
    public static string ForegroundProcessName()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "";
        if (Math.Abs((DateTime.UtcNow - _cachedAt).TotalMilliseconds) < 200) return _cachedProcName;

        GetWindowThreadProcessId(hwnd, out var pid);
        string name = "";

        if (pid != 0)
        {
            // PROCESS_QUERY_LIMITED_INFORMATION (0x1000)：只要进程名，不碰内存，权限要求最低
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);
            if (hProcess != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
                    {
                        name = Path.GetFileName(sb.ToString()).ToLowerInvariant();
                    }
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }
            // else：没权限打开（受保护进程 / 其它用户）→ 名字留空，不抛异常
        }

        _cachedProcName = name;
        _cachedPid = pid;
        _cachedAt = DateTime.UtcNow;
        return _cachedProcName;
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

    /// <summary>窗口在屏幕坐标系（物理像素）中的矩形；句柄还没创建时返回 false。</summary>
    public static bool TryGetWindowScreenRect(Window window, out RECT rect)
    {
        rect = default;
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out rect);
    }

    /// <summary>鼠标屏幕坐标（物理像素）是否落在窗口内。</summary>
    public static bool WindowContainsScreenPoint(Window window, int x, int y)
    {
        if (!TryGetWindowScreenRect(window, out var r)) return false;
        return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    // ————————————————————— 云母材质 / 圆角 —————————————————————

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;   // Win10 1809~2004
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    const int DWMWA_MICA_EFFECT = 1029;      // Win11 21H2 的私有属性

    const int DWMWCP_ROUND = 2;              // 圆角
    const int DWMSBT_MAINWINDOW = 2;         // Mica（主窗口云母）

    const int WCA_ACCENT_POLICY = 19;
    const int ACCENT_ENABLE_BLURBEHIND = 3;
    const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    /// <summary>当前系统是不是 Win11（原生圆角与云母只在这上面有）。</summary>
    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Win10 1803 起才有亚克力模糊。</summary>
    public static bool SupportsAcrylic => Environment.OSVersion.Version.Build >= 17134;

    /// <summary>这台机器能不能给窗口套一层半透明背板（Win11 云母 / Win10 亚克力）。</summary>
    public static bool SupportsBackdrop => IsWindows11 || SupportsAcrylic;

    static bool TrySetDwm(IntPtr hwnd, int attribute, int value)
    {
        try { return DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0; }
        catch { return false; }
    }

    /// <summary>
    /// 给无边框窗口套上 Win11 的圆角 + 云母材质。返回 true 表示云母真的生效了。
    ///
    /// ★ 云母要求窗口**不能是 AllowsTransparency**（分层窗口没有 DWM 背景），
    ///   所以设置窗口得用 WindowStyle=None + 透明 Background + 扩展帧到客户区这套写法。
    ///   Win10 上这两个 API 都返回非 0，调用方据此回退成不透明底色。
    /// </summary>
    public static WindowBackdrop ApplyBackdrop(Window window, bool material, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return WindowBackdrop.None;

        // 深色标题栏/边框：Win10 20H1 之前是属性 19，之后是 20
        if (!TrySetDwm(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0))
            TrySetDwm(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, dark ? 1 : 0);

        // 圆角只有 Win11 有 —— 这里交给 DWM 自己加，不做任何手工绘制。
        // Win10 原本就是直角，保持直角就是"原生"。
        if (IsWindows11)
            TrySetDwm(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);

        if (!material) return WindowBackdrop.None;

        if (IsWindows11)
        {
            // 先把边框扩展到整个客户区，否则背景层只在标题栏那一条
            try
            {
                var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);
            }
            catch { /* 老系统没有也不影响后面的判断 */ }

            // 22H2+：官方 SYSTEMBACKDROP_TYPE；21H2：私有 MICA_EFFECT 兜底
            if (TrySetDwm(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW) ||
                TrySetDwm(hwnd, DWMWA_MICA_EFFECT, 1))
                return WindowBackdrop.Mica;
        }

        // Win10：没有云母，退到亚克力（模糊背板）
        if (SupportsAcrylic && ApplyAcrylic(hwnd, dark)) return WindowBackdrop.Acrylic;

        return WindowBackdrop.None;
    }

    /// <summary>
    /// Win10 的亚克力/模糊背板（SetWindowCompositionAttribute，未公开但稳定）。
    /// 1803+ 用 ACRYLIC，更早只支持普通 BLUR。
    /// </summary>
    static bool ApplyAcrylic(IntPtr hwnd, bool dark)
    {
        // GradientColor 是 AABBGGRR；深色用深灰，浅色用近白
        var tint = unchecked((int)(dark ? 0xC8202020u : 0xC8F5F5F5u));
        foreach (var state in new[] { ACCENT_ENABLE_ACRYLICBLURBEHIND, ACCENT_ENABLE_BLURBEHIND })
        {
            var policy = new AccentPolicy { AccentState = state, AccentFlags = 2, GradientColor = tint };
            var size = Marshal.SizeOf<AccentPolicy>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, buffer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = buffer,
                    SizeOfData = size,
                };
                if (SetWindowCompositionAttribute(hwnd, ref data) != 0) return true;
            }
            catch { /* 换下一种 */ }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return false;
    }

    /// <summary>
    /// 把窗口提到「普通窗口层」的最前面（不动 TOPMOST 区）。
    /// 用途：设置窗口打开后要盖住贴在桌面边缘的主窗 —— 主窗不是 TOPMOST，
    /// 两边都不置顶时 z 序取决于谁先 Show，很容易被主窗压住。
    /// </summary>
    public static void RaiseToFront(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>只加圆角（岛 / 主窗这类不需要云母的窗口）。</summary>
    public static void ApplyRoundedCorners(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !IsWindows11) return;
        TrySetDwm(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
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

/// <summary>
/// 全局低级鼠标钩子：下拉面板带 WS_EX_NOACTIVATE，永远不会被激活，
/// 所以 WPF 的 Deactivated 在“点别的窗口”时并不可靠。这里用 WH_MOUSE_LL
/// 拿到所有鼠标按下，判断点是否落在面板/主岛之外。
///
/// 回调运行在安装钩子的 UI 线程上；事件处理只做分发，绝不阻塞，避免被系统超时摘钩。
/// </summary>
internal sealed class GlobalMouseHook : IDisposable
{
    const int WH_MOUSE_LL = 14;

    const int WM_LBUTTONDOWN = 0x0201;
    const int WM_RBUTTONDOWN = 0x0204;
    const int WM_MBUTTONDOWN = 0x0207;

    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    readonly LowLevelMouseProc _proc;
    IntPtr _hook;

    public GlobalMouseHook() => _proc = Callback;

    /// <summary>鼠标按下（左/右/中键），参数是屏幕物理像素坐标。</summary>
    public event Action<int, int>? MouseDown;

    public bool Start()
    {
        if (_hook != IntPtr.Zero) return true;

        try
        {
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        }
        catch
        {
            _hook = IntPtr.Zero;
        }

        return _hook != IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;

        try { UnhookWindowsHookEx(_hook); }
        catch { /* 进程退出时系统也会清理 */ }
        finally { _hook = IntPtr.Zero; }
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            if (message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            {
                try
                {
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    MouseDown?.Invoke(data.pt.X, data.pt.Y);
                }
                catch
                {
                    // 钩子回调里绝不能把异常抛回系统
                }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
