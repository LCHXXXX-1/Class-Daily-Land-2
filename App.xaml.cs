using ClassDailyLand2.UpdateContract;
using System.Windows;
using System.Windows.Threading;

namespace CDL2.Updater;

public partial class App : Application
{
    internal static UpdaterLog Log { get; } = new();

    /// <summary>更新器退出码：窗口关闭时由 <see cref="UpdaterWindow"/> 写入 Environment.ExitCode。</summary>
    internal static int ExitCode { get; set; } = UpdateExitCode.BadJob;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("UI 线程异常：" + args.Exception);
            try
            {
                MessageBox.Show("更新器遇到界面异常：" + args.Exception.Message, "Class Daily Land 2 · 更新器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { /* 连弹窗都失败就算了 */ }
            args.Handled = true;
        };

        Theme.Apply();

        var cli = CommandLine.Parse(e.Args);
        var window = new UpdaterWindow(cli);
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();
        window.Show();
    }

    /// <summary>
    /// 退出兜底：引擎的取消依赖各处 ct 检查，理论上窗口一关进程就该结束；
    /// 但任何一处 await 挂在不可取消的 IO 上都会让进程变成看不见的"僵尸"，
    /// 主程序之后也不再拉起更新器（用户实测踩过）。这里硬退出，退出码保留。
    /// </summary>
    private void OnExit(object sender, ExitEventArgs e)
    {
        Environment.Exit(ExitCode);
    }
}
