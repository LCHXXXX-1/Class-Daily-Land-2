using ClassDailyLand2.App.Integrations.Weather;
using ClassDailyLand2.App.Theme;
using ClassDailyLand2.App.Win32;
using ClassDailyLand2.Core.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClassDailyLand2.App.Windows;

/// <summary>
/// 副岛内容（阶段 3 由插件提供；MVP 允许为空）。
/// <paramref name="Weather"/> 非空表示这一秒的内容是**实时天气**：
/// 收起态画天气动画而不是圆环，展开用「weather_idle_collapse_sec」那套超时（默认 5 分钟）。
/// </summary>
public sealed record SubIslandContent(string Title, string Value, double Progress,
    WeatherSnapshot? Weather = null)
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

    /// <summary>天气内容的收起超时（"5 分钟没操作就收成圆"），与普通内容的 8 秒各走各的。</summary>
    int _weatherIdleSec = 300;
    bool _weatherAnimations = true;

    /// <summary>当前内容是不是天气。进出这个状态时要重算收起时间、并在进场时自动展开一次。</summary>
    bool _weatherMode;

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
        _weatherIdleSec = Math.Clamp(s.Int("weather_idle_collapse_sec"), 0, 3600);
        _weatherAnimations = s.Bool("weather_animations");
        _animMs = s.Bool("fade_animation") ? Math.Clamp(s.Int("animation_duration_ms"), 0, 2000) : 0;
        _fade = s.Bool("fade_animation");
        // 「默认收起」是给课程 / 插件内容的；天气内容一进来就该展开给人看，
        // 否则用户在设置里随便动一下就会把天气压回圆里、再也不出来
        if (s.Bool("sub_island_default_collapsed") && _content?.Weather is null) _expanded = false;
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

        // 内容在「天气 / 非天气」之间切换时按新的超时重新起算；
        // 刚变成天气就自动展开一次 —— 否则用户看到的永远是个收起的圆，
        // 而需求是"展开着显示天气，5 分钟没操作才收成圆"。
        var nowWeather = _content?.Weather is not null;
        if (nowWeather != _weatherMode)
        {
            _weatherMode = nowWeather;
            if (nowWeather && !_expanded) SetExpanded(true);
            else if (_expanded) _collapseAt = Deadline();
        }

        TargetFollow(mainRightEdgeDip, mainTopDip);
        if (_expanded && CollapseSec > 0 && _collapseAt != DateTime.MinValue &&
            DateTime.Now >= _collapseAt)
            SetExpanded(false);
        Refresh();
    }

    /// <summary>当前内容对应的自动收起超时：天气用「无操作」那把尺，其余照旧。</summary>
    int CollapseSec => _weatherMode ? _weatherIdleSec : _autoCollapseSec;

    DateTime Deadline() =>
        CollapseSec > 0 ? DateTime.Now.AddSeconds(CollapseSec) : DateTime.MinValue;

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
        // 天气内容展开时左边多出「7 边距 + 22 图 + 7 间隔 = 36」，比默认的 15 多 21
        var art = _content.Weather is null ? 0 : 21;
        return (ft?.Width ?? 0) + 30 + art;
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
        var weather = _content?.Weather;

        // 天气内容：动画常驻。收起时占满圆（圆环/进度点让位），展开时挪到文字左边。
        if (weather is not null)
        {
            Art.Visibility = Visibility.Visible;
            Art.Kind = weather.Art;
            Art.IsDay = weather.IsDay;
            Art.Animated = _weatherAnimations;
        }
        else Art.Visibility = Visibility.Collapsed;

        RingOutline.Visibility = collapsed && weather is null ? Visibility.Visible : Visibility.Collapsed;
        ProgressDot.Visibility = collapsed && weather is null && _content is not null && _content.Progress > 0
            ? Visibility.Visible : Visibility.Collapsed;

        if (collapsed)
        {
            Art.HorizontalAlignment = HorizontalAlignment.Center;
            Art.Margin = new Thickness(0);
            Art.Width = 26;
            Art.Height = 26;

            if (weather is null)
            {
                var d = Math.Clamp(_content?.Progress ?? 0, 0, 1) * 10;
                ProgressDot.Width = d;
                ProgressDot.Height = d;
            }
            Label.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (weather is not null)
            {
                Art.HorizontalAlignment = HorizontalAlignment.Left;
                Art.Margin = new Thickness(7, 0, 0, 0);
                Art.Width = 22;
                Art.Height = 22;
                Label.Margin = new Thickness(36, 0, 10, 0);
            }
            else Label.Margin = new Thickness(15, 0, 10, 0);

            Label.Text = RenderText();
            Label.Visibility = Visibility.Visible;
        }
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        _collapseAt = expanded && CollapseSec > 0
            ? DateTime.Now.AddSeconds(CollapseSec)
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
