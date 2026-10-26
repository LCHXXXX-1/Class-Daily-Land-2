using System.Threading;
using System.Windows;

namespace ClassDailyLand2.Updater;

/// <summary>
/// 更新器 Application：没有 App.xaml 时也可以被 Program 手动 new 出来。
/// 这里集中挂全局异常捕获，避免更新器"打开 -> 卡住 -> 直接闪退"却没有任何线索。
/// </summary>
internal class App : Application
{
    private static int _attached;
    private static JobLog? _log;

    internal App()
    {
        Attach(_log);
    }

    internal static void Attach(JobLog? log)
    {
        _log = log;
        if (Interlocked.Exchange(ref _attached, 1) == 1) return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var text = "严重崩溃: " + e.ExceptionObject;
            SafeWrite(text);
            ShowFatal(text);
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            var text = "任务异常: " + e.Exception;
            SafeWrite(text);
            ShowFatal(text);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Attach(_log);
        base.OnStartup(e);
    }

    internal static void Report(Exception ex, string title = "更新器异常")
    {
        var text = title + ": " + ex;
        SafeWrite(text);
        ShowFatal(text);
    }

    private static void SafeWrite(string text)
    {
        try
        {
            _log?.Write(text);
        }
        catch
        {
            // 日志再坏也不能把异常处理器自己崩掉。
        }
    }

    private static void ShowFatal(string message)
    {
        try
        {
            if (Current?.Dispatcher is { } dispatcher && !dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(new Action(() => MessageBox.Show(message)));
            else
                MessageBox.Show(message);
        }
        catch
        {
            // 没有桌面会话（例如被服务拉起）时忽略，日志里已经留痕。
        }
    }
}
