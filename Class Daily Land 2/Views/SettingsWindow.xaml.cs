using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClassDailyLand.App.Controls;
using ClassDailyLand.App.ViewModels;
using ClassDailyLand.App.Views.Pages;
using ClassDailyLand.Core.Models;

namespace ClassDailyLand.App.Views;

/// <summary>
/// 设置中心（WinUI 3 / Windows 11 风格）：
/// 48px 标题栏 + 260px 可折叠侧栏（48px）+ 内容区（max 980）。
/// </summary>
public partial class SettingsWindow : Window
{
    const string PageHome = "home";

    readonly List<NavEntry> _entries;
    readonly Dictionary<string, UserControl> _pages = new(StringComparer.Ordinal);
    readonly Dictionary<string, RadioButton> _navButtons = new(StringComparer.Ordinal);
    RadioButton? _selectedNav;

    public SettingsViewModel Vm { get; }

    sealed record NavEntry(string Id, string Label, string Glyph, Func<UserControl> Create);

    public SettingsWindow(AppController ctx)
    {
        Vm = new SettingsViewModel(ctx);
        DataContext = Vm;

        // 先建页表，再 InitializeComponent：导航要按同样的顺序生成
        _entries = new List<NavEntry>
        {
            new(PageHome, "首页", "\uE80F", () => new HomePage()),
            new(AppSettings.PageGeneral, "通用", "\uE713", () => new GeneralPage()),
            new(AppSettings.PageIsland, "灵动岛", "\uE7F4", () => new IslandPage()),
            new("interaction", "交互", "\uE7C9", () => new InteractionPage()),
            new(AppSettings.PageAppearance, "个性化", "\uE790", () => new AppearancePage()),
            new(AppSettings.PageSchedule, "课表与提醒", "\uE787", () => new SchedulePage()),
            new(AppSettings.PageHomework, "作业", "\uE8A5", () => new HomeworkPage()),
            new(AppSettings.PagePlugins, "插件", "\uE74C", () => new PluginsPage()),
            new("advanced", "高级", "\uE90F", () => new AdvancedPage()),
            new(AppSettings.PageAbout, "关于", "\uE946", () => new AboutPage()),
        };

        InitializeComponent();
        BuildNav();

        NavToggle.Checked += (_, _) => SetNavCollapsed(true);
        NavToggle.Unchecked += (_, _) => SetNavCollapsed(false);

        Select(PageHome);

        StateChanged += (_, _) => UpdateMaximizeIcon();
        Closed += (_, _) => Vm.Detach();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DialogHost.Visibility == Visibility.Visible)
                CloseOverlay();
        };
    }

    // ————————————————————— 导航 —————————————————————

    void BuildNav()
    {
        foreach (var entry in _entries)
        {
            var button = new RadioButton
            {
                Style = (Style)FindResource("NavItem"),
                Content = entry.Label,
                Tag = entry.Glyph,
                ToolTip = entry.Label,
                GroupName = "SettingsNav",
                CommandParameter = entry.Id,
                Margin = new Thickness(0, 2, 0, 0),
            };
            var id = entry.Id;
            button.Click += (_, _) => Select(id);
            _navButtons[id] = button;

            if (id == AppSettings.PageAbout) AboutPanel.Children.Add(button);
            else NavItemsPanel.Children.Add(button);
        }
    }

    /// <summary>收起时只留图标：侧栏 260 → 48，清掉文本并收紧内边距。</summary>
    void SetNavCollapsed(bool collapsed)
    {
        NavPane.Width = collapsed ? 48 : 260;
        foreach (var entry in _entries)
        {
            if (!_navButtons.TryGetValue(entry.Id, out var button)) continue;
            button.Content = collapsed ? "" : entry.Label;
            button.Padding = collapsed ? new Thickness(8, 0, 0, 0) : new Thickness(12, 0, 0, 0);
        }
    }

    void Select(string id)
    {
        if (!_navButtons.TryGetValue(id, out var button)) return;
        if (_selectedNav == button && PageHost.Content is not null) return;

        button.IsChecked = true;
        _selectedNav = button;

        if (!_pages.TryGetValue(id, out var page))
        {
            var entry = _entries.First(e => e.Id == id);
            page = entry.Create();
            page.DataContext = Vm;
            _pages[id] = page;
            if (page is HomePage home) home.NavigateRequested += Select;
        }

        ToastSlot.Children.Clear();
        PageHost.Content = page;
        ContentScroll.ScrollToVerticalOffset(0);

        AnimatePageIn(page);
    }

    static void AnimatePageIn(UIElement page)
    {
        var transform = new System.Windows.Media.TranslateTransform(0, 10);
        page.RenderTransform = transform;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(350))
        {
            EasingFunction = (System.Windows.Media.Animation.IEasingFunction)
                System.Windows.Application.Current.Resources["EaseOutQuint"]!,
        };
        transform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, anim);
    }

    // ————————————————————— 窗口按钮 —————————————————————

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void OnToggleMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void OnClose(object sender, RoutedEventArgs e) => Close();

    void UpdateMaximizeIcon()
    {
        var key = WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize";
        MaximizeIcon.Data = FindResource(key) as System.Windows.Media.Geometry;
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
    }

    // ————————————————————— 浮层（二级对话框） —————————————————————

    public void ShowOverlay(FrameworkElement panel)
    {
        DialogHost.Children.Clear();
        var host = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        host.Children.Add(panel);
        DialogHost.Children.Add(host);
        DialogHost.Visibility = Visibility.Visible;

        panel.RenderTransform = new System.Windows.Media.ScaleTransform(0.96, 0.96);
        panel.RenderTransformOrigin = new Point(0.5, 0.5);
        var anim = new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = (System.Windows.Media.Animation.IEasingFunction)
                System.Windows.Application.Current.Resources["EaseOutQuint"]!,
        };
        panel.RenderTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, anim);
        panel.RenderTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, anim);
    }

    public void CloseOverlay()
    {
        if (DialogHost.Visibility != Visibility.Visible) return;
        DialogHost.Children.Clear();
        DialogHost.Visibility = Visibility.Collapsed;
    }

    public Grid OverlayHost => DialogHost;

    // ————————————————————— 页内提示条 —————————————————————

    public void Toast(InfoBarSeverity severity, string title, string message)
    {
        var bar = new InfoBar
        {
            Severity = severity,
            Title = title,
            Message = message,
            IsClosable = true,
        };
        ToastSlot.Children.Add(bar);
    }

    public void ClearToasts() => ToastSlot.Children.Clear();
}
