using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClassDailyLand.App.Theme;
using ClassDailyLand.App.Win32;
using ClassDailyLand.Core;
using ClassDailyLand.Core.Managers;
using ClassDailyLand.Core.Models;

namespace ClassDailyLand.App.Windows;

/// <summary>副岛内容（阶段 3 由插件提供；MVP 允许为空）。</summary>
public sealed record SubIslandContent(string Title, string Value, double Progress)
{
    public static SubIslandContent? FromProvider(Func<SubIslandContent?>? provider)
    {
        try { return provider?.Invoke(); }
        catch { return null; }     // 插件异常不能拖垮 UI
    }
}

/// <summary>
/// 副岛：贴在主岛右缘 +8px，高 40px；收起为 40×40 圆，可展开到 44~380px。
/// 主岛不在 Compact/Countdown/Alert 时自动隐藏。
///
/// **原生化**：视觉树在 SubIslandWindow.xaml（Border 胶囊 + Ellipse 圆环/进度点 +
/// TextBlock 文字），本文件只留状态机与动画驱动。位移/宽度用 WPF 的
/// DoubleAnimation（缓动取 Themes/Tokens.Base.xaml 的 EaseOutCubic），
/// 不再有 33ms 帧 timer + OnRender。
/// </summary>
public sealed partial class SubIslandWindow : Window
{
    const double H = 40;
    const double CollapsedW = 40;
    const double MinExpandedW = 44;
    const double MaxExpandedW = 380;
    const double Gap = 8;

    double _curW = CollapsedW, _curX = 0, _curY = 0;
    double _tgtW = CollapsedW, _tgtX = 0, _tgtY = 0;
    double _animMs = 220;
    bool _fade = true;
    bool _expanded;
    DateTime _collapseAt = DateTime.MinValue;
    int _autoCollapseSec = 8;
    bool _enabledBySetting = true;
    bool _mainIslandVisible = true;
    SubIslandContent? _content;
    double _pixelsPerDip = 1;

    Func<SubIslandContent?>? _provider;

    public bool Expanded => _expanded;

    public SubIslandWindow()
    {
        InitializeComponent();

        Capsule.Background = Palette.IslandBg;
        RingOutline.Stroke = Palette.RingUpcoming;
        ProgressDot.Fill = Palette.RingUpcoming;
        Label.Foreground = Palette.IslandText;
        Label.FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");

        // 先把几何属性落到具体值：WPF 的 Left/Top 默认是 NaN，
        // 拿 NaN 当起点做 DoubleAnimation 会直接抛 AnimationException。
        Left = 0;
        Top = -80;
        Width = CollapsedW;
        Height = H;

        SourceInitialized += (_, _) =>
        {
            Interop.MakeToolWindowNoActivate(this);
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        };
        Palette.Changed += (_, _) => ApplyPalette();
    }

    void ApplyPalette()
    {
        Capsule.Background = Palette.IslandBg;
        RingOutline.Stroke = Palette.RingUpcoming;
        ProgressDot.Fill = Palette.RingUpcoming;
        Label.Foreground = Palette.IslandText;
    }

    public void ApplySettings(Core.Managers.SettingsManager s)
    {
        _enabledBySetting = s.Bool("show_sub_island");
        _autoCollapseSec = Math.Clamp(s.Int("sub_island_auto_collapse_sec"), 0, 600);
        _animMs = s.Bool("fade_animation") ? Math.Clamp(s.Int("animation_duration_ms"), 0, 2000) : 0;
        _fade = s.Bool("fade_animation");
        if (s.Bool("sub_island_default_collapsed")) _expanded = false;
        Refresh();
    }

    /// <summary>阶段 3：宿主注入数据提供者（UI 线程调用，必须够快）。</summary>
    public void SetProvider(Func<SubIslandContent?>? provider)
    {
        _provider = provider;
        Refresh();
    }

    /// <summary>主岛状态决定副岛是否显示。</summary>
    public void SetMainIslandVisible(bool visible)
    {
        _mainIslandVisible = visible;
        Refresh();
    }

    /// <summary>每秒 tick：拉一次内容并跟随主岛右缘。</summary>
    public void Tick(double mainRightEdgeDip, double mainTopDip, ScheduleStatus status)
    {
        _content = SubIslandContent.FromProvider(_provider);
        TargetFollow(mainRightEdgeDip, mainTopDip);
        if (_expanded && _autoCollapseSec > 0 && _collapseAt != DateTime.MinValue &&
            DateTime.Now >= _collapseAt)
            SetExpanded(false);
        Refresh();
    }

    void TargetFollow(double mainRight, double mainTop)
    {
        var work = WindowUtil.WorkAreaDip();
        var w = _expanded
            ? Math.Clamp(MeasureContentWidth(), MinExpandedW, MaxExpandedW)
            : CollapsedW;
        var x = Math.Min(mainRight + Gap, work.Right - w - 2);
        _tgtW = w;
        _tgtX = x;
        _tgtY = mainTop;
        BeginAnim();
    }

    /// <summary>FormattedText 只用来量宽（不绘制），保持与原版一致的计算方式。</summary>
    FormattedText? BuildText(string text)
    {
        if (text.Length == 0) return null;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI, Segoe UI"), 13, Palette.IslandText, _pixelsPerDip);
        return ft;
    }

    double MeasureContentWidth()
    {
        if (_content is null) return MinExpandedW;
        var ft = BuildText(RenderText());
        return (ft?.Width ?? 0) + 30;
    }

    string RenderText()
    {
        var c = _content;
        if (c is null) return "";
        return string.IsNullOrEmpty(c.Title) ? c.Value : $"{c.Title} {c.Value}".Trim();
    }

    void BeginAnim()
    {
        if (!_fade || _animMs <= 1)
        {
            _curW = _tgtW; _curX = _tgtX; _curY = _tgtY;
            Apply();
            return;
        }
        var w = Slide(_tgtW); if (w is not null) BeginAnimation(WidthProperty, w);
        var x = Slide(_tgtX); if (x is not null) BeginAnimation(LeftProperty, x);
        var y = Slide(_tgtY); if (y is not null) BeginAnimation(TopProperty, y);
        _curW = _tgtW; _curX = _tgtX; _curY = _tgtY;
    }

    DoubleAnimation? Slide(double to) => Anim.Slide(to, (int)_animMs);

    void Apply()
    {
        Width = _curW;
        Height = H;
        Left = _curX;
        Top = _curY;
    }

    void Refresh()
    {
        var shouldShow = _enabledBySetting && _mainIslandVisible && _content is not null;
        if (shouldShow && !IsVisible) Show();
        if (!shouldShow && IsVisible) Hide();

        var collapsed = _curW <= CollapsedW + 2;
        RingOutline.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        ProgressDot.Visibility = collapsed && _content is not null && _content.Progress > 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (collapsed)
        {
            var d = Math.Clamp(_content?.Progress ?? 0, 0, 1) * 10;
            ProgressDot.Width = d;
            ProgressDot.Height = d;
            Label.Visibility = Visibility.Collapsed;
        }
        else
        {
            Label.Text = RenderText();
            Label.Visibility = Visibility.Visible;
        }
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        _collapseAt = expanded && _autoCollapseSec > 0
            ? DateTime.Now.AddSeconds(_autoCollapseSec)
            : DateTime.MinValue;
        TargetFollow(_tgtX - Gap, _tgtY);
        BeginAnim();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        SetExpanded(!_expanded);
    }
}
