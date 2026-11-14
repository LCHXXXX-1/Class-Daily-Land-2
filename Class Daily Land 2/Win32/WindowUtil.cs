using System.Windows;
using System.Windows.Interop;

namespace ClassDailyLand2.App.Win32;

/// <summary>窗口工具：把工作区（去掉任务栏）换算成 WPF DIP，供贴边定位使用。</summary>
internal static class WindowUtil
{
    /// <summary>
    /// 工作区（DIP）。
    ///
    /// <para>★ 传了窗口就取**该窗口所在显示器**的工作区（多显示器不再钉死主屏）；
    /// 没传窗口 / 还没有句柄 / 查询失败时退回主显示器 —— 行为与旧版完全一致。</para>
    /// </summary>
    public static Rect WorkAreaDip(Window? w = null)
    {
        if (w is not null)
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (Interop.TryGetWorkArea(hwnd, out var r, out var scale) && scale > 0)
                    return new Rect(r.Left / scale, r.Top / scale,
                                    (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale);
            }
            catch { /* 退回主显示器 */ }
        }

        var wa = SystemParameters.WorkArea;      // 已是 DIP，且自动排除任务栏
        return new Rect(wa.Left, wa.Top, wa.Width, wa.Height);
    }

    public static Rect FullAreaDip() =>
        new(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

    /// <summary>物理像素 -> DIP 的缩放系数（96/实际 DPI）。</summary>
    public static double DpiScale(Window w)
    {
        var src = PresentationSource.FromVisual(w);
        return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    /// <summary>把 Win32 的 RECT（物理像素）转成 DIP 矩形。</summary>
    public static Rect ToDip(in Interop.RECT r, double scale) =>
        new(r.Left / scale, r.Top / scale, (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale);

    public static IntPtr Hwnd(Window w) => new WindowInteropHelper(w).Handle;
}
