using System.Windows;

namespace ClassDailyLand.Updater;

/// <summary>
/// 主题切换（与主程序的 <c>ThemeService</c> 同构，独立小窗自己管自己那份）。
///
/// ★ pack URI 必须带程序集名：<c>pack://application:,,,/Themes/x.xaml</c> 是相对
///   **入口程序集**解析的，换宿主就找不到。这里是 updater 自己的程序集，
///   所以按 <see cref="ThemeBoot"/> 的程序集名拼。
/// </summary>
public static class ThemeBoot
{
    static readonly string AsmPart =
        Uri.EscapeDataString(typeof(ThemeBoot).Assembly.GetName().Name ?? "ClassDailyLand.Updater");

    static readonly string LightSource = $"pack://application:,,,/{AsmPart};component/Themes/Tokens.Light.xaml";
    static readonly string DarkSource = $"pack://application:,,,/{AsmPart};component/Themes/Tokens.Dark.xaml";

    public static bool IsDark { get; private set; }

    /// <summary>mode：system / light / dark。</summary>
    public static void Apply(string? mode = "system")
    {
        IsDark = mode switch
        {
            "light" => false,
            "dark" => true,
            _ => IsSystemDark(),
        };
        Swap(IsDark ? DarkSource : LightSource);
    }

    static void Swap(string target)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var other = ReferenceEquals(target, DarkSource) || target == DarkSource ? LightSource : DarkSource;

        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source is null) continue;

            if (source.EndsWith(TargetTail(target), StringComparison.OrdinalIgnoreCase)) return;
            if (source.EndsWith(TargetTail(other), StringComparison.OrdinalIgnoreCase))
            {
                dictionaries[i] = new ResourceDictionary { Source = new Uri(target, UriKind.Absolute) };
                return;
            }
        }

        dictionaries.Add(new ResourceDictionary { Source = new Uri(target, UriKind.Absolute) });
    }

    static string TargetTail(string uri)
    {
        var mark = uri.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
        return mark >= 0 ? uri[(mark + ";component/".Length)..] : uri;
    }

    static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int i) return i == 0;
        }
        catch { /* 读不到就按浅色 */ }
        return false;
    }
}
