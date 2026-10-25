using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClassDailyLand.Installer.Online;

/// <summary>
/// 安装器自己的两套色板（浅色 / 深色）。
///
/// 不引主题字典、不重写控件模板：安装器只有几个控件，把几个笔刷灌进
/// <see cref="Application.Resources"/>，XAML 里用 <c>DynamicResource</c> 取就够了 ——
/// 也就不会跟主程序 Themes\ 里的设计令牌纠缠在一起。
/// </summary>
internal static class InstallerTheme
{
    /// <summary>读 Windows 的「应用使用浅色主题」设置（0 = 深色）。读不到就按浅色。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            // 值不存在（旧系统）或读失败：一律按浅色处理
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

        // 浅色 / 深色
        Set(resources, "Bg", dark ? "#1C1C1C" : "#FAFAFA");
        Set(resources, "Fg", dark ? "#F2F2F2" : "#1A1A1A");
        Set(resources, "Subtle", dark ? "#A0A0A0" : "#6B6B6B");
        Set(resources, "FieldBg", dark ? "#262626" : "#FFFFFF");
        Set(resources, "FieldBorder", dark ? "#3A3A3A" : "#D9D9D9");

        // 两种主题共用的强调色与错误色
        Set(resources, "Accent", "#0A84FF");
        Set(resources, "Error", dark ? "#FF453A" : "#D93025");
    }

    private static void Set(ResourceDictionary resources, string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();   // 冻结：跨线程/跨窗口共享更省事
        resources[key] = brush;
    }
}
