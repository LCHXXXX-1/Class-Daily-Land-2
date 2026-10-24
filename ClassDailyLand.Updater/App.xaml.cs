using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClassDailyLand.Updater;

/// <summary>
/// 更新器入口。**没有主窗**（ShutdownMode=OnExplicitShutdown）：窗口由本类创建，
/// 引擎跑完自己调 <see cref="Application.Shutdown(int)"/> 退出并带上退出码。
///
/// 命令行（主程序传进来）：
///   --job &lt;path&gt;      要执行的任务文件（job.json）
///   --rollback &lt;dir&gt;  从备份目录回滚（上次更新被硬杀时用）
///   --log &lt;path&gt;      额外的日志落点
///   --capture &lt;png&gt;  截图自检：跑完流程后把窗口拍成 png 再退出（打包前验证外观用，
///                      不会真的自动关闭窗口；配 --job 可拍任一状态）
///   --auto-exit &lt;s&gt;  到秒自动关窗退出（**失败也关**）。自检与远程支持用 ——
///                      失败路径默认留着窗口等用户看完原因，脚本拿不到退出码。
/// </summary>
public partial class App : Application
{
    public static string? JobPath { get; private set; }
    public static string? RollbackDir { get; private set; }
    public static string? LogPath { get; private set; }
    public static string? CapturePath { get; private set; }
    public static int AutoExitSeconds { get; private set; }

    UpdaterWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 引擎/渲染抛出的异常都要留痕：更新器是唯一还能动手的进程，
        // 它自己崩了用户就只能看到"更新失败"四个字。
        DispatcherUnhandledException += (_, args) =>
        {
            UpdaterLog.Error("未处理异常：" + args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            UpdaterLog.Error("域异常：" + args.ExceptionObject);

        ParseArgs(e.Args);

        if (CapturePath is not null)
        {
            // 截图自检：窗口挂着不关，等人拍完再退
            _window = new UpdaterWindow(JobPath) { KeepOpenAfterFinish = true };
            _window.Show();
            _ = CaptureAsync(_window, CapturePath);
        }
        else if (RollbackDir is not null)
        {
            // --rollback：不看 job，直接还原备份
            _window = new UpdaterWindow(null, rollbackDir: RollbackDir);
        }
        else if (JobPath is not null && File.Exists(JobPath))
        {
            _window = new UpdaterWindow(JobPath);
        }
        else
        {
            UpdaterLog.Error("缺少 --job 参数或任务文件不存在：" + (JobPath ?? "(null)"));
            Shutdown(ClassDailyLand.UpdateContract.UpdateExitCode.BadJob);
            return;
        }

        if (_window is not null)
        {
            if (CapturePath is null && AutoExitSeconds > 0) _window.AutoExitSeconds = AutoExitSeconds;
            if (CapturePath is null) _window.Show();
        }
    }

    static void ParseArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--job" when i + 1 < args.Length:
                    JobPath = args[++i];
                    break;
                case "--rollback" when i + 1 < args.Length:
                    RollbackDir = args[++i];
                    break;
                case "--log" when i + 1 < args.Length:
                    LogPath = args[++i];
                    break;
                case "--capture" when i + 1 < args.Length:
                    CapturePath = args[++i];
                    break;
                case "--auto-exit" when i + 1 < args.Length && int.TryParse(args[i + 1], out var seconds):
                    AutoExitSeconds = Math.Clamp(seconds, 0, 600);
                    i++;
                    break;
            }
        }

        if (LogPath is not null) UpdaterLog.SetPath(LogPath);
    }

    /// <summary>等流程跑完，把窗口渲染成 png（2 倍尺寸，字更清楚）。</summary>
    async Task CaptureAsync(UpdaterWindow window, string pngPath)
    {
        try
        {
            // 正常几秒内就结束；挂 60 秒是防"引擎卡死"把自检也拖死
            await Task.WhenAny(window.Completion, Task.Delay(60_000));
            await Task.Delay(500);   // 等最后的布局与文字稳定

            window.UpdateLayout();
            var scale = 2;
            var rtb = new RenderTargetBitmap(
                (int)(window.Width * scale), (int)(window.Height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngPath))!);
            using var stream = File.Create(pngPath);
            encoder.Save(stream);

            UpdaterLog.Info("截图已保存：" + pngPath);
        }
        catch (Exception ex)
        {
            UpdaterLog.Error("截图失败：" + ex.Message);
        }
        finally
        {
            Dispatcher.Invoke(new Action(() =>
            {
                try { Application.Current.Shutdown(0); }
                catch { Environment.Exit(0); }
            }));
        }
    }
}
