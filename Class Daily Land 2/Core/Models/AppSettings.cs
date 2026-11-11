using System.Globalization;

namespace ClassDailyLand2.Core.Models;

public enum ValueKind { Int, Bool, Str }

/// <summary>一个设置键的元数据：默认值、类型、整数范围。</summary>
public sealed record SettingMeta(string Key, ValueKind Kind, object Default, int Min = 0, int Max = 0);

/// <summary>
/// settings.json 的内存表示。所有键都有类型校验：类型不符 / 越界的键回退默认值，
/// 未知键原样保留（不会被写坏）。
/// </summary>
public sealed class AppSettings
{
    public const string PageGeneral = "general";
    public const string PageAppearance = "appearance";
    public const string PageIsland = "island";
    public const string PageSchedule = "schedule";
    public const string PageHomework = "homework";
    public const string PageAbout = "about";
    public const string PagePlugins = "plugins";
    public const string PageMedia = "media";
    public const string PageDisaster = "disaster";
    public const string PageWeather = "weather";

    /// <summary>全部设置键与默认值（与需求文档第五节对应）。</summary>
    public static readonly IReadOnlyList<SettingMeta> All = new List<SettingMeta>
    {
        // —— 通用 ——
        new("show_main_window", ValueKind.Bool, true),
        new("main_width_ratio", ValueKind.Int, 22, 10, 80),          // 窗口宽度 %
        new("main_opacity", ValueKind.Int, 100, 30, 100),            // 透明度 %
        new("main_font_size", ValueKind.Int, 16, 10, 34),            // 字号
        new("board_bg_mode", ValueKind.Str, "inherit"),               // 班级日常窗口背景：inherit（跟随主题）/ color / image
        new("board_bg_color", ValueKind.Str, "#FFFFFF"),              // 纯色背景
        new("board_bg_image", ValueKind.Str, ""),                     // 背景图片（绝对路径）
        new("homework_auto_scroll", ValueKind.Bool, true),
        new("homework_scroll_interval_ms", ValueKind.Int, 1200, 100, 20000), // 滚动间隔
        new("homework_scroll_step", ValueKind.Int, 24, 1, 400),      // 滚动步长(px)
        new("homework_scroll_pause_ms", ValueKind.Int, 2500, 0, 30000),      // 到底暂停
        new("check_update_on_startup", ValueKind.Bool, false),
        new("start_with_windows", ValueKind.Bool, false),             // 开机自启（HKCU Run 键）

        // —— 更新（关于页）——
        new("update_channel", ValueKind.Str, "stable"),               // stable / beta
        new("update_source", ValueKind.Str, "gitee"),                  // gitee / github / auto（GitHub 优先→Gitee）；默认 gitee（国内更新走 Gitee 更稳）

        // —— 插件市场（插件页）——
        new("market_source", ValueKind.Str, "gitee"),                  // github / gitee / auto（GitHub 优先→Gitee）；默认 gitee（国内走 Gitee 更稳）

        // —— 日志与诊断 ——
        new("log_max_lines", ValueKind.Int, 2000, 500, 3000),         // 内存里保留的日志行数（500~3000）
        new("developer_mode", ValueKind.Bool, false),                 // 开发者选项（关于页最底部，打开后出「测试报错」）
        new("update_skipped_version", ValueKind.Str, ""),             // 「稍后再说」跳过的版本
        new("resources_version", ValueKind.Str, ""),                  // 已装资源包版本（空 = 只有内嵌资源）

        // —— 外观 ——
        new("theme", ValueKind.Str, "system"),                        // system / light / dark
        new("fade_animation", ValueKind.Bool, true),
        new("animation_duration_ms", ValueKind.Int, 220, 0, 2000),
        new("anim_fade_panel", ValueKind.Bool, true),                 // 面板展开渐显
        new("anim_fade_text", ValueKind.Bool, true),                  // 文字变化渐显
        new("settings_mica", ValueKind.Bool, true),                   // 设置窗云母材质（Win11，默认开）
        new("settings_acrylic", ValueKind.Bool, false),               // 设置窗亚克力材质（与云母互斥，默认关）
        new("color_theme", ValueKind.Str, "blue"),                    // 配色主题：blue/violet/teal/green/amber/rose/graphite
        new("settings_background", ValueKind.Str, "mica"),             // 设置窗背景：mica（云母取色）/ acrylic（亚克力模糊）/ solid / image
        new("settings_bg_color", ValueKind.Str, "#F3F3F3"),           // 纯色背景颜色
        new("settings_bg_image", ValueKind.Str, ""),                  // 自定义背景图片（绝对路径）

        // —— 灵动岛 ——
        new("show_island", ValueKind.Bool, true),
        new("island_top_margin", ValueKind.Int, 6, 0, 200),
        new("island_countdown_sec", ValueKind.Int, 60, 5, 600),
        new("island_alert_width", ValueKind.Int, 300, 160, 900),
        new("island_alert_hold_ms", ValueKind.Int, 600, 200, 10000),
        new("island_hide_on_fullscreen", ValueKind.Bool, true),
        new("island_wake_animation", ValueKind.Bool, true),
        new("show_sub_island", ValueKind.Bool, true),
        new("sub_island_default_collapsed", ValueKind.Bool, true),
        new("sub_island_auto_collapse_sec", ValueKind.Int, 8, 0, 600),

        // —— 课表与提醒 ——
        new("schedule_advance_minutes", ValueKind.Int, 2, 0, 60),     // 提前提醒
        new("schedule_time_offset_seconds", ValueKind.Int, 0, -3600, 3600), // 时间偏移
        new("weekend_custom_schedule", ValueKind.Bool, false),        // 课表编辑器里展开周末课表

        // —— 作业 ——
        new("homework_show_date", ValueKind.Bool, true),
        new("homework_history_days", ValueKind.Int, 7),              // 历史快照保留天数
        new("homework_notify_enabled", ValueKind.Bool, true),        // 提交时间到点弹通知

        // —— 媒体（内置集成：主岛媒体片段）——
        new("audio_enabled", ValueKind.Bool, true),
        new("audio_show_on", ValueKind.Str, "main"),                  // main / sub
        new("audio_hide_after_sec", ValueKind.Int, 5, 0, 120),
        new("audio_max_width", ValueKind.Int, 240, 120, 400),
        new("audio_show_title", ValueKind.Bool, true),

        // —— 灾害预警（内置集成：地震倒计时 + 气象预警）——
        new("disaster_enabled", ValueKind.Bool, true),
        new("disaster_quake_enabled", ValueKind.Bool, true),
        new("disaster_weather_enabled", ValueKind.Bool, true),
        new("disaster_province", ValueKind.Str, "四川省"),
        new("disaster_city", ValueKind.Str, ""),
        new("disaster_district", ValueKind.Str, ""),
        new("disaster_lat", ValueKind.Str, ""),                       // 留空则按省市查表
        new("disaster_lon", ValueKind.Str, ""),
        new("disaster_threshold_x10", ValueKind.Int, 20, 0, 120),      // 烈度阈值 ×10（20 = 2.0）
        new("disaster_quake_interval_sec", ValueKind.Int, 5, 3, 120),
        new("disaster_weather_interval_sec", ValueKind.Int, 60, 30, 3600),
        new("disaster_throttle_sec", ValueKind.Int, 30, 5, 600),
        new("disaster_notify_enabled", ValueKind.Bool, true),
        new("disaster_banner_enabled", ValueKind.Bool, true),
        new("disaster_small_hold_sec", ValueKind.Int, 5, 0, 120),
        new("disaster_show_status_card", ValueKind.Bool, true),

        // —— 自动定位（IP）：天气 / 气象预警 / 地震倒计时共用一个开关 ——
        // 开 = IP 定位（失败退回下面手选的省市区查表）；关 = 手填经纬度 > 省市区查表。
        new("auto_locate", ValueKind.Bool, true),

        // —— 天气（内置集成：实时天气 + 天气动画）——
        new("weather_enabled", ValueKind.Bool, true),
        new("weather_show_on", ValueKind.Str, "sub"),                  // sub / main / off
        new("weather_refresh_sec", ValueKind.Int, 900, 300, 3600),      // 实况刷新间隔
        new("weather_idle_collapse_sec", ValueKind.Int, 300, 0, 3600),  // 副岛多久没操作收成圆，0=不收
        new("weather_animations", ValueKind.Bool, true),                // 关掉只画静态图形

        // —— 关于 ——
        new("last_update_check", ValueKind.Str, ""),
    };

    /// <summary>设置中心各页拥有的键（用于「本页恢复默认」）。</summary>
    public static readonly IReadOnlyDictionary<string, string[]> PageKeys = new Dictionary<string, string[]>
    {
        [PageGeneral] = new[]
        {
            "show_main_window", "main_width_ratio", "main_opacity", "main_font_size",
            "homework_auto_scroll", "homework_scroll_interval_ms", "homework_scroll_step",
            "homework_scroll_pause_ms", "check_update_on_startup",
            "start_with_windows",
        },
        [PageAppearance] = new[]
        {
            "theme", "color_theme", "settings_background", "settings_bg_color", "settings_bg_image",
            "board_bg_mode", "board_bg_color", "board_bg_image",
            "fade_animation", "animation_duration_ms",
            "anim_fade_panel", "anim_fade_text", "settings_mica", "settings_acrylic",
        },
        [PageIsland] = new[]
        {
            "show_island", "island_top_margin", "island_countdown_sec", "island_alert_width",
            "island_alert_hold_ms", "island_hide_on_fullscreen", "island_wake_animation",
            "show_sub_island", "sub_island_default_collapsed", "sub_island_auto_collapse_sec",
        },
        [PageSchedule] = new[] { "schedule_advance_minutes", "schedule_time_offset_seconds", "weekend_custom_schedule" },
        [PageHomework] = new[] { "homework_show_date", "homework_history_days", "homework_notify_enabled" },
        // 关于页：更新通道、更新源、被跳过的版本、上次检查时间、已装资源包版本、开发者选项
        [PageAbout] = new[] { "update_channel", "update_source", "update_skipped_version", "last_update_check", "resources_version", "developer_mode" },
        // 日志页：内存里保留多少行
        ["logs"] = new[] { "log_max_lines" },
        [PagePlugins] = new[] { "market_source" },
        [PageMedia] = new[]
        {
            "audio_enabled", "audio_show_on", "audio_hide_after_sec",
            "audio_max_width", "audio_show_title",
        },
        [PageDisaster] = new[]
        {
            "disaster_enabled", "disaster_quake_enabled", "disaster_weather_enabled",
            "disaster_province", "disaster_city", "disaster_district",
            "disaster_lat", "disaster_lon", "disaster_threshold_x10",
            "disaster_quake_interval_sec", "disaster_weather_interval_sec",
            "disaster_throttle_sec", "disaster_notify_enabled", "disaster_banner_enabled",
            "disaster_small_hold_sec", "disaster_show_status_card",
            // 位置这一组由「自动定位」总开关决定用 IP 还是手选，两页都能复原它
            "auto_locate",
        },
        [PageWeather] = new[]
        {
            "weather_enabled", "weather_show_on", "weather_refresh_sec",
            "weather_idle_collapse_sec", "weather_animations", "auto_locate",
        },
    };

    static readonly Dictionary<string, SettingMeta> Meta = All.ToDictionary(m => m.Key, StringComparer.Ordinal);

    /// <summary>已知的合法值（含类型修正后的值）。未知键存在 Extras 里。</summary>
    readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    /// <summary>文件中读到但当前版本不认识的数据（类型安全的 JsonElement 已降级为 object）。</summary>
    public Dictionary<string, object?> Extras { get; } = new(StringComparer.Ordinal);

    public AppSettings()
    {
        foreach (var m in All) _values[m.Key] = m.Default;
    }

    public static object? DefaultOf(string key) => Meta.TryGetValue(key, out var m) ? m.Default : null;

    /// <summary>
    /// 从原始 JSON 字典构造。逐键校验类型，不符则回退默认值。
    /// raw 的值通常是 JsonElement / string / bool / int / long / double。
    /// </summary>
    public static AppSettings FromRaw(IReadOnlyDictionary<string, object?> raw, out List<string> repaired)
    {
        var s = new AppSettings();
        repaired = new List<string>();
        foreach (var (key, value) in raw)
        {
            if (!Meta.TryGetValue(key, out var meta))
            {
                s.Extras[key] = value;   // 保留未知键，写回时不丢
                continue;
            }
            var coerced = Coerce(meta, value);
            if (coerced is null)
            {
                repaired.Add(key);       // 类型不符 -> 默认值（保持 _values 里的默认）
                continue;
            }
            s._values[key] = coerced;
        }
        return s;
    }

    static object? Coerce(SettingMeta meta, object? value)
    {
        switch (meta.Kind)
        {
            case ValueKind.Bool:
                if (value is bool b) return b;
                if (value is string bs)
                {
                    if (bool.TryParse(bs, out var pb)) return pb;
                    if (bs == "1") return true;
                    if (bs == "0") return false;
                }
                if (value is int ib) return ib != 0;
                return null;

            case ValueKind.Int:
                int? n = value switch
                {
                    int i => i,
                    long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
                    double d when d is >= int.MinValue and <= int.MaxValue => (int)Math.Round(d, MidpointRounding.AwayFromZero),
                    string str when int.TryParse(str.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
                    _ => null,
                };
                if (n is null) return null;
                if (meta.Max > meta.Min) return Math.Clamp(n.Value, meta.Min, meta.Max);
                return n.Value;

            case ValueKind.Str:
                // 严格：数字/布尔/对象都不算字符串设置值，视为类型不符 -> 回退默认
                return value is string sstr ? sstr : null;

            default:
                return null;
        }
    }

    // ——— 类型化读取（带默认回退 + 范围钳制）———

    public int GetInt(string key)
    {
        if (!_values.TryGetValue(key, out var v)) return (int)(Meta[key].Default);
        var n = v is int i ? i : Convert.ToInt32(v, CultureInfo.InvariantCulture);
        var m = Meta[key];
        return m.Max > m.Min ? Math.Clamp(n, m.Min, m.Max) : n;
    }

    public bool GetBool(string key) => _values.TryGetValue(key, out var v) && v is bool b && b;

    public string GetString(string key) => _values.TryGetValue(key, out var v) ? v as string ?? "" : "";

    public object? Get(string key) =>
        _values.TryGetValue(key, out var v) ? v
        : Meta.TryGetValue(key, out var m) ? m.Default
        : Extras.TryGetValue(key, out var extra) ? extra   // 未知键也要读得回来，否则写了存不进盘
        : null;

    /// <summary>写入（类型不符时忽略并返回 false，避免把配置写坏）。</summary>
    public bool Set(string key, object? value)
    {
        if (!Meta.TryGetValue(key, out var meta))
        {
            Extras[key] = value;
            return true;
        }
        var coerced = Coerce(meta, value);
        if (coerced is null) return false;
        _values[key] = coerced;
        return true;
    }

    public void ResetToDefault(string key)
    {
        if (Meta.TryGetValue(key, out var m)) _values[key] = m.Default;
    }

    public void ResetPage(string page)
    {
        if (!PageKeys.TryGetValue(page, out var keys)) return;
        foreach (var k in keys) ResetToDefault(k);
    }

    /// <summary>导出为可序列化的字典（键有序，便于 diff）。</summary>
    public Dictionary<string, object?> ToDictionary()
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var m in All) d[m.Key] = _values.TryGetValue(m.Key, out var v) ? v : m.Default;
        foreach (var (k, v) in Extras) d[k] = v;
        return d;
    }

    public int ThemeFollowSystem => GetString("theme") == "system" ? 1 : 0;
}
