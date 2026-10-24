using System.Windows;
using System.Windows.Input;
using ClassDailyLand.UpdateContract;

namespace ClassDailyLand.Updater;

/// <summary>
/// 更新器小窗。只负责"显示 + 取消"，干活的是 <see cref="UpdateEngine"/>。
///
/// 窗口刻意做小（520×336）：用户在等它，信息要一眼看完 ——
/// 现在到哪一步、还要不要等、能不能取消。
/// </summary>
public partial class UpdaterWindow : Window
{
    readonly string? _jobPath;
    readonly string? _rollbackDir;
    readonly TaskCompletionSource<int> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    UpdateEngine? _engine;

    /// <summary>用户点过取消（引擎每步都要问它）。</summary>
    public bool CancelRequested { get; private set; }

    /// <summary>成功后自动关闭的秒数（来自 job 的 ui.auto_close_seconds）。</summary>
    public int AutoCloseSeconds { get; set; } = 3;

    /// <summary>
    /// 失败也到秒自动关（<c>--auto-exit</c>）。0 = 关掉这个行为：
    /// 失败路径默认留着窗口等用户看完原因再点「关闭」。
    /// </summary>
    public int AutoExitSeconds { get; set; }

    /// <summary>跑完不关窗（<c>--capture</c> 截图自检用；正常流程都是 false）。</summary>
    public bool KeepOpenAfterFinish { get; set; }

    /// <summary>流程结束（成功/失败都算）时完成；截图自检等它。</summary>
    public Task<int> Completion => _finished.Task;

    public UpdaterWindow(string? jobPath, string? rollbackDir = null)
    {
        InitializeComponent();

        _jobPath = jobPath;
        _rollbackDir = rollbackDir;

        ThemeBoot.Apply();

        // ★ 只画圆角，不加背板：WPF 下"云母 + 透明背景"会把整窗渲染成黑色。
        //   背景用主题令牌的不透明颜色（见 AGENTS 坑清单）。
        SourceInitialized += (_, _) => Win32.ApplyRoundedCorners(this);

        Loaded += (_, _) => _ = RunAsync();
    }

    // ─────────────── 供引擎调用的显示方法 ───────────────
    // 引擎的 await 大多没配 ConfigureAwait(false)，正常都回到 UI 线程；
    // 这里仍统一走 Ui() 封送 —— 跨线程碰 WPF 属性会直接抛，更新器崩不起。

    void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    public void SetVersion(string from, string to) => Ui(() =>
        VersionText.Text = string.IsNullOrWhiteSpace(from) ? to : $"{from}  →  {to}");

    public void SetStage(string title, string? detail = null) => Ui(() =>
    {
        StageText.Text = title;
        DetailText.Text = detail ?? "";
    });

    /// <summary>显示进度（0–100）。percent 为 null = 不确定阶段，隐藏进度条。</summary>
    public void SetProgress(double? percent) => Ui(() =>
    {
        if (percent is null)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ProgressPanel.Visibility = Visibility.Visible;
        Bar.Value = Math.Clamp(percent.Value, 0, 100);
    });

    public void SetBytes(long received, long total) => Ui(() =>
        BytesText.Text = total > 0
            ? $"{FormatSize(received)} / {FormatSize(total)}（{received * 100.0 / total:0}%）"
            : FormatSize(received));

    public void SetSpeed(double bytesPerSecond) => Ui(() =>
        SpeedText.Text = bytesPerSecond > 1 ? FormatSize((long)bytesPerSecond) + "/s" : "");

    public void SetHint(string text) => Ui(() => HintText.Text = text);

    public void SetCancellable(bool enabled) => Ui(() =>
    {
        CancelButton.IsEnabled = enabled;
        CloseButton.IsEnabled = enabled;
    });

    // ─────────────── 交互 ───────────────

    void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (CancelRequested) return;
        CancelRequested = true;

        SetStage("正在取消…", "请稍候，正在安全地停下手头的操作。");
        SetCancellable(false);
        _engine?.Cancel();
    }

    // ─────────────── 主流程 ───────────────

    async Task RunAsync()
    {
        SetProgress(null);

        if (_rollbackDir is not null)
        {
            TitleText.Text = "恢复上一版本";
            SetStage("正在恢复…", _rollbackDir);
            HintText.Text = "正在从备份还原，完成后会自动启动程序。";
            SetCancellable(false);

            _engine = new UpdateEngine(this);
            var code = await _engine.RollbackAsync(_rollbackDir);
            Finish(code);
            return;
        }

        if (_jobPath is null || !File.Exists(_jobPath))
        {
            SetStage("无法更新", "任务文件缺失（job.json）。");
            SetHint("这个更新器要由主程序启动才有任务可执行。");
            Finish(UpdateExitCode.BadJob);
            return;
        }

        _engine = new UpdateEngine(this);
        var exitCode = await _engine.RunAsync(_jobPath);
        Finish(exitCode);
    }

    void Finish(int exitCode)
    {
        Ui(() =>
        {
            _finished.TrySetResult(exitCode);

            if (exitCode != UpdateExitCode.Success)
            {
                // 失败：保留窗口，让用户看完原因
                SetCancellable(false);
                CancelButton.Content = "关闭";
                CancelButton.IsEnabled = true;
                CancelButton.Click -= OnCancelClick;
                CancelButton.Click += (_, _) => ShutdownNow(exitCode);

                // --auto-exit：自检/支持场景下失败也到秒自动关，脚本才拿得到退出码
                if (!KeepOpenAfterFinish && AutoExitSeconds > 0)
                    StartAutoClose(exitCode, AutoExitSeconds);
                return;
            }

            SetCancellable(false);
            CancelButton.Content = "完成";   // 成功后不再是"取消"，窗口几秒后自动收起

            if (KeepOpenAfterFinish) return;   // 截图自检：挂着等人拍

            // 成功后按 job 声明的秒数自动收起（默认 3 秒，够读完"更新完成"）
            var seconds = Math.Clamp(AutoCloseSeconds, 0, 30);
            if (seconds <= 0)
            {
                ShutdownNow(exitCode);
                return;
            }

            StartAutoClose(exitCode, seconds);
        });
    }

    void StartAutoClose(int exitCode, int seconds)
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(seconds),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ShutdownNow(exitCode);
        };
        timer.Start();
    }

    void ShutdownNow(int exitCode)
    {
        try { Application.Current.Shutdown(exitCode); }
        catch { Environment.Exit(exitCode); }
    }

    static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / 1024.0 / 1024.0:0.#} MB";
    }
}
