using System.Windows;
using System.Windows.Media;

namespace ClassDailyLand.App.Theme;

/// <summary>
/// 主题切换服务：换掉 App.Resources 里的 Tokens.Light/Dark 字典，
/// 所有 DynamicResource 立刻重解析（设置中心整窗换色，不重建可视树）。
/// 岛 / 主窗 / 面板是自绘的，走 <see cref="Palette"/>，这里同步调用它，保证两边颜色同源。
/// </summary>
public static class ThemeService
{
    const string LightSource = "pack://application:,,,/Themes/Tokens.Light.xaml";
    const string DarkSource = "pack://application:,,,/Themes/Tokens.Dark.xaml";

    /// <summary>当前是否为深色（跟随系统时按系统实际值）。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>主题字典已替换后触发（自绘窗口用它重画）。</summary>
    public static event Action? Changed;

    /// <summary>mode：system / light / dark。</summary>
    public static void Apply(string mode)
    {
        IsDark = mode switch
        {
            "light" => false,
            "dark" => true,
            _ => IsSystemDark(),
        };

        SwapThemeDictionary(IsDark);
        Palette.Apply(mode);          // 岛 / 主窗 / 面板同步
        Changed?.Invoke();
    }

    static void SwapThemeDictionary(bool dark)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var target = dark ? DarkSource : LightSource;
        var other = dark ? LightSource : DarkSource;

        // 已经是目标主题就别再动：重复 Add 会让字典里堆好几份色板，
        // 反复替换还会让整窗可视树重解析（值没变也当变化处理，是这类 bug 的根源）
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source is null) continue;
            if (SameUri(source, target)) return;
            if (!SameUri(source, other)) continue;

            dictionaries[i] = new ResourceDictionary { Source = new Uri(target, UriKind.Absolute) };
            return;
        }

        dictionaries.Add(new ResourceDictionary { Source = new Uri(target, UriKind.Absolute) });
    }

    static bool SameUri(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    static string Normalize(string uri) => uri.Replace("pack://application:,,,/", "").TrimStart('/');

    static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int i) return i == 0;
        }
        catch { /* 读不到注册表就按浅色（与 tokens.css 默认 :root 一致） */ }
        return false;
    }

    /// <summary>取当前主题下的资源（画刷等），供代码里取色用。</summary>
    public static T? Find<T>(string key) where T : class =>
        Application.Current.TryFindResource(key) as T;

    public static Brush Text => Find<Brush>("TextFillColorPrimaryBrush") ?? Brushes.Black;
    public static Brush TextSecondary => Find<Brush>("TextFillColorSecondaryBrush") ?? Brushes.Gray;
    public static Brush Accent => Find<Brush>("AccentFillColorDefaultBrush") ?? Brushes.DodgerBlue;
}
