using ClassDailyLand2.App.Theme;
using ClassDailyLand2.App.Win32;
using ClassDailyLand2.Core;
using ClassDailyLand2.Core.Managers;
using ClassDailyLand2.Core.Models;
using ClassDailyLand2.Core.Plugins;
using ClassDailyLand2.Plugin;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;

namespace ClassDailyLand2.App.Windows;

/// <summary>
/// 主窗口：无边框、贴屏幕右侧、顶部对齐。
/// 版式对齐原版 PySide6：**无卡片背景**，纯文字列 ——
/// 标题「班级日常」/「今日值日生：X」/「应到 N 人 · 实到 M 人」/ 作业（按学科分组，组内编号）。
/// </summary>
public sealed class MainBoardWindow : Window
{
    const double PadX = 12;
    const double PadY = 8;
    const double Spacing = 4;

    readonly Border _root = new();
    readonly StackPanel _stack = new() { Orientation = Orientation.Vertical };
    readonly TextBlock _title = new();
    readonly TextBlock _dutyLabel = new();
    readonly TextBlock _attendanceLabel = new();
    readonly TextBlock _homeworkEmpty = new();
    readonly WrapPanel _titleRow = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel _badgePanel = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel _pluginSections = new() { Orientation = Orientation.Vertical };
    readonly StackPanel _homeworkPanel = new() { Orientation = Orientation.Vertical };
    readonly ScrollViewer _homeworkScroll = new();

    readonly DispatcherTimer _scrollTimer = new();

    double _fontSize = 16;
    double _widthRatio = 0.22;
    int _scrollStep = 24;

    /// <summary>自动滚动方向：1 = 向下，-1 = 向上（到底/到顶就掉头，来回滚）。</summary>
    int _scrollDirection = 1;
    int _scrollPauseMs = 2500;
    bool _autoScroll = true;
    bool _pausedByHover;
    DateTime _pauseUntil = DateTime.MinValue;
    HomeworkManager _homework = null!;
    StudentOnDuty _duty = null!;

    const double FontAnimMs = 400;
    static readonly Duration FontAnimDuration = new(TimeSpan.FromMilliseconds(FontAnimMs));
    static readonly IEasingFunction FontAnimEase = CreateFontAnimEase();

    static IEasingFunction CreateFontAnimEase()
    {
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    // ——— 插件注入口（宿主只放 Func，窗口不认识 PluginManager） ———
    public Func<IReadOnlyList<BoardSectionRender>>? BoardSectionsProvider { get; set; }
    public Func<BoardBadge?>? BoardBadgeProvider { get; set; }
    public Func<IReadOnlyList<BoardActionSpec>>? BoardActionsProvider { get; set; }

    string _badgeKey = "\u0000";
    string _sectionKey = "\u0000";

    public event Action? PrevDutyRequested;
    public event Action? NextDutyRequested;
    public event Action? AttendanceRequested;
    public event Action? EditHomeworkRequested;
    public event Action? TempAdjustRequested;
    public event Action? HolidayRequested;
    public event Action? TimetableRequested;   // 右键「编辑课表」：打开课表编辑页
    public event Action? WeekendRequested;
    public event Action? RollCallRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public MainBoardWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        // 主窗是贴边的桌面挂件，不该占任务栏；托盘图标才是它的入口。
        // （任务栏里应当只剩设置窗口一个条目。）
        ShowInTaskbar = false;
        ShowActivated = false;
        Title = "班级日常";

        // ★ 触屏：把"按住"交给系统去变成右键。
        //   WPF 默认会自己接管笔/触的"按住"（弹出自己的手势提示），那会把系统的
        //   「长按 = 右键」顶掉，用户长按半天什么也没发生。
        //   关掉它之后，系统/驱动把长按转成右键 → 我们的 OnMouseRightButtonUp 正常弹菜单。
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsTapFeedbackEnabled(this, false);

        // 窗口刚 Show 出来时 DWM 还没稳定，圆角/材质容易"迟一拍"。
        // 首帧渲染完再补挂一次（幂等，代价只有两个 DwmSetWindowAttribute）。
        ContentRendered += (_, _) =>
        {
            if (_rendered) return;
            _rendered = true;
            ApplyMaterial();
        };

        // 关掉 WPF/DWM 的非客户区白边：无边框窗口在 Win10/Win11 上很容易被 DWM
        // 画出一圈白色轮廓，这里强制不画玻璃帧、不画标题栏、不做圆角。
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            NonClientFrameEdges = NonClientFrameEdges.None,
            UseAeroCaptionButtons = false,
        });

        MinHeight = 100;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");

        // 原版主窗是一块实心底板（无圆角、无描边），文字直接铺在上面。
        // 这里用不透明窗口 + 窗口背景色，避免透明分层窗口在个别环境下文字被裁切。
        Background = Palette.WindowBackground;
        AllowsTransparency = false;

        _root.Padding = new Thickness(PadX, PadY, PadX, PadY);
        _root.BorderThickness = new Thickness(0);
        _root.BorderBrush = Brushes.Transparent;
        _root.SnapsToDevicePixels = true;

        _title.Text = "班级日常";
        _title.FontWeight = FontWeights.Bold;
        _homeworkEmpty.Text = "暂无作业";
        _homeworkEmpty.TextWrapping = TextWrapping.Wrap;
        _homeworkEmpty.Visibility = Visibility.Collapsed;

        _homeworkScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _homeworkScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _homeworkScroll.Content = _homeworkPanel;
        // 手指上下滑：无条件开（自动滚动在手指按住时会暂停，见 MouseEnter/Leave）
        TouchMode.EnableTouchScroll(_homeworkScroll);
        _homeworkScroll.MouseEnter += (_, _) => _pausedByHover = true;
        _homeworkScroll.MouseLeave += (_, _) => _pausedByHover = false;
        _homeworkScroll.Focusable = false;

        // 标题行用 WrapPanel：窄窗口时插件徽标自动换到下一行，不会把标题挤没
        _titleRow.Children.Add(_title);
        _titleRow.Children.Add(_badgePanel);
        _stack.Children.Add(_titleRow);
        _stack.Children.Add(Gap(Spacing));
        _stack.Children.Add(_dutyLabel);
        _stack.Children.Add(Gap(Spacing));
        _stack.Children.Add(_attendanceLabel);
        _stack.Children.Add(Gap(Spacing));
        _stack.Children.Add(_homeworkScroll);
        _stack.Children.Add(_homeworkEmpty);
        _stack.Children.Add(_pluginSections);
        // ★ 透明背景（不是 null）让整块底板都能被触摸/鼠标命中。
        //   背景为 null 的元素不参与命中测试 → 只有文字上长按才有事件，空白处毫无反应。
        _root.Background = Brushes.Transparent;
        _root.Child = _stack;
        Content = _root;

        _scrollTimer.Interval = TimeSpan.FromMilliseconds(1200);
        _scrollTimer.Tick += (_, _) => StepScroll();

        ApplyTheme();
        Palette.Changed += (_, _) => ApplyTheme();
        SourceInitialized += (_, _) =>
        {
            Interop.MakeToolWindowNoActivate(this, hideFromAltTab: false);
            DisableDwmBorder();
            Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Loaded);
        };
    }

    FrameworkElement Gap(double h) => new Border { Height = h };

    // ————————————————————— 数据 —————————————————————

    public void BindManagers(HomeworkManager homework, StudentOnDuty duty)
    {
        _homework = homework;
        _duty = duty;
        homework.Changed += (_, _) => Dispatcher.BeginInvoke(RefreshHomework);
        duty.Changed += (_, _) => Dispatcher.BeginInvoke(RefreshDuty);
        RefreshDuty();
        RefreshHomework();
    }

    /// <summary>数据变化后的整体刷新（值日生/出勤由 Manager 事件触发）。</summary>
    public void RefreshAll()
    {
        RefreshDuty();
        RefreshHomework();
    }

    void RefreshDuty()
    {
        if (_duty is null) return;
        var name = string.IsNullOrEmpty(_duty.TodayName) ? "未设置" : _duty.TodayName;
        _dutyLabel.Text = $"今日值日生：{name}";
        _attendanceLabel.Text = $"应到 {_duty.Config.ShouldCount} 人 · 实到 {_duty.Config.ActualCount} 人";
    }

    /// <summary>
    /// 作业列表：按学科分组（同原版 get_grouped_homework），组标题「学科 (日期)」，
    /// 组内条目「  1. 内容」编号。
    ///
    /// ★ 1.7.30 起分三段排（详细规则见 <see cref="HomeworkDue"/>）：
    ///     ① 今天已到点（顶到最前，最近到点的排第一行）
    ///     ② 正常（原文件顺序，组内也按原顺序）
    ///     ③ 补交 / 已过点没赶上的（沉到最底下）
    ///   分段只是**渲染顺序**，homework.json 里的顺序一点没动 —— 编辑器看到的是原顺序。
    /// </summary>
    public void RefreshHomework()
    {
        if (_homework is null) return;
        var fromFont = _dutyLabel.FontSize;
        _homeworkPanel.Children.Clear();
        var items = _homework.Items;
        _homeworkEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _homeworkScroll.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var now = DateTime.Now;
        foreach (var slot in new[] { HomeworkSlot.Due, HomeworkSlot.Normal, HomeworkSlot.Bottom })
        {
            var bucket = items
                .Where(i => !string.IsNullOrWhiteSpace(i.Content) && HomeworkDue.SlotOf(i, now) == slot)
                .ToList();
            if (bucket.Count == 0) continue;

            // 到点那段：谁最近到点谁在最前 —— 用户要的"刚交的那个调到第一行"
            if (slot == HomeworkSlot.Due)
                bucket = bucket.OrderByDescending(i => i.DueFiredTime ?? DateTime.MinValue).ToList();

            foreach (var group in GroupBySubject(bucket))
                RenderGroup(group, fromFont);
        }

        _homeworkScroll.ScrollToVerticalOffset(0);
        _scrollDirection = 1;
        _stack.UpdateLayout();

        // ★ 作业增减后行数变了，窗口高度必须重算：
        //   否则新加一堆作业窗口还是原来那么高（滚动条不出现），删完又留下一大片空白。
        //   放到 Loaded 优先级，等布局跑完再量 DesiredSize。
        Dispatcher.BeginInvoke(new Action(Reposition), DispatcherPriority.Loaded);
    }

    /// <summary>按学科归组，保持首次出现的顺序（LinkedDictionary 语义）。</summary>
    static List<(string Subject, string Date, List<HomeworkItem> Items)> GroupBySubject(List<HomeworkItem> items)
    {
        var groups = new List<(string Subject, string Date, List<HomeworkItem> Items)>();
        foreach (var item in items)
        {
            var subject = (item.Subject ?? "").Trim();
            var content = (item.Content ?? "").Trim();
            if (content.Length == 0) continue;
            var date = string.IsNullOrEmpty(item.Date) ? "" : item.Date.Trim();
            var idx = groups.FindIndex(g => string.Equals(g.Subject, subject, StringComparison.Ordinal));
            if (idx < 0)
            {
                groups.Add((subject, date, new List<HomeworkItem> { item }));
            }
            else
            {
                var g = groups[idx];
                g.Items.Add(item);
                if (string.IsNullOrEmpty(g.Date) && date.Length > 0)
                    groups[idx] = (g.Subject, date, g.Items);
            }
        }
        return groups;
    }

    void RenderGroup((string Subject, string Date, List<HomeworkItem> Items) group, double fromFont)
    {
        var heading = new TextBlock
        {
            Text = string.IsNullOrEmpty(group.Date) ? group.Subject : $"{group.Subject} ({group.Date})",
            FontSize = _fontSize,          // 新行一律按"当前"字号建，别沿用可能还在动画中的旧值
            FontWeight = FontWeights.Bold,
            Foreground = BoardText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 2),
        };
        _homeworkPanel.Children.Add(heading);

        // 只有在"字号真的变了"的时候才做动画；数据刷新时不该闪一下
        if (Math.Abs(fromFont - _fontSize) >= 0.01) AnimateFont(heading, _fontSize, fromFont);

        var now = DateTime.Now;
        for (var i = 0; i < group.Items.Count; i++)
        {
            var item = group.Items[i];
            var row = new TextBlock
            {
                FontSize = _fontSize,
                Foreground = BoardText,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = _fontSize * 1.35,
            };

            row.Inlines.Add(new Run($"  {i + 1}. {(item.Content ?? "").Trim()}"));

            // 同科目可能有两条（语文：晚自习交 / 第二节晚自习交）—— 每条后面挂上自己的提交时间，
            // 否则两行都叫「语文」，分不出哪条是哪条。
            var hint = HomeworkDue.BoardHint(item, now);
            if (hint.Length > 0)
                row.Inlines.Add(new Run("  " + hint)
                {
                    FontSize = _fontSize * 0.72,
                    Foreground = BoardTextDim,
                });

            _homeworkPanel.Children.Add(row);
            if (Math.Abs(fromFont - _fontSize) >= 0.01) AnimateFont(row, _fontSize, fromFont);
        }
    }

    /// <summary>设置里改了「触屏优化」后重新落一次滑动能力（幂等，尺寸在设置窗那边处理）。</summary>
    public void ApplyTouchMode() => TouchMode.EnableTouchScroll(_homeworkScroll);

    // ————————————————————— 插件内容 —————————————————————

    /// <summary>
    /// 重画插件贡献的区块与徽标。每秒都会被调一次，所以先比"内容签名"，
    /// 没变就直接返回 —— 不然每秒重建一遍可视树，滚动条会一直被拉回顶部。
    /// </summary>
    public void RefreshPluginContent()
    {
        RefreshPluginBadges();
        RefreshPluginSections();
    }

    void RefreshPluginBadges()
    {
        BoardBadge? badge = null;
        try { badge = BoardBadgeProvider?.Invoke(); }
        catch { /* 插件异常按"没有徽标"处理 */ }

        var key = badge is null ? "" : badge.Text + "|" + badge.Tone;
        if (key == _badgeKey) return;
        _badgeKey = key;

        _badgePanel.Children.Clear();
        if (badge is null) return;

        var (fg, bg) = BoardBadgeColors(badge.Tone);
        _badgePanel.Children.Add(new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 1, 6, 2),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = badge.Text,
                FontSize = Math.Max(10, _fontSize - 5),
                Foreground = fg,
                TextWrapping = TextWrapping.NoWrap,
            },
        });
    }

    void RefreshPluginSections()
    {
        IReadOnlyList<BoardSectionRender> sections;
        try { sections = BoardSectionsProvider?.Invoke() ?? Array.Empty<BoardSectionRender>(); }
        catch { return; }

        var key = string.Join("\u0001", sections.Select(s =>
            s.Section.Title + "\u0002" + string.Join("\u0003", s.Section.Lines)));
        if (key == _sectionKey) return;
        _sectionKey = key;

        _pluginSections.Children.Clear();
        foreach (var item in sections)
        {
            if (!string.IsNullOrWhiteSpace(item.Section.Title))
            {
                _pluginSections.Children.Add(new TextBlock
                {
                    Text = item.Section.Title,
                    FontSize = _fontSize,
                    FontWeight = FontWeights.Bold,
                    Foreground = Palette.Text,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, Spacing * 2, 0, 2),
                });
            }
            foreach (var line in item.Section.Lines)
            {
                if (line is null) continue;
                _pluginSections.Children.Add(new TextBlock
                {
                    Text = "  " + line,
                    FontSize = _fontSize,
                    Foreground = Palette.Text,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = _fontSize * 1.35,
                });
            }
        }

        // 内容变高/变矮了，窗口要跟着重新贴边
        if (IsVisible) Reposition();
    }

    static (Brush Foreground, Brush Background) BoardBadgeColors(BadgeTone tone)
    {
        var dark = Palette.IsDark;
        Brush fg = tone switch
        {
            BadgeTone.Accent => Palette.Accent,
            BadgeTone.Success => Frozen(dark ? Color.FromRgb(0x4C, 0xD9, 0x6A) : Color.FromRgb(0x10, 0x7C, 0x10)),
            BadgeTone.Warning => Frozen(dark ? Color.FromRgb(0xFF, 0xC8, 0x3D) : Color.FromRgb(0x9D, 0x5D, 0x00)),
            BadgeTone.Danger => Frozen(dark ? Color.FromRgb(0xFF, 0x6B, 0x5E) : Color.FromRgb(0xC4, 0x2B, 0x1C)),
            _ => Palette.TextDim,
        };
        return (fg, Palette.CardAlt);
    }

    // ————————————————————— 外观与尺寸 —————————————————————

    /// <summary>
    /// 自定义整窗背景：纯色 / 图片；返回 null 表示跟随主题（走材质那套）。
    /// scrim 是压在图片上的薄雾，保证文字在花哨壁纸上也读得清。
    /// </summary>
    Brush? BuildBoardBackground(out Brush scrim)
    {
        scrim = Brushes.Transparent;

        if (string.Equals(_boardBgMode, "color", StringComparison.OrdinalIgnoreCase))
        {
            var color = ParseColor(_boardBgColor, Colors.White);
            return Frozen(color);
        }

        if (string.Equals(_boardBgMode, "image", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_boardBgImage)
            && File.Exists(_boardBgImage))
        {
            var brush = LoadImageBrush(_boardBgImage);
            if (brush is null) return null;

            var tint = Palette.WindowBackground is SolidColorBrush scb ? scb.Color : Colors.Black;
            var alpha = (byte)(ThemeService.IsDark ? 0x8C : 0x66);
            scrim = Frozen(Color.FromArgb(alpha, tint.R, tint.G, tint.B));
            return brush;
        }

        return null;
    }

    /// <summary>自定义纯色底偏暗时，文字整体换成浅色（标题 / 值日 / 考勤 / 作业一起换）。</summary>
    void ResolveBoardText()
    {
        _boardText = null;
        if (!HasCustomBackground) return;
        if (!string.Equals(_boardBgMode, "color", StringComparison.OrdinalIgnoreCase)) return;

        var color = ParseColor(_boardBgColor, Colors.White);
        var luma = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
        if (luma < 0.55) _boardText = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF4));
    }

    static Color ParseColor(string? hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex ?? ""); }
        catch { return fallback; }
    }

    static Brush? LoadImageBrush(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;   // 读进内存，不锁文件
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            return brush;
        }
        catch (Exception ex)
        {
            Logger.Current?.Warn("[作业区背景] 图片加载失败：" + ex.Message);
            return null;
        }
    }

    static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    public void ApplySettings(SettingsManager s)
    {
        _fontSize = Math.Clamp(s.Int("main_font_size"), 10, 34);
        _widthRatio = Math.Clamp(s.Int("main_width_ratio"), 10, 80) / 100.0;
        _opacity = Math.Clamp(s.Int("main_opacity"), 30, 100) / 100.0;
        _material = s.Bool("settings_mica");
        _boardBgMode = s.Str("board_bg_mode");
        _boardBgColor = s.Str("board_bg_color");
        _boardBgImage = s.Str("board_bg_image");
        Opacity = _opacity;
        _autoScroll = s.Bool("homework_auto_scroll");
        _scrollStep = s.Int("homework_scroll_step");
        _scrollPauseMs = s.Int("homework_scroll_pause_ms");
        _scrollTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(s.Int("homework_scroll_interval_ms"), 100, 20000));

        ApplyTheme();
        ApplyFontScale(_fontSize);
        UpdateScrollEnabled();
        _stack.UpdateLayout();
        Reposition();              // 底板 padding 变了，窗口高度得跟着重算
    }

    /// <summary>
    /// 拖动滑块时的高频预览：不重建作业 DOM，只就地做字号过渡并重排。
    /// </summary>
    public void ApplyPreview(SettingsManager s)
    {
        _fontSize = Math.Clamp(s.Int("main_font_size"), 10, 34);
        _widthRatio = Math.Clamp(s.Int("main_width_ratio"), 10, 80) / 100.0;
        _opacity = Math.Clamp(s.Int("main_opacity"), 30, 100) / 100.0;
        ApplyBackdropPaint();

        ApplyFontScale(_fontSize);
    }

    /// <summary>
    /// 先按目标字号布局并量出窗口高度，再让字号与窗口高度用 400ms 动画同步过渡。
    /// 这样拖动滑块时文字与窗口高度一起平滑变化，不会生硬跳变。
    /// </summary>
    void ApplyFontScale(double newFont)
    {
        var oldTitle = _title.FontSize;
        var oldBody = _dutyLabel.FontSize;
        var oldEmpty = _homeworkEmpty.FontSize;
        var oldHomework = _homeworkPanel.Children.OfType<TextBlock>().FirstOrDefault()?.FontSize ?? oldBody;
        var fromHeight = ActualHeight > 0 ? ActualHeight : Height;
        var animateHeight = !double.IsNaN(fromHeight) && fromHeight > 0;

        ClearFontAnimations(_title);
        ClearFontAnimations(_dutyLabel);
        ClearFontAnimations(_attendanceLabel);
        ClearFontAnimations(_homeworkEmpty);
        foreach (var tb in _homeworkPanel.Children.OfType<TextBlock>())
            ClearFontAnimations(tb);

        // 目标布局：此时没有动画，Reposition 量到的是最终高度
        _title.FontSize = newFont + 4;
        _dutyLabel.FontSize = _attendanceLabel.FontSize = _homeworkEmpty.FontSize = newFont;
        foreach (var tb in _homeworkPanel.Children.OfType<TextBlock>())
        {
            tb.FontSize = newFont;
            if (!double.IsNaN(tb.LineHeight)) tb.LineHeight = newFont * 1.35;
        }
        foreach (var tb in _pluginSections.Children.OfType<TextBlock>())
        {
            tb.FontSize = newFont;
            if (!double.IsNaN(tb.LineHeight)) tb.LineHeight = newFont * 1.35;
        }

        Reposition();
        var targetHeight = Height;   // Reposition 里就是 DesiredSize + Padding，并按工作区夹取

        if (animateHeight)
        {
            Height = fromHeight;
            AnimateDouble(this, HeightProperty, targetHeight, fromHeight);
        }

        AnimateFont(_title, newFont + 4, oldTitle);
        AnimateFont(_dutyLabel, newFont, oldBody);
        AnimateFont(_attendanceLabel, newFont, oldBody);
        AnimateFont(_homeworkEmpty, newFont, oldEmpty);
        foreach (var tb in _homeworkPanel.Children.OfType<TextBlock>())
            AnimateFont(tb, newFont, oldHomework);

        _stack.UpdateLayout();
    }

    static void ClearFontAnimations(TextBlock tb)
    {
        tb.BeginAnimation(TextBlock.FontSizeProperty, null);
        tb.BeginAnimation(TextBlock.LineHeightProperty, null);
    }

    static void AnimateDouble(UIElement target, DependencyProperty dp, double to, double from)
    {
        target.BeginAnimation(dp, null);
        target.SetValue(dp, to);
        if (Math.Abs(from - to) < 0.01) return;

        target.BeginAnimation(dp, new DoubleAnimation(from, to, FontAnimDuration)
        {
            EasingFunction = FontAnimEase,
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    static void AnimateFont(TextBlock tb, double to, double from)
    {
        AnimateDouble(tb, TextBlock.FontSizeProperty, to, from);
        if (!double.IsNaN(tb.LineHeight))
            AnimateDouble(tb, TextBlock.LineHeightProperty, to * 1.35, from * 1.35);
    }

    void DisableDwmBorder()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // 非客户区渲染关掉：没有这圈 DWM 自己画的边框 / 标题栏残影。
            var disabled = 0;
            DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref disabled, sizeof(int));

            // Win11 的圆角 / 白色描边也关掉；老系统会直接返回失败，无副作用。
            var donotround = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref donotround, sizeof(int));

            // 强制让非客户区样式立即重算。
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        catch
        {
            // 边框优化失败不能影响挂件显示。
        }
    }

    const int DWMWA_NCRENDERING_POLICY = 2;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // ————————————————————— 云母 / 亚克力背板 —————————————————————
    bool _rendered;                 // 首帧渲染后是否已补挂过一次材质
    string _boardBgMode = "inherit";      // 整窗背景：inherit / color / image
    string _boardBgColor = "#FFFFFF";
    string _boardBgImage = "";
    Brush? _boardText;                     // 文字色（自定义深底时改浅色；null = 跟随主题）

    /// <summary>整窗文字色：默认跟随主题，自定义深色底时用浅色。</summary>
    Brush BoardText => _boardText ?? Palette.Text;

    /// <summary>同色但更淡：作业行末尾的提交时间用它，不抢正文的注意力。</summary>
    Brush BoardTextDim => Dim(BoardText);

    static Brush Dim(Brush source)
    {
        if (source is not SolidColorBrush solid) return source;
        var c = solid.Color;
        return Frozen(Color.FromArgb((byte)(c.A * 0.62), c.R, c.G, c.B));
    }

    bool HasCustomBackground =>
        !string.Equals(_boardBgMode, "inherit", StringComparison.OrdinalIgnoreCase);

    bool _material;                 // 设置里的「窗口材质」开关
    bool _materialHooked;           // 句柄未就绪时只挂一次 SourceInitialized
    WindowBackdrop _backdrop;       // 实际生效的材质（系统不支持时是 None）
    double _opacity = 1;            // 用户设定的整体不透明度（云母态折进底板 alpha）

    void ApplyMaterial()
    {
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero)
        {
            if (_materialHooked) return;
            _materialHooked = true;
            SourceInitialized += (_, _) => ApplyMaterial();
            return;
        }

        // 自定义了整窗背景：系统材质让位，否则亚克力/云母会画在自己的底上面
        if (HasCustomBackground)
        {
            Interop.ClearBackdrop(this);
            _backdrop = WindowBackdrop.None;
            if (Interop.IsWindows11) Interop.ApplyRoundedCorners(this);
            else Interop.UseSquareCorners(this);
            ApplyBackdropPaint();
            Logger.Current?.Info($"[材质] 自定义背景（{_boardBgMode}），已关闭系统材质");
            return;
        }

        // 主窗贴壁纸，要的是**实时模糊**（亚克力）；Mica 只是壁纸采样，几乎看不出"糊"。
        // 关掉开关时把 accent policy 撤掉，退回不透明底色。
        if (_material)
        {
            _backdrop = Interop.ApplyBlur(this, ThemeService.IsDark, 0xA0);
        }
        else
        {
            Interop.ClearBlur(this);
            _backdrop = Interop.ApplyBackdrop(this, false, ThemeService.IsDark);
        }
        // Win11 用系统原生圆角（用户明确要）；非 Win11 保持直角，
        // 且材质在上面已经整体回退成纯色 —— 不会出现"半透明但没模糊"的坏状态。
        if (Interop.IsWindows11) Interop.ApplyRoundedCorners(this);
        else Interop.UseSquareCorners(this);

        ApplyBackdropPaint();
        Logger.Current?.Info($"[材质] 开关={(_material ? "开" : "关")} 实际={_backdrop} 系统Build={Environment.OSVersion.Version.Build}");
    }

    void ApplyBackdropPaint()
    {
        // 自定义整窗背景优先：整块窗口铺自己的底色/图片，图片上再压一层薄雾
        if (BuildBoardBackground(out var boardScrim) is { } boardBackground)
        {
            Opacity = _opacity;
            Background = boardBackground;
            _root.Background = boardScrim;      // 纯色时透明，图片时是主题色薄雾
            ApplyWindowFrame(material: false);
            return;
        }

        var material = _backdrop != WindowBackdrop.None;

        if (material)
        {
            // 云母：窗口自身必须透明，DWM 的背板才透得上来。
            // ★ Opacity 不能小于 1 —— WPF 会把窗口变成分层窗口（WS_EX_LAYERED），
            //   云母随即失效；所以自定义不透明度改成折进底板的 alpha。
            Opacity = 1;
            Background = Brushes.Transparent;
            _root.Background = TintedBackdrop();
        }
        else
        {
            Opacity = _opacity;
            Background = Palette.WindowBackground;
            // 非云母：必须 Transparent（不能是 null）整块底板才能命中触摸
            _root.Background = Brushes.Transparent;
        }

        ApplyWindowFrame(material);
    }

    /// <summary>玻璃帧 + 圆角：重设 chrome 会让 DWM 重算非客户区，圆角必须放在它之后设。</summary>
    void ApplyWindowFrame(bool material)
    {
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = material ? new Thickness(-1) : new Thickness(0),
            CornerRadius = new CornerRadius(0),
            NonClientFrameEdges = NonClientFrameEdges.None,
            UseAeroCaptionButtons = false,
        });

        if (Interop.IsWindows11) Interop.ApplyRoundedCorners(this);
        else Interop.UseSquareCorners(this);
    }

    /// <summary>云母底板上压的一层"雾"：既看得到材质，又保住文字对比度。</summary>
    Brush TintedBackdrop()
    {
        var c = Palette.WindowBackground is SolidColorBrush scb ? scb.Color : Colors.Black;

        // 亚克力自身已带一层浓度（ApplyBlur 的 tintAlpha），这里只压薄薄一层主题色
        // 把文字对比度托住；再厚就看不见模糊了。
        var alpha = (byte)Math.Clamp(0x3D * _opacity, 0, 255);
        return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    void ApplyTheme()
    {
        // 原版主窗：整块 window_bg 底板 + 纯文字，无卡片、无描边。
        // 底色不在这里直接给：开了云母窗口要透明，统一交给 ApplyBackdropPaint。

        // ★ 必须是 Transparent 而不是 null：null 背景的容器不参与命中测试，
        //   整块底板只有文字上能收到触摸/鼠标事件（长按菜单曾因此只在文字处出现）。
        _root.Background = Brushes.Transparent;
        ResolveBoardText();        // 自定义深色底时整窗文字换浅色
        _title.Foreground = BoardText;
        _dutyLabel.Foreground = BoardText;
        _attendanceLabel.Foreground = BoardText;
        _homeworkEmpty.Foreground = BoardText;
        foreach (var tb in _homeworkPanel.Children.OfType<TextBlock>())
            tb.Foreground = BoardText;
        // 插件区块/徽标是按当时的色板造出来的，换主题后必须重建一次
        _badgeKey = "\u0000";
        _sectionKey = "\u0000";
        RefreshPluginContent();
        foreach (var tb in new[] { _title, _dutyLabel, _attendanceLabel, _homeworkEmpty })
            tb.TextWrapping = TextWrapping.Wrap;

        // 主题一变（尤其深/浅切换），云母的暗色标记和底板"雾"都要重挂
        ApplyMaterial();
    }

    /// <summary>
    /// 贴屏幕右缘、顶部对齐（原版 setGeometry(x, 0, ...)）。
    /// 先定宽度，再按该宽度测量内容高度，最后把窗口放到右下角对齐的位置。
    /// </summary>
    public void Reposition()
    {
        var work = WindowUtil.WorkAreaDip();
        var width = Math.Max(190, Math.Round(work.Width * _widthRatio));
        Width = width;

        // 作业滚动区一直可以长到"工作区底部 - 顶部标题余量"：以前卡在 50% 屏高，
        // 屏幕高时明明还有地方却提前出滚动条。
        _homeworkScroll.MaxHeight = Math.Max(80, work.Height - 100);

        // 内容宽度 = 窗口宽 - 左右内边距（必须在此约束下测量，否则文本按自然宽度撑开）
        var contentWidth = Math.Max(60, width - PadX * 2);
        _stack.Width = contentWidth;
        _stack.Measure(new Size(contentWidth, double.PositiveInfinity));

        var desired = _stack.DesiredSize.Height + PadY * 2;
        if (double.IsNaN(desired) || desired <= 0) desired = 220;
        BeginAnimation(HeightProperty, null);

        // 上限放到工作区高度（任务栏上方）：内容真的比屏幕还高时才由滚动区接手。
        Height = Math.Clamp(desired, 100, Math.Max(160, work.Height));

        Left = Math.Round(work.Right - width);
        Top = Math.Round(work.Top);
        UpdateLayout();
    }

    // ————————————————————— 自动滚动 —————————————————————

    void UpdateScrollEnabled()
    {
        if (_autoScroll)
        {
            if (!_scrollTimer.IsEnabled) _scrollTimer.Start();
        }
        else
        {
            _scrollTimer.Stop();
            _homeworkScroll.ScrollToVerticalOffset(0);
            _scrollDirection = 1;
        }
    }

    void StepScroll()
    {
        if (!_autoScroll || _pausedByHover) return;

        var max = _homeworkScroll.ScrollableHeight;
        if (max <= 1)
        {
            // 内容还没超过可视高度：不用滚，方向复位到向下
            _homeworkScroll.ScrollToVerticalOffset(0);
            _scrollDirection = 1;
            return;
        }

        var current = _homeworkScroll.VerticalOffset;

        // 到底/到顶之后先停一会儿，再掉头（来回滚）
        if (DateTime.Now < _pauseUntil) return;

        if (_scrollDirection == 1)
        {
            var next = current + _scrollStep;
            if (next >= max)
            {
                _homeworkScroll.ScrollToVerticalOffset(max);
                _pauseUntil = DateTime.Now.AddMilliseconds(Math.Max(0, _scrollPauseMs));
                _scrollDirection = -1;
            }
            else
            {
                _homeworkScroll.ScrollToVerticalOffset(next);
            }
        }
        else
        {
            var next = current - _scrollStep;
            if (next <= 0)
            {
                _homeworkScroll.ScrollToVerticalOffset(0);
                _pauseUntil = DateTime.Now.AddMilliseconds(Math.Max(0, _scrollPauseMs));
                _scrollDirection = 1;
            }
            else
            {
                _homeworkScroll.ScrollToVerticalOffset(next);
            }
        }
    }

    public void SetVisible(bool visible)
    {
        if (visible)
        {
            Show();
            Reposition();
        }
        else Hide();
    }

    // ————————————————————— 右键菜单 —————————————————————

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        ShowContextMenu(null);       // 鼠标右键：按光标位置弹
        e.Handled = true;
    }

    // ————————————————————— 菜单的打开与"点外面就关" —————————————————————

    ContextMenu? _openMenu;
    GlobalMouseHook? _menuHook;
    DateTime _menuOpenedAt = DateTime.MinValue;

    /// <summary>
    /// 弹出菜单。screenPoint 为空时按鼠标位置弹（鼠标右键），否则按给定屏幕坐标弹（触屏长按）。
    ///
    /// 同时挂一个全局低级鼠标钩子：**点在菜单外面就关掉**。
    /// 本窗是 ShowActivated=false 的桌面挂件，WPF 的 Deactivated / StaysOpen 在"点别的窗口、
    /// 点桌面"时都靠不住，只有 WH_MOUSE_LL 能跨窗口拿到点击（下拉面板也是这么做的）。
    /// </summary>
    void ShowContextMenu(Point? screenPoint)
    {
        var menu = BuildContextMenu(screenPoint);
        _openMenu = menu;
        _menuOpenedAt = DateTime.UtcNow;

        menu.Opened += (_, _) => StartMenuOutsideHook();
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openMenu, menu)) _openMenu = null;
            StopMenuOutsideHook();
        };

        menu.IsOpen = true;
    }

    void StartMenuOutsideHook()
    {
        if (_menuHook is not null) return;

        _menuHook = new GlobalMouseHook();
        _menuHook.MouseDown += OnGlobalMouseDown;

        if (!_menuHook.Start())
        {
            _menuHook.MouseDown -= OnGlobalMouseDown;
            _menuHook.Dispose();
            _menuHook = null;
        }
    }

    void StopMenuOutsideHook()
    {
        if (_menuHook is null) return;

        _menuHook.MouseDown -= OnGlobalMouseDown;
        _menuHook.Dispose();
        _menuHook = null;
    }

    void OnGlobalMouseDown(int screenX, int screenY)
    {
        // 宽限期：刚弹出时（还在布局、或触摸序列的尾巴事件）不要判点外面，
        // 否则菜单会一闪就没。
        if ((DateTime.UtcNow - _menuOpenedAt).TotalMilliseconds < 250) return;

        var menu = _openMenu;
        if (menu is not null && menu.IsOpen)
        {
            if (IsPointInsideMenu(menu, screenX, screenY)) return;
            menu.IsOpen = false;        // 点在菜单外面（任何窗口 / 桌面都算）：关掉
            return;
        }

        // 触屏侧边菜单同样"点外面就关"
        var card = _touchMenuCard;
        if (card is null || _touchMenu is not { IsOpen: true }) return;
        if (IsPointInsideTouchMenu(card, screenX, screenY)) return;
        CloseTouchMenu();
    }

    static bool IsPointInsideMenu(ContextMenu menu, int screenX, int screenY)
    {
        try
        {
            // 还没测量完（宽高为 0）时无法判断内外，一律当作里面，避免误关
            if (menu.ActualWidth <= 1 || menu.ActualHeight <= 1) return true;

            var topLeft = menu.PointToScreen(new Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(menu);
            var width = menu.ActualWidth * dpi.DpiScaleX;
            var height = menu.ActualHeight * dpi.DpiScaleY;

            const double pad = 2;   // 边界容差：刚好点在边框上不算"外面"
            return screenX >= topLeft.X - pad && screenX <= topLeft.X + width + pad &&
                   screenY >= topLeft.Y - pad && screenY <= topLeft.Y + height + pad;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>菜单项清单：右键菜单与触屏侧边菜单共用，保证两边永远一样。</summary>
    sealed record MenuEntry(string Text, Action Action, bool IsSeparator = false)
    {
        public static MenuEntry Sep => new("", null!, IsSeparator: true);
    }

    List<MenuEntry> GetMenuEntries()
    {
        var list = new List<MenuEntry>();

        MenuEntry Item(string text, Action action) => new(text, () =>
        {
            Logger.Current?.Action("班级日常", "菜单：" + text);
            action();
        });

        // 顺序对齐原版 gui.py::_show_menu
        list.Add(Item("上一位值日生", () => PrevDutyRequested?.Invoke()));
        list.Add(Item("下一位值日生", () => NextDutyRequested?.Invoke()));
        list.Add(MenuEntry.Sep);
        list.Add(Item("出勤人数", () => AttendanceRequested?.Invoke()));
        list.Add(MenuEntry.Sep);
        list.Add(Item("编辑作业", () => EditHomeworkRequested?.Invoke()));
        list.Add(MenuEntry.Sep);
        list.Add(Item("临时调课", () => TempAdjustRequested?.Invoke()));
        list.Add(MenuEntry.Sep);
        list.Add(Item("假期与调休", () => HolidayRequested?.Invoke()));
        list.Add(Item("编辑课表…", () => TimetableRequested?.Invoke()));

        // 插件菜单项：排在「设置」前面，各自夹一层 try（插件回调不能拖垮菜单）
        IReadOnlyList<BoardActionSpec> pluginActions = Array.Empty<BoardActionSpec>();
        try { pluginActions = BoardActionsProvider?.Invoke() ?? Array.Empty<BoardActionSpec>(); }
        catch { /* 拿不到就当没有 */ }
        if (pluginActions.Count > 0)
        {
            list.Add(MenuEntry.Sep);
            foreach (var spec in pluginActions)
            {
                var action = spec.Invoke;
                list.Add(Item(spec.Text, () =>
                {
                    try { action(); }
                    catch (Exception ex) { Console.Error.WriteLine($"插件菜单「{spec.Text}」异常: {ex.Message}"); }
                }));
            }
        }

        list.Add(Item("设置", () => SettingsRequested?.Invoke()));
        list.Add(MenuEntry.Sep);
        list.Add(Item("随机点名", () => RollCallRequested?.Invoke()));
        list.Add(MenuEntry.Sep);
        list.Add(Item("退出", () => ExitRequested?.Invoke()));
        return list;
    }

    /// <summary>鼠标右键菜单（老样子：系统 ContextMenu，按光标位置弹）。</summary>
    private ContextMenu BuildContextMenu(Point? screenPoint = null)
    {
        var menu = new ContextMenu { PlacementTarget = this };

        if (screenPoint is { } pt)
        {
            // 触屏没有鼠标位置：按手指落点定位（AbsolutePoint 用的是屏幕设备坐标）
            menu.Placement = PlacementMode.AbsolutePoint;
            menu.HorizontalOffset = pt.X;
            menu.VerticalOffset = pt.Y;
        }
        else
        {
            menu.Placement = PlacementMode.MousePoint;
        }

        foreach (var entry in GetMenuEntries())
        {
            if (entry.IsSeparator)
            {
                menu.Items.Add(new Separator());
                continue;
            }

            var mi = new MenuItem { Header = entry.Text };
            var action = entry.Action;
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        return menu;
    }

    // ————————————————————— 触屏侧边菜单：从板子左边伸出来 —————————————————————

    Popup? _touchMenu;
    Border? _touchMenuCard;

    /// <summary>侧边菜单卡片宽度（也是每项"伸出来"的行程）。</summary>
    const double TouchMenuWidth = 216;

    /// <summary>每一项依次伸出来的间隔（毫秒）。</summary>
    const int TouchMenuStaggerMs = 32;

    /// <summary>
    /// 触屏长按专用：贴在「班级日常」**左边**、向外张开的菜单，
    /// 每一项从板子边缘**依次伸出来**（右键仍然是普通 ContextMenu）。
    /// </summary>
    void ShowTouchMenu()
    {
        CloseTouchMenu();
        _menuOpenedAt = DateTime.UtcNow;

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        foreach (var entry in GetMenuEntries())
            stack.Children.Add(entry.IsSeparator ? TouchSeparator() : TouchMenuRow(entry));

        var card = new Border
        {
            Width = TouchMenuWidth,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6),
            BorderThickness = new Thickness(1),
            // ★ 项从右边缘"伸出来"，还没进场的部分要挡住，不然会先糊在板子上
            ClipToBounds = true,
            Child = stack,
        };
        card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Left,     // 右边缘贴住窗口左边
            HorizontalOffset = 8,               // 往右压一点，像长在板子边上
            VerticalOffset = 12,
            AllowsTransparency = true,
            StaysOpen = true,                   // 关闭自己管（全局钩子判"点外面"）
            Child = card,
        };

        _touchMenu = popup;
        _touchMenuCard = card;

        popup.Opened += (_, _) => StartMenuOutsideHook();
        popup.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_touchMenu, popup)) return;
            _touchMenu = null;
            _touchMenuCard = null;
            StopMenuOutsideHook();
        };

        popup.IsOpen = true;
        AnimateTouchMenuRows(stack);
    }

    FrameworkElement TouchMenuRow(MenuEntry entry)
    {
        var text = new TextBlock
        {
            Text = entry.Text,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

        var row = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = text,
        };

        var action = entry.Action;
        row.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            CloseTouchMenu();
            try { action(); }
            catch (Exception ex) { Logger.Current?.Warn("侧边菜单动作异常：" + ex.Message); }
        };

        // 悬停高亮（触屏开出来之后也允许用鼠标点）
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

        return row;
    }

    FrameworkElement TouchSeparator()
    {
        var line = new Border
        {
            Height = 1,
            Margin = new Thickness(10, 5, 10, 5),
        };
        line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        return line;
    }

    /// <summary>
    /// 每一项**依次伸出来**：从卡片右边缘（贴着板子那侧）滑到自己的位置，
    /// 加速出来、到头轻轻收住，和提示框是同一套手感。
    /// </summary>
    void AnimateTouchMenuRows(Panel stack)
    {
        var index = 0;
        foreach (var child in stack.Children)
        {
            if (child is not FrameworkElement row) continue;

            var delay = TimeSpan.FromMilliseconds(index * TouchMenuStaggerMs);
            index++;

            var transform = new TranslateTransform(TouchMenuWidth, 0);
            row.RenderTransform = transform;
            row.Opacity = 0;

            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
            {
                From = TouchMenuWidth,
                To = 0,
                BeginTime = delay,
                Duration = TimeSpan.FromMilliseconds(240),
                AccelerationRatio = 0.65,
                DecelerationRatio = 0.35,
            });

            row.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
            {
                From = 0,
                To = 1,
                BeginTime = delay,
                Duration = TimeSpan.FromMilliseconds(140),
            });
        }
    }

    void CloseTouchMenu()
    {
        if (_touchMenu is null) return;
        try { _touchMenu.IsOpen = false; } catch { /* 已经关了 */ }
    }

    static bool IsPointInsideTouchMenu(Border card, int screenX, int screenY)
    {
        try
        {
            if (card.ActualWidth <= 1 || card.ActualHeight <= 1) return true;   // 还没量完，别误关

            var topLeft = card.PointToScreen(new Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(card);
            var width = card.ActualWidth * dpi.DpiScaleX;
            var height = card.ActualHeight * dpi.DpiScaleY;

            const double pad = 2;
            return screenX >= topLeft.X - pad && screenX <= topLeft.X + width + pad &&
                   screenY >= topLeft.Y - pad && screenY <= topLeft.Y + height + pad;
        }
        catch
        {
            return true;
        }
    }

    // ————————————————————— 触屏长按 = 右键菜单 —————————————————————

    /// <summary>按住多久算长按（毫秒）。用户要求 250ms：更快出菜单，且与滚动手势仍能区分。</summary>
    const int TouchHoldMs = 250;

    /// <summary>按住期间允许的手指抖动（像素），超过即视为拖动/滚动。</summary>
    const double TouchHoldSlop = 14;

    DispatcherTimer? _holdTimer;
    Point _holdStart;
    bool _holdActive;
    bool _holdFired;

    void EnsureHoldTimer()
    {
        if (_holdTimer is not null) return;
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TouchHoldMs) };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer?.Stop();
            if (!_holdActive) return;

            // 只打标记：此刻手指还按着，这时弹菜单会在松手的瞬间被当成点到外面关掉。
            // 真正弹菜单放到 TouchUp 里（见 OnPreviewTouchUp）。
            _holdFired = true;
        };
    }

    protected override void OnPreviewTouchDown(TouchEventArgs e)
    {
        base.OnPreviewTouchDown(e);

        // ★ 关键：本窗是 ShowActivated=false 的挂件，非焦点状态下触摸事件到不了长按逻辑
        //   （用户实测：必须先点一下让窗口拿到焦点，长按才有效）。这里触摸按下就主动激活。
        if (!IsActive)
        {
            try { Activate(); } catch { /* 激活失败不影响后续 */ }
        }

        EnsureHoldTimer();
        _holdStart = e.GetTouchPoint(this).Position;
        _holdActive = true;
        _holdFired = false;
        _holdTimer?.Stop();
        _holdTimer?.Start();
    }

    protected override void OnPreviewTouchMove(TouchEventArgs e)
    {
        base.OnPreviewTouchMove(e);
        if (!_holdActive) return;

        var p = e.GetTouchPoint(this).Position;
        if (Math.Abs(p.X - _holdStart.X) > TouchHoldSlop || Math.Abs(p.Y - _holdStart.Y) > TouchHoldSlop)
        {
            _holdActive = false;
            _holdTimer?.Stop();
        }
    }

    protected override void OnPreviewTouchUp(TouchEventArgs e)
    {
        base.OnPreviewTouchUp(e);

        var wasHold = _holdFired;
        _holdActive = false;
        _holdTimer?.Stop();

        if (!wasHold) return;

        // 长按完成：手指离开的这一刻才弹菜单（按住时弹会被松手事件立刻关掉）
        _holdFired = false;
        if (!e.Handled)
        {
            e.Handled = true;   // 阻止这次触摸被提升成鼠标点击

            // 等这一串触摸事件（含被提升的鼠标事件）都处理完再弹，
            // 否则菜单刚出现就被紧跟的抬起事件当成点到外面关掉（用户看到的闪现）。
            //
            // ★ 触屏不走右键菜单：弹"从班级日常左边伸出来"的侧边菜单（每一项依次伸出）。
            //   鼠标右键仍然是老样子的 ContextMenu（见 OnMouseRightButtonUp）。
            Dispatcher.BeginInvoke(new Action(ShowTouchMenu), DispatcherPriority.Input);
        }
    }

    // ————————————————————— 交互：F1 退出 / 触摸拖动滚动 —————————————————————
    Point? _dragStart;
    double _dragOffset;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.F1)
        {
            ExitRequested?.Invoke();
            e.Handled = true;
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        _dragStart = e.GetPosition(this);
        _dragOffset = _homeworkScroll.VerticalOffset;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_dragStart is null) return;
        var dy = e.GetPosition(this).Y - _dragStart.Value.Y;
        if (Math.Abs(dy) < 3) return;
        PauseAutoScroll();
        _homeworkScroll.ScrollToVerticalOffset(_dragOffset - dy);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        _dragStart = null;

        // 长按已经弹过菜单：吞掉触屏提升出来的这次点击，别顺手当成单击
        if (_holdFired)
        {
            _holdFired = false;
            e.Handled = true;
        }
    }

    /// <summary>手动滚动后让自动滚动让位 4 秒（原版 MANUAL_SCROLL_HOLD_MS）。</summary>
    void PauseAutoScroll() => _pauseUntil = DateTime.Now.AddMilliseconds(4000);

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        PauseAutoScroll();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关窗 = 隐藏（托盘「退出」才真正结束进程）
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _scrollTimer.Stop();
        base.OnClosed(e);
    }
}
