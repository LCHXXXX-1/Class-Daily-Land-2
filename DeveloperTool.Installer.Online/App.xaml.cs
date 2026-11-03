using System.Windows;
using System.Windows.Input;

namespace DeveloperTool.Installer.Online;

public partial class App : Application
{
    public App()
    {
        // InvariantGlobalization=true 之后，运行时里没有任何区域数据（ICU 不随单文件打包），
        // 任何一次 CurrentCulture 查表都可能抛 CultureNotFoundException —— 典型就是 WPF
        // 输入框去取输入法语言时。在窗口起来之前把两条线程文化都钉成不变文化，绕开查表。
        //
        // 位置：构造函数是比 InitializeComponent()（由生成的 App.g.cs 的 Main 调用）更早的
        // 唯一时机，必须放在这里，不能挪进 OnStartup。
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture =
            System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture =
            System.Globalization.CultureInfo.InvariantCulture;
    }

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
