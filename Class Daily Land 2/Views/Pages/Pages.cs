using ClassDailyLand2.App.Controls;
using ClassDailyLand2.App.Integrations.Audio;
using ClassDailyLand2.App.Integrations.Disaster;
using ClassDailyLand2.App.Market;
using ClassDailyLand2.App.Plugins;
using ClassDailyLand2.App.Updates;
using ClassDailyLand2.App.Theme;
using ClassDailyLand2.App.ViewModels;
using ClassDailyLand2.App.Views.Dialogs;
using ClassDailyLand2.Core.Market;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClassDailyLand2.App.Views.Pages;

// ============================================================================
//  各页的代码后置：XAML 负责版式，这里只做「需要跑代码才能接上」的少量交互
//  （DataContext 由外壳在创建后赋值，所以取 VM 的时机一律放在 Loaded）。
// ============================================================================

public partial class GeneralPage : UserControl
{
    public GeneralPage() => InitializeComponent();
}

public partial class AppearancePage : UserControl
{
    public AppearancePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is not SettingsViewModel vm) return;
            // Win11 → 云母，Win10 1803+ → 亚克力；更老的系统才置灰
            var supported = vm.SupportsBackdrop;
            MicaRow.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
            MicaUnsupportedRow.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;

            BuildThemeSwatches();
            BuildBackgroundSwatches();
            SyncBackgroundRows(vm);
            BuildBoardSwatches();
            SyncBoardRows(vm);

            vm.PropertyChanged += OnAppearanceChanged;
            Unloaded += (_, _) => vm.PropertyChanged -= OnAppearanceChanged;
        };
    }

    SettingsViewModel? Vm => DataContext as SettingsViewModel;

    void OnAppearanceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Vm is not { } vm) return;

        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.SettingsBackground):
                SyncBackgroundRows(vm);
                break;
            case nameof(SettingsViewModel.SettingsBackgroundColor):
                BuildBackgroundSwatches();
                break;
            case nameof(SettingsViewModel.SettingsBackgroundImage):
                SyncBackgroundRows(vm);
                break;
            case nameof(SettingsViewModel.BoardBackground):
                SyncBoardRows(vm);
                break;
            case nameof(SettingsViewModel.BoardBackgroundImage):
                SyncBoardRows(vm);
                break;
            case nameof(SettingsViewModel.ThemeMode):
                // 深浅一变，纯色预设跟着换一套（浅色主题里给深色底会读不清字）
                BuildBackgroundSwatches();
                BuildBoardSwatches();
                break;
        }
    }

    // ————————————— 窗口背景 —————————————

    /// <summary>纯色预设：按当前深浅给两套，避免"浅色主题配深底"看不清。</summary>
    static readonly (string Hex, string Name)[] LightBackgrounds =
    {
        ("#F3F3F3", "纸白"), ("#E9EEF6", "雾蓝"), ("#EAF3EC", "浅苔"),
        ("#F7F0E4", "暖砂"), ("#F2EAF5", "薄紫"), ("#E8F2F4", "淡青"),
    };

    static readonly (string Hex, string Name)[] DarkBackgrounds =
    {
        ("#1A1A1C", "石墨"), ("#191C22", "深海"), ("#181F1B", "墨绿"),
        ("#221D18", "深棕"), ("#1F1A24", "夜紫"), ("#101418", "纯黑"),
    };

    void SyncBackgroundRows(SettingsViewModel vm)
    {
        var mode = vm.SettingsBackground;
        BgColorRow.Visibility = mode == "solid" ? Visibility.Visible : Visibility.Collapsed;
        BgImageRow.Visibility = mode == "image" ? Visibility.Visible : Visibility.Collapsed;

        var path = vm.SettingsBackgroundImage;
        BgImageText.Text = string.IsNullOrWhiteSpace(path) ? "未选择图片" : System.IO.Path.GetFileName(path);
    }

    void BuildBackgroundSwatches()
    {
        var dark = ThemeService.IsDark;
        BuildColorStrip(BgColorStrip, dark ? DarkBackgrounds : LightBackgrounds,
            Vm?.SettingsBackgroundColor ?? "", hex =>
            {
                if (Vm is not { } vm) return;
                vm.SettingsBackgroundColor = hex;
                vm.SettingsBackground = "solid";     // 点颜色即切纯色模式，少一步
            });
    }

    // ————————————— 作业区域 —————————————

    /// <summary>作业区色板：浅色/深色各一组，深底会自动把文字换浅。</summary>
    static readonly (string Hex, string Name)[] BoardBackgrounds =
    {
        ("#FFFFFF", "白"), ("#F4F5F8", "浅灰"), ("#EDF3FF", "淡蓝"), ("#EDF7EE", "淡绿"),
        ("#FFF6E9", "暖黄"), ("#2B2B31", "深灰"), ("#1F2430", "深蓝"), ("#241F2E", "深紫"),
    };

    void SyncBoardRows(SettingsViewModel vm)
    {
        var mode = vm.BoardBackground;
        BoardColorRow.Visibility = mode == "color" ? Visibility.Visible : Visibility.Collapsed;
        BoardImageRow.Visibility = mode == "image" ? Visibility.Visible : Visibility.Collapsed;

        var path = vm.BoardBackgroundImage;
        BoardImageText.Text = string.IsNullOrWhiteSpace(path) ? "未选择图片" : System.IO.Path.GetFileName(path);
    }

    void BuildBoardSwatches()
    {
        BuildColorStrip(BoardColorStrip, BoardBackgrounds,
            Vm?.BoardBackgroundColor ?? "", hex =>
            {
                if (Vm is not { } vm) return;
                vm.BoardBackgroundColor = hex;
                vm.BoardBackground = "color";
            });
    }

    void OnPickBoardImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择「班级日常」窗口背景图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        if (Vm is { } vm)
        {
            vm.BoardBackgroundImage = dialog.FileName;
            vm.BoardBackground = "image";
            SyncBoardRows(vm);
        }
    }

    void OnClearBoardImage(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.BoardBackgroundImage = "";
        SyncBoardRows(vm);
    }

    // ————————————— 通用色块条 —————————————

    /// <summary>
    /// 一排圆形色块：选中项带强调色环与对勾。两个页面（窗口背景 / 作业区）共用，
    /// 只是传入的预设和"点下去写哪个设置"不同。
    /// </summary>
    void BuildColorStrip(WrapPanel target, (string Hex, string Name)[] presets, string current, Action<string> picked)
    {
        target.Children.Clear();

        var dark = ThemeService.IsDark;
        var now = (current ?? "").Trim().ToUpperInvariant();

        foreach (var (hex, name) in presets)
        {
            var selected = string.Equals(hex, now, StringComparison.OrdinalIgnoreCase);

            var dot = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.FindResource("ControlStrokeColorDefaultBrush"),
            };

            var check = new TextBlock
            {
                Text = "✓",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = selected ? Visibility.Visible : Visibility.Collapsed,
                Foreground = dark ? Brushes.White : Brushes.Black,
            };

            var ring = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                BorderThickness = new Thickness(2),
                BorderBrush = (Brush)Application.Current.FindResource("AccentFillColorDefaultBrush"),
                Visibility = selected ? Visibility.Visible : Visibility.Collapsed,
            };

            var inner = new Grid { Width = 26, Height = 26 };
            inner.Children.Add(dot);
            inner.Children.Add(check);

            var halo = new Grid { Width = 34, Height = 34, Margin = new Thickness(0, 0, 6, 0) };
            halo.Children.Add(ring);
            halo.Children.Add(new Border
            {
                Width = 26,
                Height = 26,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = inner,
            });

            var button = new Button
            {
                Style = (Style)Application.Current.FindResource("SubtleButton"),
                Width = 34,
                Height = 34,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Content = halo,
                ToolTip = name,
            };
            button.Click += (_, _) =>
            {
                picked(hex);
                BuildBackgroundSwatches();
                BuildBoardSwatches();
            };

            target.Children.Add(button);
        }
    }

    void OnPickBackgroundImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        if (Vm is { } vm)
        {
            vm.SettingsBackgroundImage = dialog.FileName;
            vm.SettingsBackground = "image";
            SyncBackgroundRows(vm);
        }
    }

    void OnClearBackgroundImage(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.SettingsBackgroundImage = "";
        SyncBackgroundRows(vm);
    }

    /// <summary>
    /// 主题色色块。用代码建视觉元素而不是 XAML 模板 + ItemsControl：
    /// 每颗色球要叠"径向渐变球体 + 左上镜面高光 + 选中环 + 对勾"四层，
    /// 写成模板要三套 DataTemplate 加转换器，还是这里直白。
    /// </summary>
    void BuildThemeSwatches()
    {
        ThemeStrip.Children.Clear();

        var current = ColorThemes.Get(Vm?.ColorTheme).Key;
        foreach (var theme in ColorThemes.All)
            ThemeStrip.Children.Add(CreateSwatch(theme, theme.Key == current));

        var hit = ColorThemes.Get(current);
        ThemeHint.Text = $"当前：{hit.Name} · {hit.Hint}";
    }

    FrameworkElement CreateSwatch(ColorTheme theme, bool selected)
    {
        var orb = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = BuildOrb(theme.Seed),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = theme.Seed,
                Opacity = 0.38,
                BlurRadius = 12,
                ShadowDepth = 2,
                Direction = 270,
            },
        };

        // 左上镜面高光：让色球像玻璃珠，而不是一块纯色圆
        var specular = new System.Windows.Shapes.Ellipse
        {
            Width = 14,
            Height = 9,
            Margin = new Thickness(5, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Fill = new LinearGradientBrush(
                Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
                90),
            IsHitTestVisible = false,
        };

        var check = new TextBlock
        {
            Text = "✓",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = selected ? Visibility.Visible : Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        var inner = new Grid { Width = 34, Height = 34 };
        inner.Children.Add(orb);
        inner.Children.Add(specular);
        inner.Children.Add(check);

        var ring = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(theme.Seed),
            Visibility = selected ? Visibility.Visible : Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        var halo = new Grid { Width = 44, Height = 44 };
        halo.Children.Add(ring);
        halo.Children.Add(new Border
        {
            Width = 34,
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = inner,
        });

        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("SubtleButton"),
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = halo,
            ToolTip = $"{theme.Name} · {theme.Hint}",
        };
        button.Click += (_, _) =>
        {
            if (Vm is { } vm) vm.ColorTheme = theme.Key;
            BuildThemeSwatches();
        };

        var label = new TextBlock
        {
            Text = theme.Name,
            FontSize = 11.5,
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)Application.Current.FindResource(
                selected ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"),
        };

        var item = new StackPanel
        {
            Margin = new Thickness(0, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        item.Children.Add(button);
        item.Children.Add(label);
        return item;
    }

    /// <summary>玻璃色球：左上提亮、主体种子色、右下压暗。</summary>
    static Brush BuildOrb(Color seed)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.30, 0.24),
            Center = new Point(0.34, 0.28),
            RadiusX = 0.95,
            RadiusY = 0.95,
        };
        brush.GradientStops.Add(new GradientStop(ColorThemes.Lighten(seed, 0.42), 0));
        brush.GradientStops.Add(new GradientStop(seed, 0.52));
        brush.GradientStops.Add(new GradientStop(Darken(seed, 0.28), 1));
        brush.Freeze();
        return brush;
    }

    static Color Darken(Color c, double t) => Color.FromRgb(
        (byte)(c.R * (1 - t)),
        (byte)(c.G * (1 - t)),
        (byte)(c.B * (1 - t)));
}

public partial class InteractionPage : UserControl
{
    public InteractionPage() => InitializeComponent();
}

public partial class IslandPage : UserControl
{
    public IslandPage() => InitializeComponent();
}

public partial class SchedulePage : UserControl
{
    public SchedulePage()
    {
        InitializeComponent();
        TimetableLink.Click += (_, _) => (Window.GetWindow(this) as SettingsWindow)?.OpenTimetableEditor();
        HolidaysLink.Click += (_, _) => Show(vm => new HolidaysDialog(vm));
        DutyLink.Click += (_, _) => Show(vm => new DutyDialog(vm));
        TempAdjustLink.Click += (_, _) => Show(vm => new TempAdjustDialog(vm));
        StatusTestLink.Click += (_, _) => Show(vm => new StatusTestDialog(vm));
    }

    void Show(Func<SettingsViewModel, FrameworkElement> create)
    {
        if (DataContext is not SettingsViewModel vm) return;
        DialogLauncher.Show(this, create(vm));
    }
}

public partial class HomeworkPage : UserControl
{
    bool _built;
    bool _hooked;
    bool _historyLoading;
    SettingsViewModel? _vm;

    public HomeworkPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is not SettingsViewModel vm) return;

            if (!_built)
            {
                _built = true;
                // 内嵌模式：改动即时生效，没有取消 / 确认按钮
                EditorHost.Content = new HomeworkEditor(vm, embedded: true);
                OpenFolderButton.Click += (_, _) => DialogLauncher.OpenFolder(vm.ConfigPath);
            }

            Hook(vm);
            SyncHistoryDays();
        };
        Unloaded += (_, _) => Unhook();
    }

    void Hook(SettingsViewModel vm)
    {
        if (_hooked) return;
        _hooked = true;
        _vm = vm;
        // 「恢复本页默认」会广播 null；之外只关心保留天数这一项
        vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    void Unhook()
    {
        if (!_hooked || _vm is null) return;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _hooked = false;
    }

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(SettingsViewModel.HomeworkHistoryDays))
            SyncHistoryDays();
    }

    /// <summary>把设置值回填到下拉框（不触发写回）。</summary>
    void SyncHistoryDays()
    {
        if (DataContext is not SettingsViewModel vm) return;

        _historyLoading = true;
        try
        {
            ComboBoxItem? match = null;
            foreach (var item in HistoryDaysBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is string tag && int.TryParse(tag, out var days) && days == vm.HomeworkHistoryDays)
                {
                    match = item;
                    break;
                }
            }

            if (match is not null) HistoryDaysBox.SelectedItem = match;
            else if (HistoryDaysBox.SelectedItem is null && HistoryDaysBox.Items.Count > 1) HistoryDaysBox.SelectedIndex = 1;
        }
        finally
        {
            _historyLoading = false;
        }
    }

    void OnHistoryDaysChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_historyLoading || DataContext is not SettingsViewModel vm) return;
        if (HistoryDaysBox.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            int.TryParse(tag, out var days))
            vm.HomeworkHistoryDays = days;
    }
}

public partial class PluginsPage : UserControl
{
    PluginManager? _manager;
    MarketService? _market;
    bool _hooked;
    bool _installing;
    bool _syncing;
    bool _applyingSource;   // 正在把设置里的值写回选择器：此时不触发刷新

    /// <summary>市场里比本机新的插件（id → 视图），用来在已安装行上亮「可更新」。</summary>
    readonly Dictionary<string, MarketView> _updates = new(StringComparer.OrdinalIgnoreCase);

    public PluginsPage()
    {
        InitializeComponent();

        InstallPackageLink.Click += (_, _) => InstallPackage();
        InstallFolderLink.Click += (_, _) => InstallFolder();
        RescanLink.Click += (_, _) =>
        {
            _manager?.Refresh();
            Refresh();
            DialogLauncher.Toast(this, InfoBarSeverity.Information, "重新扫描", "已按插件目录的当前内容重新加载。");
        };

        OpenFolderLink.Click += (_, _) =>
        {
            if (_manager is not null) DialogLauncher.OpenFolder(_manager.PluginsRoot);
        };
        // 运行日志已独立成左侧「日志」页，这里只做跳转
        LogLink.Click += (_, _) =>
        {
            if (Window.GetWindow(this) is SettingsWindow window) window.NavigateTo("logs");
        };

        // ── 插件市场 ──
        PluginTabs.SelectedChanged += SwitchTab;
        MarketSyncButton.Click += (_, _) => _ = SyncMarketAsync();
        MarketSourceBar.SelectedChanged += OnMarketSourceChanged;
        MarketSearch.TextChanged += (_, _) => RenderMarket();

        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    void Attach()
    {
        if (_hooked || DataContext is not SettingsViewModel vm) return;
        _hooked = true;
        _manager = vm.Ctx.Plugins;
        _market = vm.Ctx.Market;
        _market.Changed += OnMarketChanged;

        // 把设置里的更新源回填到选择器（不触发刷新）
        _applyingSource = true;
        MarketSourceBar.SelectedValue = vm.MarketSource;
        _applyingSource = false;

        Refresh();

        // 静默拉一次市场：缓存未过期不会发请求。拿到「可更新」才能在这里提示，
        // 不然用户只会看到"怎么还是旧版、怎么没有设置按钮"
        _ = RefreshMarketAsync(force: false);

        // 页面被缓存，切回来时若正停在整个市场页，顺手补一次（有缓存就不发请求）
        if (string.Equals(PluginTabs.SelectedValue, "market", StringComparison.Ordinal))
            _ = RefreshMarketAsync(force: false);
    }

    void Detach()
    {
        if (_market is not null) _market.Changed -= OnMarketChanged;
        _hooked = false;
    }

    // ————————————————————— 刷新 —————————————————————

    void Refresh()
    {
        if (_manager is null) return;

        var plugins = _manager.Plugins;
        var loaded = plugins.Count(p => p.Loaded);
        var failed = plugins.Count(p => p.Error is not null);

        // 市场里比本机新的那些（纯内存，不联网）
        _updates.Clear();
        if (_market is not null)
            foreach (var view in _market.Views)
                if (view.State == MarketState.Update) _updates[view.Entry.Id] = view;

        NoPluginNotice.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginHostPanel.Visibility = plugins.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        UpdateNotice.Visibility = plugins.Count > 0 && _updates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_updates.Count > 0)
        {
            var names = string.Join("、", _updates.Values.Select(v => v.Entry.Name).Take(3));
            var more = _updates.Count > 3 ? $" 等 {_updates.Count} 个" : "";
            UpdateNotice.Message = $"{names}{more}有新版本，可在「插件市场」里更新（新版可能带来新的设置项）。";
        }

        PluginHostPanel.Children.Clear();
        foreach (var info in plugins) PluginHostPanel.Children.Add(BuildRow(info));

        OpenFolderLink.Hint = _manager.PluginsRoot;
        SummaryText.Text = $"共 {plugins.Count} 个插件 · 已加载 {loaded} 个" +
                           (failed > 0 ? $" · 失败 {failed} 个" : "") +
                           $" · 契约 v{PluginManager.ApiVersion} · 插件目录：{_manager.PluginsRoot}";
    }

    // ————————————————————— 单个插件一行 —————————————————————

    FrameworkElement BuildRow(PluginInfo info)
    {
        var content = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };

        // 第一行：名称 + 版本 + 状态徽标
        var head = new WrapPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = info.Name,
            FontSize = (double)FindResource("FontSizeBody"),
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(MakeBadge("v" + info.Version, BadgeVariant.Neutral));
        head.Children.Add(info.Loaded
            ? MakeBadge("已加载", BadgeVariant.Ok)
            : info.Enabled
                ? MakeBadge("未加载", BadgeVariant.Warn)
                : MakeBadge("已禁用", BadgeVariant.Neutral));
        if (!string.IsNullOrWhiteSpace(info.Author)) head.Children.Add(MakeBadge(info.Author, BadgeVariant.Neutral));
        if (_updates.TryGetValue(info.Id, out var pending))
            head.Children.Add(MakeBadge($"可更新 v{pending.Entry.Version}", BadgeVariant.Accent));
        content.Children.Add(head);

        if (!string.IsNullOrWhiteSpace(info.Description))
            content.Children.Add(new TextBlock
            {
                Text = info.Description,
                Margin = new Thickness(0, 3, 0, 0),
                FontSize = (double)FindResource("FontSizeCaption"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
            });

        // 出错的行把原因原样摆出来 —— 这是插件系统最有价值的一行信息
        if (!string.IsNullOrWhiteSpace(info.Error))
            content.Children.Add(new TextBlock
            {
                Text = "加载失败：" + info.Error,
                Margin = new Thickness(0, 4, 0, 0),
                FontSize = (double)FindResource("FontSizeCaption"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Theme.Palette.Danger,
            });

        var meta = new List<string> { "契约 v" + info.ApiVersion };
        if (info.Loaded) meta.Add($"注册 {info.Registrations} 个显示位");
        // 插件自己算的一句话（如「2029 届 · 还有 218 天」），比描述更有用
        if (info.Loaded && info.HasSettings &&
            _manager?.SettingsSummary(info.Id) is { Length: > 0 } settingsSummary)
            meta.Add(settingsSummary);
        if (!string.IsNullOrWhiteSpace(info.Homepage)) meta.Add(info.Homepage!);
        content.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", meta),
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = (double)FindResource("FontSizeCaption"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
        });

        // 右侧操作区
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var toggle = new FluentToggle { Label = "启用", IsChecked = info.Enabled, VerticalAlignment = VerticalAlignment.Center };
        toggle.Click += (_, _) =>
        {
            var want = toggle.IsChecked == true;
            if (_manager is null) return;
            if (!_manager.SetEnabled(info.Id, want))
            {
                toggle.IsChecked = !want;      // 起不来就把开关拨回去，别骗用户
                DialogLauncher.Toast(this, InfoBarSeverity.Error, "没有生效",
                    $"{info.Name} 没能" + (want ? "启用" : "禁用") + "，原因见运行日志。");
            }
            Refresh();
        };
        actions.Children.Add(toggle);

        // 市场里有新版：直接给一个「更新」，省得用户切到市场再找一遍
        if (_updates.TryGetValue(info.Id, out var pendingUpdate))
        {
            var updateButton = new Button
            {
                Content = "更新",
                Style = (Style)FindResource("AccentButton"),
                Padding = new Thickness(12, 3, 12, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = !_installing,
            };
            updateButton.Click += (_, _) => _ = InstallMarketAsync(pendingUpdate);
            actions.Children.Add(updateButton);
        }

        // 只有插件主动声明过设置页，这里才有「设置」—— 空设置页比没有更糟
        if (info.Loaded && info.HasSettings)
            actions.Children.Add(MakeButton("设置", () => OpenPluginSettings(info)));

        actions.Children.Add(MakeButton("重载", () =>
        {
            _manager?.Reload(info.Id);
            Refresh();
        }));
        actions.Children.Add(MakeButton("目录", () => DialogLauncher.OpenFolder(info.Directory)));
        actions.Children.Add(MakeButton("卸载", () =>
            DialogLauncher.Confirm(this, "卸载插件",
                $"确定卸载「{info.Name}」吗？插件目录与它的数据都会被删除，无法恢复。",
                () =>
                {
                    var result = _manager?.Uninstall(info.Id);
                    if (result is { Ok: false })
                        DialogLauncher.Toast(this, InfoBarSeverity.Error, "卸载失败", result.Error ?? "");
                    Refresh();
                }, okText: "卸载")));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(content, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(content);
        grid.Children.Add(actions);

        return new Border
        {
            Padding = new Thickness(18, 12, 18, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = (Brush)FindResource("DividerStrokeColorDefaultBrush"),
            Child = grid,
        };
    }

    Badge MakeBadge(string text, BadgeVariant variant) => new()
    {
        Text = text,
        Variant = variant,
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    Button MakeButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource("SubtleButton"),
            Padding = new Thickness(10, 3, 10, 4),
            Margin = new Thickness(8, 0, 0, 0),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    // ————————————————————— 安装 —————————————————————

    void InstallPackage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择插件包",
            Filter = "插件包 (*.cblplugin;*.zip)|*.cblplugin;*.zip|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        Install(dlg.FileName, overwrite: false);
    }

    void InstallFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择插件文件夹（含 plugin.json）" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        Install(dlg.FolderName, overwrite: false);
    }

    void Install(string path, bool overwrite)
    {
        if (_manager is null) return;
        var result = _manager.Install(path, overwrite);
        if (result.Ok)
        {
            DialogLauncher.Toast(this, InfoBarSeverity.Success, "已安装",
                $"「{result.PluginId}」已经装好并启用。");
            Refresh();
            return;
        }

        // 已装过：问一句再覆盖，不要直接失败也不要静默覆盖
        if (!overwrite && result.PluginId is not null)
        {
            DialogLauncher.Confirm(this, "覆盖安装",
                $"已经装过「{result.PluginId}」了。用这个包覆盖它吗？（插件自己的数据目录会保留）",
                () => Install(path, overwrite: true), okText: "覆盖");
            return;
        }

        DialogLauncher.Toast(this, InfoBarSeverity.Error, "安装失败", result.Error ?? "未知原因");
        Refresh();
    }

    /// <summary>打开插件自己声明的设置页（声明式渲染，插件不碰任何 WPF 类型）。</summary>
    void OpenPluginSettings(PluginInfo info)
    {
        if (_manager is null || DataContext is not SettingsViewModel vm) return;

        var spec = _manager.SettingsPageOf(info.Id);
        if (spec is null)
        {
            DialogLauncher.Toast(this, InfoBarSeverity.Warning, "没有设置页",
                $"「{info.Name}」没有向宿主要求设置页。");
            return;
        }

        DialogLauncher.Show(this, new PluginSettingsPanel(vm, _manager, info, spec));
    }

    // =====================================================================
    //  插件市场
    //  -------------------------------------------------------------------
    //  页面只读 MarketService 的 Status / Views，不自己发请求：
    //  拉索引、下载、哈希校验、调用 PluginInstaller 全在服务层。
    // =====================================================================

    void SwitchTab(string value)
    {
        var market = string.Equals(value, "market", StringComparison.Ordinal);
        InstalledPanel.Visibility = market ? Visibility.Collapsed : Visibility.Visible;
        MarketPanel.Visibility = market ? Visibility.Visible : Visibility.Collapsed;

        if (market) _ = RefreshMarketAsync(force: false);
        else Refresh();
    }

    async Task<bool> RefreshMarketAsync(bool force)
    {
        var market = _market;
        if (market is null) return false;
        return await market.RefreshAsync(force);
    }

    /// <summary>
    /// 「立即同步」：绕过 10 分钟缓存强制拉一次索引，并把结果直接告诉用户
    /// （成功给插件数与来源，失败给每个源各自的原因 —— 与自动刷新不同，点了按钮就一定要有回音）。
    /// </summary>
    async Task SyncMarketAsync()
    {
        var market = _market;
        if (market is null || _syncing) return;

        _syncing = true;
        MarketSyncButton.IsEnabled = false;
        MarketSyncButton.Content = "同步中…";

        try
        {
            var ok = await market.RefreshAsync(force: true);
            var status = market.Status;

            if (!ok && status.Stage == MarketStage.Loading)
            {
                DialogLauncher.Toast(this, InfoBarSeverity.Information, "正在同步",
                    "上一次同步还没结束，稍等一下再看。");
                return;
            }

            DialogLauncher.Toast(this,
                ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                ok ? "市场已同步" : "同步失败",
                ok
                    ? $"{market.Views.Count} 个插件 · {(string.IsNullOrWhiteSpace(status.FreshnessText) ? "刚刚" : status.FreshnessText)}"
                    : (string.IsNullOrWhiteSpace(status.Message) ? "两个源都没拉到，检查网络后再试。" : status.Message));

            RenderMarket();
            Refresh();   // 让已安装行的「可更新」徽标跟上
        }
        finally
        {
            _syncing = false;
            MarketSyncButton.Content = "立即同步";
            MarketSyncButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 切换插件市场的更新源：写进设置并立刻重拉一次索引。
    /// 缓存里记着上次用的是哪个源，换源后不重拉的话页面会继续显示旧源的数据。
    /// </summary>
    void OnMarketSourceChanged(string value)
    {
        if (_applyingSource) return;
        if (DataContext is not SettingsViewModel vm) return;
        if (string.Equals(vm.MarketSource, value, StringComparison.OrdinalIgnoreCase)) return;

        vm.MarketSource = value;

        var label = value switch
        {
            "github" => "只走 GitHub",
            "gitee" => "只走 Gitee",
            _ => "自动（GitHub 优先，失败换 Gitee）",
        };
        DialogLauncher.Toast(this, InfoBarSeverity.Information, "已切换更新源", label);

        _ = RefreshMarketAsync(force: true);
    }

    void OnMarketChanged() => Dispatcher.BeginInvoke(new Action(() =>
    {
        RenderMarket();
        // 市场数据到位后，已安装列表的「可更新」徽标要跟着出来
        if (_market?.Status.Stage == MarketStage.Ready) Refresh();
    }));

    void RenderMarket()
    {
        var market = _market;
        if (market is null) return;

        var status = market.Status;
        MarketBar.Severity = status.Stage switch
        {
            MarketStage.Failed => status.FromCache ? InfoBarSeverity.Warning : InfoBarSeverity.Error,
            MarketStage.Loading => InfoBarSeverity.Information,
            _ => status.Warnings.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
        };
        MarketBar.Title = status.Stage == MarketStage.Failed && status.FromCache
            ? "无法连接市场（显示缓存）"
            : status.Stage == MarketStage.Failed ? "无法连接插件市场" : "插件市场";
        MarketBar.Message = BuildMarketMessage(status);

        var keyword = (MarketSearch.Text ?? "").Trim();
        var views = market.Views.Where(v => Matches(v, keyword)).ToList();

        MarketHost.Children.Clear();
        foreach (var view in views) MarketHost.Children.Add(BuildMarketRow(view));
        MarketEmpty.Visibility = views.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MarketEmpty.Text = market.Views.Count == 0 ? "市场里还没有插件" : "没有匹配的插件";

        MarketSummary.Text = market.Views.Count == 0
            ? (status.FetchedAt is null ? "" : "数据：" + status.FreshnessText)
            : $"共 {market.Views.Count} 个插件" +
              (market.UpdateCount > 0 ? $" · {market.UpdateCount} 个可更新" : "") +
              (status.FetchedAt is null ? "" : $" · 数据：{status.FreshnessText}");
    }

    static string BuildMarketMessage(MarketStatus status) => status.Stage switch
    {
        MarketStage.Loading => string.IsNullOrWhiteSpace(status.Message) ? "正在获取插件市场…" : status.Message!,
        MarketStage.Failed => string.IsNullOrWhiteSpace(status.Message)
            ? "无法连接插件市场，检查网络后点「刷新市场」。"
            : status.Message,
        _ => "安装包会做 sha256 校验，并核对包内的 id / 版本；装完立即生效，不用重启。",
    };

    static bool Matches(MarketView view, string keyword)
    {
        if (keyword.Length == 0) return true;
        var entry = view.Entry;
        return Contains(entry.Name, keyword)
               || Contains(entry.Summary, keyword)
               || Contains(entry.Author, keyword)
               || Contains(entry.Id, keyword)
               || (entry.Tags?.Any(tag => Contains(tag, keyword)) ?? false);
    }

    static bool Contains(string? text, string keyword) =>
        !string.IsNullOrEmpty(text) && text.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);

    FrameworkElement BuildMarketRow(MarketView view)
    {
        var entry = view.Entry;
        var content = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };

        // 第一行：名称 + 版本 + 信任级别 + 已装版本
        var head = new WrapPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = (double)FindResource("FontSizeBody"),
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(MakeBadge("v" + entry.Version, BadgeVariant.Neutral));
        head.Children.Add(entry.TrustOrDefault switch
        {
            "official" => MakeBadge("官方", BadgeVariant.Ok),
            "verified" => MakeBadge("认证", BadgeVariant.Accent),
            _ => MakeBadge("社区", BadgeVariant.Neutral),
        });
        if (view.State == MarketState.Update && !string.IsNullOrWhiteSpace(view.InstalledVersion))
            head.Children.Add(MakeBadge($"已装 v{view.InstalledVersion}", BadgeVariant.Accent));
        if (view.State == MarketState.Revoked) head.Children.Add(MakeBadge("已下架", BadgeVariant.Warn));
        content.Children.Add(head);

        if (!string.IsNullOrWhiteSpace(entry.Summary))
            content.Children.Add(new TextBlock
            {
                Text = entry.Summary,
                Margin = new Thickness(0, 3, 0, 0),
                FontSize = (double)FindResource("FontSizeCaption"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
            });

        // 元信息：作者 · 分类 · 体积 · 上架日期 ·（不兼容时的原因）
        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Author)) meta.Add(entry.Author);
        meta.Add(CategoryName(entry.CategoryOrDefault));
        if (entry.Size > 0) meta.Add($"{entry.Size / 1024.0:0.#} KB");
        if (!string.IsNullOrWhiteSpace(entry.PublishedAt)) meta.Add(entry.PublishedAt);
        if (!string.IsNullOrWhiteSpace(view.Reason)) meta.Add(view.Reason);
        content.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", meta),
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = (double)FindResource("FontSizeCaption"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
        });

        // 右侧操作区
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (view.CanInstall)
        {
            var button = new Button
            {
                Content = view.ActionText,
                Style = (Style)FindResource(view.State == MarketState.Update ? "AccentButton" : "SubtleButton"),
                Padding = new Thickness(12, 3, 12, 4),
                IsEnabled = !_installing,
            };
            button.Click += (_, _) => _ = InstallMarketAsync(view);
            actions.Children.Add(button);
        }
        else
        {
            actions.Children.Add(new TextBlock
            {
                Text = view.ActionText,
                FontSize = (double)FindResource("FontSizeCaption"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
            });
        }

        if (!string.IsNullOrWhiteSpace(entry.Repo)) actions.Children.Add(MakeButton("源码", () => OpenUrl(entry.Repo!)));
        if (!string.IsNullOrWhiteSpace(entry.Homepage)) actions.Children.Add(MakeButton("主页", () => OpenUrl(entry.Homepage!)));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(content, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(content);
        grid.Children.Add(actions);

        return new Border
        {
            Padding = new Thickness(18, 12, 18, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = (Brush)FindResource("DividerStrokeColorDefaultBrush"),
            Child = grid,
        };
    }

    static string CategoryName(string category) => category switch
    {
        "timetable" => "课表",
        "weather" => "天气",
        "reminder" => "提醒",
        "tools" => "工具",
        "media" => "媒体",
        "theme" => "外观",
        "data" => "数据",
        _ => "其它",
    };

    /// <summary>
    /// 装之前的分级确认：降级要问、社区插件要问（插件与主程序同进程，没有沙箱）。
    /// 官方 / 认证插件直接装，别让用户每装一个都点两次。
    /// </summary>
    async Task InstallMarketAsync(MarketView view)
    {
        if (_market is null || _installing) return;
        var entry = view.Entry;

        if (view.State == MarketState.Downgrade)
        {
            DialogLauncher.Confirm(this, "降级安装",
                $"「{entry.Name}」本机是 v{view.InstalledVersion}，市场上是 v{entry.Version}（更低）。确定降级吗？插件自己的数据会保留。",
                () => _ = RunInstallAsync(view), okText: "降级");
            return;
        }

        if (entry.TrustOrDefault == "community")
        {
            var changelog = entry.Changelog is { Count: > 0 }
                ? "\n\n更新内容：" + string.Join("；", entry.Changelog.Take(3))
                : "";
            var hash = entry.Sha256.Length >= 12 ? entry.Sha256[..12] : entry.Sha256;
            DialogLauncher.Confirm(this, "安装社区插件",
                $"「{entry.Name}」v{entry.Version} 由 {entry.Author} 提供。插件以完整权限运行在主程序进程里，请只装你信任的来源。\n\n包已做 sha256 校验：{hash}…{changelog}",
                () => _ = RunInstallAsync(view), okText: "安装");
            return;
        }

        await RunInstallAsync(view);
    }

    async Task RunInstallAsync(MarketView view)
    {
        var market = _market;
        if (market is null) return;

        _installing = true;
        MarketSyncButton.IsEnabled = false;

        // 下载进度就地反馈在状态条上，不弹模态框（包通常只有几十 KB）
        var progress = new Progress<double>(p =>
        {
            MarketBar.Severity = InfoBarSeverity.Information;
            MarketBar.Title = "正在安装";
            MarketBar.Message = $"正在下载「{view.Entry.Name}」… {p * 100:0}%";
        });

        try
        {
            var (ok, message) = await market.InstallAsync(view, progress);
            DialogLauncher.Toast(this,
                ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                ok ? "已安装" : "安装失败",
                message);
            if (ok) Refresh();
        }
        finally
        {
            _installing = false;
            MarketSyncButton.IsEnabled = true;
            RenderMarket();
        }
    }

    static void OpenUrl(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* 打不开链接不影响任何功能 */ }
    }
}

public partial class AdvancedPage : UserControl
{
    public AdvancedPage()
    {
        InitializeComponent();
        OpenConfigLink.Click += (_, _) => Open(vm => vm.ConfigPath);
        OpenPluginsLink.Click += (_, _) => Open(vm => vm.Ctx.Plugins.PluginsRoot);
        InstallLocalLink.Click += (_, _) => InstallPlugin(vm: DataContext as SettingsViewModel);
        ExportScheduleLink.Click += (_, _) => Export();
        ImportScheduleLink.Click += (_, _) => Import();
        ResetLink.Click += (_, _) => Reset();
    }

    /// <summary>高级页的「安装本地插件…」：和插件页走同一条流程，装完跳到插件页看结果。</summary>
    void InstallPlugin(SettingsViewModel? vm)
    {
        if (vm is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择插件包",
            Filter = "插件包 (*.cblplugin;*.zip)|*.cblplugin;*.zip|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        var result = vm.Ctx.Plugins.Install(dlg.FileName, overwrite: false);
        if (result.Ok)
        {
            DialogLauncher.Toast(this, InfoBarSeverity.Success, "已安装", $"「{result.PluginId}」已经装好并启用。");
            return;
        }
        if (result.PluginId is not null)
        {
            DialogLauncher.Confirm(this, "覆盖安装",
                $"已经装过「{result.PluginId}」了。用这个包覆盖它吗？（插件自己的数据目录会保留）",
                () =>
                {
                    var retry = vm.Ctx.Plugins.Install(dlg.FileName, overwrite: true);
                    DialogLauncher.Toast(this,
                        retry.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                        retry.Ok ? "已安装" : "安装失败",
                        retry.Ok ? $"「{retry.PluginId}」已经装好并启用。" : retry.Error ?? "");
                }, okText: "覆盖");
            return;
        }
        DialogLauncher.Toast(this, InfoBarSeverity.Error, "安装失败", result.Error ?? "未知原因");
    }

    void Open(Func<SettingsViewModel, string> path)
    {
        if (DataContext is SettingsViewModel vm) DialogLauncher.OpenFolder(path(vm));
    }

    void Export()
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出课表 JSON",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"课表_{DateTime.Today:yyyyMMdd}.json",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        System.IO.File.WriteAllText(dlg.FileName, vm.Ctx.Schedule.ExportJson(), System.Text.Encoding.UTF8);
        DialogLauncher.Toast(this, InfoBarSeverity.Success, "已导出", dlg.FileName);
    }

    void Import()
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入课表 JSON",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        DialogLauncher.Confirm(this, "导入课表",
            "导入会覆盖当前全部课表（含假期与调休），确定继续吗？", () =>
        {
            try
            {
                var ok = vm.Ctx.Schedule.ImportJson(
                    System.IO.File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8));
                DialogLauncher.Toast(this, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error, "导入课表",
                    ok ? "导入完成，灵动岛已按新课表运行。" : "文件内容不是有效的课表 JSON。");
            }
            catch (Exception ex)
            {
                DialogLauncher.Toast(this, InfoBarSeverity.Error, "导入课表", "导入失败：" + ex.Message);
            }
        });
    }

    void Reset()
    {
        if (DataContext is not SettingsViewModel vm) return;
        DialogLauncher.Confirm(this, "恢复全部默认",
            "所有设置都会回到默认值。已经装好的插件、课表和值日名单不受影响。", () =>
        {
            vm.ResetAll();
            DialogLauncher.Toast(this, InfoBarSeverity.Success, "已恢复默认", "所有设置已回到初始值。");
        }, okText: "恢复");
    }
}

public partial class AboutPage : UserControl
{
    SettingsViewModel? _vm;
    bool _hooked;
    bool _spinning;
    bool _applyingDev;   // 正在把设置回填到开关：此时不写回设置

    public AboutPage()
    {
        InitializeComponent();

        // ★ 状态订阅只挂一次：页面会被 SettingsWindow 缓存，之前写在 Loaded 里
        //   每次切回关于页都挂一次 Click，连点 5 次会发 5 个请求（见 AGENTS 坑清单）。
        DataContextChanged += (_, _) => Hook();
        Loaded += (_, _) => { Hook(); Render(); };
    }

    void Hook()
    {
        if (_hooked) return;
        if (DataContext is not SettingsViewModel vm) return;
        _vm = vm;
        _hooked = true;

        ChannelHint.Text = vm.UpdateChannel == "beta" ? "测试版可能不稳定" : "";

        vm.Ctx.Updates.StatusChanged += OnStatusChanged;

        // 开发者选项：开关状态回填 + 只有打开时才露出调试入口与插件开发工具
        _applyingDev = true;
        DeveloperToggle.IsChecked = vm.DeveloperMode;
        _applyingDev = false;
        var devVisible = vm.DeveloperMode ? Visibility.Visible : Visibility.Collapsed;
        TestErrorRow.Visibility = devVisible;
        ErrorCodeRow.Visibility = devVisible;
        PluginDeveloperToolRow.Visibility = devVisible;

        DeveloperToggle.Checked += (_, _) => ApplyDeveloperMode(true);
        DeveloperToggle.Unchecked += (_, _) => ApplyDeveloperMode(false);

        // 通道切换后要重新检查（同一个源的 stable/beta 是不同的块）
        ChannelBar.SelectedChanged += _ =>
        {
            ChannelHint.Text = vm.UpdateChannel == "beta" ? "测试版可能不稳定" : "";
            vm.Ctx.CheckForUpdate();
        };
    }

    void OnStatusChanged(UpdateStatus status)
    {
        // CheckAsync 里 ConfigureAwait(false)，回调可能在后台线程
        Dispatcher.BeginInvoke(() => Render(status));
    }

    void OnCheckUpdateClick(object sender, RoutedEventArgs e) => _vm?.Ctx.CheckForUpdate();

    /// <summary>打开 GitHub 仓库，方便用户点 Star（打不开也不影响任何功能）。</summary>
    void OnOpenStarClick(object sender, RoutedEventArgs e)
    {
        const string url = "https://github.com/LCHXXXX-1/Class-Daily-Land-2";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* 打不开链接不影响功能 */ }
    }

    /// <summary>开发者选项开关：写进设置，并立刻控制「测试报错」按钮的显隐。</summary>
    void ApplyDeveloperMode(bool on)
    {
        if (_applyingDev || _vm is null) return;
        _vm.DeveloperMode = on;
        var visibility = on ? Visibility.Visible : Visibility.Collapsed;
        TestErrorRow.Visibility = visibility;
        ErrorCodeRow.Visibility = visibility;
        PluginDeveloperToolRow.Visibility = visibility;
    }

    /// <summary>错误码清单：非模态打开，方便对照着报错窗看。</summary>
    void OnShowErrorCodesClick(object sender, RoutedEventArgs e)
    {
        var window = new ErrorCodeListWindow();
        if (Window.GetWindow(this) is Window owner) window.Owner = owner;
        window.Show();
    }

    /// <summary>
    /// 测试报错：直接**结束主窗口**，模拟最真实的异常退出。
    /// 报信进程立即弹「关键进程异常退出」，下次启动再补报「上次异常退出」—— 两层兜底一次验证。
    /// </summary>
    void OnTestErrorClick(object sender, RoutedEventArgs e) => _vm?.Ctx.DevTestTerminateMain();

    void OnOpenPluginDeveloperToolClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        if (_vm.Ctx.OpenPluginDeveloperTool(out var message))
        {
            DialogLauncher.Toast(this, InfoBarSeverity.Success, "插件开发工具已打开", message);
            return;
        }

        DialogLauncher.Toast(this, InfoBarSeverity.Warning, "无法打开插件开发工具", message);
    }

    /// <summary>
    /// 「立即更新」：直接拉起更新器，不过确认框（用户明确要求的交互：
    /// 检查更新过后 = 再次检查 + 立即更新，点了立即更新就把更新器拉起来）。
    /// 下载期间程序照常运行，只在替换文件前由更新器关闭，设置与插件不受影响。
    /// </summary>
    void OnUpdateInstallClick(object sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;

        var offer = vm.Ctx.Updates.Status.App;
        if (offer is null) return;

        // ApplyUpdateAsync：启动预复制到 %Temp% 的更新器 → 主程序继续在后台运行
        _ = vm.Ctx.ApplyUpdateAsync(offer);
    }

    /// <summary>
    /// 重新安装最新版本：不比较版本号，直接重新下载并替换程序文件。
    /// 程序文件疑似损坏、更新到一半断了时的自救入口；会先确认，因为安装时要关程序。
    /// </summary>
    void OnReinstallClick(object sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;

        DialogLauncher.Confirm(this, "重新安装最新版本",
            "会重新下载最新版并替换程序文件，安装时会自动关闭程序。设置与插件会保留。",
            () => _ = vm.Ctx.ReinstallLatestAsync(),
            okText: "重新安装");
    }

    /// <summary>重启软件：走正常关闭流程（清运行标记、卸插件），再启动新实例。</summary>
    void OnRestartClick(object sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;

        DialogLauncher.Confirm(this, "重启软件",
            "关闭并重新启动程序吗？",
            () => vm.Ctx.Restart(),
            okText: "重启");
    }

    void Render() => Render(_vm?.Ctx.Updates.Status ?? UpdateStatus.Idle);

    void Render(UpdateStatus status)
    {
        if (status.Stage == UpdateStage.Checking)
        {
            CheckUpdateButton.IsEnabled = false;
            CheckUpdateText.Text = "正在检查…";
            StartSpin();
        }
        else
        {
            CheckUpdateButton.IsEnabled = true;
            // 检查跑完过一轮（无论有没有新版本）就变成"再次检查"
            var checkedBefore = status.Stage is UpdateStage.UpToDate or UpdateStage.Available;
            CheckUpdateText.Text = checkedBefore ? "再次检查" : "检查更新";
            StopSpin();
        }

        // 有新版本才亮出「立即更新」；同时把检查按钮降级成次要样式，突出主操作
        var hasUpdate = status.App is not null;
        UpdateInstallButton.Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdateButton.Style = (Style)FindResource(hasUpdate ? "SubtleButton" : "AccentButton");

        if (status.Stage is UpdateStage.Idle or UpdateStage.Checking)
        {
            UpdateBar.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateBar.Visibility = Visibility.Visible;
        switch (status.Stage)
        {
            case UpdateStage.UpToDate:
                UpdateBar.Severity = InfoBarSeverity.Information;
                UpdateBar.Title = "已是最新";
                UpdateBar.Message = status.Message;
                break;

            case UpdateStage.Available:
                UpdateBar.Severity = status.App?.IsMandatory == true
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success;
                UpdateBar.Title = status.App?.IsMandatory == true ? "发现新版本（强制）" : "发现新版本";
                // 更新内容直接贴在状态条上（没有确认框了，用户一眼能看到改了什么）
                var preview = status.App is { Changelog.Count: > 0 } app
                    ? "  · " + string.Join("  · ", app.Changelog.Take(3))
                    : "";
                UpdateBar.Message = (status.Message ?? "") + preview;
                break;

            case UpdateStage.Failed:
                UpdateBar.Severity = InfoBarSeverity.Warning;
                UpdateBar.Title = "检查更新失败";
                UpdateBar.Message = string.IsNullOrWhiteSpace(status.Error) ? status.Message : status.Error;
                break;
        }
    }

    void StartSpin()
    {
        if (_spinning) return;
        _spinning = true;

        var transform = new RotateTransform();
        CheckIcon.RenderTransform = transform;
        CheckIcon.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        transform.BeginAnimation(RotateTransform.AngleProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            });
    }

    void StopSpin()
    {
        if (!_spinning) return;
        _spinning = false;
        CheckIcon.RenderTransform = null;
    }
}

// ============================================================================
//  媒体（内置集成）：状态行每秒刷新一次，其余都是普通绑定
// ============================================================================

public partial class MediaPage : UserControl
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _loading;

    public MediaPage()
    {
        InitializeComponent();
        InjectButton.Click += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm) vm.Ctx.Audio?.InjectTestMedia();
            UpdateStatus();
        };
        ClearInjectButton.Click += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm) vm.Ctx.Audio?.ClearTestMedia();
            UpdateStatus();
        };
        Loaded += (_, _) => { Init(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += (_, _) => UpdateStatus();
    }

    void Init()
    {
        UnavailableNotice.Visibility = AudioService.Available ? Visibility.Collapsed : Visibility.Visible;
        if (DataContext is not SettingsViewModel vm) return;

        _loading = true;
        foreach (var item in ShowOnBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals((string?)item.Tag, vm.AudioShowOn, StringComparison.OrdinalIgnoreCase))
            {
                ShowOnBox.SelectedItem = item;
                break;
            }
        }
        _loading = false;
        UpdateStatus();
    }

    void UpdateStatus()
    {
        if (DataContext is SettingsViewModel vm)
            StatusText.Text = vm.Ctx.Audio?.StatusText ?? "";
    }

    void OnShowOnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        if (ShowOnBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            vm.AudioShowOn = tag;
    }
}

// ============================================================================
//  灾害预警（内置集成）：省市县三级联动 + 状态行 + 演练
// ============================================================================

public partial class DisasterPage : UserControl
{
    const string AnyCity = "（不限）";
    const string AnyDistrict = "（不限）";

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _loading;

    public DisasterPage()
    {
        InitializeComponent();
        ApplyCoordButton.Click += (_, _) => ApplyCoord();
        DrillButton.Click += (_, _) => Drill();
        ClearDrillButton.Click += (_, _) => ClearDrill();

        Loaded += (_, _) => { Init(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += (_, _) => UpdateStatus();
    }

    void Init()
    {
        if (DataContext is not SettingsViewModel vm) return;
        _loading = true;

        // 省份（最后加一个"不获取气象"）
        ProvinceBox.Items.Clear();
        foreach (var p in Cities.Provinces) ProvinceBox.Items.Add(p);
        ProvinceBox.Items.Add(Cities.NoWeather);
        Select(ProvinceBox, string.IsNullOrEmpty(vm.DisasterProvince) ? "四川省" : vm.DisasterProvince);

        FillCities(vm.DisasterCity);
        FillDistricts(vm.DisasterDistrict);

        foreach (var item in ThresholdBox.Items.OfType<ComboBoxItem>())
        {
            if (int.TryParse((string?)item.Tag, out var v) && v == vm.DisasterThresholdX10)
            {
                ThresholdBox.SelectedItem = item;
                break;
            }
        }
        if (ThresholdBox.SelectedItem is null) ThresholdBox.SelectedIndex = 1;

        LatBox.Text = vm.DisasterLat;
        LonBox.Text = vm.DisasterLon;

        _loading = false;
        UpdateStatus();
    }

    static void Select(ComboBox box, string value)
    {
        foreach (var item in box.Items)
        {
            if (string.Equals(item?.ToString(), value, StringComparison.Ordinal))
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    void FillCities(string keep)
    {
        var province = ProvinceBox.SelectedItem?.ToString() ?? "";
        CityBox.Items.Clear();
        CityBox.Items.Add(AnyCity);
        foreach (var c in Cities.CitiesOf(province)) CityBox.Items.Add(c);
        Select(CityBox, string.IsNullOrEmpty(keep) ? AnyCity : keep);
    }

    void FillDistricts(string keep)
    {
        var province = ProvinceBox.SelectedItem?.ToString() ?? "";
        var city = CityBox.SelectedItem?.ToString() ?? AnyCity;
        DistrictBox.Items.Clear();
        DistrictBox.Items.Add(AnyDistrict);
        foreach (var d in Cities.DistrictsOf(province, city == AnyCity ? "" : city))
            DistrictBox.Items.Add(d);
        Select(DistrictBox, string.IsNullOrEmpty(keep) ? AnyDistrict : keep);
    }

    string Province => ProvinceBox.SelectedItem?.ToString() ?? "";
    string CityValue => CityBox.SelectedItem?.ToString() == AnyCity ? "" : CityBox.SelectedItem?.ToString() ?? "";
    string DistrictValue => DistrictBox.SelectedItem?.ToString() == AnyDistrict ? "" : DistrictBox.SelectedItem?.ToString() ?? "";

    void OnProvinceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        _loading = true;
        FillCities("");
        FillDistricts("");
        _loading = false;

        vm.SetDisasterLocation(Province, "", "");
        LatBox.Text = "";
        LonBox.Text = "";
        ApplyCoord();
    }

    void OnCityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        _loading = true;
        FillDistricts("");
        _loading = false;

        vm.SetDisasterLocation(Province, CityValue, "");
        LatBox.Text = "";
        LonBox.Text = "";
        ApplyCoord();
    }

    void OnDistrictChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        vm.SetDisasterLocation(Province, CityValue, DistrictValue);
        LatBox.Text = "";
        LonBox.Text = "";
        ApplyCoord();
    }

    void OnThresholdChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        if (ThresholdBox.SelectedItem is ComboBoxItem item && int.TryParse((string?)item.Tag, out var v))
            vm.DisasterThresholdX10 = v;
    }

    /// <summary>把（可能空着的）经纬度写回设置：空 = 按省市查表。</summary>
    void ApplyCoord()
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.SetDisasterCoord(LatBox.Text?.Trim() ?? "", LonBox.Text?.Trim() ?? "");
        UpdateStatus();
    }

    void Drill()
    {
        if (DataContext is not SettingsViewModel vm) return;
        var result = vm.Ctx.Disaster?.StartDrill();
        DialogLauncher.Toast(this, InfoBarSeverity.Warning, "地震演练", result ?? "服务未就绪");
        UpdateStatus();
    }

    void ClearDrill()
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.Ctx.Disaster?.ClearDrill();
        UpdateStatus();
    }

    void UpdateStatus()
    {
        if (DataContext is SettingsViewModel vm)
            StatusText.Text = vm.Ctx.Disaster?.StatusText ?? "";
    }
}
