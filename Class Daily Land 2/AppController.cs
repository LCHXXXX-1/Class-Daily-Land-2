using ClassDailyLand2.App.Integrations.Audio;
using ClassDailyLand2.App.Integrations.Disaster;
using ClassDailyLand2.App.Integrations.Weather;
using ClassDailyLand2.App.DeveloperTool;
using ClassDailyLand2.App.Market;
using ClassDailyLand2.App.Plugins;
using ClassDailyLand2.App.Theme;
using ClassDailyLand2.App.Tray;
using ClassDailyLand2.App.Updates;
using ClassDailyLand2.App.Views.Dialogs;
using ClassDailyLand2.App.Win32;
using ClassDailyLand2.App.Windows;
using ClassDailyLand2.Core;
using ClassDailyLand2.Core.Diagnostics;
using ClassDailyLand2.Core.Engine;
using ClassDailyLand2.Core.Managers;
using ClassDailyLand2.Core.Models;
using ClassDailyLand2.Core.Plugins;
using ClassDailyLand2.Core.Storage;
using ClassDailyLand2.Plugin;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace ClassDailyLand2.App;

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
    public static string Version { get; } = ClassDailyLand2.App.Updates.AppVersion.Current;

    public App App { get; }
    public DataPaths Paths { get; private set; } = null!;
    public Logger Log { get; private set; } = null!;

    /// <summary>看门狗观察进程（开发者选项触发）。挂在字段上防止 GC 回收掉事件。</summary>
    ClassDailyLand2.App.Diagnostics.ProcessWatchdog? _watchdog;

    public SettingsManager Settings { get; private set; } = null!;
    public ScheduleManager Schedule { get; private set; } = null!;
    public HomeworkManager Homework { get; private set; } = null!;

    /// <summary>作业历史快照（settings\homeworks\yyyy-MM-dd.json）。</summary>
    public HomeworkHistoryManager HomeworkHistory { get; private set; } = null!;

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

    /// <summary>
    /// 内置实时天气集成（IP 定位 + Open-Meteo + 天气动画）。
    /// 它的 <see cref="WeatherService.Location"/> 同时是灾害预警自动定位的坐标来源。
    /// </summary>
    public WeatherService Weather { get; private set; } = null!;

    MediaCardWindow? _mediaCard;

    /// <summary>检查更新编排（关于页与启动检查共用同一个实例）。</summary>
    public UpdateService Updates { get; private set; } = null!;

    /// <summary>插件市场（索引 / 下载校验 / 安装编排）。</summary>
    public MarketService Market { get; private set; } = null!;

    // 资源包热刷新的命名事件（更新器装完资源会 Set 一下）—— P4 接入
    // RegisteredWaitHandle? _resourcesRegistration;

    Views.SettingsWindow? _settingsWindow;

    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>作业提交时间看门狗：每秒扫一遍，到点就通知 + 重排作业区。</summary>
    readonly HomeworkDueWatcher _homeworkDue = new();

    /// <summary>提交时段内每 5 秒重扫一次；记录每条作业上次扫的时间。</summary>
    readonly Dictionary<string, DateTime> _dueSweepAt = new(StringComparer.Ordinal);

    // ————— 状态演示：在**真实灵动岛**上按倍速跑一遍 —————
    // 演示期间 Tick 的状态来源改成模拟时钟，其它（副岛、主窗、插件）照旧跟随，
    // 这样用户看到的就是岛上真实的表现，而不是对话框里另画的预览。
    DispatcherTimer? _demoTimer;
    Func<DateTime, ScheduleStatus>? _demoProvider;
    IslandInput? _demoInput;              // 最近一次真实 tick 的输入模板（演示帧复用）
    DateTime _demoStart;
    double _demoTotalSim;
    double _demoSpeed = 60;
    double _demoRealBase;                 // 暂停前累计的真实秒
    DateTime _demoResumeAt = DateTime.Now;

    /// <summary>是否正在演示。</summary>
    public bool DemoRunning => _demoProvider is not null;

    /// <summary>演示当前的模拟时刻（真实时间轴 + 倍速）。</summary>
    public DateTime DemoClock =>
        _demoStart.AddSeconds((_demoRealBase + (DateTime.Now - _demoResumeAt).TotalSeconds) * _demoSpeed);

    public double DemoTotalSim => _demoTotalSim;
    public double DemoSpeed
    {
        get => _demoSpeed;
        set
        {
            if (value <= 0 || Math.Abs(value - _demoSpeed) < 0.001) return;
            // 换速度以"当前进度"为新起点，避免时间轴跳一段
            _demoRealBase += (DateTime.Now - _demoResumeAt).TotalSeconds;
            _demoResumeAt = DateTime.Now;
            _demoSpeed = value;
        }
    }

    /// <summary>在真实灵动岛上开始演示。</summary>
    public void StartDemo(Func<DateTime, ScheduleStatus> provider, DateTime start, double totalSimSeconds, double speed)
    {
        StopDemo();
        _demoProvider = provider;
        _demoStart = start;
        _demoTotalSim = Math.Max(1, totalSimSeconds);
        _demoSpeed = speed <= 0 ? 60 : speed;
        _demoRealBase = 0;
        _demoResumeAt = DateTime.Now;

        if (_demoTimer is null)
        {
            _demoTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33),
            };
            _demoTimer.Tick += (_, _) => DemoTick();   // 只订阅一次
        }
        if (!_demoTimer.IsEnabled) _demoTimer.Start();

        Tick();          // 立刻推一帧，岛马上切到演示状态
        Log?.Info($"[演示] 开始：{start:HH:mm:ss} 起，共 {totalSimSeconds:0} 秒模拟时长，{_demoSpeed:0}×");
    }

    public void PauseDemo()
    {
        if (_demoProvider is null) return;
        _demoRealBase += (DateTime.Now - _demoResumeAt).TotalSeconds;
        _demoTimer?.Stop();
    }

    public void ResumeDemo()
    {
        if (_demoProvider is null) return;
        _demoResumeAt = DateTime.Now;
        _demoTimer?.Start();
    }

    public void StopDemo()
    {
        _demoTimer?.Stop();
        if (_demoProvider is null) return;
        _demoProvider = null;
        _demoRealBase = 0;
        Log?.Info("[演示] 结束，灵动岛恢复真实课表状态");
        Tick();          // 立刻回到真实状态
    }

    /// <summary>演示帧：只更新灵动岛（不动 Duty / 音频 / 插件，避免每 33ms 跑整套逻辑）。</summary>
    void DemoTick()
    {
        try
        {
            if (_demoProvider is null || Island is null) return;

            var clock = DemoClock;
            var status = _demoProvider(clock);
            var text = ScheduleText.WithPlugin(ScheduleText.Core(status), GetPluginIslandText());

            Island.SetText(text, animate: true);
            if (_demoInput is not null)
                // 演示期间强制可见：不因为"前台是全屏窗口"就被藏掉
                Island.Update(_demoInput with { Status = status, IslandText = text, Visible = true, HideOnFullscreen = false });
            else
                Tick();

            var played = (_demoRealBase + (DateTime.Now - _demoResumeAt).TotalSeconds) * _demoSpeed;
            if (played >= _demoTotalSim) StopDemo();
        }
        catch (Exception ex)
        {
            Log?.Warn("[演示] 帧处理失败：" + ex.Message);
            StopDemo();
        }
    }

    ScheduleStatus _last = ScheduleStatus.Empty;
    bool _firstTick = true;
    bool _alertInFlight;
    DispatcherTimer? _alertClear;

    public string DataRoot => Paths?.Root ?? "";
    public ScheduleStatus CurrentStatus { get; private set; } = ScheduleStatus.Empty;

    public AppController(App app) => App = app;

    // ————————————————————— 启动 —————————————————————

    DateTime _startedAt;

    public void Startup()
    {
        Paths = DataPaths.Create();
        Log = new Logger(Paths);
        Logger.Current = Log;   // Core 层（AtomicJson / ScheduleEngine / Managers）经它写详细日志
        Log.VersionOverride = VersionInfo.Version;
        ToastService.Init(Log);   // 启动即注册系统通知身份（HKCU，不需要管理员；每台机器各自建）
        _startedAt = DateTime.Now;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Log.Section("ClassDailyLand2 启动");
        Log.EnvironmentBanner();
        Log.Trace($"[启动] 数据目录：{Paths.Root}（回退：{Paths.UsedFallback}）");
        if (Paths.UsedFallback) Log.Warn(Paths.FallbackNotice ?? "");

        // 上次「作业/课表/设置自检」如果没正常结束（程序被杀 / 异常退出），
        // 数据目录里会留下 *.test-backup.json —— 先恢复，再加载设置、作业与课表。
        RecoverTestBackups();

        Settings = new SettingsManager(Paths);
        Settings.Load();
        Log.Step("启动", $"设置加载完成（被修正 {Settings.RepairedKeys.Count} 项）", sw.ElapsedMilliseconds);

        // 内存日志上限（500~3000）跟着设置走，用户在日志页改完立刻生效
        Log.Capacity = Settings.Int("log_max_lines");
        Log.Step("启动", $"日志上限 {Log.Capacity} 行");

        // 异常退出检测：正常关闭会删掉运行标记；标记还在 = 上一次没走正常流程就结束了
        CheckAbnormalExit();

        // 正常关机 / 重启：系统会先发 WM_QUERYENDSESSION（WPF 转成 SessionEnding），
        // 趁这一刻把运行标记清掉，下次开机就不会被误判成上次异常退出。
        if (Application.Current is { } app)
            app.SessionEnding += (_, _) => ClearRunningMarker();
        sw.Restart();

        Schedule = new ScheduleManager(Paths);
        Schedule.Load();
        Log.Step("启动", "课表加载完成", sw.ElapsedMilliseconds);
        Homework = new HomeworkManager(Paths);
        // 每次作业落盘（Changed 在 Save 里触发）都写一份当日快照；保留天数从 settings.json 读。
        // 订阅放在 Load 之前：首次运行 homework.json 由 Load 里的 Save 生成时也能留下快照。
        HomeworkHistory = new HomeworkHistoryManager(Paths, () => Settings.Int("homework_history_days"));
        Homework.Changed += (_, _) =>
        {
            HomeworkHistory.SaveSnapshot(Homework.Items);
            if (HomeworkHistory.LastError is { } error) Log?.Warn(error);
        };
        sw.Restart();
        Homework.Load();
        Log.Step("启动", $"作业加载完成（{Homework.Items.Count} 项）", sw.ElapsedMilliseconds);

        // 课表与作业都加载完：把"选了某一节课"的提交时间对齐到当前课表。
        // 覆盖两种情况：关闭期间课表被改过；上次测试课表/测试作业没走完恢复流程。
        SyncHomeworkDueToSchedule();
        HomeworkHistory.CleanOldSnapshots(Settings.Int("homework_history_days"));
        if (HomeworkHistory.LastError is { } historyError) Log.Warn(historyError);
        Duty = new StudentOnDuty(Paths);
        Duty.Load();
        Agreement = new AgreementManager(Paths);
        Agreement.Load();

        foreach (var key in Settings.RepairedKeys)
            Log.Warn($"设置项 {key} 类型不符，已回退默认值");

        // 提前提醒 / 时间偏移：settings.json 为准（设置中心改的是它），同步进 schedule.json
        SyncRemindersIntoSchedule();
        ThemeService.Apply(Settings.Str("theme"));
        ThemeService.ApplyColorTheme(Settings.Str("color_theme"));

        // 提示框入场动画的时长跟着设置走（关掉动画就直接落位）
        Views.UiMotion.DurationProvider = () =>
            Settings.Bool("fade_animation") ? Math.Clamp(Settings.Int("animation_duration_ms"), 0, 2000) : 0;

        // 插件宿主：先建出来（注册表要能被窗口取数），窗口与托盘都接好后再真正加载，
        // 这样插件的 Initialize 里调 ShowMainWindow / OpenSettings 时窗口已经存在。
        Plugins = new PluginManager(this, Paths, Log);
        Market = new MarketService(Plugins, Paths, Log, Settings);
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

        // 实时天气：定位是异步的（IP 三个源 +缓存），所以灾害预警拿的是个**惰性 Func**，
        // 不受创建顺序影响；定位结果回来时由下面的事件驱动灾害重新取坐标。
        Weather = new WeatherService(App.Dispatcher, Settings, Log, Paths.Root);
        Weather.ApplySettings();
        Weather.LocationChanged += () =>
        {
            if (App.Dispatcher.CheckAccess()) Disaster?.ApplySettings();
            else App.Dispatcher.BeginInvoke(new Action(() => Disaster?.ApplySettings()));
        };

        Disaster = new DisasterService(
            App.Dispatcher, Settings, Log,
            Path.Combine(Paths.Root, "disaster-cache.json"),
            isInClass: () => CurrentStatus.IsOngoing,
            // 灾害类通知走 urgent：能穿透「专注助手 / 免打扰」
            notify: (title, message) => Tray?.Balloon(title, message, 6, urgent: true),
            autoLocation: () => Weather?.Location);
        Disaster.ApplySettings();

        Main = new MainBoardWindow();
        Island = new DynamicIslandWindow();
        SubIsland = new SubIslandWindow();
        Panels = new IslandPanelHost(this);

        Main.BindManagers(Homework, Duty);
        Main.ApplySettings(Settings);
        Island.ApplySettings(Settings);
        SubIsland.ApplySettings(Settings);
        SubIsland.SetProvider(PickSubIslandContent);
        // 先跑一遍让 _subIslandOwner 有个初值：主窗区块"要不要放天气"依赖它，
        // 而主窗可能在第一次副岛 tick 之前就被要求刷新
        PickSubIslandContent();   // 插件优先，没插件时用内置内容源

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

        // 数据文件点名：哪个文件在、哪个缺，一眼就能看到（排查"功能不对劲"从这里开始）
        LogDataFiles();

        // 托盘建好之后才加载插件：插件可能一上来就 Toast（需要托盘图标）
        Plugins.LoadAll();

        _tick.Tick += (_, _) => Tick();
        _tick.Start();

        // 上次更新的结果：有就弹一次气泡（consumed=true 后不再弹）
        var lastMessage = LastUpdate.Consume(out var lastResult);
        if (lastMessage is not null)
            Tray?.Balloon("班级日常", lastMessage, 4);
        Tick();       // 立刻刷一次，不等 1 秒

        // 启动提示：关掉窗口后程序还在跑，不说一句用户会以为已经退出了
        Tray?.Balloon("Class Daily Land 2", "已进入后台运行，可在托盘图标查看", 3);

        if (Paths.UsedFallback) Tray?.Balloon("数据目录已回退", Paths.FallbackNotice ?? "");

        // 启动自动检查更新：延迟 3 秒后检查，有 App 更新就弹置顶窗口；所有异常都必须落日志。
        StartAutoUpdateCheck();
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
        Schedule.Changed += (_, _) =>
        {
            // 课表时间改了：按节次设置的作业提交时间要跟着改。
            // 在 UI 线程时同步跑，保证后面的编辑器刷新下拉框时拿到的已经是新时间。
            void Apply()
            {
                SyncHomeworkDueToSchedule();
                Tick();
            }

            if (dispatcher.CheckAccess()) Apply();
            else dispatcher.BeginInvoke(new Action(Apply));
        };
        Duty.Changed += (_, _) => dispatcher.BeginInvoke(() => Main?.RefreshAll());
    }

    /// <summary>
    /// 课表保存后，把「选了某一节课」的作业提交时间按当前课表重算一遍。
    /// 只动 due_kind=period 且记录了节次下标的条目；自定义 / 补交 / 不交不受影响。
    /// </summary>
    void SyncHomeworkDueToSchedule()
    {
        if (Homework is null) return;

        var now = DateTime.Now;
        var changed = false;
        foreach (var item in Homework.Items)
        {
            if (HomeworkDue.RefreshPeriodDue(item, Schedule.Engine, now))
                changed = true;
        }

        if (!changed) return;
        Log?.Info("[作业] 课表时间变了，已同步按节次设置的提交时间");
        Homework.Save();
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
        Main.TempAdjustRequested += OpenTempAdjust;
        Main.HolidayRequested += OpenHolidayEditor;
        Main.TimetableRequested += () => ShowSettings("timetable");
        Main.WeekendRequested += OpenWeekendEditor;
        Main.RollCallRequested += OpenRollCall;
        Main.SettingsRequested += ShowSettings;
        Main.ExitRequested += Exit;
    }

    /// <summary>运行标记：存在 = 本次运行还没正常结束。</summary>
    string RunningMarkerPath => Path.Combine(Paths.Root, ".running.json");

    /// <summary>
    /// 异常退出检测：正常关闭（<see cref="Shutdown"/>）会删掉标记；启动时标记还在，
    /// 说明上次是崩溃 / 断电 / 被强制结束 —— 按 <see cref="ErrorCodes.UnexpectedExit"/> 补报一次。
    /// </summary>
    void CheckAbnormalExit()
    {
        try
        {
            if (!File.Exists(RunningMarkerPath))
            {
                WriteRunningMarker();
                return;
            }

            var previous = File.ReadAllText(RunningMarkerPath);

            // 系统刚启动（5 分钟内）说明上次十有八九是正常关机 / 重启 / 断电：
            // 关机时进程是被系统直接结束的，走不到正常关闭那条路，标记自然会留下。
            // 这种场景不该打扰用户，只留一条日志。
            if (Environment.TickCount64 < 5 * 60 * 1000)
            {
                Log.Info("[异常退出] 开机不足 5 分钟，上次按正常关机处理，不上报");
                WriteRunningMarker();
                return;
            }

            // 被更新程序强杀属于"预期内"的结束：新更新器替换成功后会清标记，
            // 但旧版更新器不会（或清失败），这里再兜一层——若更新结果是成功、
            // 且完成时间不早于本次运行标记，就按正常结束处理，不弹异常退出上报。
            if (WasClosedByUpdater(previous))
            {
                Log.Info("[异常退出] 上次是被更新程序关闭的（更新已完成），按正常结束处理");
                WriteRunningMarker();
                return;
            }

            Log.Warn($"[异常退出] 上一次运行未正常结束：{previous}");

            // 写入新标记要先于上报：如果这次又崩，下次还能接着报
            WriteRunningMarker();

            // 报告走独立进程：就算主程序随后再挂，窗口和日志也都活了下来
            Views.Dialogs.ErrorReportWindow.Report(
                "上次异常退出",
                "上一次运行没有正常关闭就结束了（可能是崩溃、断电或被强制结束）。" +
                "日志里最后几行是当时的现场，可以导出后反馈。", null, "系统 · 启动检查",
                ErrorCodes.UnexpectedExit);
        }
        catch (Exception ex)
        {
            Log?.Warn("[异常退出] 检测失败：" + ex.Message);
            try { WriteRunningMarker(); } catch { }
        }
    }

    /// <summary>
    /// 测试按钮会在数据目录留两份 <c>*.test-backup.json</c>；正常结束会删掉。
    /// 启动时如果还看到它们，说明上次测试没走完恢复流程 —— 先还原再加载。
    /// </summary>
    void RecoverTestBackups()
    {
        RestoreOne("settings.test-backup.json", Paths.Settings, "设置");
        RestoreOne("schedule.test-backup.json", Paths.Schedule, "课表");
        RestoreOne("schedule.timetable-backup.json", Paths.Schedule, "课表（测试）");
        RestoreOne("homework.test-backup.json", Paths.Homework, "作业");

        void RestoreOne(string name, string target, string label)
        {
            var backup = Path.Combine(Paths.Root, name);
            if (!File.Exists(backup)) return;

            try
            {
                File.Copy(backup, target, overwrite: true);
                Log.Warn($"[测试] 上次自检未正常结束，已用备份恢复{label}");
            }
            catch (Exception ex)
            {
                Log.Warn($"[测试] 恢复{label}备份失败：" + ex.Message);
                return;   // 失败就保留文件，下次再试
            }

            try { File.Delete(backup); } catch { }
        }
    }

    /// <summary>
    /// 上次结束是不是"更新程序干的"：更新结果成功，且完成时间不早于本次运行标记。
    /// 是的话不该按异常退出上报（否则每次更新完都白弹一次崩溃窗）。
    /// </summary>
    bool WasClosedByUpdater(string markerJson)
    {
        try
        {
            var result = LastUpdate.Read();
            if (result is null || !result.Ok) return false;

            DateTime started;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(markerJson);
                if (!doc.RootElement.TryGetProperty("started_at", out var startedElement)) return false;
                if (!DateTime.TryParse(startedElement.GetString(), out started)) return false;
            }
            catch
            {
                return false;
            }

            if (!DateTime.TryParse(result.FinishedAt, out var finished)) return false;

            // 允许 5 秒时钟误差
            return finished >= started.AddSeconds(-5);
        }
        catch
        {
            return false;
        }
    }

    void WriteRunningMarker()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            started_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            version = Logger.AppVersion,
        });
        File.WriteAllText(RunningMarkerPath, payload);
        Log.Trace($"[异常退出] 已写入运行标记 {RunningMarkerPath}");
    }

    void ClearRunningMarker()
    {
        try
        {
            if (File.Exists(RunningMarkerPath)) File.Delete(RunningMarkerPath);
            Log.Trace("[异常退出] 运行标记已清除（本次正常关闭）");
        }
        catch (Exception ex)
        {
            Log?.Warn("[异常退出] 清除运行标记失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 开发者选项入口：**结束主窗口**。界面上、日志里都不出现"测试/模拟"字样 ——
    /// 这是一次真实的异常退出，验证的正是两层兜底：
    ///   1. 报信进程立即弹出「关键进程异常退出」（主窗口进程就是被结束的那个关键进程）；
    ///   2. 下次启动还会按 ESY4003「上次异常退出」补报一次。
    /// </summary>
    public void DevTestTerminateMain()
    {
        Log.Warn("[看门狗] 主窗口进程被强制结束：程序未走正常关闭流程，运行标记保留，下次启动会补报 ESY4003");

        // 顺序很重要：先把报告写盘、报信进程拉起来，再结束自己 —— 反了窗口就跟主程序一起没了
        Views.Dialogs.ErrorReportWindow.Report(
            "主窗口被强制结束",
            "主窗口进程已被强制结束，程序没有走正常关闭流程。",
            null, "系统 · 进程看门狗", ErrorCodes.ProcessTerminated);

        // 给报信进程 1.5 秒拉起窗口，然后结束自己（Exit 不走 WPF 正常关闭 → 标记保留）
        Task.Delay(1500).ContinueWith(_ => Environment.Exit(1));
    }

    static string SafePid(System.Diagnostics.Process process)
    {
        try { return process.Id.ToString(); } catch { return "?"; }
    }

    /// <summary>
    /// 把程序期望的数据文件逐个点名：存在就记路径、大小与最后修改时间，缺失就明说
    /// 「缺失 → 将使用默认值」。用户说"某个功能不对劲"时，看这几行就知道是不是文件没了。
    /// </summary>
    void LogDataFiles()
    {
        var items = new (string Name, string Path)[]
        {
            ("设置", Paths.Settings),
            ("课表", Paths.Schedule),
            ("作业", Paths.Homework),
            ("作业历史", Paths.HomeworkHistory),
            ("日志", Paths.Log),
            ("姓名", Paths.Name),
            ("配置", Paths.Config),
            ("协议同意", Paths.Agreement),
            ("插件目录", Paths.PluginsRoot),
        };

        Log.Info($"[数据文件] 根目录：{Paths.Root}");
        foreach (var (name, path) in items)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var count = Directory.GetDirectories(path).Length;
                    Log.Info($"[数据文件] {name}：{path}（目录，{count} 个子目录）");
                }
                else if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    Log.Info($"[数据文件] {name}：{path}（{info.Length} 字节，最后修改 {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}）");
                }
                else
                {
                    Log.Info($"[数据文件] {name}：缺失 → {path}（将使用默认值）");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[数据文件] {name}：检查失败 → {path}（{ex.GetType().Name}: {ex.Message}）");
            }
        }
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

        // 插件装好/卸掉各弹一条 Windows 通知 —— 市场页、拖包安装、文件夹安装、插件自更新都走同一个事件
        Plugins.Installed += (_, name, version) =>
            Tray?.Balloon("插件已安装", string.IsNullOrWhiteSpace(version) ? name : $"{name} {version}");
        Plugins.Uninstalled += (_, name) => Tray?.Balloon("插件已卸载", name);

        // 报错窗的恢复操作：主进程模式交给控制器（完整收尾）；报信进程模式自己干
        Views.Dialogs.ErrorReportWindow.RestartHook = () => { Restart(); return true; };
        Views.Dialogs.ErrorReportWindow.ReinstallHook = () => { _ = ReinstallLatestAsync(); return true; };

        // 出错时除了弹窗，也在右下角冒一条 Windows 通知：窗口可能被挡住或在别的虚拟桌面
        Views.Dialogs.ErrorReportWindow.Reported += (kind, module) =>
            Tray?.Balloon($"Class Daily Land 2 · {kind}", $"出错模块：{module}。最近 200 行日志已保存，可导出", 5);
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
        // 天气放到**标题行**（「班级日常」后面，带天气动画），不再占作业区的区块
        Main.WeatherProvider = () =>
        {
            if (!WeatherOnBoard) return null;
            if (Weather is not { } w || w.Snapshot is not { } s) return null;
            return new WeatherChip($"{s.TempText} {s.Text}", s.Code.ToString(), s.IsDay, w.Animations,
                string.Join("\n", s.Lines), Disaster?.WeatherWarningText);
        };
        // 徽标是单值位：只留给插件（天气已经有自己的胶囊了，再挂一个就重复）
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
        // 演示期间状态来自模拟时钟；平时就是真实课表
        var status = _demoProvider is not null ? _demoProvider(DemoClock) : Schedule.Compute(now);
        CurrentStatus = status;

        // 内置集成每秒跑一次：媒体决定片段，天气负责定位/实况，灾害预警决定是否红条接管
        Audio.Tick(status.IsOngoing);
        Weather?.Tick();     // 可能同步完成定位 → 立刻触发 LocationChanged → Disaster.ApplySettings
        Weather?.Flush();    // 定位/实况变了就落一次盘（没变是空操作）
        Disaster.Tick();

        var emergency = Disaster.Emergency;
        var media = Audio.Display;

        // 灾害预警时文案由它接管，且不做逐字切换动画（每秒都在变）
        var text = emergency?.Text
                   ?? ScheduleText.WithPlugin(ScheduleText.Core(status), GetPluginIslandText());
        Island.SetText(text, animate: emergency is null);
        var islandInput = new IslandInput(
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
            Emergency: emergency);

        _demoInput = islandInput;      // 演示帧复用这份模板，只换状态与文案
        Island.Update(islandInput);

        SubIsland.Tick(Island.RightEdgeDip, Island.TopDip, status);
        // 主窗的插件区块/徽标可能是动态的（倒计时、天气…），每秒给一次机会；
        // RefreshPluginContent 内部比内容签名，没变就直接返回。
        Main.RefreshPluginContent();

        // 作业提交时间：到点弹 Windows 通知，并把作业区重排（顶置 / 沉底）
        CheckHomeworkDue(now);

        // 每秒把快照推给插件（插件在自己的回调里读，不再各自去摸 Manager）
        Plugins.RaiseTick(BuildSnapshot(status, text));

        DetectEdges(status);
        _last = status;
        _firstTick = false;
    }

    /// <summary>
    /// 作业提交时间看门狗（每秒）。
    ///   · 刚到点 → 写 due_fired（重启后不会重复提醒）+ 弹一条 Windows 通知；
    ///   · 排位变了（顶置 / 沉底 / 跨天）→ 重画作业区。
    /// 判断与排位规则全在 <see cref="HomeworkDue"/>，这里只负责"落地"。
    /// </summary>
    void CheckHomeworkDue(DateTime now)
    {
        var justDue = _homeworkDue.Tick(Homework.Items, now);

        if (justDue.Count > 0)
        {
            var stamp = HomeworkDue.ToText(now);
            foreach (var item in justDue) item.DueFired = stamp;

            // 一次落盘：Changed 会顺带刷新主窗与当天的历史快照
            Homework.Save();
            _homeworkDue.Tick(Homework.Items, now);   // 标记落盘后重算一次签名，下一拍不再判成"变了"
        }

        if (_homeworkDue.LayoutChanged) Main?.RefreshHomework();

        // 到点的那几行：重建行之后统一扫一次
        if (justDue.Count > 0) Main?.FlashHomeworkDue(justDue);

        // 提交时段内每 5 秒再扫一次（直接动画当前行，不重建列表）
        var sweepNow = CollectDueSweeps(now, justDue);
        if (sweepNow.Count > 0) Main?.FlashHomeworkNow(sweepNow);

        // 同一拍到点的作业合成**一条**通知（同一节下课时，多个科目一起交）
        if (justDue.Count > 0) NotifyHomeworkDue(justDue);
    }

    /// <summary>
    /// 找出「正在提交时段内、距上次扫色已满 5 秒」的作业。
    /// 到点那一拍已经由 FlashHomeworkDue 处理，这里排除掉，避免同一秒扫两次。
    /// </summary>
    List<HomeworkItem> CollectDueSweeps(DateTime now, IReadOnlyList<HomeworkItem> exclude)
    {
        var excluded = exclude.Select(HomeworkDue.KeyOf).ToHashSet(StringComparer.Ordinal);
        var list = new List<HomeworkItem>();

        foreach (var item in Homework.Items)
        {
            var key = HomeworkDue.KeyOf(item);
            if (HomeworkDue.SlotOf(item, now) != HomeworkSlot.Top)
            {
                _dueSweepAt.Remove(key);
                continue;
            }

            if (_dueSweepAt.TryGetValue(key, out var last) &&
                now - last < TimeSpan.FromSeconds(5))
                continue;

            _dueSweepAt[key] = now;
            if (!excluded.Contains(key)) list.Add(item);
        }

        // 删掉的作业不会再把 key 清掉，偶尔剪一次
        if (_dueSweepAt.Count > 512)
        {
            var alive = Homework.Items.Select(HomeworkDue.KeyOf).ToHashSet(StringComparer.Ordinal);
            foreach (var key in _dueSweepAt.Keys.Where(k => !alive.Contains(k)).ToList())
                _dueSweepAt.Remove(key);
        }

        return list;
    }

    /// <summary>
    /// 「本节下课需要提交作业」——同一次到点的作业按科目聚合成一条通知：
    ///
    ///   本节下课需要提交作业
    ///   语文：背诵课文　默写单词
    ///
    ///   数学：习题 1.2
    ///
    /// 同科目多条作业并在一行，科目之间空一行；不逐条弹，不然一节下课时屏幕会被刷满。
    /// </summary>
    void NotifyHomeworkDue(IReadOnlyList<HomeworkItem> items)
    {
        var groups = new List<(string Subject, List<string> Contents)>();
        foreach (var item in items)
        {
            var content = (item.Content ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');
            if (content.Length == 0) continue;
            if (content.Length > 20) content = content[..20] + "…";

            var subject = string.IsNullOrWhiteSpace(item.Subject) ? "作业" : item.Subject.Trim();
            var index = groups.FindIndex(g => string.Equals(g.Subject, subject, StringComparison.Ordinal));
            if (index < 0) groups.Add((subject, new List<string> { content }));
            else groups[index].Contents.Add(content);
        }
        if (groups.Count == 0) return;

        // ★ Windows 的 toast 正文最多 4 行：前 3 行写详细科目，剩下的一律收成
        //   「还有 N 科：物理、化学」放在第 4 行，避免 Windows 把后面几行静默吃掉。
        var ordered = groups.OrderBy(g => g.Subject, StringComparer.CurrentCulture).ToList();
        var lines = new List<string>();
        var detailCount = ordered.Count <= 4 ? ordered.Count : 3;
        for (var i = 0; i < detailCount; i++)
        {
            var group = ordered[i];
            var text = group.Contents.Count <= 1
                ? group.Contents.FirstOrDefault() ?? ""
                : $"{group.Contents[0]} 等 {group.Contents.Count} 项";
            var line = $"{group.Subject}：{text}";
            if (line.Length > 28) line = line[..28] + "…";
            lines.Add(line);
        }
        if (ordered.Count > 4)
        {
            lines.Add($"还有 {ordered.Count - 3} 科：" +
                      string.Join("、", ordered.Skip(3).Select(g => g.Subject)));
        }

        var body = string.Join("\n", lines);

        // 关掉提醒只是**不弹通知**：due_fired 已经写了、作业区照样顶到第一行，
        // 排序和打扰是两件事，别一起关掉
        if (!Settings.Bool("homework_notify_enabled"))
        {
            Log?.Info($"[作业到点] 提醒已关闭（作业仍顶到第一位）：\n{body}");
            return;
        }

        Log?.Info($"[作业到点] 已顶到作业区第一位：\n{body}");
        Tray?.Balloon("本节下课需要提交作业", body, 8);
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
        Log?.Info($"[课表边沿] {(isStart ? "上课" : "下课")}瞬间：{text}");
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
        Log?.Trace($"[设置分发] 生效键：{string.Join(", ", e.Keys)}");
        foreach (var key in e.Keys)
        {
            switch (key)
            {
                case "theme":
                    ThemeService.Apply(Settings.Str("theme"));
                    break;
                case "color_theme":
                    ThemeService.ApplyColorTheme(Settings.Str("color_theme"));
                    break;
                case "schedule_advance_minutes":
                case "schedule_time_offset_seconds":
                    SyncRemindersIntoSchedule();
                    break;
                case "start_with_windows":
                    // 设置一改就立刻落到注册表：用户不用重启程序，也不用再点一次"应用"
                    if (!StartupManager.SetStartup(Settings.Bool("start_with_windows")))
                        Log?.Warn("开机自启写入注册表失败（可能被组策略拦住）");
                    break;
                case "homework_history_days":
                    // 天数改小后立刻按新值清理，不用等到下一次保存
                    HomeworkHistory?.CleanOldSnapshots(Settings.Int("homework_history_days"));
                    break;
                case "log_max_lines":
                    // 调小时立刻裁掉多余的旧行，日志页马上就能看出变化
                    if (Log is not null) Log.Capacity = Settings.Int("log_max_lines");
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
        // 天气与定位：weather_* 是自己的键；auto_locate 与手选省市是**回退位置的输入**，
        // 改了它们天气的回退坐标也得跟着重算（否则 IP 挂掉时会退回一个过期的省）
        if (e.Keys.Any(k => k.StartsWith("weather_", StringComparison.Ordinal) ||
                            k == "auto_locate" ||
                            k is "disaster_province" or "disaster_city" or "disaster_district"))
            Weather?.ApplySettings();
        // auto_locate / 省市区同时决定灾害预警用哪套坐标
        if (e.Keys.Any(k => k == "auto_locate" ||
                            k is "disaster_province" or "disaster_city" or "disaster_district"))
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

    /// <summary>
    /// 副岛内容的归属（"disaster-quake" / "disaster-weather" / "plugin" / "media" /
    /// "weather" / "course" / null）。**主窗区块要不要放天气就看它** ——
    /// <c>SubIsland.Tick</c> 每秒先跑、<c>Main.RefreshPluginContent</c> 后跑，
    /// 所以主窗读到的一定是本秒刚算出来的判定，不会差一帧。
    /// </summary>
    string? _subIslandOwner;

    /// <summary>
    /// 副岛内容：灾害 &gt; 插件 &gt; 媒体 &gt; 实时天气 &gt; 内置当前课程。
    ///
    /// <para>这个顺序不是随手排的：地震红条与气象预警必须能抢占（用户定的规则是
    /// 「有天气预警就让天气去作业窗口」）；插件是用户显式装的，压过内置内容；
    /// 天气排在内置课程之前 —— 副岛默认就该显示天气，课程在主岛上还有。</para>
    /// </summary>
    SubIslandContent? PickSubIslandContent()
    {
        if (Disaster?.QuakeSubIsland is { } q)
            return SetOwner("disaster-quake", new SubIslandContent(q.Title, q.Value, q.Progress));

        if (Disaster?.WeatherSubIsland is { } w)
            return SetOwner("disaster-weather", new SubIslandContent(w.Title, w.Value, w.Progress));

        if (Plugins?.Registry.SubIsland() is { } p)
            return SetOwner("plugin", new SubIslandContent(p.Title, p.Value, p.Progress));

        if (Audio?.SubIsland is { } a)
            return SetOwner("media", new SubIslandContent(a.Title, a.Value, 0));

        if (WeatherOnSub && Weather?.Snapshot is { } s)
            return SetOwner("weather", new SubIslandContent(s.TempText, s.Text, 0, s));

        return SetOwner("course", BuiltInSubIslandContent());
    }

    SubIslandContent? SetOwner(string owner, SubIslandContent? content)
    {
        _subIslandOwner = content is null ? null : owner;
        return content;
    }

    /// <summary>开关选了副岛、且已经有数据（这一秒它有没有真的拿到副岛，看 <see cref="_subIslandOwner"/>）。</summary>
    bool WeatherOnSub => Weather is { Enabled: true, ShowOn: "sub", Snapshot: not null };

    /// <summary>
    /// 天气该不该出现在**作业窗口**：选了主窗；或选了副岛却被别人抢走 ——
    /// 气象预警挤占就是最常见的一种，这正是「预警占上去、天气挤到作业那边去」。
    /// </summary>
    bool WeatherOnBoard => Weather is { } w && w.Enabled && w.Snapshot is not null &&
                           (w.ShowOn == "main" || (w.ShowOn == "sub" && _subIslandOwner != "weather"));

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
            // 插件 / 程序内部主动要开设置页时，最小化的话先还原（右键「设置」走的是 ShowSettings，不会到这儿）
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
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

    public void ShowSettings()
    {
        // 设置窗已经开着但被最小化：不要从托盘/右键把它强行弹出来（很多人是故意收起来的），
        // 只发一条普通通知，告诉用户去任务栏打开。
        if (_settingsWindow is { IsVisible: true, WindowState: WindowState.Minimized })
        {
            Log?.Info("[设置] 已最小化到任务栏，右键「设置」只发提示，不还原窗口");
            Tray?.Balloon("设置已在任务栏最小化", "请在任务栏打开设置窗口。", 5);
            return;
        }

        EnsureSettings();
    }

    /// <summary>打开设置中心并跳到指定页（插件调 OpenSettings("plugins") 用）。</summary>
    public void ShowSettings(string? pageKey)
    {
        var window = EnsureSettings();
        if (string.Equals(pageKey, "timetable", StringComparison.OrdinalIgnoreCase))
        {
            window.OpenTimetableEditor();   // 课表编辑现在是「课表与提醒」里的二级页
            return;
        }
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

    /// <summary>
    /// 打开插件开发工具。DeveloperTool 已从主程序分离：不再内置，先查已安装的位置
    /// （注册表 InstallLocation → %LocalAppData%\DeveloperTool → 旧版随主程序部署的目录），
    /// 找到就启动；没找到返回 NotInstalled，由界面引导下载在线安装器。
    /// 开发工具始终与主程序分进程，避免 Roslyn / WebView2 等开发依赖增加常驻进程负担。
    /// </summary>
    public DeveloperToolOpenResult OpenPluginDeveloperTool(out string message)
    {
        var executable = DeveloperToolService.FindInstalledExe();
        if (executable is null)
        {
            message = "未检测到已安装的插件开发工具。";
            Log.Info("[插件开发工具] 未找到已安装的 DeveloperTool，转入下载引导");
            return DeveloperToolOpenResult.NotInstalled;
        }

        var toolDirectory = Path.GetDirectoryName(executable)!;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = toolDirectory,
                UseShellExecute = true,
            })?.Dispose();
            message = "你现在可以新建、编译并测试插件。";
            Log.Info("[插件开发工具] 已启动：" + executable);
            return DeveloperToolOpenResult.Launched;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            Log.Warn("[插件开发工具] 启动失败：" + ex.Message);
            return DeveloperToolOpenResult.Failed;
        }
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

    /// <summary>
    /// 「编辑作业」：独立窗口（不再是设置中心的页内浮层）—— 作业多的时候 760px 的浮层太窄，
    /// 分组列表挤成一条。窗口是模态的，关掉就回主界面。
    /// </summary>
    public void OpenHomeworkEditor()
    {
        if (_homeworkEditor is not null)
        {
            _homeworkEditor.Activate();
            return;
        }

        var window = new Views.HomeworkEditorWindow(Homework, HomeworkHistory, Schedule)
        {
            Owner = Main?.IsVisible == true ? Main : null,
        };

        _homeworkEditor = window;
        window.Closed += (_, _) => _homeworkEditor = null;

        window.ShowDialog();
    }

    Views.HomeworkEditorWindow? _homeworkEditor;

    public void OpenDutyEditor() => OpenDialog(vm => new DutyDialog(vm));
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
        if (Updates is null) return;
        if (_updateLaunched)
        {
            // 已有更新器在跑：明确提示而不是没反应（用户中断更新后常以为坏了）
            Log?.Warn("[更新] 已有更新任务在执行，忽略本次点击");
            Tray?.Balloon("正在更新", "已有一个更新任务在进行中，请等它完成", 5);
            return;
        }

        var result = await Updates.ApplyAsync(offer).ConfigureAwait(true);
        if (!result.Ok)
        {
            Log?.Warn("启动更新失败：" + result.Message);
            Tray?.Balloon("更新失败", result.Message, 5);
            return;
        }

        _updateLaunched = true;

        HideForUpdater();
        WatchUpdaterProcess(result.Pid);

        Log?.Info("更新器已启动，主程序已隐藏并保持后台运行（替换前由更新器关闭）");
        Tray?.Balloon("正在更新", "更新器正在下载，安装时会自动关闭程序", 5);
    }

    /// <summary>
    /// 更新器启动后的隐藏处理：只藏起可见窗口，不退出进程、不 Shutdown、不 Dispose 托盘。
    /// 更新器会在真正替换文件前强杀主程序；这里提前退出反而会让文件句柄/进程关系失控。
    /// </summary>
    public void HideForUpdater()
    {
        _updateLaunched = true;

        try { Panels?.Collapse(); } catch (Exception ex) { Log?.Warn("隐藏下拉面板失败：" + ex.Message); }
        try { _settingsWindow?.Hide(); } catch { }
        try { _homeworkEditor?.Hide(); } catch { }
        try { _rollCall?.Hide(); } catch { }
        try { _mediaCard?.Hide(); } catch { }
        try { SubIsland?.Hide(); } catch { }
        try { Island?.Hide(); } catch { }
        try { Main?.Hide(); } catch { }
    }

    int _autoUpdateCheckStarted;

    /// <summary>启动自动检查更新：延迟 3 秒，强制把异常写进日志，避免任务被吞掉导致无提示。</summary>
    void StartAutoUpdateCheck()
    {
        if (Interlocked.Exchange(ref _autoUpdateCheckStarted, 1) == 1) return;

        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            try
            {
                App.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        var updates = Updates;
                        if (updates is null)
                        {
                            Log?.Info("[自动更新] UpdateService 未初始化");
                            return;
                        }

                        var status = await updates.CheckAsync(silent: false).ConfigureAwait(true);

                        // 预复制更新器失败不应该让弹窗消失：再跑一次静默检查，只负责“有没有新版本”。
                        if (status.App is null && status.Stage == UpdateStage.Failed)
                        {
                            Log?.Info("[自动更新] 首次检查失败，改用静默检查再试一次...");
                            status = await updates.CheckAsync(silent: true).ConfigureAwait(true);
                        }

                        Log?.Info($"[自动更新] 检查完成，状态：{status.Stage}，是否有更新：{status.HasAnyUpdate}，App：{status.App?.Version.ToString() ?? "null"}");

                        if (status.Stage == UpdateStage.Failed)
                            Log?.Error("[自动更新] 检查失败：" + (status.Error ?? status.Message));

                        if (status.HasAnyUpdate && status.App != null)
                        {
                            Log?.Info($"[自动更新] 发现新版本 {status.App.Version}，准备弹窗...");
                            var prompt = new UpdatePromptWindow(updates, status.App, this);
                            prompt.ShowDialog();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log?.Error("[自动更新] 检查或弹窗失败: " + ex.ToString());
                    }
                });
            }
            catch (Exception ex)
            {
                Log?.Error("[自动更新] 启动延迟检查调度失败: " + ex.ToString());
            }
        });
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
        try { Weather?.Flush(); Weather?.Dispose(); } catch { }
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

    /// <summary>重新安装最新版本：复用更新器，**不比较版本号**，直接重新下载并替换程序文件。</summary>
    public async Task ReinstallLatestAsync()
    {
        if (Updates is null) return;
        if (_updateLaunched)
        {
            // 已经有更新器在跑：再拉一个会互相抢文件，明确告诉用户而不是没反应
            Log?.Warn("[重装] 已有更新/重装任务在执行，忽略本次");
            Tray?.Balloon("重新安装", "已有一个更新任务在进行中，请等它完成", 5);
            return;
        }

        var result = await Updates.ReinstallLatestAsync().ConfigureAwait(true);
        if (!result.Ok)
        {
            Log?.Warn("重新安装失败：" + result.Message);
            Tray?.Balloon("重新安装失败", result.Message, 5);
            return;
        }

        _updateLaunched = true;
        HideForUpdater();
        WatchUpdaterProcess(result.Pid);

        Log?.Info("[重装] 更新器已启动，主程序隐藏并保持后台运行");
        Tray?.Balloon("正在重新安装", "更新器正在下载最新版，安装时会自动关闭程序", 5);
    }

    /// <summary>
    /// 盯着更新器进程：它一退出就解除"更新进行中"的锁存。
    ///
    /// 没有这一步的话，用户中途取消/关闭更新器后，主程序永远以为"更新还在跑"，
    /// 之后点「立即更新 / 重新安装」都会被静默忽略（用户实测踩过）。
    /// 更新器正常完成时会自己重启主程序，那时锁存状态随重启自然消失，不受影响。
    /// </summary>
    private void WatchUpdaterProcess(int pid)
    {
        if (pid <= 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(pid);
                // 最多等 30 分钟：正常更新几分钟内必然结束
                for (var i = 0; i < 3600; i++)
                {
                    if (proc.HasExited) break;
                    await Task.Delay(500).ConfigureAwait(false);
                }
                proc.Dispose();
            }
            catch { /* 进程已经没了 = 已退出 */ }

            _updateLaunched = false;
            Log?.Info("[更新] 更新器进程已结束，解除更新锁存");
        });
    }

    /// <summary>
    /// 重启软件：先收尾（停定时器、卸插件、清运行标记），再启动新实例，最后关闭自己。
    /// 新实例带 <c>--restart</c> 参数：单实例锁没释放时会等旧实例退出，而不是被当成第二实例唤醒旧的。
    /// </summary>
    public void Restart()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            Log?.Warn("[重启] 找不到自身可执行文件，无法重启");
            return;
        }

        Log.Info("[重启] 用户请求重启软件");

        // 先把"正常关闭"该做的做了：运行标记必须清掉，否则新实例会误报"上次异常退出"
        try { Plugins?.ShutdownAll(); } catch (Exception ex) { Log?.Warn("[重启] 卸载插件失败：" + ex.Message); }
        try { _watchdog?.Dispose(); } catch { }
        ClearRunningMarker();

        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            info.ArgumentList.Add("--restart");
            System.Diagnostics.Process.Start(info);
        }
        catch (Exception ex)
        {
            Log?.Warn("[重启] 启动新实例失败：" + ex.Message);
            Tray?.Balloon("重启失败", ex.Message, 5);
            return;
        }

        Application.Current.Shutdown();
    }

    public void Shutdown()
    {
        Log?.Section("ClassDailyLand2 退出");
        if (_startedAt != default)
        {
            var uptime = DateTime.Now - _startedAt;
            Log?.Step("退出", $"本次运行时长 {uptime.Hours} 小时 {uptime.Minutes} 分 {uptime.Seconds} 秒");
        }
        Log?.Memory("退出前");
        Log?.Step("退出", "开始收尾：停止定时器 / 关闭插件 / 释放托盘");

        _tick.Stop();
        try { Plugins?.ShutdownAll(); }
        catch { /* 退出路径不抛 */ }
        try { _watchdog?.Dispose(); }      // 观察进程还活着就一起带走（标记为预期退出，不会报错）
        catch { /* 退出路径不抛 */ }
        Tray?.Dispose();
        Tray = null;

        // 正常关闭：删掉运行标记，下次启动就不会报"上次异常退出"
        ClearRunningMarker();

        Log?.Step("退出", "收尾完成");
        Log?.Section("进程退出");
    }
}
