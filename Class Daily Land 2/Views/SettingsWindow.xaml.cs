using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClassDailyLand.App.Controls;
using ClassDailyLand.App.Theme;
using ClassDailyLand.App.Win32;
using ClassDailyLand.App.ViewModels;
using ClassDailyLand.App.Views.Pages;
using ClassDailyLand.Core.Models;

namespace ClassDailyLand.App.Views;

/// <summary>
/// 设置中心（对齐 QML 版那套 Windows 11 设置）：
/// 1232×762、48px 标题栏、288px 固定侧栏（**不可折叠**，Win11 设置里没有汉堡）、
/// 内容区左右各 28 内边距、内容列 900。
///
/// 三处动画都从 QML 版搬过来：
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

    /// <summary>强调条滑动时长。</summary>
    const int MarkerMs = 200;

    readonly List<NavEntry> _entries;
    readonly Dictionary<string, UserControl> _pages = new(StringComparer.Ordinal);
    readonly Dictionary<string, RadioButton> _navButtons = new(StringComparer.Ordinal);
    RadioButton? _selectedNav;
    string _currentId = "";
    Action? _confirmOnOk;
    bool _subOpen;

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
            new(AppSettings.PageMedia, "媒体", "\uE8D6", () => new MediaPage()),
            new(AppSettings.PageDisaster, "灾害预警", "\uE730", () => new DisasterPage()),
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

        // 手指滑动：无条件开。设置页是最常被触摸操作的地方，
        // 只有滚轮能滚的界面在平板上等于"卡死"。
        TouchMode.EnableTouchScroll(ScrollCur);
        TouchMode.EnableTouchScroll(SubScroll);
        TouchMode.EnableTouchScroll(NavScroll);

        // 点击目标大小：按设置决定
        ApplyTouchOptimize();

        SourceInitialized += (_, _) => ApplyWindowChrome();
        // 主窗是贴桌面边缘的挂件，和设置窗口都不置顶时很容易压在上面（会露出半截作业列表）
        ContentRendered += (_, _) => Interop.RaiseToFront(this);
        Loaded += (_, _) => Select(_pendingPage ?? PageHome, animate: false);
        NavScroll.ScrollChanged += (_, _) =>
        {
            // 动画进行中时别硬拉回原位（那一下就是"卡"的观感来源）
            if (!MarkerShift.HasAnimatedProperties) SyncMarker(_selectedNav, animate: false);
        };

        StateChanged += (_, _) => UpdateMaximizeIcon();
        ThemeService.Changed += OnThemeChanged;
        Vm.PropertyChanged += OnVmChanged;
        Closed += (_, _) =>
        {
            ThemeService.Changed -= OnThemeChanged;
            Vm.PropertyChanged -= OnVmChanged;
            Vm.Detach();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (ConfirmHost.Visibility == Visibility.Visible) { CloseConfirm(); return; }
            if (DialogHost.Visibility == Visibility.Visible) CloseOverlay();
        };
    }

    // ————————————————————— 窗口外观（云母 / 圆角） —————————————————————

    void OnThemeChanged() => ApplyWindowChrome();

    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.SettingsMica) or nameof(SettingsViewModel.ThemeMode))
            ApplyWindowChrome();
        if (e.PropertyName is nameof(SettingsViewModel.TouchOptimize))
            ApplyTouchOptimize();
    }

    /// <summary>
    /// 触屏优化：放大点击目标 + 开手指拖动滚动。切换即时生效，不需要重开窗口。
    ///
    /// 尺寸走窗口资源覆盖（模板里是 DynamicResource，改完立刻重解析）；
    /// 导航项还要额外补两点：项间距跟着加大，强调条位置重算（行高变了）。
    /// </summary>
    void ApplyTouchOptimize()
    {
        var on = Vm.TouchOptimize;
        TouchMode.Apply(this, on);

        foreach (var button in _navButtons.Values)
            button.Margin = new Thickness(0, on ? 5 : 2, 0, 0);

        // 注意：滑动能力在上面已经无条件开好了，这里只管尺寸 ——
        // 触屏用户关掉这个开关也依然能上下滑。
        TouchMode.EnableTouchScroll(FindDescendant<ScrollViewer>(SubHost));

        // 行高变了 → 强调条要重新对齐（不等下一次切页才正位）
        if (IsLoaded) SyncMarker(_selectedNav, animate: false);
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
    void ApplyWindowChrome()
    {
        // Win11 → 云母（Mica）；Win10 1803+ → 亚克力（Acrylic）；更老/关掉 → 不透明。
        // 圆角不在这里做：Win11 由 DWM 自己加，Win10 原生就是直角。
        var kind = Interop.ApplyBackdrop(this, Vm.SettingsMica, ThemeService.IsDark);

        if (kind == WindowBackdrop.None)
        {
            Background = (Brush)FindResource("SolidBackgroundFillColorBaseBrush");
            TitleBar.Background = Background;
            NavPane.Background = Background;
            ContentPane.Background = (Brush)FindResource("SolidBackgroundFillColorTertiaryBrush");
            return;
        }

        // 云母 / 亚克力：窗口和侧栏都透明，让背板透上来，内容区只压一层半透明
        Background = Brushes.Transparent;
        TitleBar.Background = Brushes.Transparent;
        NavPane.Background = Brushes.Transparent;
        ContentPane.Background = (Brush)FindResource("LayerFillColorDefaultBrush");
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

    /// <summary>
    /// 把强调条挪到高亮项上。
    /// 位置用 TranslatePoint 算到 MarkerCanvas 坐标系 —— 导航项可能在滚动区里，
    /// 也可能在底部固定的「关于」面板里，只有相对画布算才两边都对。
    /// </summary>
    void SyncMarker(RadioButton? button, bool animate)
    {
        if (button is null || !button.IsVisible)
        {
            NavMarker.Visibility = Visibility.Collapsed;
            _markerShown = false;
            return;
        }
        var point = button.TranslatePoint(new Point(0, 0), MarkerCanvas);
        var target = point.Y + (button.ActualHeight - NavMarker.Height) / 2;

        // 起点取「当前真正在显示的值」：动画进行中时 HasAnimatedProperties 为真，
        // 这时 MarkerShift.Y 读到的就是动画中间值，打断后能从原地继续滑，不会跳。
        var from = MarkerShift.Y;
        var interrupted = MarkerShift.HasAnimatedProperties;

        MarkerShift.BeginAnimation(TranslateTransform.YProperty, null);
        MarkerShift.Y = _markerShown ? from : target;

        NavMarker.Visibility = Visibility.Visible;
        _markerShown = true;

        if (NavMarker.Opacity < 1)
        {
            NavMarker.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(140))));
        }

        if (!animate || Math.Abs(target - MarkerShift.Y) < 0.5)
        {
            MarkerShift.Y = target;
            return;
        }

        // 位移用 Transform 而不是 Canvas.Top：Canvas.Top 是布局属性，
        // 每帧都会触发父级重新排列，滑动看着就会一顿一顿的。
        var anim = Anim.Slide(target, MarkerMs);
        if (anim is not null)
        {
            if (interrupted) anim.EasingFunction = Anim.Ease("EaseStandard");
            MarkerShift.BeginAnimation(TranslateTransform.YProperty, anim);
        }
        else MarkerShift.Y = target;
    }

    /// <summary>强调条是否已经显示过（首次出现不要从 0 滑下来）。</summary>
    bool _markerShown;

    int IndexOf(string id)
    {
        for (var i = 0; i < _entries.Count; i++)
            if (_entries[i].Id == id) return i;
        return -1;
    }

    /// <summary>
    /// 这一跳该往哪个方向动（对位 QML 的 motionBetween）：
    ///   down —— 目标在下方的顶层页；up —— 目标在上方；
    ///   forward —— 首屏 / 目标不是顶层页。
    /// </summary>
    string DirectionFor(string prev, string next)
    {
        if (string.IsNullOrEmpty(prev)) return "forward";
        var a = IndexOf(prev);
        var b = IndexOf(next);
        if (a >= 0 && b >= 0) return b > a ? "down" : "up";
        return "forward";
    }

    void Select(string id) => Select(id, animate: true);

    void Select(string id, bool animate)
    {
        if (!_navButtons.TryGetValue(id, out var button)) return;
        if (_currentId == id && PageHost.Content is not null) return;

        if (_subOpen)
        {
            _subOpen = false;
            SubScroll.Visibility = Visibility.Collapsed;
            SubHost.Content = null;
            ScrollCur.Visibility = Visibility.Visible;
            ScrollCur.RenderTransform = Transform.Identity;
        }

        var direction = DirectionFor(_currentId, id);

        if (!_pages.TryGetValue(id, out var page))
        {
            var entry = _entries.First(e => e.Id == id);
            page = entry.Create();
            page.DataContext = Vm;
            _pages[id] = page;
            if (page is HomePage home) home.NavigateRequested += Select;
        }

        var previous = PageHost.Content as UIElement;
        PageHost.Content = page;
        ToastSlot.Children.Clear();
        ScrollCur.ScrollToVerticalOffset(0);

        button.IsChecked = true;
        _selectedNav = button;
        SyncMarker(button, animate);

        if (animate && previous is not null) PlayPageTransition(previous, direction);
        else ClearPreviousLayer();

        _currentId = id;
    }

    /// <summary>
    /// 整段平移：旧页往反方向整段移出、新页从反侧整段移入。
    /// 透明度一律不碰（恒为 1）—— 淡化会让两页同时半透明地叠着，"糊在一起"。
    /// </summary>
    void PlayPageTransition(UIElement oldPage, string direction)
    {
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
    public void ShowOverlay(FrameworkElement panel)
    {
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
            _ => "设置 › 二级页",
        };

        SubHost.Content = panel;
        SubScroll.Visibility = Visibility.Visible;
        SubScroll.ScrollToVerticalOffset(0);

        // 子页没有导航高亮（对齐 QML：navMarkerShown = False）
        NavMarker.Visibility = Visibility.Collapsed;

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
    public void Confirm(string title, string message, Action onOk,
                        string okText = "确定", string cancelText = "取消")
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
        cancel.Click += (_, _) => CloseConfirm();

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

        Slide(card, 0, 12, 0, 0, 180);
        ok.Focus();
    }

    public void CloseConfirm()
    {
        _confirmOnOk = null;
        if (ConfirmHost.Visibility != Visibility.Visible) return;
        ConfirmHost.Children.Clear();
        ConfirmHost.Visibility = Visibility.Collapsed;
    }

    public bool IsConfirmOpen => ConfirmHost.Visibility == Visibility.Visible;

    string? _pendingPage;

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
