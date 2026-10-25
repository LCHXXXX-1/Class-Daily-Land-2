using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeveloperIDE;

/// <summary>深色/浅色都跟随全局主题的输入对话框（不借 WinForms，保持整套观感一致）。</summary>
public sealed class PromptWindow : Window
{
    readonly TextBox _input;

    PromptWindow(string title, string label, string initial)
    {
        var panel = ThemeBrush("WindowBackgroundBrush", 0x1E, 0x1E, 0x1E);
        var field = ThemeBrush("ButtonBackgroundBrush", 0x2D, 0x2D, 0x30);
        var line = ThemeBrush("BorderBrush", 0x3F, 0x3F, 0x46);
        var text = ThemeBrush("TextBrush", 0xE6, 0xE6, 0xE6);
        var dim = ThemeBrush("DimTextBrush", 0x9D, 0x9D, 0x9D);

        Title = title;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = panel;
        Foreground = text;
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
        FontSize = 13;

        var body = new StackPanel { Margin = new Thickness(14) };
        body.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = dim,
            Margin = new Thickness(0, 0, 0, 6),
        });

        _input = new TextBox
        {
            Text = initial,
            Background = field,
            Foreground = text,
            CaretBrush = text,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
        };
        body.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };

        var ok = new Button
        {
            Content = "创建",
            Width = 88,
            Height = 26,
            IsDefault = true,
            Background = ThemeBrush("AccentBackgroundBrush", 0x1F, 0x6F, 0x3D),
            BorderBrush = ThemeBrush("AccentBorderBrush", 0x2E, 0xA0, 0x43),
            Foreground = ThemeBrush("AccentTextBrush", 0xFF, 0xFF, 0xFF),
        };
        ok.Click += (_, _) => { if (_input.Text.Trim().Length > 0) DialogResult = true; };

        var cancel = new Button
        {
            Content = "取消",
            Width = 88,
            Height = 26,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
            Background = field,
            BorderBrush = line,
            Foreground = text,
        };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        Content = body;
        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    /// <summary>取当前主题画刷；字典还没合并时退回兜底色（比如设计期预览）。</summary>
    static Brush ThemeBrush(string key, byte r, byte g, byte b) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(Color.FromRgb(r, g, b));

    /// <summary>返回输入的文本；用户取消时返回 null。</summary>
    public static string? Ask(Window owner, string title, string label, string initial)
    {
        var dialog = new PromptWindow(title, label, initial) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._input.Text.Trim() : null;
    }
}
