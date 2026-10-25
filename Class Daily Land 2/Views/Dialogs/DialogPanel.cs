using ClassDailyLand.App.Controls;
using ClassDailyLand.App.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClassDailyLand.App.Views.Dialogs;

/// <summary>
/// 二级浮层面板基类（对位 web 版 lib/editors.js 的 open()）：
/// 标题 + 副标题 + 自己滚的内容区 + 固定底栏，圆角 14、最高 88%。
/// 浮层画在设置窗口内部的遮罩上，不另开窗口。
/// </summary>
public class DialogPanel : Border
{
    protected readonly StackPanel Body = new();
    protected readonly StackPanel Footer = new();
    readonly TextBlock _title = new();
    readonly TextBlock _subtitle = new();
    readonly ScrollViewer _scroll;

    public SettingsViewModel Vm { get; }
    protected AppController Ctx => Vm.Ctx;

    /// <summary>面板标题（二级页当成页标题用）。</summary>
    public string Title => _title.Text;

    /// <summary>这一页是从哪个顶层页进来的，用来画面包屑。</summary>
    public virtual string ParentPageTitle => "课表与提醒";

    public DialogPanel(SettingsViewModel vm, string title, string subtitle, double width)
    {
        Vm = vm;

        Style = (Style)Application.Current.Resources["EdPanel"]!;
        Width = width;
        MaxHeight = 620;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(20);

        _title.Style = (Style)Application.Current.Resources["EdTitle"]!;
        _title.Text = title;
        _subtitle.Style = (Style)Application.Current.Resources["EdSubtitle"]!;
        _subtitle.Text = subtitle;
        _subtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;

        var head = new Border { Style = (Style)Application.Current.Resources["EdHead"]! };
        var headStack = new StackPanel();
        headStack.Children.Add(_title);
        headStack.Children.Add(_subtitle);
        head.Child = headStack;

        _scroll = new ScrollViewer { Style = (Style)Application.Current.Resources["EdBody"]! };
        _scroll.Content = Body;

        var foot = new Border { Style = (Style)Application.Current.Resources["EdFoot"]! };
        Footer.Orientation = Orientation.Horizontal;
        Footer.HorizontalAlignment = HorizontalAlignment.Right;
        foot.Child = Footer;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(head, 0);
        Grid.SetRow(_scroll, 1);
        Grid.SetRow(foot, 2);
        grid.Children.Add(head);
        grid.Children.Add(_scroll);
        grid.Children.Add(foot);
        Child = grid;
    }

    /// <summary>往内容区追加一块（自动带 12px 下间距由调用方控制）。</summary>
    protected void Add(UIElement element) => Body.Children.Add(element);

    protected void AddText(string text, Style style) => Body.Children.Add(new TextBlock { Style = style, Text = text });

    /// <summary>底栏按钮；primary 走强调色，danger 走危险色。</summary>
    protected Button AddButton(string text, Action? onClick = null, bool primary = false, bool danger = false)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)Application.Current.Resources[primary && !danger ? "AccentButton" : "FluentButton"]!,
        };
        if (danger) button.Style = (Style)Application.Current.Resources["EdDangerButton"]!;
        button.Click += (_, _) => onClick?.Invoke();
        Footer.Children.Add(button);
        return button;
    }

    /// <summary>底栏左侧的说明文字（对位 .ed-count）。</summary>
    protected TextBlock AddFooterInfo(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]!,
        };
        Footer.Children.Insert(0, label);
        return label;
    }

    public void Close() => DialogLauncher.Close(this);

    // ————————————————————— 常用小构件 —————————————————————

    protected static Style S(string key) => (Style)Application.Current.Resources[key]!;

    protected static TextBlock Label(string text) => new() { Text = text, Style = S("EdFieldLabel") };

    protected static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Style = S("EdRow") };
        foreach (var c in children) row.Children.Add(c);
        return row;
    }

    protected static TextBox Input(string text, double width, string? placeholder = null)
    {
        var box = new TextBox { Text = text, Style = S("EdInput") };
        if (width > 0) box.Width = width;
        if (placeholder is not null) box.Tag = placeholder;
        return box;
    }

    protected static TextBox NumberInput(string text, double width = 110)
    {
        var box = new TextBox { Text = text, Style = S("EdNumberInput") };
        box.Width = width;
        return box;
    }

    protected static TextBox TimeInput(string text) => new() { Text = text, Style = S("EdTimeInput") };

    protected static TextBox Area(string text, double minHeight = 160)
    {
        var box = new TextBox { Text = text, Style = S("EdArea") };
        box.MinHeight = minHeight;
        return box;
    }

    protected static ComboBox Select(IEnumerable<string> options, int selected)
    {
        var combo = new ComboBox { Style = S("EdSelect") };
        foreach (var o in options) combo.Items.Add(o);
        combo.SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, combo.Items.Count - 1));
        return combo;
    }

    protected static Button SmallButton(string text, Action onClick)
    {
        var b = new Button { Content = text, Style = S("EdSmallButton") };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>分组卡片（标题 + 副标题 + 内容）。</summary>
    protected static HeaderedContentControl Card(string title, string subtitle, UIElement content)
    {
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = title, Style = S("EdCardTitle") });
        if (!string.IsNullOrEmpty(subtitle))
            head.Children.Add(new TextBlock { Text = subtitle, Style = S("EdCardSubtitle") });
        return new HeaderedContentControl
        {
            Style = S("EdCard"),
            Header = head,
            Content = content,
        };
    }

    protected static TextBlock Empty(string text) => new() { Text = text, Style = S("EdEmpty") };

    /// <summary>一句提示（对位 editors.js 的 tell）。</summary>
    public static void Tell(DependencyObject owner, string message, string title = "提示")
        => DialogLauncher.Toast(owner, InfoBarSeverity.Information, title, message);

    /// <summary>
    /// 确认框（危险操作前用）。
    /// ★ 画在设置窗口内部，不弹系统 MessageBox —— 用户要求"所有提示都在这个窗口里弹"。
    /// 回调式是因为窗口内浮层是异步的（原来是同步 MessageBox）。
    /// </summary>
    protected void AskConfirm(string message, Action onOk, string title = "确认")
        => DialogLauncher.Confirm(this, title, message, onOk);
}
