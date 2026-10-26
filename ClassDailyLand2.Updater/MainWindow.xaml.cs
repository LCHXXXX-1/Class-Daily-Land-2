using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClassDailyLand2.UpdateContract;

namespace ClassDailyLand2.Updater;

/// <summary>
/// 更新器界面：一张无边框圆角卡片 + 状态 / 进度 / 错误 + 重试与取消。
///
/// 编排（下载 → 校验 → 强杀主程序 → 清理 → 解压）仍然全在 <see cref="Program"/> 里，
/// 这里只实现 <see cref="IUpdateUi"/> 把过程显示出来 —— 所以"点重试"等于再跑一遍 <see cref="Program.RunAsync"/>，
/// 不需要两套流程。
/// </summary>
public partial class MainWindow : Window, IUpdateUi
{
    private readonly UpdateJob? _job;
    private readonly string? _loadError;
    private readonly bool _directLaunch;
    private readonly JobLog _log;

    private readonly DispatcherTimer _timer;
    private double _targetPercent;
    private double _shownPercent;
    private string _detail = "";

    // 实时下载速度：每 300ms 算一次区间速度，避免每读一块就刷一次界面
    private readonly Stopwatch _speedWatch = new();
    private long _speedLastBytes;
    private double _speedLastSeconds;

    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _cleanupScheduled;

    /// <summary>进程退出码（主程序此时已退出，没人读它，但保留原本的语义）。</summary>
    public static int LastExitCode { get; private set; } = UpdateExitCode.BadJob;

    internal MainWindow(UpdateJob? job, string? loadError, bool directLaunch, JobLog log)
    {
        InitializeComponent();

        _job = job;
        _loadError = loadError;
        _directLaunch = directLaunch;
        _log = log;

        // 进度条不做逐字节跳变：用一个 16ms 的插值让它平滑追上目标值。
        // 先建 Timer，再建下载进度回调：回调可能比构造函数更早被 Progress<T> 触发。
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnTick, Dispatcher);
        _timer.Stop();

        // 下载占整条进度的 0~90%，剩下的留给解压与替换。
        // Progress<T> 的回调可能在后台线程触发，所有控件更新必须走 Dispatcher。
        Download = new System.Progress<DownloadProgressReport>(report =>
        {
            var percent = report.Total > 0
                ? Math.Clamp(report.Received * 90.0 / report.Total, 0, 90)
                : 0;
            var detail = $"{Megabytes(report.Received)} / {Megabytes(report.Total)} MB";

            // 区间速度（不是平均值）：最近 300ms 内收了多少字节。
            var now = _speedWatch.Elapsed.TotalSeconds;
            var window = now - _speedLastSeconds;
            var delta = report.Received - _speedLastBytes;
            var updateSpeed = window >= 0.3 && delta >= 0;
            var speed = updateSpeed ? $"{(delta / window) / 1024 / 1024:F1} MB/s" : "";

            try
            {
                Dispatcher.Invoke(() =>
                {
                    _targetPercent = Math.Clamp(percent, 0, 100);
                    _detail = detail;
                    DetailText.Text = detail;
                    if (!_timer.IsEnabled) _timer.Start();

                    if (updateSpeed)
                    {
                        SpeedText.Text = speed;
                        _speedLastBytes = report.Received;
                        _speedLastSeconds = now;
                    }
                });
            }
            catch (Exception ex)
            {
                _log.Write("下载进度更新失败：" + ex);
            }
        });
    }

    public IProgress<DownloadProgressReport> Download { get; }

    // ————————————————————— IUpdateUi —————————————————————

    public void Status(string text) => OnUi(() => StatusText.Text = text);

    public void Detail(string text) => OnUi(() =>
    {
        _detail = text;
        DetailText.Text = text;
    });

    public void SetProgress(double percent) => OnUi(() =>
    {
        _targetPercent = Math.Clamp(percent, 0, 100);
        if (!_timer.IsEnabled) _timer.Start();
    });

    public void DownloadFinished() => OnUi(() => SpeedText.Text = "0.0 MB/s");

    public void Fail(string message) => OnUi(() =>
    {
        Status(message);
        StatusText.Foreground = (Brush)FindResource("Error");

        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;

        RetryButton.Visibility = Visibility.Visible;
        FinishButton.Visibility = Visibility.Collapsed;

        CancelButton.Visibility = Visibility.Visible;
        CancelButton.Content = "关闭";
        CancelButton.IsEnabled = true;

        SpeedText.Text = "0.0 MB/s";
    });

    public void Succeeded() => OnUi(() =>
    {
        // 最后一句文案由 Program 写（"安装完成，请点击「完成」。"），这里只收尾控件
        StatusText.Foreground = (Brush)FindResource("Subtle");

        ErrorText.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;

        // 不自动重启、也不自动关闭：等用户点「完成」再决定是否启动主程序
        CancelButton.Visibility = Visibility.Collapsed;
        FinishButton.Visibility = Visibility.Visible;
        FinishButton.IsEnabled = true;

        SpeedText.Text = "0.0 MB/s";

        SetProgress(100);
    });

    // ————————————————————— 生命周期 —————————————————————

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // 直接双击打开的：只提示"请勿直接运行"，不进入任何更新流程
        if (_directLaunch)
        {
            EnterWarningMode();
            return;
        }

        if (_loadError is not null)
        {
            Fail("无法开始更新：" + _loadError);
            return;
        }

        // 先把"正在连接服务器…"亮出来，稍停 300ms 让 UI 真的画出来，再开始下载
        Status("正在连接服务器…");
        SetProgress(0);
        await Task.Delay(300);

        await StartAsync();
    }

    /// <summary>
    /// 提示模式：用户直接双击了 Updater.exe（没带 --job / --url）。
    /// 收掉进度相关控件，只留一句说明与「关闭」，避免"双击后闪退/什么也没发生"。
    /// </summary>
    private void EnterWarningMode()
    {
        TitleText.Foreground = (Brush)FindResource("Warn");

        StatusText.Text = "此程序为 Class Daily Land 2 的独立更新组件，请勿直接运行。" +
                          "请在主程序或开发工具中点击「检查更新」进行升级。";
        StatusText.Foreground = (Brush)FindResource("Fg");
        StatusText.Margin = new Thickness(0, 12, 0, 2);

        DownloadProgress.Visibility = Visibility.Collapsed;
        SpeedText.Visibility = Visibility.Collapsed;
        DetailRow.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;

        RetryButton.Visibility = Visibility.Collapsed;
        FinishButton.Visibility = Visibility.Collapsed;
        AutoRunCheckBox.Visibility = Visibility.Collapsed;
        CancelButton.Content = "关闭";
        CancelButton.IsEnabled = true;

        LastExitCode = UpdateExitCode.BadJob;
        _log.Write("提示模式：没有启动参数，已显示「请勿直接运行」");
    }

    private async Task StartAsync()
    {
        if (_job is null || _running) return;

        _running = true;
        RetryButton.Visibility = Visibility.Collapsed;
        FinishButton.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        StatusText.Foreground = (Brush)FindResource("Subtle");
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.Content = "取消";
        CancelButton.IsEnabled = true;

        _speedWatch.Restart();
        _speedLastBytes = 0;
        _speedLastSeconds = 0;
        SpeedText.Text = "0.0 MB/s";

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            LastExitCode = await Program.RunAsync(_job, _log, this, _cts.Token);

            if (LastExitCode == UpdateExitCode.Success)
            {
                // 停在完成态：用户点「完成」后再按复选框决定是否启动主程序并退出更新器
                Succeeded();
                return;
            }

            // 失败态已由 Program→Fail 画好；这里只保证按钮可用
            CancelButton.Content = "关闭";
        }
        catch (OperationCanceledException)
        {
            LastExitCode = UpdateExitCode.Cancelled;
            Status("已取消。");
            RetryButton.Visibility = Visibility.Visible;
            CancelButton.Content = "关闭";
        }
        catch (Exception ex)
        {
            _log.Write("更新界面异常：" + ex);
            LastExitCode = UpdateExitCode.ReplaceFailed;
            Fail("更新失败：" + ex.Message);
        }
        finally
        {
            _running = false;
        }
    }

    // ————————————————————— 交互 —————————————————————

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        await StartAsync();
    }

    private async void Finish_Click(object sender, RoutedEventArgs e)
    {
        FinishButton.IsEnabled = false;

        // 复选框决定是否启动更新后的主程序；不勾选就只是关闭更新器
        if (_job is not null && AutoRunCheckBox.IsChecked == true)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            StatusText.Foreground = (Brush)FindResource("Subtle");
            Status("正在启动主程序…");

            if (!Program.LaunchApp(_job, _log, out var error))
            {
                StatusText.Text = "主程序启动失败";
                StatusText.Foreground = (Brush)FindResource("Error");

                ErrorText.Text = "无法启动主程序，请手动打开。" +
                                 (string.IsNullOrWhiteSpace(error) ? "" : "\n" + error);
                ErrorText.Visibility = Visibility.Visible;

                // 别关窗口：用户可以重试启动，也可以直接关闭更新器
                FinishButton.IsEnabled = true;
                CancelButton.Visibility = Visibility.Visible;
                CancelButton.Content = "关闭";
                CancelButton.IsEnabled = true;
                return;
            }

            // 等主程序真正起来再退出更新器，避免父进程一关就把它带走
            await Task.Delay(2000);
        }

        ScheduleCleanup();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // 提示模式下"关闭"就是关掉整个程序（本来也没有后台任务）
        if (_directLaunch)
        {
            Application.Current.Shutdown();
            return;
        }

        if (_running)
        {
            // 取消只是置位：下载/解压会在下一个检查点退出，不会硬杀进程
            _cts?.Cancel();
            CancelButton.IsEnabled = false;
            Status("正在取消…");
            return;
        }

        Close();
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 无边框窗口：按住卡片任意空白处可以拖动（按钮自己会吃掉点击）
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* 鼠标已经抬起，忽略 */ }
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关窗口 = 取消；已经成功时顺手把临时文件与更新器自己清掉
        _cts?.Cancel();
        if (LastExitCode == UpdateExitCode.Success) ScheduleCleanup();
        base.OnClosing(e);
    }

    /// <summary>成功收尾时清理下载包、临时目录与更新器自身；只安排一次。</summary>
    private void ScheduleCleanup()
    {
        if (_cleanupScheduled || _job is null) return;
        _cleanupScheduled = true;

        var zip = Path.Combine(_job.Paths.WorkDir, "payload.zip");
        Cleanup.Schedule(_job.Paths.WorkDir, zip, _log);
    }

    // ————————————————————— 小工具 —————————————————————

    private void OnTick(object? sender, EventArgs e)
    {
        var delta = _targetPercent - _shownPercent;
        if (Math.Abs(delta) < 0.5)
        {
            _shownPercent = _targetPercent;
            _timer.Stop();
        }
        else
        {
            _shownPercent += delta * 0.25;
        }

        DownloadProgress.Value = _shownPercent;
        PercentText.Text = $"{_shownPercent:0}%";
        DetailText.Text = _detail;
    }

    /// <summary>标记一下：所有界面更新都走这里，线程安全且不需要调用方关心在哪个线程。</summary>
    private void OnUi(Action action)
    {
        try
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.Invoke(action);
        }
        catch (Exception ex)
        {
            // UI 更新失败不能把更新器整个进程带崩；至少留下原因，主流程继续走失败/重试路径。
            _log.Write("界面更新失败：" + ex);
        }
    }

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0");
}
