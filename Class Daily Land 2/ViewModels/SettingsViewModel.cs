using System.ComponentModel;
using System.Windows.Threading;
using ClassDailyLand.App.Theme;
using ClassDailyLand.Core.Managers;
using ClassDailyLand.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassDailyLand.App.ViewModels;

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
///     settings_mica / anim_fade_panel / anim_fade_text / block_update_on_source → 同名新增
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

    public bool BlockUpdateOnSource
    {
        get => S.Bool("block_update_on_source");
        set => Write("block_update_on_source", value);
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

    /// <summary>非 Win11 时云母不可用（外观页那行会置灰并说明原因）。</summary>
    public bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>关于页 / 外观页脚注用的系统描述。</summary>
    public string SystemDescription =>
        IsWindows11 ? "Windows 11" : $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}";

    public string MaterialDescription =>
        SettingsMica && IsWindows11 ? "Windows 11 云母（Mica）" : "普通";

    /// <summary>外观页「窗口材质」分区的脚注。</summary>
    public string MaterialNote => "当前系统：" + SystemDescription;

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
