using System.Globalization;

namespace ClassDailyLand.Core.Models;

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

    /// <summary>全部设置键与默认值（与需求文档第五节对应）。</summary>
    public static readonly IReadOnlyList<SettingMeta> All = new List<SettingMeta>
    {
        // —— 通用 ——
        new("show_main_window", ValueKind.Bool, true),
        new("main_width_ratio", ValueKind.Int, 22, 10, 80),          // 窗口宽度 %
        new("main_opacity", ValueKind.Int, 100, 30, 100),            // 透明度 %
        new("main_font_size", ValueKind.Int, 16, 10, 34),            // 字号
        new("homework_auto_scroll", ValueKind.Bool, true),
        new("homework_scroll_interval_ms", ValueKind.Int, 1200, 100, 20000), // 滚动间隔
        new("homework_scroll_step", ValueKind.Int, 24, 1, 400),      // 滚动步长(px)
        new("homework_scroll_pause_ms", ValueKind.Int, 2500, 0, 30000),      // 到底暂停
        new("check_update_on_startup", ValueKind.Bool, false),
        new("block_update_on_source", ValueKind.Bool, true),          // 源码运行时保护更新

        // —— 外观 ——
        new("theme", ValueKind.Str, "system"),                        // system / light / dark
        new("fade_animation", ValueKind.Bool, true),
        new("animation_duration_ms", ValueKind.Int, 220, 0, 2000),
        new("anim_fade_panel", ValueKind.Bool, true),                 // 面板展开渐显
        new("anim_fade_text", ValueKind.Bool, true),                  // 文字变化渐显
        new("settings_mica", ValueKind.Bool, false),                  // 设置窗云母材质（Win11）

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

        // —— 作业 ——
        new("homework_show_date", ValueKind.Bool, true),

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
            "homework_scroll_pause_ms", "check_update_on_startup", "block_update_on_source",
        },
        [PageAppearance] = new[]
        {
            "theme", "fade_animation", "animation_duration_ms",
            "anim_fade_panel", "anim_fade_text", "settings_mica",
        },
        [PageIsland] = new[]
        {
            "show_island", "island_top_margin", "island_countdown_sec", "island_alert_width",
            "island_alert_hold_ms", "island_hide_on_fullscreen", "island_wake_animation",
            "show_sub_island", "sub_island_default_collapsed", "sub_island_auto_collapse_sec",
        },
        [PageSchedule] = new[] { "schedule_advance_minutes", "schedule_time_offset_seconds" },
        [PageHomework] = new[] { "homework_show_date" },
        [PageAbout] = Array.Empty<string>(),
        [PagePlugins] = Array.Empty<string>(),
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
