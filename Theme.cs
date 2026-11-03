using System.Windows;
using System.Windows.Media;

namespace CDL2.Updater;

/// <summary>
/// 更新器的配色：跟随系统深浅色，全部内联、不依赖主程序的任何资源 ——
/// 更新器必须在主程序文件被替换时也能正常显示，不能引用会被替换掉的库或字典。
/// </summary>
public static class Theme
{
    public static bool IsDark { get; private set; }

    public static Color Background => IsDark ? FromRgb(0x20, 0x20, 0x20) : FromRgb(0xFB, 0xFB, 0xFB);
    public static Color CardBorder => IsDark ? FromRgb(0x3D, 0x3D, 0x3D) : FromRgb(0xE0, 0xE0, 0xE0);
    public static Color Track => IsDark ? FromRgb(0x2D, 0x2D, 0x2D) : FromRgb(0xE8, 0xE8, 0xE8);
    public static Color Text => IsDark ? FromRgb(0xFF, 0xFF, 0xFF) : FromRgb(0x1B, 0x1B, 0x1B);
    public static Color TextSecondary => IsDark ? FromRgb(0x9A, 0x9A, 0x9A) : FromRgb(0x61, 0x61, 0x61);
    public static Color Accent => IsDark ? FromRgb(0x60, 0xCD, 0xFF) : FromRgb(0x00, 0x5F, 0xB8);
    public static Color Error => IsDark ? FromRgb(0xFF, 0x99, 0xA4) : FromRgb(0xC4, 0x2B, 0x1C);
    public static Color Success => IsDark ? FromRgb(0x6C, 0xCB, 0x5F) : FromRgb(0x0F, 0x7B, 0x0F);
    public static Color ButtonFace => IsDark ? FromRgb(0x37, 0x37, 0x37) : FromRgb(0xF0, 0xF0, 0xF0);
    public static Color ButtonHover => IsDark ? FromRgb(0x45, 0x45, 0x45) : FromRgb(0xE2, 0xE2, 0xE2);
    public static Color CardBg => IsDark ? FromRgb(0x26, 0x26, 0x26) : FromRgb(0xFF, 0xFF, 0xFF);
    public static Color ChipBg => IsDark ? FromRgb(0x2D, 0x2D, 0x2D) : FromRgb(0xF2, 0xF3, 0xF5);
    public static Color ChipBorder => IsDark ? FromRgb(0x3D, 0x3D, 0x3D) : FromRgb(0xE2, 0xE4, 0xE8);
    public static Color ErrorSoft => IsDark ? FromRgb(0x3A, 0x27, 0x27) : FromRgb(0xFB, 0xE9, 0xE7);
    public static Color AccentLight => IsDark ? FromRgb(0x7F, 0xD9, 0xFF) : FromRgb(0x3D, 0x8B, 0xE8);
    public static Color AccentSoft => IsDark ? FromRgb(0x1E, 0x33, 0x48) : FromRgb(0xE8, 0xF1, 0xFB);

    public static void Apply() => IsDark = ReadSystemPrefersDark();

    private static bool ReadSystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    public static SolidColorBrush Brush(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    private static Color FromRgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
