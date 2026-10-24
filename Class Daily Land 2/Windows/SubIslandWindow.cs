using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
/// </summary>
public sealed class SubIslandWindow : Window
{
    const double H = 40;
    const double CollapsedW = 40;
    const double MinExpandedW = 44;
    const double MaxExpandedW = 380;
    const double Gap = 8;

    static readonly Typeface UiTypeface = new("Microsoft YaHei UI, Segoe UI");
    static readonly Pen RingPen = MakeRingPen();
    static readonly Brush RingBrush = MakeRingBrush();
    FormattedText? _cachedFt;
    string _cachedFtKey = "";

    double _curW = CollapsedW, _curX = 0, _curY = 0;
    double _tgtW = CollapsedW, _tgtX = 0, _tgtY = 0;
    double _fromW = CollapsedW, _fromX = 0, _fromY = 0;
    DateTime _animStart = DateTime.MinValue;
    double _animMs = 220;
    bool _fade = true;
    bool _expanded;
    DateTime _collapseAt = DateTime.MinValue;
    int _autoCollapseSec = 8;
    bool _enabledBySetting = true;
    bool _mainIslandVisible = true;
    SubIslandContent? _content;
    double _pixelsPerDip = 1;

    readonly DispatcherTimer _frameTimer = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(33),
    };
    DateTime _lastFrame = DateTime.MinValue;

    Func<SubIslandContent?>? _provider;

    public bool Expanded => _expanded;

    public SubIslandWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Focusable = false;
        Width = CollapsedW;
        Height = H;
        Left = 0;
        Top = -80;

        _frameTimer.Tick += (_, _) => Step();
        SourceInitialized += (_, _) =>
        {
            Interop.MakeToolWindowNoActivate(this);
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        };
        Palette.Changed += (_, _) => InvalidateVisual();
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

    static Pen MakeRingPen()
    {
        var p = new Pen(new SolidColorBrush(Color.FromRgb(0x4d, 0xa3, 0xff)), 2);
        p.Freeze();
        return p;
    }

    static Brush MakeRingBrush()
    {
        var b = new SolidColorBrush(Color.FromRgb(0x4d, 0xa3, 0xff));
        b.Freeze();
        return b;
    }

    /// <summary>FormattedText 只在内容/字号变化时重建（OnRender 内禁止 new 大对象）。</summary>
    FormattedText? BuildText(string text)
    {
        if (text.Length == 0) return null;
        var key = text + "|" + _pixelsPerDip;
        if (_cachedFt is not null && key == _cachedFtKey) return _cachedFt;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            UiTypeface, 13, Palette.IslandText, _pixelsPerDip);
        _cachedFt = ft;
        _cachedFtKey = key;
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
            InvalidateVisual();
            return;
        }
        _fromW = _curW; _fromX = _curX; _fromY = _curY;
        _animStart = DateTime.Now;
        if (!_frameTimer.IsEnabled)
        {
            _lastFrame = DateTime.Now;
            _frameTimer.Start();
        }
    }

    void Step()
    {
        var now = DateTime.Now;
        _lastFrame = now;
        var t = _animMs <= 0 ? 1 : Math.Clamp((now - _animStart).TotalMilliseconds / _animMs, 0, 1);
        var k = Easing.OutCubic(t);
        _curW = _fromW + (_tgtW - _fromW) * k;
        _curX = _fromX + (_tgtX - _fromX) * k;
        _curY = _fromY + (_tgtY - _fromY) * k;
        if (t >= 1) { _curW = _tgtW; _curX = _tgtX; _curY = _tgtY; _frameTimer.Stop(); }
        Apply();
        InvalidateVisual();
    }

    void Apply()
    {
        if (Math.Abs(Width - _curW) > 0.4 || Math.Abs(Left - _curX) > 0.4 || Math.Abs(Top - _curY) > 0.4)
        {
            Width = _curW;
            Height = H;
            Left = _curX;
            Top = _curY;
        }
    }

    void Refresh()
    {
        var shouldShow = _enabledBySetting && _mainIslandVisible && _content is not null;
        if (shouldShow && !IsVisible) Show();
        if (!shouldShow && IsVisible) Hide();
        InvalidateVisual();
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

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0.5 || _content is null) return;

        var radius = h / 2;
        var rect = new Rect(0.5, 0.5, w - 1, h - 1);
        dc.DrawRoundedRectangle(Palette.IslandBg, null, rect, radius, radius);

        if (w <= CollapsedW + 2)
        {
            // 收起态：小圆环 + 进度点
            var c = new Point(w / 2, h / 2);
            dc.DrawEllipse(null, RingPen, c, 9, 9);
            var frac = Math.Clamp(_content.Progress, 0, 1);
            if (frac > 0)
                dc.DrawEllipse(RingBrush, null, c, 5 * frac, 5 * frac);
            return;
        }

        var ft = BuildText(RenderText());
        if (ft is null) return;
        dc.PushClip(new RectangleGeometry(rect, radius, radius));
        dc.DrawText(ft, new Point(15, (h - ft.Height) / 2));
        dc.Pop();
    }

    protected override void OnClosed(EventArgs e)
    {
        _frameTimer.Stop();
        base.OnClosed(e);
    }
}
