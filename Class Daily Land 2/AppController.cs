using ClassDailyLand.App.Integrations.Audio;
using ClassDailyLand.App.Integrations.Disaster;
using ClassDailyLand.App.Plugins;
using ClassDailyLand.App.Theme;
using ClassDailyLand.App.Tray;
using ClassDailyLand.App.Updates;
using ClassDailyLand.App.Views.Dialogs;
using ClassDailyLand.App.Win32;
using ClassDailyLand.App.Windows;
using ClassDailyLand.Core;
using ClassDailyLand.Core.Engine;
using ClassDailyLand.Core.Managers;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Core.Plugins;
using ClassDailyLand.Core.Storage;
using ClassDailyLand.Plugin;
using System.Windows;
using System.Windows.Threading;

namespace ClassDailyLand.App;

/// <summary>
/// 应用装配器：唯一同时持有 Manager 与窗口的地方。窗口只读数据、发请求，彼此不引用。
/// 1 秒 tick 驱动课表状态、主岛/副岛/主窗刷新与上课/下课边沿检测。
/// </summary>
public sealed class AppController
{
    /// <summary>
    /// 当前版本号（例如 <c>1.0.0</c> / <c>1.1.0-beta.1</c>）。
    ///
    /// ★ 唯一真源是 <c>Updates\VersionInfo.cs</c> 的常量；
    ///   这里只是把 <see cref="Updates.AppVersion"/> 暴露成静态属性，
    ///   关于页与插件宿主（<c>IPluginHost.HostVersion</c>）都从这里读。
    ///   之前这里写死 <c>"1.0.0-mvp"</c>，与程序集 <c>1.0.0.0</c> 对不上，
    ///   更新检查根本无从比较。
    /// </summary>
    public static string Version { get; } = ClassDailyLand.App.Updates.AppVersion.Current;

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

    /// <summary>插件宿主。窗口只通过它的注册表取内容，不直接碰插件实例。</summary>
    public PluginManager Plugins { get; private set; } = null!;

    /// <summary>内置媒体集成（SMTC → 主岛媒体片段）。</summary>
    public AudioService Audio { get; private set; } = null!;

    /// <summary>内置灾害预警集成（地震倒计时 + 气象预警）。</summary>
    public DisasterService Disaster { get; private set; } = null!;

    MediaCardWindow? _mediaCard;

    /// <summary>检查更新编排（关于页与启动检查共用同一个实例）。</summary>
    public UpdateService Updates { get; private set; } = null!;

    // 资源包热刷新的命名事件（更新器装完资源会 Set 一下）—— P4 接入
    // RegisteredWaitHandle? _resourcesRegistration;

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

        // 插件宿主：先建出来（注册表要能被窗口取数），窗口与托盘都接好后再真正加载，
        // 这样插件的 Initialize 里调 ShowMainWindow / OpenSettings 时窗口已经存在。
        Plugins = new PluginManager(this, Paths, Log);
        Updates = new UpdateService(Settings, Paths, msg => Log.Info(msg));
        Updates.StatusChanged += status =>
            App.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Tray is not null) Tray.PendingUpdateVersion = status.App?.Version.ToString();
            }));

        // 内置集成：媒体（SMTC）与灾害预警。都只依赖 Paths/Settings/Dispatcher，
        // 所以放在窗口之前建好，窗口一出来就能拿到内容。
        // 触屏优化是全局的：主窗/面板在构造时就要读到，所以先落一次
        TouchMode.SetGlobal(Settings.Bool("touch_optimize"));

        Audio = new AudioService(Paths, Log);
        Audio.ApplySettings(Settings);

        Disaster = new DisasterService(
            App.Dispatcher, Settings, Log,
            Path.Combine(Paths.Root, "disaster-cache.json"),
            isInClass: () => CurrentStatus.IsOngoing,
            notify: (title, message) => Tray?.Balloon(title, message));
        Disaster.ApplySettings();

        Main = new MainBoardWindow();
        Island = new DynamicIslandWindow();
        SubIsland = new SubIslandWindow();
        Panels = new IslandPanelHost(this);

        Main.BindManagers(Homework, Duty);
        Main.ApplySettings(Settings);
        Island.ApplySettings(Settings);
        SubIsland.ApplySettings(Settings);
        SubIsland.SetProvider(PickSubIslandContent);   // 插件优先，没插件时用内置内容源

        WireEvents();
        WireWindows();
        WirePlugins();

        if (Settings.Bool("show_island"))
        {
            Island.Show();
            Island.StartWakeup(Settings.Bool("island_wake_animation"));
        }
        Main.SetVisible(Settings.Bool("show_main_window"));

        Tray = new TrayIcon(Log);
        WireTray();
        SyncTrayChecks();

        // 托盘建好之后才加载插件：插件可能一上来就 Toast（需要托盘图标）
        Plugins.LoadAll();

        _tick.Tick += (_, _) => Tick();
        _tick.Start();

        // 启动时静默检查（延迟 6s，先让窗口与托盘稳定）；成功结果写 last_update_check
        if (Settings.Bool("check_update_on_startup")) _ = RunStartupCheckAsync();

        // 上次更新的结果：有就弹一次气泡（consumed=true 后不再弹）
        var lastMessage = LastUpdate.Consume(out var lastResult);
        if (lastMessage is not null)
            Tray?.Balloon("班级日常", lastMessage, 4);
        Tick();       // 立刻刷一次，不等 1 秒

        if (Paths.UsedFallback) Tray?.Balloon("数据目录已回退", Paths.FallbackNotice ?? "");
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
        Island.MediaPlayPauseRequested += () => Audio.TogglePlayPause();
        Island.MediaExpandRequested += ToggleMediaCard;
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
        Tray.CheckUpdateRequested += () =>
        {
            // 托盘点"检查更新"没有地方显示结果，所以直接把关于页打开，
            // 让状态条（已是最新 / 发现新版本 / 失败）落在用户看得见的地方。
            ShowSettings();
            _settingsWindow?.NavigateTo(AppSettings.PageAbout);
            CheckForUpdate();
        };
        Tray.ExitRequested += Exit;

        // 插件菜单项：每次都现取，插件禁用后菜单立刻少一项
        Tray.PluginActions = () => Plugins?.Registry.TrayActions() ?? Array.Empty<TrayActionSpec>();
    }

    /// <summary>
    /// 把插件注册表接到各个显示位。
    /// 窗口侧只拿到 <c>Func</c>，不引用 PluginManager ——
    /// 这样窗口在没有插件的情况下也能独立跑（冒烟测试直接 new 一个就行）。
    /// </summary>
    void WirePlugins()
    {
        PluginIslandTextProvider = () => Plugins.Registry.IslandText();
        PluginExtraWidthProvider = () => Plugins.Registry.IslandExtraWidth();

        Main!.BoardSectionsProvider = () => Plugins.Registry.BoardSections();
        Main.BoardBadgeProvider = () => Plugins.Registry.BoardBadgeValue();
        Main.BoardActionsProvider = () => Plugins.Registry.BoardActions();

        Panels.PluginPanelsProvider = () =>
        {
            var list = Plugins.Registry.Panels().ToList();
            if (Disaster?.Panel is { } d)
                list.Add(new PanelRender(new PanelSpec(d.Title, 250, () => d.Lines), d.Lines));
            return list;
        };

        // 装/卸/启停插件后立刻把界面拉一遍，不用等下一秒 tick（也不用重启）
        Plugins.Changed += () =>
        {
            if (App.Dispatcher.CheckAccess()) RefreshPluginSurfaces();
            else App.Dispatcher.BeginInvoke(RefreshPluginSurfaces);
        };
    }

    void RefreshPluginSurfaces()
    {
        Main?.RefreshPluginContent();
        Tick();
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

        // 内置集成每秒跑一次：媒体决定片段，灾害预警决定是否红条接管
        Audio.Tick(status.IsOngoing);
        Disaster.Tick();

        var emergency = Disaster.Emergency;
        var media = Audio.Display;

        // 灾害预警时文案由它接管，且不做逐字切换动画（每秒都在变）
        var text = emergency?.Text
                   ?? ScheduleText.WithPlugin(ScheduleText.Core(status), GetPluginIslandText());
        Island.SetText(text, animate: emergency is null);
        Island.Update(new IslandInput(
            status,
            text,
            ExtraWidth: GetPluginExtraWidth(),
            CountdownSec: Settings.Int("island_countdown_sec"),
            HideOnFullscreen: Settings.Bool("island_hide_on_fullscreen"),
            IsFullscreen: Interop.IsForegroundFullscreen(),
            IsOfficeForeground: Interop.IsOfficeForeground(),
            Visible: showIsland,
            Badge: GetPluginIslandBadge(),
            Media: media,
            Emergency: emergency));

        SubIsland.Tick(Island.RightEdgeDip, Island.TopDip, status);
        // 主窗的插件区块/徽标可能是动态的（倒计时、天气…），每秒给一次机会；
        // RefreshPluginContent 内部比内容签名，没变就直接返回。
        Main.RefreshPluginContent();

        // 每秒把快照推给插件（插件在自己的回调里读，不再各自去摸 Manager）
        Plugins.RaiseTick(BuildSnapshot(status, text));

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

        // 插件也要知道设置变了（比如它跟着 theme 换配色）
        try { Plugins?.RaiseSettingsChanged(e.Keys); }
        catch (Exception ex) { Log?.Warn("通知插件设置变更失败: " + ex.Message); }

        if (e.Keys.Any(k => k == "touch_optimize"))
        {
            TouchMode.SetGlobal(Settings.Bool("touch_optimize"));
            // 主窗与下拉面板是常驻窗口，设置窗口关掉后也要保持新手感
            if (Main is not null) Main.ApplyTouchMode();
        }

        if (e.Keys.Any(k => k.StartsWith("audio_", StringComparison.Ordinal)))
            Audio?.ApplySettings(Settings);
        if (e.Keys.Any(k => k.StartsWith("disaster_", StringComparison.Ordinal)))
            Disaster?.ApplySettings();

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

    /// <summary>主岛右侧徽标（插件竞争位，没有插件时为空）。</summary>
    IslandBadge? GetPluginIslandBadge()
    {
        try { return Plugins?.Registry.IslandBadgeValue(); }
        catch (Exception ex) { Log?.Warn("插件 island_badge 异常: " + ex.Message); return null; }
    }

    /// <summary>每秒推给插件的只读快照。</summary>
    HostSnapshot BuildSnapshot(ScheduleStatus s, string text) => new(
        Status: s.Status,
        StatusText: text,
        Course: s.Course,
        Index: s.Index,
        Start: s.Start,
        End: s.End,
        RemainMinutes: s.RemainMin,
        UntilMinutes: s.UntilMin,
        HolidayName: s.Name,
        Now: DateTime.Now.AddSeconds(Schedule.OffsetSeconds));

    /// <summary>副岛内容：插件优先（按 Priority 竞争），没有插件接管时用内置的当前课程。</summary>
    SubIslandContent? PickSubIslandContent()
    {
        // 优先级：灾害预警 > 插件 > 媒体（显示位置设为副岛时）> 内置当前课程
        if (Disaster?.SubIsland is { } d)
            return new SubIslandContent(d.Title, d.Value, d.Progress);

        var fromPlugin = Plugins?.Registry.SubIsland();
        if (fromPlugin is not null)
            return new SubIslandContent(fromPlugin.Title, fromPlugin.Value, fromPlugin.Progress);

        if (Audio?.SubIsland is { } a)
            return new SubIslandContent(a.Title, a.Value, 0);

        return BuiltInSubIslandContent();
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

    /// <summary>打开设置中心并跳到指定页（插件调 OpenSettings("plugins") 用）。</summary>
    public void ShowSettings(string? pageKey)
    {
        var window = EnsureSettings();
        if (!string.IsNullOrWhiteSpace(pageKey)) window.NavigateTo(pageKey!);
    }

    /// <summary>插件主动请求刷新的入口（IPluginHost.Invalidate）。</summary>
    public void RequestPluginRefresh()
    {
        if (App.Dispatcher.CheckAccess()) Tick();
        else App.Dispatcher.BeginInvoke(Tick);
    }

    /// <summary>点岛上媒体片段：展开/收起媒体卡（手机灵动岛那种展开）。</summary>
    void ToggleMediaCard()
    {
        var media = Audio?.Display;
        var island = Island;
        if (media is null || island is null) return;

        if (_mediaCard is { IsVisible: true })
        {
            _mediaCard?.Dismiss();
            return;
        }

        var card = _mediaCard;
        if (card is null)
        {
            var audio = Audio!;
            card = new MediaCardWindow();
            card.PlayPauseRequested += () => audio.TogglePlayPause();
            _mediaCard = card;
        }
        card.Present(media, island.RightEdgeDip, island.TopDip + island.HeightDip);
    }

    /// <summary>宿主侧的"打开目录"（插件只能打开程序目录内部，校验在 PluginHostContext）。</summary>
    public void OpenDirectory(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        })?.Dispose();
    }

    /// <summary>在设置中心里打开一个二级浮层（菜单项与设置页共用这条路径）。</summary>
    void OpenDialog(Func<ViewModels.SettingsViewModel, FrameworkElement> create)
    {
        var window = EnsureSettings();
        window.ShowOverlay(create(window.Vm));
    }

    void OnIslandClicked()
    {
        // 灾害预警占着胶囊时，点一下 = 确认（收起红条，恢复上课显示）
        if (Disaster?.Emergency is not null)
        {
            Disaster.DismissEmergency();
            Tick();
            return;
        }

        // 先通知插件（它们可能想在面板展开前更新内容），再展开/收起「今日课程」面板
        try { Plugins?.RaiseIslandClicked(); }
        catch (Exception ex) { Log?.Warn("通知插件岛点击失败: " + ex.Message); }

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
    /// <summary>
    /// 关于页「检查更新」按钮。状态变化由 <see cref="UpdateService.StatusChanged"/> 播给 UI。
    /// </summary>
    public void CheckForUpdate()
    {
        if (Updates is null) return;
        _ = Updates.CheckAsync(silent: false);
    }

    /// <summary>更新器只允许拉起一次：连点两次会变成两个更新器抢着替换同一批文件。</summary>
    bool _updateLaunched;

    /// <summary>
    /// 用户在关于页点「立即更新」：只拉起更新器，**主程序继续留在后台运行**。
    ///
    /// 更新器在"检查更新"时就已经复制到 %Temp%，点下去只剩"启动它"——窗口秒开。
    /// 下载、校验、解压都在主程序活着的时候进行；只有真正覆盖程序目录前，
    /// 更新器才会强杀主程序并等文件释放。这里绝不能退出主程序。
    /// </summary>
    public async Task ApplyUpdateAsync(UpdateOffer offer)
    {
        if (Updates is null || _updateLaunched) return;

        var result = await Updates.ApplyAsync(offer).ConfigureAwait(true);
        if (!result.Ok)
        {
            Log?.Warn("启动更新失败：" + result.Message);
            Tray?.Balloon("更新失败", result.Message, 5);
            return;
        }

        _updateLaunched = true;

        Log?.Info("更新器已启动，主程序保持运行（替换前由更新器关闭）");
        Tray?.Balloon("正在更新", "更新器正在下载，安装时会自动关闭程序", 5);
    }

    /// <summary>启动时的静默检查：不弹窗、不改 UI，只在有新版本时给托盘气泡。</summary>
    async Task RunStartupCheckAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);

            // 源码运行保护：开发机上自动更新会把源码弄乱
            if (Settings.Bool("block_update_on_source") &&
                UpdateEnvironment.IsSourceRun(UpdateEnvironment.InstallDir))
            {
                Log.Info("启动检查跳过：源码运行");
                return;
            }

            // 6 小时节流，避免每次启动都打一次网络
            var last = Settings.Str("last_update_check");
            if (DateTime.TryParse(last, out var when) && DateTime.Now - when < TimeSpan.FromHours(6))
                return;

            var updates = Updates;
            if (updates is null) return;

            var status = await updates.CheckAsync(silent: true).ConfigureAwait(false);
            if (status.Stage == UpdateStage.Available && status.App is not null)
            {
                Tray?.Balloon("发现新版本 " + status.App.Version,
                    "打开「设置 > 关于」查看更新内容", 5);
            }
        }
        catch (Exception ex)
        {
            Log?.Warn("启动检查更新异常：" + ex.Message);
        }
    }

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
        try { Plugins?.ShutdownAll(); }
        catch (Exception ex) { Log?.Error("卸载插件失败: " + ex); }
        try { Audio?.Shutdown(); } catch { }
        try { Disaster?.Dispose(); } catch { }
        try { _mediaCard?.Close(); } catch { }
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
        try { Plugins?.ShutdownAll(); }
        catch { /* 退出路径不抛 */ }
        Tray?.Dispose();
        Tray = null;
    }
}
