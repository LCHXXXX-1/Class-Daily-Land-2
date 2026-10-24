using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClassDailyLand.App.Theme;

namespace ClassDailyLand.App.Windows;

/// <summary>
/// 极简两字段数字输入弹窗（MVP 用；阶段 2 的编辑器会替换它）。
/// 纯代码 + 自绘圆角卡片，跟随主题。
/// </summary>
public sealed class NumberInputDialog : Window
{
    readonly TextBox _first = new();
    readonly TextBox _second = new();

    // 必须存成字段再订阅：否则退订时写一个新的 lambda，删的是「另一个」委托，
    // 静态的 Palette.Changed 会一直抓着这个窗口（每开一次弹窗泄漏一个）。
    readonly EventHandler _paletteChanged;

    NumberInputDialog(string title, string firstLabel, string firstValue, string secondLabel, string secondValue)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = title;

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 16, 18, 14),
            MinWidth = 320,
        };

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        sp.Children.Add(Field(firstLabel, _first, firstValue));
        sp.Children.Add(Field(secondLabel, _second, secondValue));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = Btn("取消", () => { DialogResult = false; Close(); });
        var ok = Btn("确定", () =>
        {
            DialogResult = true;
            Close();
        });
        ok.IsDefault = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        sp.Children.Add(buttons);

        card.Child = sp;
        Content = card;

        _first.Focus();
        _first.SelectAll();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        };
        _paletteChanged = (_, _) => ApplyTheme();
        Palette.Changed += _paletteChanged;
        Closed += OnClosed;
        ApplyTheme();
    }

    /// <summary>窗口关闭时解除对静态事件的订阅（否则静态事件会一直持有本窗口）。</summary>
    void OnClosed(object? sender, EventArgs e) => Palette.Changed -= _paletteChanged;

    void ApplyTheme()
    {
        if (Content is not Border card) return;
        card.Background = Palette.Card;
        card.BorderBrush = Palette.Border;
        var sp = (StackPanel)card.Child;
        foreach (var tb in FindChildren<TextBlock>(sp))
            tb.Foreground = tb.Tag as string == "label" ? Palette.TextDim : Palette.Text;
        foreach (var tb in FindChildren<TextBox>(sp))
        {
            tb.Background = Palette.CardAlt;
            tb.BorderBrush = Palette.Border;
            tb.Foreground = Palette.Text;
        }
        foreach (var b in FindChildren<Button>(sp))
        {
            b.Background = Palette.CardAlt;
            b.BorderBrush = Palette.Border;
            b.Foreground = Palette.Text;
        }
    }

    static IEnumerable<T> FindChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in FindChildren<T>(child)) yield return d;
        }
    }

    static UIElement Field(string label, TextBox box, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lb = new TextBlock
        {
            Text = label,
            Tag = "label",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
        };
        box.Text = value;
        box.Height = 30;
        box.Padding = new Thickness(8, 0, 8, 0);
        box.BorderThickness = new Thickness(1);
        box.VerticalContentAlignment = VerticalAlignment.Center;
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        Grid.SetColumn(lb, 0);
        Grid.SetColumn(box, 1);
        g.Children.Add(lb);
        g.Children.Add(box);
        return g;
    }

    Button Btn(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            MinWidth = 76,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            BorderThickness = new Thickness(1),
            FontSize = 12,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.GetPosition(this).Y < 46) try { DragMove(); } catch { }
    }

    public static void Show(string title, string firstLabel, string firstValue,
        string secondLabel, string secondValue, Action<string, string> onOk)
    {
        var dlg = new NumberInputDialog(title, firstLabel, firstValue, secondLabel, secondValue);
        if (dlg.ShowDialog() == true)
            onOk(Normalize(dlg._first.Text, firstValue), Normalize(dlg._second.Text, secondValue));
    }

    static string Normalize(string? text, string fallback) =>
        int.TryParse((text ?? "").Trim(), out var v) && v >= 0 ? v.ToString() : fallback;
}

/// <summary>静态入口（保持 AppController 里的调用形式简洁）。</summary>
public static class NumberInput
{
    public static void Show(string title, string firstLabel, string firstValue,
        string secondLabel, string secondValue, Action<string, string> onOk) =>
        NumberInputDialog.Show(title, firstLabel, firstValue, secondLabel, secondValue, onOk);
}
