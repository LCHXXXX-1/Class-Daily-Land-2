using System.Windows;
using System.Windows.Input;

namespace ClassDailyLand2.Installer.Online;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 先把色板灌进 Application.Resources，后面 MainWindow 的 DynamicResource 才有东西可查。
        // 必须在 base.OnStartup 之后、窗口创建之前完成（StartupUri 的窗口在这里之后才建）。
        base.OnStartup(e);

        // 触屏：关掉"按住弹圆圈"的系统反馈（安装器也是自绘按钮，圈圈会糊上去）
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window) Stylus.SetIsPressAndHoldEnabled(window, false);
            }));

        InstallerTheme.Apply(InstallerTheme.IsSystemDark());
    }
}
