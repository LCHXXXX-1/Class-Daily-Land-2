using ClassDailyLand2.UpdateContract;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace IDEUpdater;

/// <summary>
/// 更新器界面：左侧品牌栏 + 右侧「更新组件」面板（状态胶囊 / 进度 / 七步清单 / 完成）。
///
/// 编排（下载 → 校验 → 等 IDE 退出 → 清理 → 解压）仍然全在 <see cref="Program"/> 里，
/// 这里只实现 <see cref="IUpdateUi"/> 把过程显示出来 —— 所以"点重试"等于再跑一遍 <see cref="Program.RunAsync"/>，
/// 不需要两套流程。步骤点亮是从 <see cref="Status"/> 的关键词推导的：Program 一行不用改。
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

    // 步骤清单：Program 的每句状态文案按关键词点亮对应一格（-1 = 还没开始）
    // 注意：这里全限定 Shapes.Ellipse —— 本文件同时用 System.IO 的 Path，两个命名空间不能同时裸 using
    private System.Windows.Shapes.Ellipse[] _stepDots = null!;
    private TextBlock[] _stepLabels = null!;
    private int _step = -1;

    /// <summary>进程退出码（IDE 此时已退出，没人读它，但保留原本的语义）。</summary>
    public static int LastExitCode { get; private set; } = UpdateExitCode.BadJob;

    internal MainWindow(UpdateJob? job, string? loadError, bool directLaunch, JobLog log)
    {
        InitializeComponent();

        _job = job;
        _loadError = loadError;
        _directLaunch = directLaunch;
        _log = log;

        _stepDots = new[] { StepDot0, StepDot1, StepDot2, StepDot3, StepDot4, StepDot5, StepDot6 };
        _stepLabels = new[] { StepLbl0, StepLbl1, StepLbl2, StepLbl3, StepLbl4, StepLbl5, StepLbl6 };

        VersionText.Text = job is not null
            ? $"{job.App.CurrentVersion} → {job.App.TargetVersion}"
            : "—";

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

    public void Status(string text) => OnUi(() =>
    {
        StatusText.Text = text;

        var step = StepFromStatus(text);
        if (step >= 0) ApplyStep(step);
    });

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

        SetPill("失败", "Error", Brushes.White);

        RetryButton.Visibility = Visibility.Visible;
        FinishButton.IsEnabled = false;
    });

    public void Succeeded() => OnUi(() =>
    {
        // 最后一句文案由 Program 写（"安装完成，请点击「完成」。"），这里只收尾控件
        StatusText.Foreground = (Brush)FindResource("Subtle");

        ErrorText.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;

        SetPill("已完成", "Accent", Brushes.White);
        ApplyStep(6);

        // 不自动重启、也不自动关闭：等用户点「完成」再决定是否启动 IDE
        FinishButton.IsEnabled = true;

        SpeedText.Text = "0.0 MB/s";

        SetProgress(100);
    });

    // ————————————————————— 步骤推导 —————————————————————

    /// <summary>
    /// 从状态文案推当前步骤：0 准备 / 1 下载 / 2 校验 / 3 关闭程序 / 4 备份(清理) / 5 替换 / 6 完成。
    /// 关键词按"越具体越先"排；认不出来的文案返回 -1，步骤保持原位（不猜）。
    /// </summary>
    private static int StepFromStatus(string? text)
    {
        if (string.IsNullOrEmpty(text)) return -1;

        if (text!.Contains("安装完成")) return 6;
        if (text!.Contains("准备安装")) return 3;
        if (text!.Contains("校验")) return 2;
        if (text!.Contains("清理")) return 4;
        if (text!.Contains("替换") || text!.Contains("资源包")) return 5;
        if (text!.Contains("正在下载")) return 1;
        if (text!.Contains("连接")) return 0;

        return -1;
    }

    private void ApplyStep(int index)
    {
        if (index == _step) return;
        _step = index;

        var accent = (Brush)FindResource("Accent");
        var fg = (Brush)FindResource("Fg");
        var subtle = (Brush)FindResource("Subtle");

        for (var i = 0; i < _stepDots.Length; i++)
        {
            var reached = i <= index;
            _stepDots[i].Fill = reached ? accent : subtle;
            _stepLabels[i].Foreground = reached ? fg : subtle;
        }
    }

    /// <summary>状态胶囊：文案 + 背景令牌 + 前景。</summary>
    private void SetPill(string text, string bgKey, Brush foreground)
    {
        StatusPillText.Text = text;
        StatusPillBorder.Background = (Brush)FindResource(bgKey);
        StatusPillText.Foreground = foreground;
    }

    // ————————————————————— 生命周期 —————————————————————

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // 只在弹出这一瞬间抢一次前台：短暂抬 Topmost 再放下（SetForegroundWindow 常被系统前台锁拦下），
        // 之后不常驻置顶 —— 更新可能跑几分钟，窗口没资格一直压在用户所有窗口上面。
        Topmost = true;
        Topmost = false;
        Activate();

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
    /// 提示模式：用户直接双击了 IDEUpdater.exe（没带 --job / --url）。
    /// 界面保持完整卡片（就是设计稿那个样子），只把状态换成「请勿直接运行」，不进入任何更新流程。
    /// </summary>
    private void EnterWarningMode()
    {
        VersionText.Text = "请勿直接运行";
        SetPill("提示", "Warn", (Brush)FindResource("Fg"));

        StatusText.Text = "此程序为 DeveloperTool 的独立更新组件，请勿直接运行。" +
                          "请在 DeveloperTool 中点击「检查更新」进行升级。";
        StatusText.Foreground = (Brush)FindResource("Fg");

        ErrorText.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        FinishButton.IsEnabled = false;

        ApplyStep(-2);   // 全部退回未开始

        LastExitCode = UpdateExitCode.BadJob;
        _log.Write("提示模式：没有启动参数，已显示「请勿直接运行」");
    }

    private async Task StartAsync()
    {
        if (_job is null || _running) return;

        _running = true;
        RetryButton.Visibility = Visibility.Collapsed;
        FinishButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        StatusText.Foreground = (Brush)FindResource("Subtle");
        SetPill("正在处理", "ButtonBg", (Brush)FindResource("Fg"));

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
                // 停在完成态：用户点「完成」后再按复选框决定是否启动 IDE 并退出更新器
                Succeeded();
                return;
            }

            // 失败态已由 Program→Fail 画好；「完成」保持禁用，右上角 ✕ 关窗口
        }
        catch (OperationCanceledException)
        {
            LastExitCode = UpdateExitCode.Cancelled;
            Status("已取消。");
            RetryButton.Visibility = Visibility.Visible;
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

        // 复选框决定是否启动更新后的 IDE；不勾选就只是关闭更新器
        if (_job is not null && AutoRunCheckBox.IsChecked == true)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            StatusText.Foreground = (Brush)FindResource("Subtle");
            Status("正在启动 DeveloperTool…");

            if (!Program.LaunchApp(_job, _log, out var error))
            {
                StatusText.Text = "DeveloperTool 启动失败";
                StatusText.Foreground = (Brush)FindResource("Error");

                ErrorText.Text = "无法启动 DeveloperTool，请手动打开。" +
                                 (string.IsNullOrWhiteSpace(error) ? "" : "\n" + error);
                ErrorText.Visibility = Visibility.Visible;

                // 别关窗口：用户可以重试启动，也可以直接右上角 ✕ 退出更新器
                FinishButton.IsEnabled = true;
                return;
            }

            // 等 IDE 真正起来再退出更新器，避免父进程一关就把它带走
            await Task.Delay(2000);
        }

        ScheduleCleanup();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // 右上角 ✕：提示模式下就是关掉整个程序（本来也没有后台任务）
        if (_directLaunch)
        {
            Application.Current.Shutdown();
            return;
        }

        if (_running)
        {
            // 取消只是置位：下载/解压会在下一个检查点退出，不会硬杀进程
            _cts?.Cancel();
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
