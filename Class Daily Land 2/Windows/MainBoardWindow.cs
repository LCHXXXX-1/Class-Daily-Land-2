using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClassDailyLand.App.Theme;
using ClassDailyLand.App.Win32;
using ClassDailyLand.Core.Managers;

namespace ClassDailyLand.App.Windows;

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
    readonly StackPanel _homeworkPanel = new() { Orientation = Orientation.Vertical };
    readonly ScrollViewer _homeworkScroll = new();
    readonly DispatcherTimer _scrollTimer = new();

    double _fontSize = 16;
    double _widthRatio = 0.22;
    int _scrollStep = 24;
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

    public event Action? PrevDutyRequested;
    public event Action? NextDutyRequested;
    public event Action? AttendanceRequested;
    public event Action? EditHomeworkRequested;
    public event Action? TimetableManagerRequested;
    public event Action? TempAdjustRequested;
    public event Action? HolidayRequested;
    public event Action? WeekendRequested;
    public event Action? RollCallRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public MainBoardWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        Title = "班级日常";
        MinHeight = 100;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");

        // 原版主窗是一块实心底板（无圆角、无描边），文字直接铺在上面。
        // 这里用不透明窗口 + 窗口背景色，避免透明分层窗口在个别环境下文字被裁切。
        Background = Palette.WindowBackground;
        AllowsTransparency = false;

        _root.Padding = new Thickness(PadX, PadY, PadX, PadY);
        _root.SnapsToDevicePixels = true;

        _title.Text = "班级日常";
        _title.FontWeight = FontWeights.Bold;
        _homeworkEmpty.Text = "暂无作业";
        _homeworkEmpty.TextWrapping = TextWrapping.Wrap;
        _homeworkEmpty.Visibility = Visibility.Collapsed;

        _homeworkScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _homeworkScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _homeworkScroll.Content = _homeworkPanel;
        _homeworkScroll.MouseEnter += (_, _) => _pausedByHover = true;
        _homeworkScroll.MouseLeave += (_, _) => _pausedByHover = false;
        _homeworkScroll.Focusable = false;

        _stack.Children.Add(_title);
        _stack.Children.Add(Gap(Spacing));
        _stack.Children.Add(_dutyLabel);
        _stack.Children.Add(Gap(Spacing));
        _stack.Children.Add(_attendanceLabel);
        _stack.Children.Add(Gap(Spacing));
        _stack.Children.Add(_homeworkScroll);
        _stack.Children.Add(_homeworkEmpty);
        _root.Child = _stack;
        Content = _root;

        _scrollTimer.Interval = TimeSpan.FromMilliseconds(1200);
        _scrollTimer.Tick += (_, _) => StepScroll();

        ApplyTheme();
        Palette.Changed += (_, _) => ApplyTheme();
        SourceInitialized += (_, _) => Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Loaded);
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
    /// </summary>
    void RefreshHomework()
    {
        if (_homework is null) return;
        var fromFont = _dutyLabel.FontSize;
        _homeworkPanel.Children.Clear();
        var items = _homework.Items;
        _homeworkEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _homeworkScroll.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // 按学科归组，保持首次出现的顺序（LinkedDictionary 语义）
        var groups = new List<(string Subject, string Date, List<string> Contents)>();
        foreach (var item in items)
        {
            var subject = (item.Subject ?? "").Trim();
            var content = (item.Content ?? "").Trim();
            if (content.Length == 0) continue;
            var date = string.IsNullOrEmpty(item.Date) ? "" : item.Date.Trim();
            var idx = groups.FindIndex(g => string.Equals(g.Subject, subject, StringComparison.Ordinal));
            if (idx < 0)
            {
                groups.Add((subject, date, new List<string> { content }));
            }
            else
            {
                var g = groups[idx];
                g.Contents.Add(content);
                if (string.IsNullOrEmpty(g.Date) && date.Length > 0)
                    groups[idx] = (g.Subject, date, g.Contents);
            }
        }

        foreach (var g in groups)
        {
            var heading = new TextBlock
            {
                Text = string.IsNullOrEmpty(g.Date) ? g.Subject : $"{g.Subject} ({g.Date})",
                FontSize = fromFont,
                FontWeight = FontWeights.Bold,
                Foreground = Palette.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 2),
            };
            _homeworkPanel.Children.Add(heading);
            AnimateFont(heading, _fontSize, fromFont);

            for (var i = 0; i < g.Contents.Count; i++)
            {
                var row = new TextBlock
                {
                    Text = $"  {i + 1}. {g.Contents[i]}",
                    FontSize = fromFont,
                    Foreground = Palette.Text,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = fromFont * 1.35,
                };
                _homeworkPanel.Children.Add(row);
                AnimateFont(row, _fontSize, fromFont);
            }
        }
        _homeworkScroll.ScrollToVerticalOffset(0);
        _stack.UpdateLayout();
    }

    // ————————————————————— 外观与尺寸 —————————————————————

    public void ApplySettings(SettingsManager s)
    {
        _fontSize = Math.Clamp(s.Int("main_font_size"), 10, 34);
        _widthRatio = Math.Clamp(s.Int("main_width_ratio"), 10, 80) / 100.0;
        Opacity = Math.Clamp(s.Int("main_opacity"), 30, 100) / 100.0;
        _autoScroll = s.Bool("homework_auto_scroll");
        _scrollStep = s.Int("homework_scroll_step");
        _scrollPauseMs = s.Int("homework_scroll_pause_ms");
        _scrollTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(s.Int("homework_scroll_interval_ms"), 100, 20000));

        ApplyTheme();
        ApplyFontScale(_fontSize);
        UpdateScrollEnabled();
        _stack.UpdateLayout();
    }

    /// <summary>
    /// 拖动滑块时的高频预览：不重建作业 DOM，只就地做字号过渡并重排。
    /// </summary>
    public void ApplyPreview(SettingsManager s)
    {
        _fontSize = Math.Clamp(s.Int("main_font_size"), 10, 34);
        _widthRatio = Math.Clamp(s.Int("main_width_ratio"), 10, 80) / 100.0;
        Opacity = Math.Clamp(s.Int("main_opacity"), 30, 100) / 100.0;

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

    void ApplyTheme()
    {
        // 原版主窗：整块 window_bg 底板 + 纯文字，无卡片、无描边
        Background = Palette.WindowBackground;
        if (_root.Background is not null) _root.Background = null;
        _title.Foreground = Palette.Text;
        _dutyLabel.Foreground = Palette.Text;
        _attendanceLabel.Foreground = Palette.Text;
        _homeworkEmpty.Foreground = Palette.Text;
        foreach (var tb in _homeworkPanel.Children.OfType<TextBlock>())
            tb.Foreground = Palette.Text;
        foreach (var tb in new[] { _title, _dutyLabel, _attendanceLabel, _homeworkEmpty })
            tb.TextWrapping = TextWrapping.Wrap;
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
        _homeworkScroll.MaxHeight = Math.Max(80, work.Height * 0.5);

        // 内容宽度 = 窗口宽 - 左右内边距（必须在此约束下测量，否则文本按自然宽度撑开）
        var contentWidth = Math.Max(60, width - PadX * 2);
        _stack.Width = contentWidth;
        _stack.Measure(new Size(contentWidth, double.PositiveInfinity));

        var desired = _stack.DesiredSize.Height + PadY * 2;
        if (double.IsNaN(desired) || desired <= 0) desired = 220;
        BeginAnimation(HeightProperty, null);
        Height = Math.Clamp(desired, 100, Math.Max(160, work.Height - 40));

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
        }
    }

    void StepScroll()
    {
        if (!_autoScroll || _pausedByHover || DateTime.Now < _pauseUntil) return;
        var max = _homeworkScroll.ScrollableHeight;
        if (max <= 1)
        {
            _homeworkScroll.ScrollToVerticalOffset(0);
            return;
        }
        var next = _homeworkScroll.VerticalOffset + _scrollStep;
        if (next >= max)
        {
            _homeworkScroll.ScrollToVerticalOffset(max);
            _pauseUntil = DateTime.Now.AddMilliseconds(Math.Max(0, _scrollPauseMs));
        }
        else
        {
            _homeworkScroll.ScrollToVerticalOffset(next);
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
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };

        void Item(string text, Action action)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        // 菜单项与顺序对齐原版 gui.py::_show_menu
        Item("上一位值日生", () => PrevDutyRequested?.Invoke());
        Item("下一位值日生", () => NextDutyRequested?.Invoke());
        menu.Items.Add(new Separator());
        Item("出勤人数", () => AttendanceRequested?.Invoke());
        menu.Items.Add(new Separator());
        Item("编辑作业", () => EditHomeworkRequested?.Invoke());
        menu.Items.Add(new Separator());
        Item("课表管理", () => TimetableManagerRequested?.Invoke());
        Item("临时调课", () => TempAdjustRequested?.Invoke());
        menu.Items.Add(new Separator());
        Item("假期与调休", () => HolidayRequested?.Invoke());
        Item("周末作息", () => WeekendRequested?.Invoke());
        menu.Items.Add(new Separator());
        Item("设置", () => SettingsRequested?.Invoke());
        menu.Items.Add(new Separator());
        Item("随机点名", () => RollCallRequested?.Invoke());
        menu.Items.Add(new Separator());
        Item("退出", () => ExitRequested?.Invoke());

        menu.IsOpen = true;
        e.Handled = true;
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
