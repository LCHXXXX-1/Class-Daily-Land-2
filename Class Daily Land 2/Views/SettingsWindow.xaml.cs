using ClassDailyLand2.App.Controls;
using ClassDailyLand2.App.Theme;
using ClassDailyLand2.App.ViewModels;
using ClassDailyLand2.App.Views.Pages;
using ClassDailyLand2.App.Win32;
using ClassDailyLand2.Core.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace ClassDailyLand2.App.Views;

/// <summary>
/// 设置中心（WinUI 3 / Win11「设置」观感）：
/// 1232×762、48px 标题栏、264px 固定侧栏（品牌块 + 「查找设置」搜索框 + 大项导航，不可折叠）、
/// 内容区顶部一条横向胶囊放当前大项的子页面，下方 40px 横向内边距、内容列 900。
///
/// 两级导航：
///   左侧「大项」→ 顶部胶囊「子页面」。左侧选中项仍用液态玻璃强调条滑动。
///
/// 三处动画：
///   1. 导航强调条  —— 单条独立强调条，200ms OutCubic 跟着高亮项滑动
///   2. 页面转场    —— 280ms、0.92 倍页宽/高整段平移、按方向、**完全不淡化**
///   3. 二级浮层    —— forward 从右推进来 / back 送回右边，页面同时整段让位
///
/// 另外：所有确认提示都画在本窗口内（<see cref="Confirm"/>），不弹系统 MessageBox。
/// </summary>
public partial class SettingsWindow : Window
{
    const string PageHome = "home";

    /// <summary>整段平移的比例（页宽/页高的 0.92）。中间留 8% 重叠，换页不留两条空白。</summary>
    const double ShiftRatio = 0.92;

    /// <summary>转场时长，与 QML 版一致。</summary>
    const int TransitionMs = 280;

    /// <summary>左侧大项。每个大项下面挂若干子页面，子页面显示在顶部胶囊里。</summary>
    readonly List<NavGroup> _groups;

    /// <summary>所有页面按「大项 → 子项」顺序摊平，用来算切页方向。</summary>
    readonly List<NavEntry> _flatPages;

    readonly Dictionary<string, UserControl> _pages = new(StringComparer.Ordinal);
    readonly Dictionary<string, NavGroup> _pageToGroup = new(StringComparer.Ordinal);
    readonly Dictionary<string, RadioButton> _groupButtons = new(StringComparer.Ordinal);
    readonly Dictionary<string, RadioButton> _subButtons = new(StringComparer.Ordinal);
    RadioButton? _selectedNav;
    string _currentId = "";
    string _currentGroupId = "";
    string _currentSubId = "";
    bool _subLensShown;
    DispatcherTimer? _transitionScrollBarTimer;
    /// <summary>上次写进 WindowChrome 的玻璃帧厚度（只在变化时重建 Chrome，避免闪屏）。</summary>
    Thickness _appliedGlassFrame = new Thickness(-1);
    Action? _confirmOnOk;
    Action? _confirmOnCancel;
    bool _subOpen;

    public SettingsViewModel Vm { get; }

    sealed record NavEntry(string Id, string Label, string Glyph, Func<UserControl> Create);

    /// <summary>左侧大项。<paramref name="PinBottom"/> = 钉在导航卡底部（「关于」）。</summary>
    sealed record NavGroup(string Id, string Label, string Glyph, bool PinBottom, NavEntry[] Pages);

    public SettingsWindow(AppController ctx)
    {
        Vm = new SettingsViewModel(ctx);
        DataContext = Vm;

        // 两级导航：左边是大项，顶部胶囊是当前大项下的子页面。
        // 先建页表，再 InitializeComponent：导航要按同样的顺序生成。
        _groups = new List<NavGroup>
        {
            new("grp-home", "首页", "\uE80F", false, new[]
            {
                new NavEntry(PageHome, "首页", "\uE80F", () => new HomePage()),
            }),
            new("grp-general", "常规", "\uE713", false, new[]
            {
                new NavEntry(AppSettings.PageGeneral, "通用", "\uE713", () => new GeneralPage()),
                new NavEntry("interaction", "交互", "\uE7C9", () => new InteractionPage()),
            }),
            new("grp-appearance", "外观", "\uE790", false, new[]
            {
                new NavEntry(AppSettings.PageAppearance, "个性化", "\uE790", () => new AppearancePage()),
            }),
            new("grp-features", "功能", "\uE7F4", false, new[]
            {
                new NavEntry(AppSettings.PageIsland, "灵动岛", "\uE7F4", () => new IslandPage()),
                new NavEntry(AppSettings.PageMedia, "媒体", "\uE8D6", () => new MediaPage()),
                new NavEntry(AppSettings.PageWeather, "天气", "\uE706", () => new WeatherPage()),
                new NavEntry(AppSettings.PageDisaster, "灾害预警", "\uE730", () => new DisasterPage()),
            }),
            new("grp-teaching", "教学", "\uE787", false, new[]
            {
                new NavEntry(AppSettings.PageSchedule, "课表与提醒", "\uE787", () => new SchedulePage()),
                new NavEntry(AppSettings.PageHomework, "作业", "\uE8A5", () => new HomeworkPage()),
            }),
            new("grp-extensions", "扩展", "\uE74C", false, new[]
            {
                new NavEntry(AppSettings.PagePlugins, "插件", "\uE74C", () => new PluginsPage()),
            }),
            new("grp-system", "系统", "\uE90F", false, new[]
            {
                new NavEntry("logs", "日志", "\uE81E", () => new LogPage()),
                new NavEntry("advanced", "高级", "\uE90F", () => new AdvancedPage()),
            }),
            new("grp-about", "关于", "\uE946", true, new[]
            {
                new NavEntry(AppSettings.PageAbout, "关于", "\uE946", () => new AboutPage()),
            }),
        };

        _flatPages = _groups.SelectMany(g => g.Pages).ToList();
        foreach (var group in _groups)
            foreach (var entry in group.Pages)
                _pageToGroup[entry.Id] = group;

        InitializeComponent();
        BuildNav();

        // 「查找设置」：输入即过滤导航，Enter 跳到第一个匹配项
        SearchBox.TextChanged += (_, _) => FilterNav(SearchBox.Text);
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var match = FirstMatch(SearchBox.Text);
            if (match is not null) Select(match);
        };

        // 玻璃的柔光：指针在侧栏里移动时，那团光跟着走（离开就淡出）
        NavGlass.MouseEnter += (_, _) => FadeSheen(1);
        NavGlass.MouseLeave += (_, _) => FadeSheen(0);
        NavGlass.MouseMove += OnGlassMouseMove;

        // 手指滑动：无条件开。设置页是最常被触摸操作的地方，
        // 只有滚轮能滚的界面在平板上等于"卡死"。
        TouchMode.EnableTouchScroll(ScrollCur);
        TouchMode.EnableTouchScroll(SubScroll);
        TouchMode.EnableTouchScroll(NavScroll);

        SourceInitialized += (_, _) => ApplyWindowChrome();

        // 和主窗一样：SourceInitialized 时 DWM 还没稳定，云母/圆角偶尔不会立刻生效
        // （表现就是"要点一下 / 重开才出来"）。首帧渲染后再补挂一次（幂等）。
        ContentRendered += (_, _) =>
        {
            if (_chromeRendered) return;
            _chromeRendered = true;
            ApplyWindowChrome();
        };
        // 主窗是贴桌面边缘的挂件，和设置窗口都不置顶时很容易压在上面（会露出半截作业列表）
        ContentRendered += (_, _) => Interop.RaiseToFront(this);
        Loaded += (_, _) => Select(_pendingPage ?? PageHome, animate: false);
        NavScroll.ScrollChanged += (_, _) =>
        {
            // 动画进行中（含刚开始、还没出第一帧）都别去"校正"它 ——
            // 以前只看 HasAnimatedProperties，动画还没启动的那一帧会把玻璃直接摆到终点，
            // 观感就是"顿一下再滑"，也就是用户说的"卡"。
            if (DateTime.UtcNow < _animGate) return;
            if (!LensShift.HasAnimatedProperties) SyncMarker(_selectedNav, animate: false);
        };

        // 顶部胶囊滚动 / 窗口缩放时，灰色选中底片跟着当前子项重新落位（不播动画）
        SubNavScroll.ScrollChanged += (_, _) =>
        {
            if (_subLensShown) SyncSubNavLens(SelectedSubButton, animate: false);
        };
        SubNavPanel.SizeChanged += (_, _) =>
        {
            if (_subLensShown) SyncSubNavLens(SelectedSubButton, animate: false);
        };

        StateChanged += (_, _) => UpdateMaximizeIcon();
        ThemeService.Changed += OnThemeChanged;
        Vm.PropertyChanged += OnVmChanged;
        Closed += (_, _) =>
        {
            _transitionScrollBarTimer?.Stop();
            ThemeService.Changed -= OnThemeChanged;
            Vm.PropertyChanged -= OnVmChanged;
            Vm.Detach();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (ConfirmHost.Visibility == Visibility.Visible)
            {
                // ESC 与点「取消」同义：确认框里可能有"稍后再说"这类需要留痕的动作
                var callback = _confirmOnCancel;
                CloseConfirm();
                callback?.Invoke();
                return;
            }
            if (DialogHost.Visibility == Visibility.Visible) CloseOverlay();
        };
    }

    // ————————————————————— 窗口外观（云母 / 圆角） —————————————————————

    void OnThemeChanged() => ApplyWindowChrome();

    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.SettingsMica) or nameof(SettingsViewModel.SettingsAcrylic)
            or nameof(SettingsViewModel.ThemeMode)
            or nameof(SettingsViewModel.SettingsBackground) or nameof(SettingsViewModel.SettingsBackgroundColor)
            or nameof(SettingsViewModel.SettingsBackgroundImage))
            ApplyWindowChrome();
    }

    /// <summary>
    /// 柔光跟着指针走：把渐变的圆心换成指针在卡片里的相对位置。
    /// 用百分比而不是像素，卡片缩放/触屏下尺寸变化都不会跑偏。
    /// </summary>
    void OnGlassMouseMove(object sender, MouseEventArgs e)
    {
        if (NavSheen.ActualWidth < 1 || NavSheen.ActualHeight < 1) return;

        var p = e.GetPosition(NavSheen);
        var cx = Math.Clamp(p.X / NavSheen.ActualWidth, 0, 1);
        var cy = Math.Clamp(p.Y / NavSheen.ActualHeight, 0, 1);

        // 柔光是一大块径向渐变，每动一像素就重画整张卡会发涩 —— 位移超过 2% 才更新一次
        var dx = Math.Abs(cx - _sheenX);
        var dy = Math.Abs(cy - _sheenY);
        if (dx < 0.02 && dy < 0.02) return;

        _sheenX = cx;
        _sheenY = cy;
        NavSheenBrush.Center = new Point(cx, cy);
        NavSheenBrush.GradientOrigin = new Point(cx, cy);
        FadeSheen(1);
    }

    double _sheenX = 0.18, _sheenY;

    /// <summary>玻璃滑动期间的时间闸门：在这之前不接受任何"校正位置"的调用。</summary>
    DateTime _animGate;

    /// <summary>柔光淡入/淡出（0/1）。</summary>
    void FadeSheen(double to)
    {
        if (Math.Abs(NavSheen.Opacity - to) < 0.01 && !NavSheen.HasAnimatedProperties) return;
        NavSheen.BeginAnimation(OpacityProperty,
            new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(180))));
    }

    /// <summary>在可视树里找第一个指定类型的后代（子页宿主里的 ScrollViewer 用）。</summary>
    static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var deeper = FindDescendant<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    /// <summary>
    /// Win11 云母 + 圆角。
    ///
    /// 生效时窗口和标题栏/侧栏都透明，让 DWM 的背景透上来，只有内容区压一层
    /// 半透明 LayerFill —— 这就是 Win11「设置」的层次（侧栏是云母本身，
    /// 内容区是云母 + 一层薄白）。
    /// 云母没生效（Win10 / 关掉开关）就退回原来的不透明底色，保证可读。
    /// </summary>
    bool _chromeRendered;

    void ApplyWindowChrome()
    {
        var mode = Vm.SettingsBackground;                     // mica / solid / image
        var imagePath = Vm.SettingsBackgroundImage;
        var useImage = mode == "image" && !string.IsNullOrWhiteSpace(imagePath) && System.IO.File.Exists(imagePath);
        var custom = mode == "solid" || useImage;

        // 每次重新判断：云母态给卡片顶边加了 1px 白高光，亚克力下这条线会非常显眼
        Resources.Remove("HairlineHighlightBrush");

        // 自定义背景（纯色/图片）要先把 DWM 背板撤掉，否则云母会画在自己背景前面
        WindowBackdrop kind;
        if (custom)
        {
            Interop.ClearBackdrop(this);
            kind = WindowBackdrop.None;
        }
        else if (Vm.SettingsAcrylic)
        {
            // 亚克力开关：逐帧模糊（Win11 系统背板 / Win10 accent）。失败就回退纯色。
            kind = Interop.ApplyBlur(this, ThemeService.IsDark);
        }
        else
        {
            kind = Interop.ApplyBackdrop(this, Vm.SettingsMica, ThemeService.IsDark);
        }

        // Windows 10 的 accent 亚克力和整窗玻璃帧叠在一起时，窗口边缘会多出一圈白边。
        // Win10 亚克力把 GlassFrameThickness 收到 0（AccentPolicy 自己会铺满客户区）；
        // Win11 不动 Chrome，继续由 DWM 背板负责云母/亚克力。
        // ★ 只在厚度真的变化时重建 Chrome：每次 ApplyWindowChrome（主题/背景一变就跑）
        //   都 new 一个会重设非客户区，肉眼可见地闪一下。
        if (!Interop.IsWindows11)
        {
            var glass = kind == WindowBackdrop.Acrylic ? new Thickness(0) : new Thickness(-1);
            if (glass != _appliedGlassFrame)
            {
                _appliedGlassFrame = glass;
                WindowChrome.SetWindowChrome(this, new WindowChrome
                {
                    CaptionHeight = 48,
                    ResizeBorderThickness = new Thickness(6),
                    CornerRadius = new CornerRadius(0),
                    GlassFrameThickness = glass,
                    NonClientFrameEdges = NonClientFrameEdges.None,
                    UseAeroCaptionButtons = false,
                });
            }
        }

        Core.Logger.Current?.Info($"[设置背景] 模式={mode} 材质={kind} 深色={ThemeService.IsDark}");

        // 背景层
        if (useImage) Background = BuildImageBrush(imagePath!);
        else if (mode == "solid") Background = BuildSolidBrush(Vm.SettingsBackgroundColor);
        else if (kind == WindowBackdrop.None) Background = (Brush)FindResource("SolidBackgroundFillColorBaseBrush");
        else Background = Brushes.Transparent;                // 让云母/亚克力透上来

        if (kind == WindowBackdrop.None)
        {
            // 材质没生效：撤掉云母态覆盖，卡片/面板回到 App 级画刷
            Resources.Remove("CardBackgroundFillColorDefaultBrush");
            Resources.Remove("SolidBackgroundFillColorQuarternaryBrush");

            // 自定义背景时标题栏和侧栏透明（否则背景图被整块底色挡住），内容区压薄雾
            TitleBar.Background = custom ? Brushes.Transparent : Background;
            NavPane.Background = custom ? Brushes.Transparent : Background;
            ContentPane.Background = (Brush)FindResource(
                custom ? "LayerFillColorDefaultBrush" : "SolidBackgroundFillColorTertiaryBrush");
            return;
        }

        // 云母 / 亚克力：窗口与侧栏透明，内容区一层薄雾
        TitleBar.Background = Brushes.Transparent;
        NavPane.Background = Brushes.Transparent;


        if (kind == WindowBackdrop.Acrylic)
        {
            // ★ 亚克力本身就是"奶白磨砂"，再叠 25% 白就等于盖了块白板（"伤风景"就是这个）。
            //   这里只压 12% 白，卡片也减半，让底下的壁纸模糊能透出来。
            //   卡片顶边那条 72% 白的高光在亚克力上会变成明显的白边，直接撤掉。
            ContentPane.Background = (Brush)FindResource("AcrylicLayerBrush");
            Resources["CardBackgroundFillColorDefaultBrush"] = FindResource("AcrylicCardBrush");
            Resources["HairlineHighlightBrush"] = Brushes.Transparent;
        }
        else
        {
            ContentPane.Background = (Brush)FindResource("MicaLayerBrush");
            Resources["CardBackgroundFillColorDefaultBrush"] = FindResource("MicaCardBackgroundBrush");
        }
        Resources["SolidBackgroundFillColorQuarternaryBrush"] = FindResource("MicaDialogBrush");
    }

    /// <summary>纯色背景：解析失败就退回主题底色（填错色值也不至于看到黑窗口）。</summary>
    static Brush BuildSolidBrush(string? hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex ?? "");
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return (Brush)Application.Current.FindResource("SolidBackgroundFillColorBaseBrush");
        }
    }

    /// <summary>图片背景：铺满窗口（保持比例裁切）。</summary>
    static Brush BuildImageBrush(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;    // 读进内存，不锁文件
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return (Brush)Application.Current.FindResource("SolidBackgroundFillColorBaseBrush");
        }
    }

    // ————————————————————— 导航 —————————————————————

    void BuildNav()
    {
        foreach (var group in _groups)
        {
            var button = new RadioButton
            {
                Style = (Style)FindResource("NavItem"),
                Content = group.Label,
                Tag = group.Glyph,
                ToolTip = group.Label,
                GroupName = "SettingsGroupNav",
                CommandParameter = group.Id,
                Margin = new Thickness(0, 2, 0, 0),
            };
            var firstPage = group.Pages[0].Id;
            button.Click += (_, _) => Select(firstPage);
            _groupButtons[group.Id] = button;

            if (group.PinBottom) AboutPanel.Children.Add(button);
            else NavGroupPanel.Children.Add(button);
        }
    }

    /// <summary>
    /// 顶部胶囊：优先使用页面自己提供的子标签（插件页的已安装/插件市场），
    /// 否则使用大项下的子页面。同一个大项内切页时只改选中态，不重建按钮。
    /// </summary>
    void BuildSubNav(NavGroup group, string currentPageId, UserControl? page)
    {
        // 换大项时整排标签都换了：先收起旧底片，等新标签量完尺寸再摆到新位置
        SubLens.Visibility = Visibility.Collapsed;
        _subLensShown = false;

        SubNavPanel.Children.Clear();
        _subButtons.Clear();

        // 页面自己带顶部标签：例如插件页的「已安装 / 插件市场」
        if (page is ISettingsPageTabs host && host.Tabs.Count > 0)
        {
            _currentSubId = host.CurrentTabId;
            foreach (var tab in host.Tabs)
            {
                var id = tab.Id;
                var button = CreateSubButton(tab.Label,
                    string.Equals(id, _currentSubId, StringComparison.Ordinal),
                    () => SelectPageTab(host, id));
                _subButtons[id] = button;
                SubNavPanel.Children.Add(button);
            }
            ScheduleSubLens(_currentSubId);
            return;
        }

        _currentSubId = currentPageId;
        foreach (var entry in group.Pages)
        {
            var id = entry.Id;
            var button = CreateSubButton(entry.Label,
                string.Equals(id, currentPageId, StringComparison.Ordinal),
                () => Select(id));
            _subButtons[id] = button;
            SubNavPanel.Children.Add(button);
        }
        ScheduleSubLens(_currentSubId);
    }

    RadioButton CreateSubButton(string label, bool selected, Action onClick)
    {
        var button = new RadioButton
        {
            Style = (Style)FindResource("SubNavItem"),
            Content = label,
            ToolTip = label,
            GroupName = "SettingsSubNav",
            IsChecked = selected,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>新按钮刚进可视树，还没量出 ActualWidth：等这一轮布局完成再摆灰色底片。</summary>
    void ScheduleSubLens(string subId)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!string.Equals(_currentSubId, subId, StringComparison.Ordinal)) return;
            if (_subButtons.TryGetValue(subId, out var current))
                SyncSubNavLens(current, animate: false);
        }));
    }

    void SyncSubNavSelection(string subId)
    {
        foreach (var (id, button) in _subButtons)
            button.IsChecked = string.Equals(id, subId, StringComparison.Ordinal);
    }

    /// <summary>点顶部胶囊里的页面自有标签（插件页的已安装 / 插件市场），只切页内面板。</summary>
    void SelectPageTab(ISettingsPageTabs host, string tabId)
    {
        host.SelectTab(tabId);
        _currentSubId = tabId;
        SyncSubNavSelection(tabId);
        ScrollCur.ScrollToVerticalOffset(0);
        if (_subButtons.TryGetValue(tabId, out var button))
            SyncSubNavLens(button, animate: true);
    }

    RadioButton? SelectedSubButton =>
        _subButtons.TryGetValue(_currentSubId, out var button) ? button : null;

    /// <summary>
    /// 顶部胶囊的灰色选中底片：在子页面标签之间左右滑动。
    /// 位置按标签当前在屏幕上的实际位置算，滚动 / 缩放后也能重新对齐。
    /// </summary>
    void SyncSubNavLens(RadioButton? button, bool animate)
    {
        if (button is null || !button.IsVisible || button.ActualWidth < 1)
        {
            SubLens.Visibility = Visibility.Collapsed;
            _subLensShown = false;
            return;
        }

        var point = button.TranslatePoint(new Point(0, 0), SubLensCanvas);
        var from = SubLensShift.X;
        var fromWidth = SubLens.ActualWidth > 0 ? SubLens.ActualWidth : SubLens.Width;
        var targetWidth = button.ActualWidth;
        var interrupted = SubLensShift.HasAnimatedProperties;

        SubLens.Height = button.ActualHeight;
        SubLensShift.Y = point.Y;
        SubLensShift.BeginAnimation(TranslateTransform.XProperty, null);
        SubLens.BeginAnimation(FrameworkElement.WidthProperty, null);
        SubLens.Width = fromWidth;
        SubLens.Visibility = Visibility.Visible;

        if (!_subLensShown)
        {
            SubLensShift.X = point.X;
            SubLens.Width = targetWidth;
            _subLensShown = true;
            return;
        }

        SubLensShift.X = from;
        if (!animate || Math.Abs(point.X - from) < 0.5)
        {
            SubLensShift.X = point.X;
            SubLens.Width = targetWidth;
            return;
        }

        // 统一 350ms：这个时长下短距离不会显得急，长距离也不会拖。
        const int ms = 350;

        // 用二次减速而不是三次减速：前段不会“冲一下再爬”，慢速滑动时更均匀。
        var anim = Anim.Slide(point.X, ms, "EaseStandard");
        if (anim is not null)
        {
            if (interrupted) anim.EasingFunction = Anim.Ease("EaseStandard");
            SubLensShift.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        else SubLensShift.X = point.X;

        // 标签宽度不同（例如「已安装」→「插件市场」）时一起把灰底拉宽/收窄，
        // 否则宽度会在起步前瞬间跳一下，看起来就是“不流畅”。
        if (Math.Abs(targetWidth - fromWidth) > 0.5)
        {
            // 宽度比位移稍快收尾，避免灰底在移动末尾还在慢慢“长胖”。
            var widthMs = Math.Clamp((int)(ms * 0.72), 220, 430);
            var widthAnim = new DoubleAnimation(targetWidth,
                new Duration(TimeSpan.FromMilliseconds(widthMs)))
            {
                EasingFunction = Anim.Ease("EaseStandard"),
            };
            SubLens.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);
        }
        else SubLens.Width = targetWidth;
    }

    /// <summary>按关键字过滤左侧大项（大项名或它下面任一子页命中都保留）。</summary>
    void FilterNav(string? keyword)
    {
        var key = (keyword ?? "").Trim();
        foreach (var group in _groups)
        {
            if (!_groupButtons.TryGetValue(group.Id, out var button)) continue;
            var hit = key.Length == 0 ||
                      group.Label.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                      group.Pages.Any(p => p.Label.Contains(key, StringComparison.OrdinalIgnoreCase));
            button.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
        }

        // 当前选中项被过滤掉时，强调条一起藏起来（SyncMarker 内部按 IsVisible 处理）
        if (IsLoaded) SyncMarker(_selectedNav, animate: false);
    }

    /// <summary>搜索回车时跳到的第一个页面：先按大项名，再按子页名。</summary>
    string? FirstMatch(string? keyword)
    {
        var key = (keyword ?? "").Trim();
        foreach (var group in _groups)
        {
            var groupHit = key.Length == 0 ||
                           group.Label.Contains(key, StringComparison.OrdinalIgnoreCase);
            foreach (var entry in group.Pages)
            {
                if (groupHit || entry.Label.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return entry.Id;
            }
        }
        return null;
    }

    /// <summary>
    /// 把选中那块「液态玻璃」挪到高亮项上：位置、尺寸都跟着按钮走。
    ///
    /// 位置用 TranslatePoint 算到 LensCanvas 坐标系 —— 导航项可能在滚动区里，
    /// 也可能在底部固定的「关于」面板里，只有相对画布算才两边都对。
    ///
    /// 位移只动 Transform：Canvas.Top 是布局属性，每帧都会触发父级重新排列，
    /// 滑动看着就会一顿一顿的。落位用 EaseLiquid（BackEase，轻微过冲），
    /// 同时横向微鼓、纵向压扁再回弹 —— 就是"被吸过去的一滴玻璃"。
    /// </summary>
    void SyncMarker(RadioButton? button, bool animate)
    {
        if (button is null || !button.IsVisible || button.ActualWidth < 1)
        {
            NavLens.Visibility = Visibility.Collapsed;
            _markerShown = false;
            return;
        }

        var point = button.TranslatePoint(new Point(0, 0), LensCanvas);
        var from = LensShift.Y;
        var interrupted = LensShift.HasAnimatedProperties;

        // 尺寸跟着条目走：触屏模式下条目会变高，玻璃跟着一起长
        // 上限：永远别顶出卡片（溢出会被 LensCanvas 裁掉，右端圆角会变成直角）
        var maxWidth = LensCanvas.ActualWidth - 12;
        NavLens.Width = maxWidth > 32 ? Math.Min(button.ActualWidth, maxWidth) : button.ActualWidth;
        NavLens.Height = button.ActualHeight;
        LensShift.X = point.X;

        LensShift.BeginAnimation(TranslateTransform.YProperty, null);
        NavLens.Visibility = Visibility.Visible;


        if (!_markerShown)
        {
            // 首次出现：直接落位，别从 0 滑下来
            LensShift.Y = point.Y;
            _markerShown = true;
            return;
        }

        LensShift.Y = from;

        if (!animate || Math.Abs(point.Y - from) < 0.5)
        {
            LensShift.Y = point.Y;
            return;
        }

        // 时长跟距离走：跨一屏的大跳给足时间，隔壁一格也留一点点时间，别像瞬移。
        var distance = Math.Abs(point.Y - from);
        var ms = Math.Clamp((int)(240 + distance * 1.5), 240, 520);

        // 用平滑减速（PowerEase/Out）而不是 EaseLiquid（BackEase 会过冲再弹回）：
        // 玻璃块这么大，一次过冲看起来就像"滑过头又缩回来"，也就是"不流畅"。
        var anim = Anim.Slide(point.Y, ms, "EaseDecelerate");
        if (anim is not null)
        {
            if (interrupted) anim.EasingFunction = Anim.Ease("EaseDecelerate");

            // 动画期间别让 ScrollChanged 之类的校正插进来
            _animGate = DateTime.UtcNow.AddMilliseconds(ms + 140);
            LensShift.BeginAnimation(TranslateTransform.YProperty, anim);
        }
        else LensShift.Y = point.Y;

    }

    /// <summary>选中块是否已经显示过（首次出现不要从 0 滑下来）。</summary>
    bool _markerShown;

    int IndexOf(string id) => _flatPages.FindIndex(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    int GroupIndexOf(string id) =>
        _groups.FindIndex(g => string.Equals(g.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// 这一跳该往哪个方向动：
    ///   · 换左侧大项 —— 页面上下移动（大项顺序靠后 = down，靠前 = up）；
    ///   · 同一大项内换子页 —— 页面左右移动（子页顺序靠后 = forward，靠前 = back）。
    /// 这样左侧导航的上下位置、顶部胶囊的左右位置和页面移动方向是一致的。
    /// </summary>
    string DirectionFor(string prevPage, string nextPage, string prevGroup, string nextGroup)
    {
        if (string.IsNullOrEmpty(prevPage)) return "forward";

        if (!string.IsNullOrEmpty(prevGroup) && !string.IsNullOrEmpty(nextGroup) &&
            !string.Equals(prevGroup, nextGroup, StringComparison.Ordinal))
        {
            var ga = GroupIndexOf(prevGroup);
            var gb = GroupIndexOf(nextGroup);
            if (ga >= 0 && gb >= 0) return gb > ga ? "down" : "up";
        }

        var a = IndexOf(prevPage);
        var b = IndexOf(nextPage);
        if (a >= 0 && b >= 0) return b > a ? "forward" : "back";
        return "forward";
    }

    void Select(string id) => Select(id, animate: true);

    void Select(string id, bool animate)
    {
        if (!_pageToGroup.TryGetValue(id, out var group)) return;

        var entry = group.Pages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        if (entry is null) return;

        if (!_groupButtons.TryGetValue(group.Id, out var groupButton)) return;

        // 作业页的提交时段自检进行中：不让切页（同页重复点没事）
        if (NavigationLocked && !string.Equals(id, _currentId, StringComparison.Ordinal))
        {
            // 被点亮的导航按钮和顶部子项都拨回当前页；强调条本来就没动
            if (_selectedNav is not null) _selectedNav.IsChecked = true;
            SyncSubNavSelection(_currentSubId);
            Toast(InfoBarSeverity.Information, "测试进行中",
                "测试结束或点「中断测试」后才能切换页面。");
            return;
        }

        if (_currentId == id && PageHost.Content is not null) return;

        if (_subOpen)
        {
            _subOpen = false;
            SubScroll.Visibility = Visibility.Collapsed;
            SubHost.Content = null;
            ScrollCur.Visibility = Visibility.Visible;
            ScrollCur.RenderTransform = Transform.Identity;
            // 直接收起、没走 Slide：把 ShowOverlay 时挂的位图缓存清掉，否则文字发虚
            ScrollCur.CacheMode = null;
            SubScroll.CacheMode = null;
        }

        var direction = DirectionFor(_currentId, id, _currentGroupId, group.Id);

        if (!_pages.TryGetValue(id, out var page))
        {
            page = entry.Create();
            page.DataContext = Vm;
            _pages[id] = page;
            if (page is HomePage home) home.NavigateRequested += Select;
        }

        var previous = PageHost.Content as UIElement;
        PageHost.Content = page;
        ToastSlot.Children.Clear();
        ScrollCur.ScrollToVerticalOffset(0);

        groupButton.IsChecked = true;
        _selectedNav = groupButton;
        SyncMarker(groupButton, animate);

        // 先记下当前页：后面胶囊的 SizeChanged / ScrollChanged 都按新页对齐
        _currentId = id;

        // 同一个大项内切页：只改胶囊选中态；换大项才重建胶囊。
        // ★ 页面自带顶部标签（插件页的已安装/插件市场）时必须重建：此时
        //   SubNavPanel 里还是上一个页面的按钮，只改选中态会全部落空。
        var groupChanged = !string.Equals(_currentGroupId, group.Id, StringComparison.Ordinal);
        _currentGroupId = group.Id;
        if (groupChanged || SubNavPanel.Children.Count == 0 || page is ISettingsPageTabs)
        {
            BuildSubNav(group, id, page);
        }
        else
        {
            // 同一大项内切子页：底片左右滑（ISettingsPageTabs 页面已由上面强制重建）
            _currentSubId = id;
            SyncSubNavSelection(_currentSubId);
            if (_subButtons.TryGetValue(_currentSubId, out var subButton))
                SyncSubNavLens(subButton, animate: true);
        }

        if (animate && previous is not null) PlayPageTransition(previous, direction);
        else ClearPreviousLayer();
    }

    /// <summary>
    /// 整段平移：旧页往反方向整段移出、新页从反侧整段移入。
    /// 透明度一律不碰（恒为 1）—— 淡化会让两页同时半透明地叠着，"糊在一起"。
    /// </summary>
    void PlayPageTransition(UIElement oldPage, string direction)
    {
        HideScrollBarsDuringTransition();

        PrevInner.Children.Clear();
        PrevInner.Children.Add(oldPage);
        ScrollPrev.Visibility = Visibility.Visible;

        var w = PageStack.ActualWidth;
        var h = PageStack.ActualHeight;
        var sx = w * ShiftRatio;
        var sy = h * ShiftRatio;

        (double nx, double ny, double ox, double oy) = direction switch
        {
            "forward" => (sx, 0d, -sx, 0d),
            "back" => (-sx, 0d, sx, 0d),
            "down" => (0d, sy, 0d, -sy),
            _ => (0d, -sy, 0d, sy),          // up
        };

        // 旧页被搬到 PrevInner、新页刚塞进 PageHost —— 这一帧要重新测量/排列两整页。
        // 先把起点位置摆好，等这轮布局跑完再启动动画，动画第一帧才不会撞上布局。
        ScrollCur.RenderTransform = new TranslateTransform(nx, ny);
        ScrollPrev.RenderTransform = new TranslateTransform(0, 0);

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            Slide(ScrollCur, nx, ny, 0, 0, TransitionMs);
            Slide(ScrollPrev, 0, 0, ox, oy, TransitionMs, ClearPreviousLayer);
        }));
    }

    void ClearPreviousLayer()
    {
        PrevInner.Children.Clear();
        ScrollPrev.Visibility = Visibility.Collapsed;
        ScrollPrev.RenderTransform = Transform.Identity;
        // ★ 动画被中断时 Completed 不一定触发，BitmapCache 会残留 → 文字持续发虚。
        //   这里兜底清一次；落定路径 Slide 的 Completed 已清过，重复清是幂等的。
        ScrollPrev.CacheMode = null;
        ScrollCur.CacheMode = null;
    }

    /// <summary>
    /// 页面/浮层整段滑动时先把右侧滚动条藏起来：滚动条跟着位图缓存一起移动会明显掉帧。
    /// 用计时器而不是动画 Completed，是因为连续快速切页时旧动画会被新动画打断，
    /// Completed 不一定触发；计时器每次重新计时，最后一次滑动结束再恢复。
    /// </summary>
    void HideScrollBarsDuringTransition()
    {
        // ★ 只在滚动条当前真的可见时才改成 Hidden：
        //   Hidden 仍占住那一列，但 Auto 在「内容没超框」时并不占位 ——
        //   对短页面强行 Hidden 会让内容窄 10px，恢复 Auto 时又变宽，
        //   滑动落定后反而看到一次横向抖动。
        //   ScrollPrev 接的是当前页移过去的旧内容，此刻它还是 Collapsed、
        //   ComputedVerticalScrollBarVisibility 不可信，所以镜像 ScrollCur 的判断
        //   （ScrollCur 此刻还留着旧页的布局结果，正是 ScrollPrev 要显示的内容）。
        if (ScrollCur.ComputedVerticalScrollBarVisibility == Visibility.Visible)
        {
            ScrollCur.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            ScrollPrev.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        }
        // 二级页（课表编辑 / 作业编辑器）内容总是超框，且 ShowOverlay 里
        // 这行执行时 SubScroll 还没变 Visible、Computed 值不可信，直接强制隐藏。
        SubScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;

        if (_transitionScrollBarTimer is null)
        {
            _transitionScrollBarTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(TransitionMs + 140),
            };
            _transitionScrollBarTimer.Tick += (_, _) =>
            {
                _transitionScrollBarTimer!.Stop();
                ScrollCur.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                ScrollPrev.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                SubScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            };
        }

        _transitionScrollBarTimer.Stop();
        _transitionScrollBarTimer.Start();
    }

    /// <summary>通用位移动画（不碰 opacity）。</summary>
    static void Slide(UIElement element, double fromX, double fromY,
                      double toX, double toY, int ms, Action? done = null)
    {
        var transform = new TranslateTransform(fromX, fromY);
        element.RenderTransform = transform;

        var animX = Anim.Slide(toX, ms);
        var animY = Anim.Slide(toY, ms);
        if (animY is null)          // 关掉动画时直接落位
        {
            element.RenderTransform = Transform.Identity;
            done?.Invoke();
            return;
        }

        // ★ 整页平移时给元素套一层位图缓存：否则 WPF 每一帧都要把整棵可视树
        //   重新测量+绘制一遍（设置页动辄几百个元素），60fps 根本撑不住。
        //   缓存之后一帧只是把一张位图搬个位置，这才是"顺"的关键。
        element.CacheMode = new BitmapCache();

        animY.Completed += (_, _) =>
        {
            element.CacheMode = null;      // 落定后立刻撤掉，避免文字发虚
            done?.Invoke();
        };
        transform.BeginAnimation(TranslateTransform.XProperty, animX);
        transform.BeginAnimation(TranslateTransform.YProperty, animY);
    }

    // ————————————————————— 窗口按钮 —————————————————————

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void OnToggleMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void OnClose(object sender, RoutedEventArgs e) => Close();

    void UpdateMaximizeIcon()
    {
        var key = WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize";
        MaximizeIcon.Data = FindResource(key) as Geometry;
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
    }

    // ————————————————————— 浮层（二级页 / 对话框） —————————————————————

    /// <summary>
    /// 打开二级页：整页从右侧推进来（forward），当前页整段左移让位。
    /// 与 QML 版一致 —— 不是盖一张卡片，而是页内推进 + 面包屑。
    /// </summary>
    /// <summary>打开「课表与提醒 › 课表编辑」二级页（右键菜单与页面入口都走这里）。</summary>
    public void OpenTimetableEditor()
    {
        Select(AppSettings.PageSchedule);
        var page = new TimetableEditorPage { DataContext = Vm };
        ShowOverlay(page);
    }

    public void ShowOverlay(FrameworkElement panel)
    {
        HideScrollBarsDuringTransition();

        // 面板当成"页"来用：铺满内容列，去掉卡片那套固定宽高与居中
        panel.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.VerticalAlignment = VerticalAlignment.Stretch;
        if (panel is Border border)
        {
            border.Width = double.NaN;
            border.Margin = new Thickness(0);
            border.MaxHeight = double.PositiveInfinity;
        }

        SubCrumb.Text = panel switch
        {
            Views.Dialogs.DialogPanel dp => $"设置 › {dp.ParentPageTitle} › {dp.Title}",
            Views.Dialogs.HomeworkEditor he => $"设置 › {he.ParentPageTitle} › 编辑作业",
            Views.Pages.TimetableEditorPage => "设置 › 课表与提醒 › 课表编辑",
            _ => "设置 › 二级页",
        };

        SubHost.Content = panel;
        SubScroll.Visibility = Visibility.Visible;
        SubScroll.ScrollToVerticalOffset(0);
        // 子页里可能自带滚动区（作业编辑器之类）：手指要能拖
        TouchMode.EnableTouchScroll(FindDescendant<ScrollViewer>(SubHost));

        // 子页没有导航高亮（对齐 QML：navMarkerShown = False）
        NavLens.Visibility = Visibility.Collapsed;

        var w = PageStack.ActualWidth * ShiftRatio;
        _subOpen = true;

        // ★ 先把两张页摆到「起点位置」，让这一帧的布局（复杂面板首次测量/排列）
        //   单独跑完，下一帧才开始动 —— 否则首次布局和动画第一帧撞在一起，
        //   表现就是「进子页时先顿一下再滑」。
        ScrollCur.RenderTransform = new TranslateTransform(0, 0);
        SubScroll.RenderTransform = new TranslateTransform(w, 0);
        ScrollCur.CacheMode = new BitmapCache();
        SubScroll.CacheMode = new BitmapCache();

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (!_subOpen) return;      // 手快已经返回了，别再播
            // 旧页整段移出后要**收起来**：0.92 的比例会留下 8% 露在左边
            // （那 8% 是为了转场中间帧不留空白，落定后就不该再看见）
            Slide(ScrollCur, 0, 0, -w, 0, TransitionMs, () => ScrollCur.Visibility = Visibility.Collapsed);
            Slide(SubScroll, w, 0, 0, 0, TransitionMs);
        }));
    }

    public void CloseOverlay()
    {
        if (!_subOpen) return;
        HideScrollBarsDuringTransition();
        _subOpen = false;

        var w = PageStack.ActualWidth * ShiftRatio;
        ScrollCur.Visibility = Visibility.Visible;   // 返回时要看得见它滑回来
        Slide(SubScroll, 0, 0, w, 0, TransitionMs, () =>
        {
            SubScroll.Visibility = Visibility.Collapsed;
            SubHost.Content = null;
        });
        Slide(ScrollCur, -w, 0, 0, 0, TransitionMs);
        SyncMarker(_selectedNav, animate: false);
    }

    void OnSubBack(object sender, RoutedEventArgs e) => CloseOverlay();

    public Grid OverlayHost => DialogHost;

    public bool IsOverlayOpen => _subOpen;

    // ————————————————————— 窗口内确认框 —————————————————————

    /// <summary>
    /// 确认提示，画在设置窗口里（替代系统 MessageBox）。
    /// 危险操作前一律走这里 —— 用户的要求是"所有提示都在这个窗口里弹"。
    /// </summary>
    /// <param name="onCancel">
    /// 用户点「取消」/ ESC 时回调（可选）。用于「稍后再说」这类**需要留痕**的操作
    /// —— 只是关闭不给回调的话，没法把"用户跳过这个版本"写进设置。
    /// </param>
    public void Confirm(string title, string message, Action onOk,
                        string okText = "确定", string cancelText = "取消", Action? onCancel = null)
    {
        CloseConfirm();

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("EdTitle"),
            Margin = new Thickness(0, 0, 0, 6),
        });
        body.Children.Add(new TextBlock
        {
            Text = message,
            Style = (Style)FindResource("EdMessage"),
            TextWrapping = TextWrapping.Wrap,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };

        var cancel = new Button
        {
            Content = cancelText,
            Style = (Style)FindResource("FluentButton"),
            MinWidth = 88,
        };
        cancel.Click += (_, _) =>
        {
            var callback = _confirmOnCancel;
            CloseConfirm();
            callback?.Invoke();
        };

        var ok = new Button
        {
            Content = okText,
            Style = (Style)FindResource("AccentButton"),
            MinWidth = 88,
            Margin = new Thickness(8, 0, 0, 0),
        };
        ok.Click += (_, _) =>
        {
            var action = _confirmOnOk;
            CloseConfirm();
            action?.Invoke();
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        body.Children.Add(buttons);

        var card = new Border
        {
            Style = (Style)FindResource("EdPanel"),
            Width = 420,
            Padding = new Thickness(24, 20, 24, 20),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = body,
        };

        ConfirmHost.Children.Clear();
        ConfirmHost.Children.Add(card);
        ConfirmHost.Visibility = Visibility.Visible;
        _confirmOnOk = onOk;
        _confirmOnCancel = onCancel;

        // 提示框统一入场：从底边加速升上来（与独立弹窗同一套）
        UiMotion.SlideUpIn(card, maxDistance: 260);
        ok.Focus();
    }

    public void CloseConfirm()
    {
        _confirmOnOk = null;
        _confirmOnCancel = null;
        if (ConfirmHost.Visibility != Visibility.Visible) return;
        ConfirmHost.Children.Clear();
        ConfirmHost.Visibility = Visibility.Collapsed;
    }

    public bool IsConfirmOpen => ConfirmHost.Visibility == Visibility.Visible;

    string? _pendingPage;

    /// <summary>
    /// 测试进行中：锁住导航（作业页的自检会置位）。切页会把「测试中」的页面搬走、
    /// 测试数据留在 homework.json 里，所以这里直接不让切。
    /// </summary>
    public bool NavigationLocked { get; set; }

    /// <summary>
    /// 跳到某一页（插件调 OpenSettings("plugins") 时用）。
    /// 窗口还没 Loaded 时先记下，等 Layout 完成再跳 —— 否则 Select 会拿不到控件。
    /// </summary>
    public void NavigateTo(string pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId)) return;
        if (!IsLoaded)
        {
            _pendingPage = pageId;
            return;
        }
        _pendingPage = null;
        Select(pageId, animate: true);
    }

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

        // 轻推一下，提示条出现时不至于硬闪
        bar.RenderTransform = new TranslateTransform(0, 8);
        var anim = Anim.Slide(0, 180);
        if (anim is not null)
            bar.RenderTransform.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    public void ClearToasts() => ToastSlot.Children.Clear();
}
