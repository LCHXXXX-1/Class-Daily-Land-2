using System.Windows.Media;

namespace ClassDailyLand2.App.Theme;

/// <summary>
/// 主题色板（纯代码，无 XAML 资源字典）。所有 Brush 都 Freeze 缓存，切换主题时整体替换。
/// 主窗、设置窗、对话框都从这里取色，因此改主题能立刻生效、不用重启。
/// </summary>
public static class Palette
{
    // —— 岛 / 面板固定深色（原版就是深色卡片，不随主题变化）——
    public static readonly Brush IslandBg = Frozen(Color.FromArgb(0xE6, 0x1c, 0x1c, 0x1e));
    public static readonly Brush IslandBorder = Frozen(Color.FromArgb(0x33, 0xff, 0xff, 0xff));
    public static readonly Brush IslandText = Frozen(Color.FromRgb(0xe6, 0xe6, 0xe6));
    public static readonly Brush PanelBg = Frozen(Color.FromArgb(0xF2, 0x1c, 0x1c, 0x1e));
    public static readonly Brush PanelText = Frozen(Color.FromRgb(0xe6, 0xe6, 0xe6));
    public static readonly Brush PanelTextDim = Frozen(Color.FromRgb(0x98, 0x98, 0x9d));
    public static readonly Brush AccentBlue = Frozen(Color.FromRgb(0x0a, 0x84, 0xff));
    public static readonly Brush RingOngoing = Frozen(Color.FromRgb(0x30, 0xd1, 0x58));
    public static readonly Brush RingUpcoming = Frozen(Color.FromRgb(0x4d, 0xa3, 0xff));
    public static readonly Brush RingIdle = Frozen(Color.FromRgb(0x8a, 0x8a, 0x8e));
    public static readonly Brush AlertStart = Frozen(Color.FromRgb(0x30, 0xd1, 0x58));
    public static readonly Brush AlertEnd = Frozen(Color.FromRgb(0xff, 0xff, 0xff));

    static readonly SolidColorBrush LightBg = Frozen(Color.FromRgb(0xf3, 0xf3, 0xf6));
    static readonly SolidColorBrush LightCard = Frozen(Color.FromRgb(0xff, 0xff, 0xff));
    static readonly SolidColorBrush LightCardAlt = Frozen(Color.FromRgb(0xf7, 0xf7, 0xf9));
    static readonly SolidColorBrush LightBorder = Frozen(Color.FromRgb(0xe3, 0xe3, 0xe8));
    static readonly SolidColorBrush LightText = Frozen(Color.FromRgb(0x1c, 0x1c, 0x1e));
    static readonly SolidColorBrush LightTextDim = Frozen(Color.FromRgb(0x6c, 0x6c, 0x72));
    static readonly SolidColorBrush LightHover = Frozen(Color.FromRgb(0xe9, 0xe9, 0xee));

    static readonly SolidColorBrush DarkBg = Frozen(Color.FromRgb(0x1a, 0x1a, 0x1c));
    static readonly SolidColorBrush DarkCard = Frozen(Color.FromRgb(0x26, 0x26, 0x28));
    static readonly SolidColorBrush DarkCardAlt = Frozen(Color.FromRgb(0x2c, 0x2c, 0x2e));
    static readonly SolidColorBrush DarkBorder = Frozen(Color.FromRgb(0x3a, 0x3a, 0x3c));
    static readonly SolidColorBrush DarkText = Frozen(Color.FromRgb(0xe6, 0xe6, 0xe6));
    static readonly SolidColorBrush DarkTextDim = Frozen(Color.FromRgb(0x98, 0x98, 0x9d));
    static readonly SolidColorBrush DarkHover = Frozen(Color.FromRgb(0x38, 0x38, 0x3a));
    static readonly SolidColorBrush LightInputBg = Frozen(Colors.White);
    static readonly SolidColorBrush DarkInputBg = Frozen(Color.FromRgb(0x2a, 0x2a, 0x2c));
    static readonly Brush LightHint = Frozen(Color.FromRgb(0x88, 0x88, 0x88));
    static readonly Brush DarkHint = Frozen(Color.FromRgb(0x8e, 0x8e, 0x93));

    public static bool IsDark { get; private set; } = true;

    public static Brush WindowBackground => IsDark ? DarkBg : LightBg;
    public static Brush Card => IsDark ? DarkCard : LightCard;
    public static Brush CardAlt => IsDark ? DarkCardAlt : LightCardAlt;
    public static Brush Border => IsDark ? DarkBorder : LightBorder;
    public static Brush Text => IsDark ? DarkText : LightText;
    public static Brush TextDim => IsDark ? DarkTextDim : LightTextDim;
    public static Brush Hover => IsDark ? DarkHover : LightHover;
    public static Brush Accent { get; } = Frozen(Color.FromRgb(0x0a, 0x84, 0xff));
    public static Brush AccentText { get; } = Frozen(Color.FromRgb(0xff, 0xff, 0xff));
    public static Brush ToggleOff => IsDark ? Frozen(Color.FromRgb(0x48, 0x48, 0x4a)) : Frozen(Color.FromRgb(0xc6, 0xc6, 0xcb));
    public static Brush Danger { get; } = Frozen(Color.FromRgb(0xff, 0x45, 0x3a));
    public static Brush InputBg => IsDark ? DarkInputBg : LightInputBg;
    public static Brush Hint => IsDark ? DarkHint : LightHint;

    /// <summary>主题变化时广播（订阅方刷新自身颜色）。</summary>
    public static event EventHandler? Changed;

    /// <summary>theme 取值：system / light / dark。</summary>
    public static void Apply(string theme)
    {
        IsDark = theme switch
        {
            "light" => false,
            "dark" => true,
            _ => IsSystemDark(),
        };
        Changed?.Invoke(null, EventArgs.Empty);
    }

    static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            if (v is int i) return i == 0;
        }
        catch { /* 注册表读不到就按深色 */ }
        return true;
    }

    static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static SolidColorBrush Frozen(byte a, byte r, byte g, byte b2) => Frozen(Color.FromArgb(a, r, g, b2));

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t),
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    public static Brush BrushWithAlpha(this Brush brush, byte alpha)
    {
        if (brush is not SolidColorBrush sb) return brush;
        var c = sb.Color;
        c.A = alpha;
        return Frozen(c);
    }
}
