using System.Windows;
using System.Windows.Interop;

namespace ClassDailyLand2.App.Win32;

/// <summary>窗口工具：把工作区（去掉任务栏）换算成 WPF DIP，供贴边定位使用。</summary>
internal static class WindowUtil
{
    /// <summary>主显示器工作区（DIP）。WPF 用 DIP，Win32 用物理像素，必须按 DPI 折算。</summary>
    public static Rect WorkAreaDip()
    {
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
