using ClassDailyLand2.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ClassDailyLand2.App.Views.Dialogs;

/// <summary>
/// 「自定义提交时间」与「补交 / 自定义」的警告弹窗。
///
/// 两个都是纯代码自绘的小卡片窗（无系统标题栏、跟随主题令牌），
/// 不依赖设置中心那套页面字典，独立「编辑作业」窗口从托盘直接打开也能用。
/// </summary>
public static class HomeworkDueDialogs
{
    /// <summary>自定义时间：返回 true 表示用户填好了。<paramref name="value"/> 为本地时间。</summary>
    public static bool TryPickTime(Window? owner, DateTime? current, out DateTime value)
    {
        var dialog = new DueTimeDialog(current);
        if (owner is not null && owner.IsVisible) dialog.Owner = owner;
        var ok = dialog.ShowDialog() == true;
        value = dialog.Value;
        return ok;
    }

    /// <summary>警告确认：返回 true 表示用户点了继续。</summary>
    public static bool Warn(Window? owner, string title, string message, string okText = "继续")
    {
        var dialog = new WarnDialog(title, message, okText);
        if (owner is not null && owner.IsVisible) dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }
}

// ————————————————————— 公共外壳 —————————————————————

/// <summary>无边框圆角卡片窗：标题 + 内容 + 底部按钮。</summary>
internal abstract class CardDialog : Window
{
    readonly Border _card = new();
    readonly StackPanel _body = new();

    protected CardDialog(string title, double width)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 13;
        Title = title;

        _card.CornerRadius = new CornerRadius(10);
        _card.BorderThickness = new Thickness(1);
        _card.Padding = new Thickness(20, 16, 20, 16);
        _card.MinWidth = width;
        _card.MaxWidth = width;
        _card.Background = DueBrushes.Br("SolidBackgroundFillColorBaseBrush", Colors.White);
        _card.BorderBrush = DueBrushes.Br("CardStrokeColorDefaultBrush", Color.FromRgb(0xD0, 0xD0, 0xD0));

        _body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = DueBrushes.Br("TextFillColorPrimaryBrush", Colors.Black),
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        });
        _card.Child = _body;
        Content = _card;

        // 提示框统一入场：从下往上滑上来 + 淡入
        Loaded += (_, _) => UiMotion.SlideUpIn(_card);

        // 只有标题区能拖：正文里的输入框/按钮不能被拖动抢走
        _card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is TextBoxBase || e.OriginalSource is Button) return;
            if (e.GetPosition(_card).Y > 44) return;
            try { DragMove(); } catch { /* 拖到一半松开：忽略 */ }
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        };
    }

    protected StackPanel Body => _body;

    protected TextBlock Text(string text, double size = 13, bool dim = false, double top = 0) => new()
    {
        Text = text,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, 0),
        Foreground = dim
            ? DueBrushes.Br("TextFillColorSecondaryBrush", Color.FromRgb(0x60, 0x60, 0x60))
            : DueBrushes.Br("TextFillColorPrimaryBrush", Colors.Black),
    };

    protected TextBox Input(string text, double width) => new()
    {
        Text = text,
        Width = width,
        Height = 32,
        Padding = new Thickness(8, 0, 8, 0),
        VerticalContentAlignment = VerticalAlignment.Center,
        FontSize = 13,
        Style = Application.Current?.TryFindResource("EdInput") as Style,
    };

    /// <summary>底部按钮行（靠右）。</summary>
    protected void AddButtons(UIElement cancel, UIElement ok)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        row.Children.Add(cancel);
        row.Children.Add(ok);
        _body.Children.Add(row);
    }

    protected static Button Btn(string text, bool accent)
    {
        var b = new Button
        {
            Content = text,
            MinWidth = 88,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            Style = Application.Current?.TryFindResource(accent ? "AccentButton" : "FluentButton") as Style,
        };
        return b;
    }
}

internal static class DueBrushes
{
    public static Brush Br(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}

// ————————————————————— 自定义时间 —————————————————————

internal sealed class DueTimeDialog : CardDialog
{
    readonly TextBox _date;
    readonly TextBox _time;
    readonly TextBlock _error;

    public DateTime Value { get; private set; }

    public DueTimeDialog(DateTime? current) : base("自定义提交时间", 400)
    {
        var seed = current ?? DateTime.Now.AddMinutes(30);
        _date = Input(seed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 148);
        _time = Input(seed.ToString("HH:mm", CultureInfo.InvariantCulture), 96);

        _date.Margin = new Thickness(0, 0, 10, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(_date);
        row.Children.Add(_time);
        Body.Children.Add(row);

        Body.Children.Add(Text("日期 yyyy-MM-dd，时间 HH:mm（24 小时制）", 11.5, dim: true, top: 8));
        Body.Children.Add(Text("时间还没到：到点自动顶到作业区第一行，并弹一条通知。\n时间已经过去（或者是以前的日子）：直接排到作业区最底下。", 11.5, dim: true, top: 4));

        _error = Text("", 12);
        _error.Foreground = DueBrushes.Br("SystemFillColorCriticalBrush", Color.FromRgb(0xC4, 0x2B, 0x1C));
        _error.Margin = new Thickness(0, 8, 0, 0);
        _error.Visibility = Visibility.Collapsed;
        Body.Children.Add(_error);

        var cancel = Btn("取消", accent: false);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = Btn("确定", accent: true);
        ok.IsDefault = true;
        ok.Click += (_, _) => Submit();
        AddButtons(cancel, ok);

        Loaded += (_, _) =>
        {
            _time.Focus();
            _time.SelectAll();
        };
    }

    void Submit()
    {
        var dateText = (_date.Text ?? "").Trim();
        var timeText = (_time.Text ?? "").Trim();
        if (dateText.Length == 0) dateText = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // 只填了时间：按今天算
        if (timeText.Length == 0)
        {
            Fail("时间不能为空，例：15:30");
            return;
        }

        if (!DateTime.TryParseExact($"{dateText} {timeText}", HomeworkDue.ParseFormats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var combined))
        {
            Fail("日期或时间看不懂，请用「2026-11-07」+「15:30」这种写法。");
            return;
        }

        Value = combined;
        DialogResult = true;
        Close();
    }

    void Fail(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
    }
}

// ————————————————————— 警告 —————————————————————

internal sealed class WarnDialog : CardDialog
{
    public WarnDialog(string title, string message, string okText) : base(title, 430)
    {
        Body.Children.Add(Text(message));

        var cancel = Btn("取消", accent: false);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = Btn(okText, accent: true);
        ok.IsDefault = true;
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        AddButtons(cancel, ok);

        Loaded += (_, _) => ok.Focus();
    }
}
