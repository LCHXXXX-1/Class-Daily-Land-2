using ClassDailyLand2.App.Theme;
using ClassDailyLand2.Core.Managers;
using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;
using System.Windows.Threading;

namespace ClassDailyLand2.App.ViewModels;

/// <summary>
/// 设置中心的视图模型：把 settings.json 的键暴露成强类型属性，页面只管绑定。
///
/// 键名沿用本项目既有的 snake_case（与 webview 版的差异集中在下面这张映射表里），
/// 单位差异（web 用 0..1 小数、我们用整数百分比）也在这里消化：
///
///   web 键 → 本项目键
///     main_auto_scroll            → homework_auto_scroll
///     main_scroll_interval        → homework_scroll_interval_ms
///     main_scroll_step            → homework_scroll_step
///     main_scroll_pause           → homework_scroll_pause_ms
///     theme_mode                  → theme
///     anim_fade_window            → fade_animation
///     anim_duration               → animation_duration_ms
///     island_show_wakeup_anim     → island_wake_animation
///     sub_island_collapsed        → sub_island_default_collapsed
///     check_update_on_start       → check_update_on_startup
///     settings_mica / anim_fade_panel / anim_fade_text → 同名新增
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    readonly AppController _ctx;
    readonly Dictionary<string, string> _keyToProperty = new(StringComparer.Ordinal);
    readonly Dictionary<string, object?> _pending = new(StringComparer.Ordinal);
    DispatcherTimer? _flushTimer;
    bool _writingFromUi;

    public SettingsViewModel(AppController ctx)
    {
        _ctx = ctx;
        ctx.Settings.SettingsChanged += OnSettingsChangedExternally;
    }

    public AppController Ctx => _ctx;
    public string Version => AppController.Version;

    // ————————————————————— 更新（关于页） —————————————————————

    /// <summary>更新通道：stable / beta。绑定到关于页的分段选择器。</summary>
    public string UpdateChannel
    {
        get
        {
            var value = S.Str("update_channel");
            return string.Equals(value, "beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "stable";
        }
        set => Write("update_channel", string.Equals(value, "beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "stable");
    }

    /// <summary>上次检查更新的时间（空 = 还没检查过）。</summary>
    public string LastUpdateCheck => S.Str("last_update_check");

    /// <summary>已安装的资源包版本（空 = 只有内嵌资源）。</summary>
    public string ResourcesVersion => S.Str("resources_version");
    public string ConfigPath => _ctx.DataRoot;

    /// <summary>实时预览通道：滑块每次变化都发（不落盘、不广播），由宿主直接刷主窗。</summary>
    public event Action<string>? PreviewChanged;

    SettingsManager S => _ctx.Settings;

    // ————————————————————— 读写通道 —————————————————————

    /// <summary>立即写入（开关、下拉这类离散值）。</summary>
    void Write(string key, object? value, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        _keyToProperty[key] = property;
        _writingFromUi = true;
        S.Set(key, value);
        _writingFromUi = false;
        OnPropertyChanged(property);
    }

    /// <summary>
    /// 防抖写入（滑块这类连续值）：UI 立刻回显，200ms 内合并成一次落盘，
    /// 避免一次拖动打出几十次原子写。
    /// </summary>
    void WriteDelayed(string key, object? value, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        _keyToProperty[key] = property;
        _pending[key] = value;
        // 先让内存值跟上（不广播、不落盘），这样同一页其它控件读到的是新值；
        // 再发一次预览事件，让主窗在拖动过程中就跟着变，不必等 200ms 落盘。
        S.Current.Set(key, value);
        OnPropertyChanged(property);
        PreviewChanged?.Invoke(key);

        if (_flushTimer is null)
        {
            _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _flushTimer.Tick += (_, _) => Flush();
        }
        _flushTimer.Stop();
        _flushTimer.Start();
    }

    void Flush()
    {
        _flushTimer?.Stop();
        if (_pending.Count == 0) return;
        var batch = _pending.ToList();
        _pending.Clear();
        _writingFromUi = true;
        foreach (var (key, value) in batch) S.Set(key, value);
        _writingFromUi = false;
    }

    /// <summary>窗口关闭前调用，确保防抖中的值都落盘。</summary>
    public void FlushPending() => Flush();

    /// <summary>窗口关闭时调用：断开与设置管理器的连接，避免反复开关窗口留下悬挂引用。</summary>
    public void Detach()
    {
        Flush();
        PreviewChanged = null;
        _ctx.Settings.SettingsChanged -= OnSettingsChangedExternally;
    }

    void OnSettingsChangedExternally(object? sender, SettingsChangedEventArgs e)
    {
        if (_writingFromUi) return;          // 自己引起的改动不再回刷，避免打断拖动
        foreach (var key in e.Keys)
            if (_keyToProperty.TryGetValue(key, out var property))
                OnPropertyChanged(property);
    }

    // ————————————————————— 通用 —————————————————————

    public bool ShowMainWindow
    {
        get => S.Bool("show_main_window");
        set => Write("show_main_window", value);
    }

    /// <summary>主窗宽度占工作区百分比（web 版是小数比例，这里统一用整数百分比）。</summary>
    public int MainWidthPercent
    {
        get => S.Int("main_width_ratio");
        set => WriteDelayed("main_width_ratio", value);
    }

    public int MainOpacityPercent
    {
        get => S.Int("main_opacity");
        set => WriteDelayed("main_opacity", value);
    }

    public int MainFontSize
    {
        get => S.Int("main_font_size");
        set => WriteDelayed("main_font_size", value);
    }

    public bool HomeworkAutoScroll
    {
        get => S.Bool("homework_auto_scroll");
        set => Write("homework_auto_scroll", value);
    }

    public int HomeworkScrollIntervalMs
    {
        get => S.Int("homework_scroll_interval_ms");
        set => WriteDelayed("homework_scroll_interval_ms", value);
    }

    public int HomeworkScrollStep
    {
        get => S.Int("homework_scroll_step");
        set => WriteDelayed("homework_scroll_step", value);
    }

    public int HomeworkScrollPauseMs
    {
        get => S.Int("homework_scroll_pause_ms");
        set => WriteDelayed("homework_scroll_pause_ms", value);
    }

    public bool CheckUpdateOnStartup
    {
        get => S.Bool("check_update_on_startup");
        set => Write("check_update_on_startup", value);
    }

    /// <summary>开机自启。真正的注册表写入在 AppController.OnSettingsChanged 里做（此处只管存设置）。</summary>
    public bool StartWithWindows
    {
        get => S.Bool("start_with_windows");
        set => Write("start_with_windows", value);
    }

    // ————————————————————— 外观 —————————————————————

    /// <summary>system / light / dark。</summary>
    public string ThemeMode
    {
        get => S.Str("theme");
        set
        {
            Write("theme", value);
            ThemeService.Apply(value);
        }
    }

    public bool FadeAnimation
    {
        get => S.Bool("fade_animation");
        set => Write("fade_animation", value);
    }

    public bool AnimFadePanel
    {
        get => S.Bool("anim_fade_panel");
        set => Write("anim_fade_panel", value);
    }

    public bool AnimFadeText
    {
        get => S.Bool("anim_fade_text");
        set => Write("anim_fade_text", value);
    }

    public int AnimationDurationMs
    {
        get => S.Int("animation_duration_ms");
        set => WriteDelayed("animation_duration_ms", value);
    }

    public bool SettingsMica
    {
        get => S.Bool("settings_mica");
        set => Write("settings_mica", value);
    }

    /// <summary>Win11 才有云母 + 原生圆角。</summary>
    public bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Win10 1803+ 有亚克力模糊；更老的系统才真的没有背板。</summary>
    public bool SupportsBackdrop => Environment.OSVersion.Version.Build >= 17134;

    /// <summary>外观页那一行的标题：Win11 叫云母，Win10 叫亚克力。</summary>
    public string MaterialRowTitle => IsWindows11 ? "云母材质（Mica）" : "亚克力模糊（Acrylic）";

    /// <summary>外观页那一行的说明。</summary>
    public string MaterialRowHint => IsWindows11
        ? "Windows 11 的半透明云母背板，会跟着系统明暗变"
        : "Windows 10 的半透明模糊背板，会跟着系统明暗变";

    /// <summary>关于页 / 外观页脚注用的系统描述。</summary>
    public string SystemDescription =>
        IsWindows11 ? "Windows 11" : $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}";

    public string MaterialDescription => !SettingsMica
        ? "普通"
        : IsWindows11 ? "Windows 11 云母（Mica）" : "Windows 10 亚克力（Acrylic）";

    /// <summary>外观页「窗口材质」分区的脚注。</summary>
    public string MaterialNote => "当前系统：" + SystemDescription;

    // ————————————————————— 触屏 —————————————————————

    /// <summary>
    /// 触屏优化：加大点击目标（控件 32→44、设置行 62→82、导航项 40→54），
    /// 并给设置页/下拉面板开手指拖动滚动。
    /// 切换是即时的（<see cref="TouchMode"/> 改窗口资源，DynamicResource 立刻重解析）。
    /// </summary>
    public bool TouchOptimize
    {
        get => S.Bool("touch_optimize");
        set => Write("touch_optimize", value);
    }

    /// <summary>这台机器有没有触摸屏（没有也能手动开，只是提示语不同）。</summary>
    public bool HasTouchScreen => System.Windows.Input.Tablet.TabletDevices
        .OfType<System.Windows.Input.TabletDevice>()
        .Any(d => d.Type == System.Windows.Input.TabletDeviceType.Touch);

    public string TouchOptimizeHint => HasTouchScreen
        ? "已检测到触摸屏。开启后点击目标放大，手指更容易点中（上下滑动本来就支持，不需要开它）"
        : "开启后点击目标放大（控件 32→44、设置行 62→82），手指更容易点中";

    // ————————————————————— 媒体（内置集成） —————————————————————

    public bool AudioEnabled
    {
        get => S.Bool("audio_enabled");
        set => Write("audio_enabled", value);
    }

    public string AudioShowOn
    {
        get => S.Str("audio_show_on");
        set => Write("audio_show_on", value);
    }

    public int AudioHideAfterSec
    {
        get => S.Int("audio_hide_after_sec");
        set => WriteDelayed("audio_hide_after_sec", Math.Clamp(value, 0, 120));
    }

    public int AudioMaxWidth
    {
        get => S.Int("audio_max_width");
        set => WriteDelayed("audio_max_width", Math.Clamp(value, 120, 400));
    }

    public bool AudioShowTitle
    {
        get => S.Bool("audio_show_title");
        set => Write("audio_show_title", value);
    }

    // ————————————————————— 灾害预警（内置集成） —————————————————————

    public bool DisasterEnabled
    {
        get => S.Bool("disaster_enabled");
        set => Write("disaster_enabled", value);
    }

    public bool DisasterQuakeEnabled
    {
        get => S.Bool("disaster_quake_enabled");
        set => Write("disaster_quake_enabled", value);
    }

    public bool DisasterWeatherEnabled
    {
        get => S.Bool("disaster_weather_enabled");
        set => Write("disaster_weather_enabled", value);
    }

    public bool DisasterNotifyEnabled
    {
        get => S.Bool("disaster_notify_enabled");
        set => Write("disaster_notify_enabled", value);
    }

    public bool DisasterBannerEnabled
    {
        get => S.Bool("disaster_banner_enabled");
        set => Write("disaster_banner_enabled", value);
    }

    public bool DisasterShowStatusCard
    {
        get => S.Bool("disaster_show_status_card");
        set => Write("disaster_show_status_card", value);
    }

    public int DisasterQuakeIntervalSec
    {
        get => S.Int("disaster_quake_interval_sec");
        set => WriteDelayed("disaster_quake_interval_sec", Math.Clamp(value, 3, 120));
    }

    public int DisasterWeatherIntervalSec
    {
        get => S.Int("disaster_weather_interval_sec");
        set => WriteDelayed("disaster_weather_interval_sec", Math.Clamp(value, 30, 3600));
    }

    public int DisasterSmallHoldSec
    {
        get => S.Int("disaster_small_hold_sec");
        set => WriteDelayed("disaster_small_hold_sec", Math.Clamp(value, 0, 120));
    }

    /// <summary>烈度阈值 ×10（20 = 2.0；UI 里是整数下拉）。</summary>
    public int DisasterThresholdX10
    {
        get => S.Int("disaster_threshold_x10");
        set => Write("disaster_threshold_x10", Math.Clamp(value, 0, 120));
    }

    public string DisasterProvince => S.Str("disaster_province");
    public string DisasterCity => S.Str("disaster_city");
    public string DisasterDistrict => S.Str("disaster_district");
    public string DisasterLat => S.Str("disaster_lat");
    public string DisasterLon => S.Str("disaster_lon");

    /// <summary>一次写入省市县三个键（只广播一次，避免连刷三遍界面）。</summary>
    public void SetDisasterLocation(string province, string city, string district)
    {
        S.SetMany(new[]
        {
            new KeyValuePair<string, object?>("disaster_province", province ?? ""),
            new KeyValuePair<string, object?>("disaster_city", city ?? ""),
            new KeyValuePair<string, object?>("disaster_district", district ?? ""),
        });
        OnPropertyChanged(nameof(DisasterProvince));
        OnPropertyChanged(nameof(DisasterCity));
        OnPropertyChanged(nameof(DisasterDistrict));
    }

    public void SetDisasterCoord(string lat, string lon)
    {
        S.SetMany(new[]
        {
            new KeyValuePair<string, object?>("disaster_lat", lat ?? ""),
            new KeyValuePair<string, object?>("disaster_lon", lon ?? ""),
        });
        OnPropertyChanged(nameof(DisasterLat));
        OnPropertyChanged(nameof(DisasterLon));
    }

    // ————————————————————— 灵动岛 —————————————————————

    public bool ShowIsland
    {
        get => S.Bool("show_island");
        set => Write("show_island", value);
    }

    public int IslandTopMargin
    {
        get => S.Int("island_top_margin");
        set => WriteDelayed("island_top_margin", value);
    }

    public int IslandCountdownSec
    {
        get => S.Int("island_countdown_sec");
        set => WriteDelayed("island_countdown_sec", value);
    }

    public int IslandAlertWidth
    {
        get => S.Int("island_alert_width");
        set => WriteDelayed("island_alert_width", value);
    }

    public int IslandAlertHoldMs
    {
        get => S.Int("island_alert_hold_ms");
        set => WriteDelayed("island_alert_hold_ms", value);
    }

    public bool IslandHideOnFullscreen
    {
        get => S.Bool("island_hide_on_fullscreen");
        set => Write("island_hide_on_fullscreen", value);
    }

    public bool IslandWakeAnimation
    {
        get => S.Bool("island_wake_animation");
        set => Write("island_wake_animation", value);
    }

    public bool ShowSubIsland
    {
        get => S.Bool("show_sub_island");
        set => Write("show_sub_island", value);
    }

    public bool SubIslandDefaultCollapsed
    {
        get => S.Bool("sub_island_default_collapsed");
        set => Write("sub_island_default_collapsed", value);
    }

    public int SubIslandAutoCollapseSec
    {
        get => S.Int("sub_island_auto_collapse_sec");
        set => WriteDelayed("sub_island_auto_collapse_sec", value);
    }

    // ————————————————————— 课表与提醒 —————————————————————

    public int ScheduleAdvanceMinutes
    {
        get => S.Int("schedule_advance_minutes");
        set => WriteDelayed("schedule_advance_minutes", value);
    }

    public int ScheduleTimeOffsetSeconds
    {
        get => S.Int("schedule_time_offset_seconds");
        set => WriteDelayed("schedule_time_offset_seconds", value);
    }

    // ————————————————————— 作业 —————————————————————

    public bool HomeworkShowDate
    {
        get => S.Bool("homework_show_date");
        set => Write("homework_show_date", value);
    }

    // ————————————————————— 维护 —————————————————————

    /// <summary>恢复某页默认值（对应 AppSettings.PageKeys）。</summary>
    public void ResetPage(string page)
    {
        S.ResetPage(page);
        OnPropertyChanged(AllProperties);      // 整页属性都重读一遍
    }

    /// <summary>property 名为 null 表示「全部属性都可能变了」。</summary>
    static readonly PropertyChangedEventArgs AllProperties = new(null);

    /// <summary>恢复全部默认（高级页的危险操作）。</summary>
    public void ResetAll()
    {
        S.ResetAll();
        OnPropertyChanged(AllProperties);
        ThemeService.Apply(S.Str("theme"));
    }
}
