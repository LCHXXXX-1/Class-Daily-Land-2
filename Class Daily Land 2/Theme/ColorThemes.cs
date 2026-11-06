using System.Windows.Media;

namespace ClassDailyLand2.App.Theme;

/// <summary>
/// 一套配色主题：只给一个种子色，强调色系在运行时按深浅推导
/// （浅色主题下用种子本身，深色主题下提亮，保证按钮/文字都有足够对比度）。
/// </summary>
public sealed record ColorTheme(string Key, string Name, string Hint, Color Seed);

/// <summary>内置配色主题。顺序即个性化页的展示顺序。</summary>
public static class ColorThemes
{
    public static readonly IReadOnlyList<ColorTheme> All = new[]
    {
        new ColorTheme("blue",     "晴空蓝", "默认", Color.FromRgb(0x00, 0x5F, 0xB8)),
        new ColorTheme("violet",   "星夜紫", "梦幻", Color.FromRgb(0x6B, 0x3F, 0xC9)),
        new ColorTheme("teal",     "碧海青", "清冷", Color.FromRgb(0x0B, 0x6E, 0x7A)),
        new ColorTheme("green",    "林野绿", "沉稳", Color.FromRgb(0x0F, 0x7B, 0x0F)),
        new ColorTheme("amber",    "落日橙", "温暖", Color.FromRgb(0xB4, 0x5A, 0x00)),
        new ColorTheme("rose",     "玫瑰粉", "明亮", Color.FromRgb(0xBF, 0x2F, 0x77)),
        new ColorTheme("graphite", "石墨灰", "克制", Color.FromRgb(0x4A, 0x4A, 0x55)),
    };

    public static ColorTheme Get(string? key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>当底色（按钮/开关轨道）用的强调色：深色主题下种子太暗，提亮 42%。</summary>
    public static Color ForBackground(ColorTheme theme, bool dark) =>
        dark ? Lighten(theme.Seed, 0.42) : theme.Seed;

    /// <summary>当文字/图标用的强调色：深色主题下要更亮才看得清。</summary>
    public static Color ForForeground(ColorTheme theme, bool dark) =>
        dark ? Lighten(theme.Seed, 0.55) : theme.Seed;

    public static Color Lighten(Color c, double t) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * t),
        (byte)(c.G + (255 - c.G) * t),
        (byte)(c.B + (255 - c.B) * t));

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}
