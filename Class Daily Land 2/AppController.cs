using System.Windows;
using System.Windows.Threading;
using ClassDailyLand.App.Theme;
using ClassDailyLand.App.Tray;
using ClassDailyLand.App.Win32;
using ClassDailyLand.App.Windows;
using ClassDailyLand.App.Views.Dialogs;
using ClassDailyLand.Core;
using ClassDailyLand.Core.Engine;
using ClassDailyLand.Core.Managers;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Core.Storage;

namespace ClassDailyLand.App;

/// <summary>
/// 应用装配器：唯一同时持有 Manager 与窗口的地方。窗口只读数据、发请求，彼此不引用。
/// 1 秒 tick 驱动课表状态、主岛/副岛/主窗刷新与上课/下课边沿检测。
/// </summary>
public sealed class AppController
{
    public const string Version = "1.0.0-mvp";

    public App App { get; }
    public DataPaths Paths { get; private set; } = null!;
    public Logger Log { get; private set; } = null!;
    public SettingsManager Settings { get; private set; } = null!;
    public ScheduleManager Schedule { get; private set; } = null!;
    public HomeworkManager Homework { get; private set; } = null!;
    public StudentOnDuty Duty { get; private set; } = null!;
    public AgreementManager Agreement { get; private set; } = null!;

    public MainBoardWindow? Main { get; private set; }
    public DynamicIslandWindow? Island { get; private set; }
    public SubIslandWindow? SubIsland { get; private set; }
    public TrayIcon? Tray { get; private set; }
    Views.SettingsWindow? _settingsWindow;

    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    ScheduleStatus _last = ScheduleStatus.Empty;
    bool _firstTick = true;
    bool _alertInFlight;
    DispatcherTimer? _alertClear;

    public string DataRoot => Paths?.Root ?? "";
    public ScheduleStatus CurrentStatus { get; private set; } = ScheduleStatus.Empty;

    public AppController(App app) => App = app;

    // ————————————————————— 启动 —————————————————————

    public void Startup()
    {
        Paths = DataPaths.Create();
        Log = new Logger(Paths);
        Log.Info("===== ClassDailyLand 启动 =====");
        if (Paths.UsedFallback) Log.Warn(Paths.FallbackNotice ?? "");

        Settings = new SettingsManager(Paths);
        Settings.Load();
        Schedule = new ScheduleManager(Paths);
        Schedule.Load();
        Homework = new HomeworkManager(Paths);
        Homework.Load();
        Duty = new StudentOnDuty(Paths);
        Duty.Load();
        Agreement = new AgreementManager(Paths);
        Agreement.Load();

        foreach (var key in Settings.RepairedKeys)
            Log.Warn($"设置项 {key} 类型不符，已回退默认值");

        // 提前提醒 / 时间偏移：settings.json 为准（设置中心改的是它），同步进 schedule.json
        SyncRemindersIntoSchedule();
        ThemeService.Apply(Settings.Str("theme"));

        Main = new MainBoardWindow();
        Island = new DynamicIslandWindow();
        SubIsland = new SubIslandWindow();
        Panels = new IslandPanelHost(this);

        Main.BindManagers(Homework, Duty);
        Main.ApplySettings(Settings);
        Island.ApplySettings(Settings);
        SubIsland.ApplySettings(Settings);
        SubIsland.SetProvider(BuiltInSubIslandContent);   // MVP：无插件时用内置内容源

        WireEvents();
        WireWindows();

        if (Settings.Bool("show_island"))
        {
            Island.Show();
            Island.StartWakeup(Settings.Bool("island_wake_animation"));
        }
        Main.SetVisible(Settings.Bool("show_main_window"));

        Tray = new TrayIcon(Log);
        WireTray();
        SyncTrayChecks();

        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        Tick();       // 立刻刷一次，不等 1 秒

        if (Paths.UsedFallback) Tray.Balloon("数据目录已回退", Paths.FallbackNotice ?? "");
    }

    void SyncRemindersIntoSchedule()
    {
        Schedule.SetAdvance(Settings.Int("schedule_advance_minutes"));
        Schedule.SetOffset(Settings.Int("schedule_time_offset_seconds"));
    }

    void WireEvents()
    {
        var dispatcher = App.Dispatcher;
        // 实时预览：滑块防抖后的改动必须马上落到窗口。已在 UI 线程就同步调用，
        // 否则用 Send 优先级派发，避免排在设置窗的布局/渲染之后（表现为「要等数据刷新才生效」）。
        Settings.SettingsChanged += (_, e) =>
        {
            if (dispatcher.CheckAccess()) OnSettingsChanged(e);
            else dispatcher.BeginInvoke(new Action(() => OnSettingsChanged(e)), DispatcherPriority.Send);
        };
        Schedule.Changed += (_, _) => dispatcher.BeginInvoke(Tick);
        Duty.Changed += (_, _) => dispatcher.BeginInvoke(() => Main?.RefreshAll());
    }

    void WireWindows()
    {
        if (Island is null || Main is null || SubIsland is null) return;

        Island.Clicked += OnIslandClicked;
        Island.IslandStateChanged += state => SubIsland.SetMainIslandVisible(
            state is IslandState.Compact or IslandState.Countdown or IslandState.Alert);

        Main.PrevDutyRequested += () => { Duty.Prev(); Tick(); };
        Main.NextDutyRequested += () => { Duty.Next(); Tick(); };
        Main.AttendanceRequested += OpenAttendanceEditor;
        Main.EditHomeworkRequested += OpenHomeworkEditor;
        Main.TimetableManagerRequested += OpenTimetableManager;
        Main.TempAdjustRequested += OpenTempAdjust;
        Main.HolidayRequested += OpenHolidayEditor;
        Main.WeekendRequested += OpenWeekendEditor;
        Main.RollCallRequested += OpenRollCall;
        Main.SettingsRequested += ShowSettings;
        Main.ExitRequested += Exit;
    }

    void WireTray()
    {
        if (Tray is null) return;
        Tray.MainToggled += on => Settings.Set("show_main_window", on);
        Tray.IslandToggled += on => Settings.Set("show_island", on);
        Tray.SubToggled += on => Settings.Set("show_sub_island", on);
        Tray.SettingsRequested += ShowSettings;
        Tray.ExitRequested += Exit;
    }

    void SyncTrayChecks() => Tray?.SetChecks(
        Settings.Bool("show_main_window"),
        Settings.Bool("show_island"),
        Settings.Bool("show_sub_island"));

    // ————————————————————— 每秒 tick —————————————————————

    void Tick()
    {
        if (Island is null || Main is null || SubIsland is null) return;

        var showIsland = Settings.Bool("show_island");
        if (showIsland && !Island.IsVisible) Island.Show();
        if (!showIsland) Island.Hide();

        Duty.RollOverIfNeeded(DateTime.Today);

        var now = DateTime.Now;
        var status = Schedule.Compute(now);
        CurrentStatus = status;

        var text = ScheduleText.WithPlugin(ScheduleText.Core(status), GetPluginIslandText());
        Island.SetText(text);
        Island.Update(new IslandInput(
            status,
            text,
            ExtraWidth: GetPluginExtraWidth(),
            CountdownSec: Settings.Int("island_countdown_sec"),
            HideOnFullscreen: Settings.Bool("island_hide_on_fullscreen"),
            IsFullscreen: Interop.IsForegroundFullscreen(),
            IsOfficeForeground: Interop.IsOfficeForeground(),
            Visible: showIsland));

        SubIsland.Tick(Island.RightEdgeDip, Island.TopDip, status);

        DetectEdges(status);
        _last = status;
        _firstTick = false;
    }

    /// <summary>上课瞬间弹「上课了！」（绿字），下课瞬间弹「下课了！」（白字）。</summary>
    void DetectEdges(ScheduleStatus now)
    {
        if (_firstTick || _alertInFlight) return;
        var started = !_last.IsOngoing && now.IsOngoing;
        var ended = _last.IsOngoing && (!now.IsOngoing || now.Index != _last.Index);
        if (started) FireAlert(ScheduleText.AlertText(true), isStart: true);
        else if (ended) FireAlert(ScheduleText.AlertText(false), isStart: false);
    }

    void FireAlert(string text, bool isStart)
    {
        _alertInFlight = true;
        Island?.ShowAlert(text, isStart);
        var hold = Math.Clamp(Settings.Int("island_alert_hold_ms"), 200, 10000);
        _alertClear?.Stop();
        _alertClear = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(hold + 80) };
        _alertClear.Tick += (_, _) =>
        {
            _alertClear!.Stop();
            _alertInFlight = false;
            Island?.ClearAlert();
        };
        _alertClear.Start();
    }

    void OnSettingsChanged(SettingsChangedEventArgs e)
    {
        foreach (var key in e.Keys)
        {
            switch (key)
            {
                case "theme":
                    ThemeService.Apply(Settings.Str("theme"));
                    break;
                case "schedule_advance_minutes":
                case "schedule_time_offset_seconds":
                    SyncRemindersIntoSchedule();
                    break;
            }
        }

        Main?.ApplySettings(Settings);
        Island?.ApplySettings(Settings);
        SubIsland?.ApplySettings(Settings);
        // 新设置中心的 VM 自己订阅了 SettingsChanged，外部改动按属性名精确回刷，这里不用再做整页重建
        SyncTrayChecks();

        if (e.Keys.Contains("show_main_window"))
            Main?.SetVisible(Settings.Bool("show_main_window"));
        if (e.Keys.Contains("show_island"))
        {
            if (Settings.Bool("show_island")) { Island?.Show(); Island?.StartWakeup(false); }
            else Island?.Hide();
        }

        Tick();
    }

    // ————————————————————— 插件显示位（阶段 3 接 PluginManager） —————————————————————

    /// <summary>阶段 3 由 PluginManager 注入；宿主在 UI 线程调用，实现必须够快（禁 IO/网络/大计算）。</summary>
    public Func<string?>? PluginIslandTextProvider { get; set; }
    public Func<int>? PluginExtraWidthProvider { get; set; }

    string? GetPluginIslandText()
    {
        try { return PluginIslandTextProvider?.Invoke(); }
        catch (Exception ex) { Log?.Warn("插件 island_text 异常: " + ex.Message); return null; }
    }

    int GetPluginExtraWidth()
    {
        try { return Math.Max(0, PluginExtraWidthProvider?.Invoke() ?? 0); }
        catch (Exception ex) { Log?.Warn("插件 island_extra 异常: " + ex.Message); return 0; }
    }

    /// <summary>
    /// MVP 内置副岛内容源（无插件时兜底）。阶段 3 由插件按 Priority 竞争接管。
    /// 每秒在 UI 线程调用，保持无 IO。
    /// </summary>
    SubIslandContent? BuiltInSubIslandContent()
    {
        var s = CurrentStatus;
        if (s.IsOngoing && s.DurationSec > 0)
        {
            var progress = Math.Clamp(1.0 - (double)s.RemainSec / s.DurationSec, 0, 1);
            return new SubIslandContent(s.Course, $"剩 {Math.Max(1, s.RemainMin)} 分钟", progress);
        }
        if (s.IsUpcoming)
            return new SubIslandContent("下节", $"{s.Course} · {Math.Max(1, s.UntilMin)} 分钟后", 0);
        return null;   // 假期 / 无课 / 已放学：不显示（假期时主岛会显示假期名）
    }

    // ————————————————————— 窗口与编辑器入口 —————————————————————

    /// <summary>打开（或激活）设置中心；二级浮层都开在它内部。</summary>
    public Views.SettingsWindow EnsureSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return _settingsWindow;
        }
        _settingsWindow = new Views.SettingsWindow(this);
        var window = _settingsWindow;
        // 实时预览：滑块防抖期间也要让主窗跟着动
        window.Vm.PreviewChanged += OnPreviewChanged;
        window.Closed += (_, _) =>
        {
            window.Vm.PreviewChanged -= OnPreviewChanged;
            if (ReferenceEquals(_settingsWindow, window)) _settingsWindow = null;
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
        return _settingsWindow;
    }

    /// <summary>预览只刷主窗（岛 / 副岛 / 托盘不需要逐帧跟手）。</summary>
    void OnPreviewChanged(string key)
    {
        switch (key)
        {
            case "main_font_size":
            case "main_width_ratio":
            case "main_opacity":
                Main?.ApplyPreview(Settings);
                break;
        }
    }

    public void ShowSettings() => EnsureSettings();

    /// <summary>在设置中心里打开一个二级浮层（菜单项与设置页共用这条路径）。</summary>
    void OpenDialog(Func<ViewModels.SettingsViewModel, FrameworkElement> create)
    {
        var window = EnsureSettings();
        window.ShowOverlay(create(window.Vm));
    }

    void OnIslandClicked()
    {
        // 点击主岛：展开/收起「今日课程」下拉面板（阶段 3 插件面板继续走 specs）
        Panels.Toggle();
    }

    public IslandPanelHost Panels { get; private set; } = null!;

    public void OpenAttendanceEditor() => NumberInput.Show(
        "出勤人数", "应到人数", Duty.Config.Should, "实到人数", Duty.Config.Actual,
        (should, actual) =>
        {
            Duty.SetAttendance(should, actual);
            Main?.RefreshAll();
        });

    public void OpenRollCall()
    {
        var win = new RollCallWindow(Duty.Names) { Owner = Main?.IsVisible == true ? Main : null };
        win.Closed += (_, _) => _rollCall = null;
        _rollCall = win;
        win.Show();
        win.Activate();
    }

    RollCallWindow? _rollCall;

    public void OpenTempAdjust() => OpenDialog(vm => new TempAdjustDialog(vm));
    public void OpenHomeworkEditor() => OpenDialog(vm => new HomeworkDialog(vm));
    public void OpenDutyEditor() => OpenDialog(vm => new DutyDialog(vm));
    public void OpenTimetableManager() => OpenDialog(vm => new TimetableDialog(vm));
    public void OpenTimetableEditor() => OpenTimetableManager();
    public void OpenHolidayEditor() => OpenDialog(vm => new HolidaysDialog(vm));
    public void OpenWeekendEditor() => OpenDialog(vm => new WeekendDialog(vm));
    public void OpenStatusTest() => OpenDialog(vm => new StatusTestDialog(vm));
    public void CheckForUpdate() => NotReady("检查更新将在阶段 4 提供");

    void NotReady(string message)
    {
        Log?.Info(message);
        Tray?.Balloon("班级日常", message, 2);
    }

    public void ShowFromSecondInstance()
    {
        if (Main is null || Island is null) return;
        Settings.Set("show_main_window", true);
        Settings.Set("show_island", true);
        Main.SetVisible(true);
        Main.Activate();
        Island.Show();
        Island.StartWakeup(Settings.Bool("island_wake_animation"));
    }

    public void Exit()
    {
        _tick.Stop();
        _alertClear?.Stop();
        try
        {
            Settings.Save();
            Schedule.Save();
        }
        catch (Exception ex) { Log?.Error("退出前保存失败: " + ex); }
        Tray?.Dispose();
        Tray = null;
        App.Shutdown();
    }

    public void Shutdown()
    {
        _tick.Stop();
        Tray?.Dispose();
        Tray = null;
    }
}
