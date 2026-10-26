using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClassDailyLand2.Updater;

/// <summary>
/// 更新器窗口的两套色板（浅色 / 深色）。
///
/// 更新器是独立进程、不引用主程序的主题字典，所以这里只灌几个笔刷进
/// <see cref="Application.Resources"/>，XAML 里用 <c>DynamicResource</c> 取 ——
/// 足够，也不会跟主程序的 Tokens 纠缠。
/// </summary>
internal static class UpdaterTheme
{
    /// <summary>读 Windows 的「应用使用浅色主题」（0 = 深色）。读不到按浅色。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(bool dark)
    {
        var resources = Application.Current.Resources;

        // 卡片本体（窗口本身是透明的，圆角与阴影都在这里画）
        Set(resources, "CardBg", dark ? "#1F1F1F" : "#FFFFFF");
        Set(resources, "CardBorder", dark ? "#3A3A3A" : "#E3E3E3");

        Set(resources, "Fg", dark ? "#F2F2F2" : "#1A1A1A");
        Set(resources, "Subtle", dark ? "#A0A0A0" : "#6B6B6B");
        Set(resources, "TrackBg", dark ? "#333333" : "#EDEDED");

        Set(resources, "Accent", dark ? "#3B9BFF" : "#0A84FF");
        Set(resources, "Error", dark ? "#FF6B5E" : "#D93025");
        Set(resources, "Warn", dark ? "#FFB340" : "#B26A00");

        Set(resources, "ButtonBg", dark ? "#2A2A2A" : "#F4F4F4");
        Set(resources, "ButtonBorder", dark ? "#3F3F3F" : "#DCDCDC");
    }

    private static void Set(ResourceDictionary resources, string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        resources[key] = brush;
    }
}
