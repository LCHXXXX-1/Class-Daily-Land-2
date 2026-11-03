using ClassDailyLand2.UpdateContract;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace CDL2.Updater;

/// <summary>
/// 更新器窗口：无边界卡片 + 阶段勾选 + 恢复操作。
/// 状态机只有四个终态：运行中 / 成功 / 失败 / 已取消；按钮条按状态重排。
/// </summary>
public partial class UpdaterWindow : Window, IUpdaterUi
{
    private enum RunState { Idle, Running, Success, Failed, Cancelled }

    private static readonly (UpdateStage Stage, string Label)[] StageOrder =
    {
        (UpdateStage.Prepare, "准备"),
        (UpdateStage.Download, "下载"),
        (UpdateStage.Verify, "校验"),
        (UpdateStage.StopApp, "关闭程序"),
        (UpdateStage.Backup, "备份"),
        (UpdateStage.Replace, "替换"),
        (UpdateStage.Finish, "完成"),
    };

    private readonly CommandLine _cli;
    private readonly Dictionary<UpdateStage, TextBlock> _stageMarks = new();
    private readonly Dictionary<UpdateStage, TextBlock> _stageDots = new();

    private UpdateJob? _job;
    private string? _loadError;
    private UpdaterEngine? _engine;
    private CancellationTokenSource? _cts;
    private RunState _state = RunState.Idle;
    private int _exitCode = UpdateExitCode.BadJob;
    private string? _lastBackupDir;
    private bool _launched;
    private bool _finished;
    private double _pendingFraction = -1;

    public UpdaterWindow(CommandLine cli)
    {
        _cli = cli;
        InitializeComponent();
        ApplyTheme();
        DwmRoundCorners();

        SourceInitialized += (_, _) => DwmRoundCorners();
        Loaded += OnWindowLoadedForBar;
        Loaded += Window_Loaded;
        BuildStageStrip();
    }

    // ─────────────────────────── 外观 ───────────────────────────

    private void ApplyTheme()
    {
        void Set(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Resources[key] = brush;
        }

        Set("_Bg", Theme.Background);
        Set("_CardBorder", Theme.CardBorder);
        Set("_Track", Theme.Track);
        Set("_Text", Theme.Text);
        Set("_TextSecondary", Theme.TextSecondary);
        Set("_Accent", Theme.Accent);
        Set("_Error", Theme.Error);
        Set("_Success", Theme.Success);
        Set("_ButtonFace", Theme.ButtonFace);
        Set("_ButtonHover", Theme.ButtonHover);
    }

    /// <summary>圆角与阴影交给 DWM（自绘透明窗口会丢系统圆角，踩过）。</summary>
    private void DwmRoundCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            const int DWMWCP_ROUND = 2;
            var preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch { /* 老系统保持直角 */ }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { /* 拖动失败无所谓 */ }
        }
    }

    // ─────────────────────────── 启动 ───────────────────────────

    /// <summary>窗口加载后先补刷进度条宽度（加载前 ActualWidth 是 0）。</summary>
    private void OnWindowLoadedForBar(object sender, RoutedEventArgs e)
    {
        if (_pendingFraction >= 0)
        {
            var f = _pendingFraction;
            _pendingFraction = -1;
            SetBar(f);
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_cli.DirectLaunch)
        {
            // 用户直接双击了 Updater.exe：解释它是什么，不执行任何更新
            Title = "Class Daily Land 2 · 更新组件";
            TitleText.Text = "更新组件";
            VersionText.Text = "请勿直接运行";
            StatusText.Text = "这是 Class Daily Land 2 的更新组件。更新由主程序在后台发起，" +
                              "下载与安装都会自动完成，不需要手动打开这个程序。";
            SetButtons(cancel: false, retry: false, rollback: false, launch: false, done: true);
            PercentText.Text = "";
            _finished = true;
            _exitCode = UpdateExitCode.BadJob;
            return;
        }

        if (_cli.HasRollback)
        {
            Title = "Class Daily Land 2 · 回滚";
            TitleText.Text = "回滚到上一版本";
            VersionText.Text = _cli.RollbackDir;
            PercentText.Text = "";
            StageStrip.Visibility = Visibility.Collapsed;
            ProgressFill.Width = 0;

            SetButtons(cancel: false, retry: false, rollback: false, launch: false, done: false);
            StatusText.Text = "正在按备份账本恢复程序文件…";

            var result = await Task.Run(() =>
                ReplaceJournal.Restore(_cli.RollbackDir!, null, App.Log)).ConfigureAwait(true);

            if (result is null)
            {
                StatusText.Text = "已恢复到更新前的状态。";
                ErrorText.Visibility = Visibility.Collapsed;
                SetButtons(cancel: false, retry: false, rollback: false, launch: true, done: true);
                _exitCode = UpdateExitCode.Success;
            }
            else
            {
                StatusText.Text = "回滚完成，但有个别文件没有恢复成功：";
                ErrorText.Text = result;
                ErrorText.Visibility = Visibility.Visible;
                SetButtons(cancel: false, retry: false, rollback: false, launch: true, done: true);
                _exitCode = UpdateExitCode.ReplaceFailed;
            }
            _finished = true;
            Environment.ExitCode = _exitCode;
            return;
        }

        // 正常路径：先读任务
        _job = JobLoader.Load(_cli, out _loadError);
        if (_job is null)
        {
            ShowLoadFailure(_loadError ?? "更新任务无效");
            return;
        }

        AttachLog();
        VersionText.Text = $"{_job.App.CurrentVersion} → {_job.App.TargetVersion}（{_job.Channel}）";
        Title = $"Class Daily Land 2 · 更新到 {_job.App.TargetVersion}";
        TitleText.Text = "正在更新 Class Daily Land 2";

        await StartRunAsync();
    }

    private void AttachLog()
    {
        if (_job is null) return;
        App.Log.Attach(_job.Paths.Log);
        LogHintText.Text = "日志：" + _job.Paths.Log;
    }

    private async Task StartRunAsync()
    {
        if (_job is null) return;

        _state = RunState.Running;
        _cts = new CancellationTokenSource();
        _engine = new UpdaterEngine(_job, App.Log, this);

        ResetStageMarks();
        ErrorText.Visibility = Visibility.Collapsed;
        SetButtons(cancel: true, retry: false, rollback: false, launch: false, done: false);

        _exitCode = await _engine.RunAsync(_cts.Token).ConfigureAwait(true);

        _finished = true;
        Environment.ExitCode = _exitCode;
        App.ExitCode = _exitCode;

        if (_state == RunState.Success && _job!.Relaunch)
            StartAutoLaunchCountdown();
    }

    // ─────────────────────────── 引擎回调 ───────────────────────────

    private static readonly Dictionary<UpdateStage, (double Start, double End)> StageRange = new()
    {
        [UpdateStage.Prepare] = (0.00, 0.05),
        [UpdateStage.Download] = (0.05, 0.35),
        [UpdateStage.Verify] = (0.35, 0.45),
        [UpdateStage.StopApp] = (0.45, 0.55),
        [UpdateStage.Backup] = (0.55, 0.65),
        [UpdateStage.Replace] = (0.65, 0.95),
        [UpdateStage.Finish] = (0.95, 1.00),
    };

    public void OnStage(UpdateStage stage, string message, double progress)
    {
        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "_TextSecondary");
        ErrorText.Visibility = Visibility.Collapsed;

        MarkStage(stage);

        var (start, end) = StageRange.TryGetValue(stage, out var range) ? range : (0.0, 1.0);
        var clamped = Math.Clamp(progress, 0.0, 1.0);
        SetBar(start + (end - start) * clamped);

        // 进入"关闭程序"之后就不许取消了：那时动了一半没法干净退场
        CancelButton.Visibility = stage >= UpdateStage.StopApp ? Visibility.Collapsed : Visibility.Visible;
    }

    public void OnDownloadProgress(long received, long? total)
    {
        var fraction = total is > 0 ? Math.Clamp((double)received / total.Value, 0, 1) : 0;
        var (start, end) = StageRange[UpdateStage.Download];
        SetBar(start + (end - start) * fraction);

        PercentText.Text = total is > 0
            ? $"{fraction * 100:0}%"
            : $"{received / 1048576.0:0.0} MB";
    }

    public void OnSuccess(string message)
    {
        _state = RunState.Success;
        _exitCode = UpdateExitCode.Success;

        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "_Success");
        MarkAllDone();
        SetBar(1.0);
        PercentText.Text = "100%";

        SetButtons(cancel: false, retry: false, rollback: false,
            launch: _job?.Relaunch ?? true, done: true);
    }

    public void OnFailure(int exitCode, string message)
    {
        _state = RunState.Failed;
        _exitCode = exitCode;

        // 失败时有没有可用备份，决定要不要亮"回滚"按钮
        _lastBackupDir = FindLatestBackupDir();

        StatusText.Text = "更新失败：";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "_Error");
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;

        SetButtons(cancel: false, retry: true, rollback: _lastBackupDir is not null,
            launch: false, done: true);
    }

    public void OnCancelled()
    {
        _state = RunState.Cancelled;
        _exitCode = UpdateExitCode.Cancelled;

        StatusText.Text = "已取消更新。";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "_TextSecondary");
        SetButtons(cancel: false, retry: true, rollback: false, launch: false, done: true);
    }

    // ─────────────────────────── 阶段条 ───────────────────────────

    private void BuildStageStrip()
    {
        for (var i = 0; i < StageOrder.Length; i++)
        {
            var (stage, label) = StageOrder[i];

            var dot = new TextBlock
            {
                Text = "●",
                FontSize = 9,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Brush(Theme.TextSecondary),
            };

            var text = new TextBlock
            {
                Text = label,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Brush(Theme.TextSecondary),
            };

            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0) };
            item.Children.Add(dot);
            item.Children.Add(text);

            if (i < StageOrder.Length - 1)
            {
                item.Children.Add(new TextBlock
                {
                    Text = "—",
                    FontSize = 11,
                    Margin = new Thickness(6, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Theme.Brush(Theme.Track),
                });
            }

            _stageMarks[stage] = text;
            _stageDots[stage] = dot;
            StageStrip.Children.Add(item);
        }

        ResetStageMarks();
    }

    private void MarkStage(UpdateStage stage)
    {
        var index = Array.FindIndex(StageOrder, s => s.Stage == stage);

        for (var i = 0; i < StageOrder.Length; i++)
        {
            if (!_stageMarks.TryGetValue(StageOrder[i].Stage, out var text)) continue;
            var dot = _stageDots.TryGetValue(StageOrder[i].Stage, out var d) ? d : null;

            if (i < index)
            {
                text.Foreground = Theme.Brush(Theme.TextSecondary);
                if (dot is not null)
                {
                    dot.Foreground = Theme.Brush(Theme.Success);
                    dot.Text = "✓";
                }
            }
            else if (i == index)
            {
                text.Foreground = Theme.Brush(Theme.Accent);
                if (dot is not null) dot.Foreground = Theme.Brush(Theme.Accent);
            }
            else
            {
                text.Foreground = Theme.Brush(Theme.TextSecondary);
                if (dot is not null)
                {
                    dot.Foreground = Theme.Brush(Theme.TextSecondary);
                    dot.Text = "●";
                }
            }
        }
    }

    private void MarkAllDone()
    {
        foreach (var (stage, _) in StageOrder)
        {
            if (_stageMarks.TryGetValue(stage, out var text)) text.Foreground = Theme.Brush(Theme.Success);
            if (_stageDots.TryGetValue(stage, out var dot))
            {
                dot.Foreground = Theme.Brush(Theme.Success);
                dot.Text = "✓";
            }
        }
    }

    private void ResetStageMarks() => MarkStage(UpdateStage.Prepare);

    // ─────────────────────────── 进度条 ───────────────────────────

    private void SetBar(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        PercentText.Text = $"{fraction * 100:0}%";

        // 宽度在 Loaded 之后才有效：先记下目标，Loaded 时再刷一次
        if (!IsLoaded)
        {
            _pendingFraction = fraction;
            return;
        }

        var track = TrackBorder.ActualWidth;
        if (track > 0) ProgressFill.Width = track * fraction;
    }

    // ─────────────────────────── 按钮状态 ───────────────────────────

    private void SetButtons(bool cancel, bool retry, bool rollback, bool launch, bool done)
    {
        // 恢复行：只在非运行态且有任务时显示；直接双击更新器（direct launch）没有可恢复的东西
        RecoveryRow.Visibility = (_state is RunState.Success or RunState.Failed or RunState.Cancelled)
                                 && _job is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

        CancelButton.Visibility = cancel ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        RollbackButton.Visibility = rollback ? Visibility.Visible : Visibility.Collapsed;
        LaunchButton.Visibility = launch ? Visibility.Visible : Visibility.Collapsed;
        DoneButton.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─────────────────────────── 交互 ───────────────────────────

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (_state == RunState.Failed || _state == RunState.Cancelled)
            await StartRunAsync();
    }

    private void OnRollbackClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastBackupDir)) return;

        RollbackButton.IsEnabled = false;
        StatusText.Text = "正在按备份账本恢复程序文件…";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "_TextSecondary");

        var result = ReplaceJournal.Restore(_lastBackupDir, null, App.Log);

        if (result is null)
        {
            StatusText.Text = "已恢复到更新前的状态。可以点「立即启动」重新打开程序。";
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "_Success");
            _exitCode = UpdateExitCode.Success;
            App.ExitCode = UpdateExitCode.Success;
        }
        else
        {
            StatusText.Text = "回滚完成，但有个别文件没有恢复成功：";
            ErrorText.Text = result;
            ErrorText.Visibility = Visibility.Visible;
            _exitCode = UpdateExitCode.ReplaceFailed;
            App.ExitCode = UpdateExitCode.ReplaceFailed;
        }

        SetButtons(cancel: false, retry: false, rollback: false, launch: true, done: true);
    }

    private void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (!TryLaunchMainApp(out var launchError))
        {
            MessageBox.Show(this, "启动主程序失败：" + launchError, "Class Daily Land 2 · 更新器",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _launched = true;
        Close();
    }

    /// <summary>重启软件：更新完成后直接把主程序拉起来（更新器不碰主程序的收尾流程）。</summary>
    private void OnRestartAppClick(object sender, RoutedEventArgs e)
    {
        if (!TryLaunchMainApp(out var launchError))
        {
            MessageBox.Show(this, "启动主程序失败：" + launchError, "Class Daily Land 2 · 更新器",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _launched = true;
        Close();
    }

    private bool TryLaunchMainApp(out string? error)
    {
        error = null;
        if (_engine is null)
        {
            error = "没有可启动的更新任务";
            return false;
        }
        return _engine.LaunchApp(out error);
    }

    private void OnDoneClick(object sender, RoutedEventArgs e) => Close();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ─────────────────────────── 自动启动 ───────────────────────────

    private async void StartAutoLaunchCountdown()
    {
        var seconds = Math.Clamp(_job?.Ui.AutoCloseSeconds ?? 3, 1, 30);

        for (var left = seconds; left > 0; left--)
        {
            DoneButton.Content = $"完成（{left} 秒后自动启动）";
            await Task.Delay(1000);
        }

        // 用户在这几秒里点过别的就别抢戏
        if (_state != RunState.Success || _launched) return;

        if (!TryLaunchMainApp(out _)) return;

        _launched = true;
        Close();
    }

    // ─────────────────────────── 失败读任务 ───────────────────────────

    private void ShowLoadFailure(string message)
    {
        _state = RunState.Failed;
        _exitCode = UpdateExitCode.BadJob;
        _finished = true;
        Environment.ExitCode = _exitCode;
        App.ExitCode = _exitCode;

        Title = "Class Daily Land 2 · 更新失败";
        TitleText.Text = "更新任务无效";
        VersionText.Text = "";
        StatusText.Text = "无法执行更新：";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "_Error");
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        PercentText.Text = "";

        SetButtons(cancel: false, retry: false, rollback: false, launch: false, done: true);
    }

    /// <summary>失败时找最近一次带 journal 的备份（重装/更新共用 BackupRoot，按时间取最新）。</summary>
    private string? FindLatestBackupDir()
    {
        try
        {
            var root = _job?.Paths.BackupRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;

            var candidates = Directory.GetDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, "journal.txt")))
                .Select(d => new DirectoryInfo(d))
                .OrderByDescending(d => d.LastWriteTime)
                .ToList();

            return candidates.FirstOrDefault()?.FullName;
        }
        catch
        {
            return null;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 替换进行中不允许关窗口：那时中断 = 程序目录处于半新半旧状态
        if (_state == RunState.Running && !_finished)
        {
            if (_cts is not null && _cts.Token.CanBeCanceled)
            {
                // 还能取消的阶段（下载等）：取消并让引擎自己收尾
                _cts.Cancel();
            }
            else
            {
                e.Cancel = true;
                StatusText.Text = "正在替换文件，这一步不能中断（中断会导致程序文件损坏）。请稍等…";
                return;
            }
        }

        base.OnClosing(e);
        if (_finished) Environment.ExitCode = _exitCode;
    }
}
