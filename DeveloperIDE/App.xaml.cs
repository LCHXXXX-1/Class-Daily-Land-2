using System;
using System.Windows;

namespace DeveloperIDE;

/// <summary>
/// 应用入口 + 全局主题开关。
///
/// 主题只做一件事：把 MergedDictionaries 里那本配色字典整本换掉，
/// 所有 {DynamicResource XxxBrush} 会自动重解析 —— 不做任何逐控件遍历赋值。
/// AvalonEdit 这类不认 DynamicResource 的控件靠 <see cref="ThemeChanged"/> 自己同步。
/// </summary>
public partial class App : Application
{
    public const string DarkSource = "Themes/DarkTheme.xaml";
    public const string LightSource = "Themes/LightTheme.xaml";

    /// <summary>当前是否深色（浅色窗口里的硬编码颜色靠它兜底）。</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>主题名（存进 ide_config.json 的字符串）。</summary>
    public static string CurrentTheme => IsDark ? "dark" : "light";

    /// <summary>配色字典换完时触发（此时 FindResource 已经能取到新颜色）。</summary>
    public static event Action? ThemeChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 上次选过浅色就照着恢复（不写回配置，省一次落盘）
        if (string.Equals(IdeConfig.Load().Theme, "light", StringComparison.OrdinalIgnoreCase))
            ApplyTheme(isDark: false, save: false);
    }

    /// <summary>切换主题并记住选择。</summary>
    public static void ApplyTheme(bool isDark) => ApplyTheme(isDark, save: true);

    public static void ApplyTheme(bool isDark, bool save)
    {
        var app = Current;
        if (app is null) return;

        var from = isDark ? LightSource : DarkSource;
        var to = isDark ? DarkSource : LightSource;
        var dictionaries = app.Resources.MergedDictionaries;

        var replaced = false;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source;
            if (source is null) continue;
            if (!source.OriginalString.EndsWith(from, StringComparison.OrdinalIgnoreCase)) continue;

            dictionaries[i] = new ResourceDictionary { Source = new Uri(to, UriKind.Relative) };
            replaced = true;
            break;
        }

        if (!replaced)
            dictionaries.Add(new ResourceDictionary { Source = new Uri(to, UriKind.Relative) });

        IsDark = isDark;

        if (save)
        {
            var config = IdeConfig.Load();
            (config with { Theme = CurrentTheme }).Save();
        }

        ThemeChanged?.Invoke();
    }
}
