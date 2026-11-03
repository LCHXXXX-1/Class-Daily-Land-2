using ClassDailyLand2.Core.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ClassDailyLand2.App.Views.Dialogs;

/// <summary>
/// 错误码清单：把 <see cref="ErrorCodes.All"/> 原样铺出来，给开发/排障时对照。
/// 只读页面，不提供编辑 —— 码表只有一个来源（代码里的 <see cref="ErrorCodes"/>）。
/// </summary>
public partial class ErrorCodeListWindow : Window
{
    public ErrorCodeListWindow()
    {
        InitializeComponent();

        CodeList.ItemsSource = ErrorCodes.All;
        CountText.Text = $"共 {ErrorCodes.All.Count} 个错误码";

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 与报错窗一致：圆角交给 DWM，不自绘
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            const int DWMWCP_ROUND = 2;
            var preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch { /* 老系统保持直角 */ }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { /* 拖动失败无所谓 */ }
    }

    void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
