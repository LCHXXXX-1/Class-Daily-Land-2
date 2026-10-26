using System.Windows;

namespace ClassDailyLand.Installer.Offline;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 先把色板灌进 Application.Resources，后面 MainWindow 的 DynamicResource 才有东西可查。
        // 必须在 base.OnStartup 之后、窗口创建之前完成（StartupUri 的窗口在这里之后才建）。
        base.OnStartup(e);

        InstallerTheme.Apply(InstallerTheme.IsSystemDark());
    }
}
