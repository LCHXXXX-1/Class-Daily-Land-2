using ClassDailyLand.App.Controls;
using System.Diagnostics;
using System.Windows;

namespace ClassDailyLand.App.Views;

/// <summary>
/// 二级浮层的统一入口：页面只负责「把哪个面板摆到遮罩上」。
/// 浮层画在设置窗口内部（对位 web 版 ed-host），宿主不用另开窗。
/// </summary>
public static class DialogLauncher
{
    public static SettingsWindow? OwnerOf(DependencyObject child) => Window.GetWindow(child) as SettingsWindow;

    /// <summary>显示浮层面板（面板自己带 EdPanel 样式与尺寸）。</summary>
    public static void Show(DependencyObject child, FrameworkElement panel)
        => OwnerOf(child)?.ShowOverlay(panel);

    public static void Close(DependencyObject child) => OwnerOf(child)?.CloseOverlay();

    public static void Toast(DependencyObject child, InfoBarSeverity severity, string title, string message)
        => OwnerOf(child)?.Toast(severity, title, message);

    /// <summary>窗口内确认框（替代系统 MessageBox）。</summary>
    public static void Confirm(DependencyObject child, string title, string message, Action onOk,
                               string okText = "确定", string cancelText = "取消", Action? onCancel = null)
        => OwnerOf(child)?.Confirm(title, message, onOk, okText, cancelText, onCancel);

    public static bool IsConfirmOpen(DependencyObject child) => OwnerOf(child)?.IsConfirmOpen == true;

    /// <summary>在资源管理器里打开一个目录（不存在时先建）。</summary>
    public static void OpenFolder(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* 打不开目录不是致命错误 */ }
    }
}
