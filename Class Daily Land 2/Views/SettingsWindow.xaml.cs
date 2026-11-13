using ClassDailyLand2.App.Controls;
using ClassDailyLand2.App.Theme;
using ClassDailyLand2.App.ViewModels;
using ClassDailyLand2.App.Views.Dialogs;
using ClassDailyLand2.App.Views.Pages;
using ClassDailyLand2.App.Win32;
using ClassDailyLand2.Core.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

using LiquidGlass.Core;

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

    // 「教学」大项里从列表提升成标签的四个页面（原来都是推进式二级页）
    public const string PageTimetable = "timetable";
    public const string PageHolidays = "holidays";
    public const string PageDuty = "duty";
    public const string PageTempAdjust = "temp-adjust";
    public const string PageStatusTest = "status-test";

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
    /// <summary>当前这排顶部标签是页面自带的（ISettingsPageTabs，如插件页的已安装/插件市场）。</summary>
    bool _subNavOwnedByTabs;
    DispatcherTimer? _transitionScrollBarTimer;
    /// <summary>上次写进 WindowChrome 的玻璃帧厚度（只在变化时重建 Chrome，避免闪屏）。</summary>
    Thickness _appliedGlassFrame = new Thickness(-1);
    Action? _confirmOnOk;
    Action? _confirmOnCancel;
    bool _subOpen;

    // 液态玻璃弹簧动画（NavLens/SubLens 位置与宽度）
    Spring? _navYSpring;
    Spring? _subXSpring;
    Spring? _subWSpring;
    DateTime _springLastTime;
    bool _springActive;

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
                // 下面四项原来是「课表与提醒」页里的列表入口（点开是推进式二级页），
                // 现在全部提升成平级标签：对话框内容不变，只换"外壳"（见 DialogPage）。
                new NavEntry(PageTimetable, "课表编辑", "\uE80A", () => new TimetableEditorPage()),
                new NavEntry(PageHolidays, "假期与调休", "\uE787", () => new DialogPage(vm => new HolidaysDialog(vm))),
                new NavEntry(PageDuty, "值日生名单", "\uE8FD", () => new DialogPage(vm => new DutyDialog(vm))),
                new NavEntry(PageTempAdjust, "临时调课", "\uE8AB", () => new DialogPage(vm => new TempAdjustDialog(vm))),
                new NavEntry(PageStatusTest, "状态测试", "\uE9D9", () => new DialogPage(vm => new StatusTestDialog(vm))),
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

        // 液态玻璃采样"身下的窗口内容"：整棵内容树（含 BackdropLayer 上的壁纸/底色、
        // 面板、卡片、文字），采样时会临时隐藏玻璃层自身避免自反馈。
        LiquidGlass.Rendering.Wpf.GlassBackdrop.SetSource(RootGrid, RootGrid);

        // 两块选中玻璃（侧栏 NavLens / 子页 SubLens）滑动时，折射层必须跟着重采样，
        // 否则玻璃里映的是上一格位置的背景。TranslateTransform.Changed 每个动画帧都会触发。
        // DX11 玻璃材质（代码设置，避免 DynamicResource 对非 Freezable 对象不生效）
        ApplyLensMaterial();
        // 玻璃开关（液态玻璃 / 背景板玻璃 / DX12）：先按设置应用一次
        ApplyGlassOptions();

        // 采样源：BackdropLayer（纯背景），不是 RootGrid（含文字）
        NavLensGlass.BackdropSource = BackdropLayer;
        SubLensGlass.BackdropSource = BackdropLayer;
        // ★ 不再给 BaseBrush 垫底色：云母下 BackdropLayer 是透明的，垫一块纯灰
        //   就是那块"玻璃下面的灰色小背景"。alpha 一路透传，玻璃只加薄白。

        LensShift.Changed += (_, _) => NavLensGlass.Refresh();
        SubLensShift.Changed += (_, _) => SubLensGlass.Refresh();

        // 首帧内容画完后重抓一次背景：启动早期抓到的快照里页面还是空的，
        // 玻璃会映出一片底色（看起来像实心块）。
        ContentRendered += (_, _) =>
        {
            LiquidGlass.Rendering.Wpf.GlassBackdrop.For(this)?.Invalidate();
            NavLensGlass.Refresh();
            SubLensGlass.Refresh();
        };
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
            // 弹簧动画进行中（含刚开始、还没出第一帧）都别去"校正"它。
            if (_springActive) return;
            SyncMarker(_selectedNav, animate: false);
        };

        // 顶部胶囊滚动 / 窗口缩放 / 标签重新排版时，选中底片跟着当前子项重新落位（不播动画）。
        // ★ 这里**不能**加 `if (_subLensShown)` 的门槛：换大项后新标签还没量出宽度时
        //   SyncSubNavLens 会先把底片收起来（_subLensShown = false），门槛一加，
        //   后面真正量完尺寸时反而没人来放它 —— 底片就此消失 / 停在旧位置。
        SubNavScroll.ScrollChanged += (_, _) =>
        {
            SyncSubNavLens(SelectedSubButton, animate: false);
            UpdateSubNavArrows();
        };
        SubNavPanel.SizeChanged += (_, _) =>
        {
            SyncSubNavLens(SelectedSubButton, animate: false);
            UpdateSubNavArrows();
        };
        // 箭头本身出现/消失会改变滚动区宽度，溢出状态要跟着重算一次
        SubNavScroll.SizeChanged += (_, _) => UpdateSubNavArrows();

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
                CancelConfirm();     // ESC = 点「取消」= 点遮罩空白处
                return;
            }
            if (DialogHost.Visibility == Visibility.Visible) CloseOverlay();
        };

        // 点确认框外面的空白（遮罩）也等于「取消」：只认"点在遮罩本体上"的，
        // 点在卡片里（含按钮、文字）时 OriginalSource 是卡片内部元素，不能当成关闭。
        ConfirmHost.MouseLeftButtonDown += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, ConfirmHost)) return;
            CancelConfirm();
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

        if (e.PropertyName is nameof(SettingsViewModel.LiquidGlass)
            or nameof(SettingsViewModel.GpuDx12))
            ApplyGlassOptions();
    }

    void ApplyGlassOptions()
    {
        // 液态玻璃（个性化页总开关）：开 = 选中胶囊用折射玻璃，关 = 普通样式
        var glass = Vm.LiquidGlass;

        // 关于页的 DX12 开关：交给渲染层决定后端（改了会重建共享管线）
        LiquidGlass.Rendering.DirectX.D3DGlassLayer.PreferDx12 = Vm.GpuDx12;

        // 普通胶囊的底色：Fluent 的选中填充（主题里有两个色号，缺了就退回 10% 黑）
        var plain = TryFindResource("TabSelectedBrush") as Brush
                    ?? new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0));
        foreach (var lens in new FrameworkElement[] { NavLens, SubLens })
            lens.SetValue(Border.BackgroundProperty, glass ? Brushes.Transparent : plain);

        NavLensGlass.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        SubLensGlass.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;

        if (glass)
        {
            NavLensGlass.Refresh();
            SubLensGlass.Refresh();
        }
        else
        {
            // 彻底放手 GPU：关掉时把设备释放掉，回到纯 WPF 绘制
            NavLensGlass.Shutdown();
            SubLensGlass.Shutdown();
        }
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

    /// <summary>弹簧动画统一调度：刷新玻璃位置/尺寸并触发 D3D 玻璃重采样。</summary>
    void StartSpring()
    {
        if (_springActive) { _springLastTime = DateTime.UtcNow; return; }
        _springActive = true;
        _springLastTime = DateTime.UtcNow;
        CompositionTarget.Rendering += OnSpringFrame;
    }

    void StopSpring()
    {
        if (!_springActive) return;
        _springActive = false;
        CompositionTarget.Rendering -= OnSpringFrame;
    }

    /// <summary>
    /// 作废顶部胶囊的弹簧。放在这里而不是各自写一行，是因为它必须和
    /// 「直接把 x/width 赋值落位」同时发生：弹簧一旦还活着，下一帧就会覆盖掉落位结果。
    /// </summary>
    void KillSubSprings()
    {
        _subXSpring = null;
        _subWSpring = null;
    }

    /// <summary>作废侧栏胶囊的弹簧（同上，落位前必须清）。</summary>
    void KillNavSpring() => _navYSpring = null;

    void OnSpringFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _springLastTime).TotalSeconds;
        _springLastTime = now;
        dt = Math.Clamp(dt, 0.0, 0.05); // 掉帧/调试时不爆

        var settled = true;
        if (_navYSpring is not null)
        {
            _navYSpring.Step(dt);
            LensShift.Y = _navYSpring.Value;
            if (!_navYSpring.Settled) settled = false;
        }
        // ★ 只在底片真的显示着的时候驱动它：换大项重建标签期间底片是收起状态，
        //   这时候往 SubLensShift / Width 里写值，就是在把上一页的位置硬塞给新一页。
        if (_subLensShown && _subXSpring is not null)
        {
            _subXSpring.Step(dt);
            SubLensShift.X = _subXSpring.Value;
            if (!_subXSpring.Settled) settled = false;
        }
        if (_subLensShown && _subWSpring is not null)
        {
            _subWSpring.Step(dt);
            SubLens.Width = _subWSpring.Value;
            if (!_subWSpring.Settled) settled = false;
        }

        if (settled) StopSpring();
    }

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

    /// <summary>
    /// 设置两块选中玻璃的材质。
    /// </summary>
    /// <remarks>
    /// 采样源是 <c>BackdropLayer</c>（只有背景、没有文字）。云母/亚克力下这一层是
    /// **透明**的 —— 背板由 DWM 绘制，WPF 拿不到像素（拿得到就等于截了桌面壁纸，
    /// 这是明确禁止的）。所以玻璃不能再垫一块底色（那就是历史上的"灰色小背景"），
    /// 只能：把背景 alpha 原样透传 + 加一层自己的薄白（SurfaceOpacity），
    /// 再加上折射带内稍厚一点的边缘（EdgeOpacity）读出玻璃厚度。
    ///
    /// 薄白的浓度按主题给：浅色底上要更白一点才看得出选中，深色底上同样的值会
    /// 糊成一块白贴纸。
    /// </remarks>
    void ApplyLensMaterial()
    {
        // 内部更透、边缘更厚，读出"透镜压缩"；云母下无真折射，靠自身薄白+边光立住形体。
        var solid = ThemeService.IsDark ? 0.10 : 0.14;
        var edge = 0.48;

        var nav = new LiquidGlass.Core.GlassParameters
        {
            CornerRadius = 28, RefractionPixels = 18, RefractionBand = 20,
            Dispersion = 0.06, RimStrength = 0, ShadowLift = 0.02,
            BlurSigma = 4, FillOpacity = 0.22, BackdropScale = 1.0,
            SurfaceOpacity = solid, EdgeOpacity = edge,
        };
        var sub = new LiquidGlass.Core.GlassParameters
        {
            CornerRadius = 26, RefractionPixels = 16, RefractionBand = 18,
            Dispersion = 0.05, RimStrength = 0, ShadowLift = 0.02,
            BlurSigma = 3.5, FillOpacity = 0.20, BackdropScale = 1.0,
            SurfaceOpacity = solid, EdgeOpacity = edge,
        };
        NavLensGlass.Material = nav;
        SubLensGlass.Material = sub;
    }

    void ApplyWindowChrome()
    {
        var mode = Vm.SettingsBackground;                     // mica / solid / image
        var imagePath = Vm.SettingsBackgroundImage;
        // 图片背景：仅图片模式用
        var useImage = !string.IsNullOrWhiteSpace(imagePath) && System.IO.File.Exists(imagePath)
                       && mode == "image";
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

        // 玻璃控件的采样层跟着窗口背景走（云母时是透明 → 折射层取不到像素，
        // 由玻璃自己的那层薄白兜底，不会画出灰块）
        BackdropLayer.Background = Background;

        LiquidGlass.Rendering.Wpf.GlassBackdrop.SetBaseBrush(RootGrid, BuildCaptureBaseBrush());

        // ★ 背景换了必须让玻璃重新采一次。XAML 里 BackdropLayer 的底色是
        //   SolidBackgroundFillColorBaseBrush（#F3F3F3），切成云母/亚克力后才变透明；
        //   而 D3D 玻璃只在 OnRender 时抓一次背景 —— 不重抓就会一直用启动时那张
        //   不透明的灰底快照，表现出来就是"胶囊底下一块灰色小背景"。
        ApplyLensMaterial();   // 深浅主题切换时薄白浓度也要跟着换
        NavLensGlass.Refresh();
        SubLensGlass.Refresh();

        if (kind == WindowBackdrop.None)
        {
            // 材质没生效：撤掉云母态覆盖，卡片/面板回到 App 级画刷
            Resources.Remove("CardBackgroundFillColorDefaultBrush");
            Resources.Remove("SolidBackgroundFillColorQuarternaryBrush");
            Resources.Remove("ControlFillColorDefaultBrush");
            Resources.Remove("ControlFillColorHoverBrush");

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
            Resources["ControlFillColorDefaultBrush"] = FindResource("AcrylicControlFillBrush");
            Resources["ControlFillColorHoverBrush"] = FindResource("AcrylicControlHoverBrush");
            Resources["HairlineHighlightBrush"] = Brushes.Transparent;
        }
        else
        {
            ContentPane.Background = (Brush)FindResource("MicaLayerBrush");
            Resources["CardBackgroundFillColorDefaultBrush"] = FindResource("MicaCardBackgroundBrush");
            Resources["ControlFillColorDefaultBrush"] = FindResource("MicaControlFillBrush");
            Resources["ControlFillColorHoverBrush"] = FindResource("MicaControlHoverBrush");
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

        // ★ 上一排标签留下的弹簧必须一起作废。
        //   OnSpringFrame 每一帧都会把 _subXSpring / _subWSpring 的值写回
        //   SubLensShift.X / SubLens.Width —— 新底片刚被 SyncSubNavLens 摆好，
        //   下一帧就被旧目标拽回上一个页面的标签位置，于是"玻璃悬在空白处"。
        KillSubSprings();

        // 横向滚动偏移也跟着标签一起换：上一组滚到右边留下的 offset 会原样保留，
        // 新一组的标签整体偏出可视区，顶栏看起来就是一片空白。
        SubNavScroll.ScrollToHorizontalOffset(0);

        SubNavPanel.Children.Clear();
        _subButtons.Clear();

        // 页面自己带顶部标签：例如插件页的「已安装 / 插件市场」
        if (page is ISettingsPageTabs host && host.Tabs.Count > 0)
        {
            _subNavOwnedByTabs = true;
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

        _subNavOwnedByTabs = false;
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
            {
                // 窗口窄、标签放不下时，选中的那个可能在可视区外（插件直接 NavigateTo 过来就会这样）
                current.BringIntoView();
                SyncSubNavLens(current, animate: false);
                UpdateSubNavArrows();
            }
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
    /// 顶部胶囊选中玻璃：在子页面标签之间左右滑动，用弹簧解析解驱动 Q 弹过冲。
    /// 位置按标签当前在屏幕上的实际位置算，滚动 / 缩放后也能重新对齐。
    /// </summary>
    void SyncSubNavLens(RadioButton? button, bool animate)
    {
        if (button is null || !button.IsVisible || button.ActualWidth < 1)
        {
            SubLens.Visibility = Visibility.Collapsed;
            _subLensShown = false;
            KillSubSprings();          // 收起时别留着弹簧在后面改位置
            return;
        }

        var point = button.TranslatePoint(new Point(0, 0), SubLensCanvas);
        var fromX = SubLensShift.X;
        var fromWidth = SubLens.ActualWidth > 0 ? SubLens.ActualWidth : SubLens.Width;
        var targetWidth = button.ActualWidth;

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

        if (!animate || (Math.Abs(point.X - fromX) < 0.5 && Math.Abs(targetWidth - fromWidth) < 0.5))
        {
            // 直接落位 = 不给弹簧留活口：否则它下一帧又把值写回旧目标
            KillSubSprings();
            SubLensShift.X = point.X;
            SubLens.Width = targetWidth;
            return;
        }

        // 弹簧：子页胶囊用 iOS release 弹簧（0.5s / bounce 0.42，约 10% 过冲）
        _subXSpring ??= new Spring(fromX, GlassSprings.Release);
        _subXSpring.Configure(GlassSprings.Release);
        _subXSpring.Target = point.X;

        _subWSpring ??= new Spring(fromWidth, GlassSprings.Release);
        _subWSpring.Configure(GlassSprings.Release);
        _subWSpring.Target = targetWidth;

        StartSpring();
    }

    // ————————————————————— 顶栏标签的左右翻页 —————————————————————

    void OnSubNavScrollLeft(object sender, RoutedEventArgs e) => ScrollSubNav(-1);

    void OnSubNavScrollRight(object sender, RoutedEventArgs e) => ScrollSubNav(+1);

    /// <summary>一次翻 0.62 屏：比整屏更容易看清"翻到哪了"，也不会翻过头。</summary>
    void ScrollSubNav(int direction)
    {
        var viewport = SubNavScroll.ViewportWidth;
        if (viewport <= 1) return;

        var offset = SubNavScroll.HorizontalOffset;
        var max = SubNavScroll.ScrollableWidth;
        var step = Math.Max(96, viewport * 0.62);
        var raw = offset + direction * step;

        // 这一下本来就够翻到底 → 目标记成"到底"。因为滚动过程中另一侧的箭头会冒出来，
        // 视口窄 30px、max 跟着变大；若把此刻的 max 死记成目标，最后会差这 30px 停住，
        // 右边箭头也就一直不消失（要点第二次才到底）。无限大交给每帧按当时的 max 收口。
        var target = raw >= max - 1 ? double.PositiveInfinity : Math.Clamp(raw, 0, max);
        GlideSubNav(target);
    }

    /// <summary>
    /// 逐帧把滚动位置推向目标（缓出）。用计时器而不是 DoubleAnimation，
    /// 是因为 HorizontalOffset 是只读的，动画得挂在一个中间依赖属性上；
    /// 而那个写法有个要命的坑：动画结束时 BeginAnimation(null) 会让该属性回落到基准值，
    /// 于是滚完 / 点一下别的东西就被拽回 0 —— 表现就是"回弹，然后箭头怎么点都没反应"。
    /// 这里一帧一帧直接改 offset，ScrollChanged 照常驱动胶囊跟随。
    /// </summary>
    void GlideSubNav(double target)
    {
        _subNavGlideTarget = target;

        if (_subNavGlideTimer is not null)
        {
            _subNavGlideTimer.Start();
            return;
        }

        _subNavGlideTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _subNavGlideTimer.Tick += (_, _) =>
        {
            // 每帧都按"此刻"的 max 收口：视口宽度会因为箭头出现/消失而变
            var goal = Math.Clamp(_subNavGlideTarget, 0, SubNavScroll.ScrollableWidth);
            var current = SubNavScroll.HorizontalOffset;
            var delta = goal - current;

            if (Math.Abs(delta) < 0.7)
            {
                SubNavScroll.ScrollToHorizontalOffset(goal);
                _subNavGlideTimer!.Stop();
                UpdateSubNavArrows();
                return;
            }

            // 0.22 的插值 ≈ 200ms 落定，末尾自然变慢
            SubNavScroll.ScrollToHorizontalOffset(current + delta * 0.22);
            UpdateSubNavArrows();
        };
        _subNavGlideTimer.Start();
    }

    DispatcherTimer? _subNavGlideTimer;
    double _subNavGlideTarget;

    /// <summary>
    /// 只有真的放不下才露出箭头；滚到两头，对应那个方向的箭头就收起。
    /// 由 ScrollChanged / SizeChanged 调用，所以窗口拉宽拉窄会自动跟着变。
    /// </summary>
    void UpdateSubNavArrows()
    {
        var overflow = SubNavScroll.ScrollableWidth > 0.5;
        var offset = SubNavScroll.HorizontalOffset;

        SubNavLeft.Visibility = overflow && offset > 0.5
            ? Visibility.Visible : Visibility.Collapsed;
        SubNavRight.Visibility = overflow && offset < SubNavScroll.ScrollableWidth - 0.5
            ? Visibility.Visible : Visibility.Collapsed;
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
    /// 位移只动 Transform，并用弹簧解析解驱动 Q 弹过冲。
    /// </summary>
    void SyncMarker(RadioButton? button, bool animate)
    {
        if (button is null || !button.IsVisible || button.ActualWidth < 1)
        {
            NavLens.Visibility = Visibility.Collapsed;
            _markerShown = false;
            KillNavSpring();
            return;
        }

        var point = button.TranslatePoint(new Point(0, 0), LensCanvas);
        var fromY = LensShift.Y;

        // 尺寸跟着条目走：触屏模式下条目会变高，玻璃跟着一起长
        var maxWidth = LensCanvas.ActualWidth - 12;
        NavLens.Width = maxWidth > 32 ? Math.Min(button.ActualWidth, maxWidth) : button.ActualWidth;
        NavLens.Height = button.ActualHeight;
        LensShift.X = point.X;

        LensShift.BeginAnimation(TranslateTransform.YProperty, null);
        NavLens.Visibility = Visibility.Visible;

        if (!_markerShown)
        {
            // 首次出现：直接落位，别从 0 滑下来
            KillNavSpring();
            LensShift.Y = point.Y;
            _markerShown = true;
            return;
        }

        if (!animate || Math.Abs(point.Y - fromY) < 0.5)
        {
            KillNavSpring();
            LensShift.Y = point.Y;
            return;
        }

        // 弹簧：侧栏导航用 layout 弹簧（0.4s / bounce 0.16，轻微 Q 弹但不晃）
        _navYSpring ??= new Spring(fromY, GlassSprings.Layout);
        _navYSpring.Configure(GlassSprings.Layout);
        _navYSpring.Target = point.Y;
        StartSpring();
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
        // 页面切换 → 玻璃折射的背景要重抓
        LiquidGlass.Rendering.Wpf.GlassBackdrop.For(this)?.Invalidate();
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
        // 提示条现在挂在窗口右下角（窗口级），切页不再清空 —— 让它按 5 秒自己收
        ScrollCur.ScrollToVerticalOffset(0);

        groupButton.IsChecked = true;
        _selectedNav = groupButton;
        SyncMarker(groupButton, animate);

        // 先记下当前页：后面胶囊的 SizeChanged / ScrollChanged 都按新页对齐
        _currentId = id;

        // 同一个大项内切页：只改胶囊选中态；换大项才重建胶囊。
        // ★ 页面自带顶部标签（插件页的已安装/插件市场）时必须重建：此时
        //   SubNavPanel 里还是上一个页面的按钮，只改选中态会全部落空。
        // ★ 反过来，从"自带标签的页面"切回普通页面时同样必须重建：否则
        //   SubNavPanel 里留着上一个页面的两个标签，新页在 _subButtons 里查不到，
        //   底片就停在旧标签的位置 —— 用户看到的就是"旧页面的东西还在、玻璃跑到空白处"。
        var groupChanged = !string.Equals(_currentGroupId, group.Id, StringComparison.Ordinal);
        _currentGroupId = group.Id;
        var ownsTabs = page is ISettingsPageTabs tabsPage && tabsPage.Tabs.Count > 0;
        if (groupChanged || SubNavPanel.Children.Count == 0 || ownsTabs || _subNavOwnedByTabs)
        {
            BuildSubNav(group, id, page);
        }
        else
        {
            // 同一大项内切子页：底片左右滑（ISettingsPageTabs 页面已由上面强制重建）
            _currentSubId = id;
            SyncSubNavSelection(_currentSubId);
            if (_subButtons.TryGetValue(_currentSubId, out var subButton))
            {
                // 窗口窄、标签被滚到视野外时（例如插件直接 NavigateTo 过来的那一页），
                // 选中了就得把它带回可视区，否则"页面换了、顶栏却看不见选的是哪个"。
                subButton.BringIntoView();
                SyncSubNavLens(subButton, animate: true);
            }
            else
                SyncSubNavLens(null, animate: false);   // 找不到对应标签：宁可收起，也别留在上一格
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
    /// 打开「课表编辑」（主窗右键菜单、托盘、插件 ShowSettings("timetable") 都走这里）。
    /// 它现在是「教学」大项下的一个平级标签，不再是推进式二级页 —— 直接切过去就是了。
    /// </summary>
    public void OpenTimetableEditor() => NavigateTo(PageTimetable);

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
            // ★ 回调里再验一次 _subOpen，注意极性：二级页**开着**时 _subOpen 才是 true。
            //   开着 → 该把底下的旧页收掉；已经被关掉/被别的导航打断 → 那页正在重新使用，
            //   绝不能收（收了就是整页空白）。
            //   写反了的表现：旧页一直留在屏幕左侧（横向平移了 92% 页宽），
            //   右边缘在内容区拉出一条竖线 + 边上一截旧页面的残影。
            Slide(ScrollCur, 0, 0, -w, 0, TransitionMs, () =>
            {
                if (_subOpen) ScrollCur.Visibility = Visibility.Collapsed;
            });
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
            // 这中间可能已经被别的导航重开了二级页：那就别把新页收掉
            if (_subOpen) return;
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
        // 连弹两张时：上一张直接清掉，别让"收起动画"和"进场动画"打架
        CloseConfirm(animate: false);

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
        cancel.Click += (_, _) => CancelConfirm();

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
            // ★ 确认框不透明：EdPanel 的底色本来就实心，这里再兜一层
            //   （曾经有透明主题变体把 Quarternary 换成带 alpha 的，框就"透"了）
            Background = OpaqueBrush("SolidBackgroundFillColorQuarternaryBrush"),
        };

        ConfirmHost.Children.Clear();
        ConfirmHost.Children.Add(card);
        ConfirmHost.Visibility = Visibility.Visible;
        _confirmOnOk = onOk;
        _confirmOnCancel = onCancel;

        // 从窗口底部升上来：先量出卡片高度，位移 = 半个遮罩高 + 半张卡 + 一点余量，
        // 正好让整块起点落在窗口下沿之外（不然只是"原地往上挪一截"，不像从底下弹出来）。
        card.Measure(new Size(420, double.PositiveInfinity));
        var cardHeight = card.DesiredSize.Height > 1 ? card.DesiredSize.Height : 180;
        var hostHeight = ConfirmHost.ActualHeight > 1 ? ConfirmHost.ActualHeight : 600;
        UiMotion.SlideUpIn(card, durationMs: ConfirmMotionMs,
                           fromBottom: hostHeight / 2 + cardHeight / 2 + 8);
        ok.Focus();
    }

    /// <summary>
    /// 确认框进/出场的时长：1 秒（用户指定，进出一样长）。
    /// 唯一例外是设置里把动画关掉了 —— 那时候仍然直接落位，不然"关动画"就等于没关。
    /// </summary>
    static int ConfirmMotionMs
        => UiMotion.DurationProvider is { } provider && provider() <= 0 ? 0 : 1000;

    /// <summary>取一个主题刷子，但强制不透明（alpha 拉满）。</summary>
    Brush OpaqueBrush(string key)
    {
        var brush = FindResource(key) as Brush;
        return brush is SolidColorBrush solid
            ? new SolidColorBrush(Color.FromRgb(solid.Color.R, solid.Color.G, solid.Color.B))
            : brush ?? Brushes.White;
    }

    /// <summary>
    /// 「取消」语义：ESC、点取消按钮、点遮罩空白处三条路都走这里。
    /// 取消也要回调 —— 确认框里可能有「稍后再说」这种需要留痕的动作。
    /// </summary>
    void CancelConfirm()
    {
        var callback = _confirmOnCancel;
        CloseConfirm();
        callback?.Invoke();
    }

    public void CloseConfirm() => CloseConfirm(animate: true);

    /// <summary>
    /// 收起确认框。默认**往下滑出窗口**（和进场同一套缓动），滑完再摘掉节点。
    /// <paramref name="animate"/>=false 时立刻清掉 —— 比如 Confirm() 自己要先关上一张再开下一张。
    /// </summary>
    void CloseConfirm(bool animate)
    {
        _confirmOnOk = null;
        _confirmOnCancel = null;
        if (ConfirmHost.Visibility != Visibility.Visible) return;

        var card = ConfirmHost.Children.OfType<FrameworkElement>().FirstOrDefault();

        if (!animate || card is null)
        {
            ConfirmHost.Children.Clear();
            ConfirmHost.Visibility = Visibility.Collapsed;
            return;
        }

        card.Measure(new Size(420, double.PositiveInfinity));
        var cardHeight = card.DesiredSize.Height > 1 ? card.DesiredSize.Height : 180;
        var hostHeight = ConfirmHost.ActualHeight > 1 ? ConfirmHost.ActualHeight : 600;

        UiMotion.SlideDownOut(card, hostHeight / 2 + cardHeight / 2 + 8, () =>
        {
            // 收起途中可能已经弹了下一张：只清自己这一张
            if (!ConfirmHost.Children.Contains(card)) return;
            ConfirmHost.Children.Clear();
            ConfirmHost.Visibility = Visibility.Collapsed;
        }, durationMs: ConfirmMotionMs);
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
        // 页面切换 → 背景内容变了，玻璃里的折射要跟着更新
        LiquidGlass.Rendering.Wpf.GlassBackdrop.For(this)?.Invalidate();
        if (!IsLoaded)
        {
            _pendingPage = pageId;
            return;
        }
        _pendingPage = null;
        Select(pageId, animate: true);
    }

    /// <summary>
    /// 采样合成图的"底层"：内容里所有透明区域透出来的那张图。
    /// </summary>
    /// <remarks>
    /// 只用**软件自己的背景**（窗口背景 / 主题底色），绝不引入桌面壁纸 ——
    /// 玻璃要折射的是"我这个窗口身下的东西"，不是用户桌面上的东西。
    /// 云母/亚克力的背板在 DWM 里、WPF 截不到像素，此时用主题底色近似
    /// （那正是云母在视觉上给出的基调），而不是去抓壁纸。
    /// </remarks>
    Brush BuildCaptureBaseBrush()
    {
        if (Background is SolidColorBrush sc && sc.Color.A > 0) return sc;
        if (Background is ImageBrush) return Background;
        return FindResource("SolidBackgroundFillColorBaseBrush") as Brush
               ?? Brushes.White;
    }

    // ————————————————————— 提示条（右下角） —————————————————————

    /// <summary>提示条宽。</summary>
    const double ToastWidth = 380;

    /// <summary>停留时间，到点自动往右收起。</summary>
    static readonly TimeSpan ToastDwell = TimeSpan.FromSeconds(5);

    /// <summary>一条提示（相同内容会合并到同一条里，只把计数加上去）。</summary>
    sealed class ToastEntry
    {
        public Grid Slot = null!;
        public Border Card = null!;
        public Border CountBadge = null!;
        public TextBlock CountText = null!;
        public TranslateTransform Nudge = null!;
        public DispatcherTimer Timer = null!;
        public string Key = "";
        public int Count = 1;
    }

    readonly List<ToastEntry> _toasts = new();

    /// <summary>
    /// 右下角提示条：从右边滑进来、往上堆，5 秒后自动向右收起。
    ///
    /// 两条规则：
    ///   · **内容相同**（严重级别 + 标题 + 正文都一样）不再叠一条，直接把这条的计数 +1 并重新计时
    ///     —— 连点几次「同步市场」不该刷出一墙一模一样的条；
    ///   · 内容不同才另起一条，新条压在下面，旧条往上让。
    /// </summary>
    public void Toast(InfoBarSeverity severity, string title, string message)
    {
        var key = $"{severity}|{title}|{message}";

        var same = _toasts.FirstOrDefault(t => t.Key == key);
        if (same is not null)
        {
            same.Count++;
            same.CountText.Text = $"×{same.Count}";
            same.CountBadge.Visibility = Visibility.Visible;
            same.Timer.Stop();
            same.Timer.Start();          // 合并一次就重新计 5 秒
            PulseToast(same);
            return;
        }

        var entry = BuildToast(severity, title, message, key);
        _toasts.Add(entry);
        ToastStack.Children.Add(entry.Slot);
        PlayToastIn(entry);
    }

    /// <summary>
    /// 把文案压成一行：换行/连续空白折叠成一个空格，超长截断。
    /// 调用方（尤其插件）经常把一整段说明塞进来，提示条接不住也没必要接。
    /// </summary>
    static string OneLine(string? raw, int max = 90)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    /// <summary>按内容造一张提示卡（图标章 + 标题/正文 + 计数 + 关闭）。</summary>
    ToastEntry BuildToast(InfoBarSeverity severity, string title, string message, string key)
    {
        var (accentKey, glyphKey) = severity switch
        {
            InfoBarSeverity.Success => ("SystemFillColorSuccessBrush", "Icon.Success"),
            InfoBarSeverity.Warning => ("SystemFillColorCautionBrush", "Icon.Warning"),
            InfoBarSeverity.Error => ("SystemFillColorCriticalBrush", "Icon.Error"),
            _ => ("SystemFillColorAttentionBrush", "Icon.Info"),
        };

        // —— 卡片本体：12 圆角、1px 细边、一层很轻的落影（提示条要"浮"在内容上） ——
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 9, 6, 9),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.16,
                Color = Colors.Black,
            },
        };
        card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorQuarternaryBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 图标
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 计数
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 关闭

        // 图标章：一圈同色淡底 + 同色描边图标（比原来那个实心圆图标轻）
        var chip = new Grid { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        var tint = new Border { CornerRadius = new CornerRadius(11), Opacity = 0.16 };
        tint.SetResourceReference(Border.BackgroundProperty, accentKey);
        chip.Children.Add(tint);

        var tick = new Icon
        {
            Size = 12,
            StrokeThickness = 1.9,
            Data = (Geometry)FindResource(glyphKey),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tick.SetResourceReference(Control.ForegroundProperty, accentKey);
        chip.Children.Add(tick);
        Grid.SetColumn(chip, 0);
        row.Children.Add(chip);

        // 文字：标题一行（超长省略），正文最多两行
        var texts = new StackPanel
        {
            Margin = new Thickness(10, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 一律单行 + 尾部省略：提示条只有"扫一眼"这一个用途，
        // 让它换行 = 卡片高度乱跳，长文案还会被裁得看不清（就是上一版那个样子）。
        var titleBlock = new TextBlock
        {
            Text = OneLine(title),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        titleBlock.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeBody");
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        texts.Children.Add(titleBlock);

        if (!string.IsNullOrWhiteSpace(message))
        {
            var messageBlock = new TextBlock
            {
                Text = OneLine(message),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0),
            };
            messageBlock.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
            messageBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            texts.Children.Add(messageBlock);
        }

        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);

        // 计数徽标：只在合并时才出现（×2 / ×3 …）
        var countText = new TextBlock
        {
            Text = "×2",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        countText.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
        countText.SetResourceReference(TextBlock.ForegroundProperty, accentKey);

        var countBadge = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 0, 7, 1),
            Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = countText,
        };
        countBadge.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        Grid.SetColumn(countBadge, 2);
        row.Children.Add(countBadge);

        // 关闭：26 见方的图标钮（SubtleButton 基础上收窄，不带文字）
        var close = new Button
        {
            Style = (Style)FindResource("SubtleButton"),
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "关闭",
        };
        var cross = new Icon
        {
            Size = 9,
            StrokeThickness = 1.5,
            Data = (Geometry)FindResource("Icon.Close"),
        };
        cross.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
        close.Content = cross;
        Grid.SetColumn(close, 3);
        row.Children.Add(close);

        card.Child = row;

        // 位移分两层：外面那层做"滑入/滑出"，里面那层做合并时的轻微上顶
        var nudge = new TranslateTransform(0, 0);
        var slide = new TranslateTransform(ToastWidth, 0);
        var group = new TransformGroup();
        group.Children.Add(nudge);
        group.Children.Add(slide);
        card.RenderTransform = group;

        // 每条外面套一格：滑出去时裁掉越界部分，收起时把这一格高度收到 0
        var slot = new Grid
        {
            Margin = new Thickness(0, 6, 0, 0),
            ClipToBounds = true,
        };
        slot.Children.Add(card);

        var timer = new DispatcherTimer { Interval = ToastDwell };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            DismissToast(null, slot, slideOut: true);
        };

        // ★ 关掉按钮：这里的 slot 是刚建好的这一格，用 slot 反查条目（entry 还没造出来）
        close.Click += (_, _) => DismissToast(null, slot, slideOut: false);

        var toast = new ToastEntry
        {
            Slot = slot,
            Card = card,
            CountBadge = countBadge,
            CountText = countText,
            Nudge = nudge,
            Timer = timer,
            Key = key,
            Count = 1,
        };

        // ★ 必须真的启动：DispatcherTimer 造出来是停着的。
        //   漏了这一行 = 提示条永远不收（用户看到的就是"5 秒了还在"）。
        timer.Start();
        return toast;
    }

    /// <summary>滑入：从右边界外滑到 0。</summary>
    void PlayToastIn(ToastEntry entry)
    {
        if (entry.Card.RenderTransform is not TransformGroup group ||
            group.Children.Count < 2 ||
            group.Children[1] is not TranslateTransform slide)
            return;

        var anim = Anim.Slide(0, 260);
        if (anim is not null) slide.BeginAnimation(TranslateTransform.XProperty, anim);
        else slide.X = 0;
    }

    /// <summary>合并时的反馈：轻轻上顶再落回（不改透明度，不抢滑入那层）。</summary>
    static void PulseToast(ToastEntry entry)
    {
        var nudge = entry.Nudge;
        nudge.BeginAnimation(TranslateTransform.YProperty, null);
        nudge.Y = -4;
        var settle = Anim.Slide(0, 240);
        if (settle is not null)
            nudge.BeginAnimation(TranslateTransform.YProperty, settle);
        else
            nudge.Y = 0;
    }

    /// <summary>收掉一条：先往右滑出去，再把这一格的高度收到 0（下面的顺势上移，不跳）。</summary>
    void DismissToast(ToastEntry? entry, Grid slot, bool slideOut)
    {
        var live = entry ?? _toasts.FirstOrDefault(t => t.Slot == slot);
        if (live is null) return;                 // 已经收过了
        if (!_toasts.Remove(live)) return;

        live.Timer.Stop();

        if (!slideOut || live.Card.RenderTransform is not TransformGroup group ||
            group.Children.Count < 2 || group.Children[1] is not TranslateTransform slide)
        {
            CollapseToastSlot(live.Slot);
            return;
        }

        var target = live.Slot.ActualWidth > 1 ? live.Slot.ActualWidth : ToastWidth;
        var outAnim = Anim.Slide(target, 200);
        if (outAnim is null)
        {
            CollapseToastSlot(live.Slot);
            return;
        }

        outAnim.Completed += (_, _) => CollapseToastSlot(live.Slot);
        slide.BeginAnimation(TranslateTransform.XProperty, outAnim);
    }

    /// <summary>把提示条那一格的高度动画到 0，动画结束后整格摘掉。</summary>
    void CollapseToastSlot(Grid slot)
    {
        var collapse = Anim.Slide(0, 170);
        if (collapse is null)
        {
            ToastStack.Children.Remove(slot);
            return;
        }

        collapse.Completed += (_, _) => ToastStack.Children.Remove(slot);

        // 先钉成当前实际高度，否则 Height 从 NaN 动画到 0 会先跳一下
        slot.Height = slot.ActualHeight;
        slot.Margin = new Thickness(0);
        slot.BeginAnimation(FrameworkElement.HeightProperty, collapse);
    }

    /// <summary>清空所有提示条（关窗 / 显式调用）。</summary>
    public void ClearToasts()
    {
        foreach (var entry in _toasts.ToList())
            DismissToast(entry, entry.Slot, slideOut: false);
    }
}
