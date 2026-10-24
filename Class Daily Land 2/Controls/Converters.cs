using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ClassDailyLand.App.Controls;

/// <summary>
/// XAML 侧用到的少量转换器。字符串/布尔 → 可见性这类都是「空值不占位」的需要，
/// 逻辑一律保持与 ui/ 里的 CSS 表现一致。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>parameter = "invert" 时反向。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var on = value switch
        {
            bool b => b,
            null => false,
            _ => true,
        };
        if (Equals(parameter, "invert")) on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    /// <summary>空 / 空白 → Collapsed；parameter = "invert" 时反过来。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = string.IsNullOrWhiteSpace(value as string);
        if (Equals(parameter, "invert")) empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 卡片内行与行之间的分隔线：CSS 是 <c>.row + .row { border-top }</c>，
/// 也就是「第一行不画顶线」。这里按 ItemsControl 的 AlternationIndex 判断。
/// </summary>
public sealed class NotFirstRowConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var index = value is int i ? i : -1;
        return index > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把 "HH:mm" / "H:mm" 之类字符串按分钟偏移量平移，返回 "HH:mm"。</summary>
public sealed class TimeOffsetConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string ?? "").Trim();
        var parts = text.Split(':');
        if (parts.Length < 2) return text;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return text;
        var offset = parameter switch
        {
            int i => i,
            string s when int.TryParse(s, out var p) => p,
            _ => 0,
        };
        var total = Math.Clamp(h * 60 + m + offset, 0, 1440);
        return $"{total / 60:D2}:{total % 60:D2}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>取 ItemsControl 容器的 AlternationIndex（供 NotFirstRowConverter 用）。</summary>
public static class RowIndex
{
    public static readonly DependencyProperty AlternationIndexProperty =
        ItemsControl.AlternationIndexProperty;
}
