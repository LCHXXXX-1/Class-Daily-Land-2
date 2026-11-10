using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClassDailyLand2.UninstallerContract;

/// <summary>
/// 卸载进度窗：纯代码构建的无边框圆角卡片（不用 XAML —— 共享源码要原样编进两个卸载器工程）。
/// 视觉语言与更新器/安装器一致：卡片 + 阴影 + 状态行 + 圆角进度条 + 一个动作按钮；
/// 干活期间按钮禁用、窗口拒绝关闭，结束（成功或失败）才放行。
/// </summary>
public sealed class UninstallProgressWindow : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _status;
    private readonly Button _action;
    private readonly Brush _subtleBrush;
    private readonly Brush _accentBrush;
    private readonly Brush _errorBrush;
    private bool _allowClose;

    /// <summary>用户点了「完成」。</summary>
    public event Action? Finished;

    public UninstallProgressWindow(string productName)
    {
        var dark = IsSystemDark();
        var bg = Frozen(dark ? "#1F1F1F" : "#FFFFFF");
        var border = Frozen(dark ? "#3A3A3A" : "#E3E3E3");
        var fg = Frozen(dark ? "#F2F2F2" : "#1A1A1A");
        _subtleBrush = Frozen(dark ? "#A0A0A0" : "#6B6B6B");
        _accentBrush = Frozen(dark ? "#3B9BFF" : "#0A84FF");
        _errorBrush = Frozen(dark ? "#FF6B5E" : "#D93025");
        var track = Frozen(dark ? "#333333" : "#EDEDED");

        Title = productName + " 卸载程序";
        Width = 480;
        Height = 240;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var card = new Border
        {
            Margin = new Thickness(14),
            CornerRadius = new CornerRadius(14),
            Background = bg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 28,
                ShadowDepth = 6,
                Opacity = 0.30,
                Color = Colors.Black,
            },
        };
        card.MouseLeftButtonDown += OnCardMouseDown;

        var grid = new Grid { Margin = new Thickness(24, 20, 24, 18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "卸载 " + productName,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg,
        };
        Grid.SetRow(title, 0);

        _bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 6,
            Margin = new Thickness(0, 16, 0, 0),
            Foreground = _accentBrush,
            Background = track,
        };
        Grid.SetRow(_bar, 1);

        _status = new TextBlock
        {
            Text = "准备开始卸载…",
            TextWrapping = TextWrapping.Wrap,
            Foreground = _subtleBrush,
            Margin = new Thickness(0, 12, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetRow(_status, 2);

        _action = new Button
        {
            Content = "完成",
            Width = 112,
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsEnabled = false,
        };
        _action.Click += (_, _) => Finished?.Invoke();
        Grid.SetRow(_action, 3);

        grid.Children.Add(title);
        grid.Children.Add(_bar);
        grid.Children.Add(_status);
        grid.Children.Add(_action);
        card.Child = grid;
        Content = card;

        Loaded += (_, _) =>
        {
            // 弹出瞬间抢一次前台（与更新器同款：短暂抬 Topmost 再放下）
            Topmost = true;
            Topmost = false;
            Activate();
        };
    }

    /// <summary>进行中：状态行 + 百分比（线程安全，删除循环在后台线程上回调）。</summary>
    public void Report(string status, double percent) => Dispatcher.Invoke(() =>
    {
        _status.Text = status;
        _status.Foreground = _subtleBrush;
        _bar.Value = Math.Clamp(percent, 0, 100);
    });

    public void Done(string message) => Dispatcher.Invoke(() =>
    {
        _bar.Value = 100;
        _status.Text = message;
        _status.Foreground = _accentBrush;
        _action.IsEnabled = true;
        _allowClose = true;
    });

    public void Fail(string message) => Dispatcher.Invoke(() =>
    {
        _status.Text = message;
        _status.Foreground = _errorBrush;
        _action.IsEnabled = true;
        _allowClose = true;
    });

    protected override void OnClosing(CancelEventArgs e)
    {
        // 干活中途关窗 = 半途而废的卸载：不放行（Alt+F4 也一样拦）
        if (!_allowClose)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* 鼠标已经抬起，忽略 */ }
        }
    }

    /// <summary>读 Windows 的「应用使用浅色主题」（0 = 深色）。读不到按浅色。</summary>
    static bool IsSystemDark()
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

    static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
