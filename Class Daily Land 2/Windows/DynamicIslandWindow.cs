using ClassDailyLand2.App.Integrations.Audio;
using ClassDailyLand2.App.Integrations.Disaster;
using ClassDailyLand2.App.Theme;
using ClassDailyLand2.App.Win32;
using ClassDailyLand2.Core.Models;
using ClassDailyLand2.Plugin;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClassDailyLand2.App.Windows;

public enum IslandState { Hidden, Mini, Compact, Countdown, Alert, Banner }

/// <summary>主岛需要的外部输入（每个 tick 由 AppController 组装后推入）。</summary>
public sealed record IslandInput(
    ScheduleStatus Status,
    string IslandText,
    int ExtraWidth,
    int CountdownSec,
    bool HideOnFullscreen,
    bool IsFullscreen,
    bool IsOfficeForeground,
    bool Visible,
    IslandBadge? Badge = null,
    MediaDisplay? Media = null,
    IslandEmergency? Emergency = null);

/// <summary>
/// 灵动岛：置顶、无边框、不抢焦点。
/// 版式严格对齐原版 PySide6 dynamic_island.py：
///   胶囊底色不透明 #1c1c1e；左侧 28px 见方圆环（灰轨道 #3a3a3c + 彩色进度弧）；
///   文字白色粗体 14px，起点 = 6 + 28 + 8；倒计时时蓝条铺满整个胶囊高度。
///
/// **原生化**：视觉树在 DynamicIslandWindow.xaml，全部是原生元素
/// （Border 胶囊 / Ellipse 圆环与圆点 / Path 进度弧 / TextBlock 文案 /
///  Rectangle 蓝条与流光）。本文件只保留状态机与动画编排：
///   · 窗口几何 → DoubleAnimation（EaseOutCubic，时长随设置）
///   · 跑马灯 / 流光 → DoubleAnimationUsingKeyFrames + RepeatBehavior.Forever
///   · 文案切换 → 旧文案上移淡出 + 新文案下方推入（双 TextBlock）
///   · 倒计时填充 → 层宽动画 + 前缘光晕
/// 原来的 33ms OnRender 帧循环整个去掉了。
/// </summary>
public sealed partial class DynamicIslandWindow : Window
{
    // ————————————————————— 几何常量（对齐原版） —————————————————————
    const double CapsuleH = 40;
    const double MiniW = 48;
    const double CompactBaseW = 240;
    const double CountdownW = 600;
    const double BannerH = 72;
    const double HiddenYOffset = -64;
    const int StepGapMs = 60;

    const double RingSize = 28;          // 圆环占位（含左右留白）
    const double RingVisual = 20;        // 实际圆的直径
    const double RingX = 6;              // 圆环左边界
    const double RingStroke = 2;
    const double TextGapAfterRing = 8;
    const double RightPadding = 10;
    const double TextSize = 14;

    const double MarqueeSpeed = 32;    // px/s
    const double MarqueeGap = 40;      // 首尾间隔
    const double MarqueeRestSec = 0.9; // 每轮回起点前停 0.9s
    const double SheenCycleMs = 1400;
    const double BarInset = 1;
    /// <summary>文案切换时长：倒计时每秒变一次，太快（140ms）看着就是"硬跳"，
    /// 放慢到 300ms 并配合先快后慢的缓动，才像数字在往上滚。</summary>
    const double TextSwitchMs = 300;
    const double CountdownFillMs = 180;
    const double BadgeGap = 10;        // 徽标与文案之间的最小间距
    const double BadgeFontSize = 11;
    const double MediaGap = 8;         // 媒体片段与文案之间的间距

    static readonly Brush CapsuleFill = Frozen(0x1c, 0x1c, 0x1e);
    static readonly Brush RingTrackBrush = Frozen(0x3a, 0x3a, 0x3c);
    static readonly Brush RingOngoing = Frozen(0x30, 0xd1, 0x58);
    static readonly Brush RingUpcoming = Frozen(0x4d, 0xa3, 0xff);
    static readonly Brush RingIdle = Frozen(0x8a, 0x8a, 0x8e);
    static readonly Brush TextFill = Frozen(0xff, 0xff, 0xff);
    static readonly Brush AlertStartFill = Frozen(0x30, 0xd1, 0x58);
    static readonly Brush AlertEndFill = Frozen(0xff, 0xff, 0xff);
    static readonly Brush ProgressFill = Frozen(0x0a, 0x84, 0xff);
    static readonly Brush SheenFill = Frozen(0xff, 0xff, 0xff, 0x46);
    static readonly Brush HeadFill = Frozen(0xf5, 0xf5, 0xff, 0xe6);
    static readonly Brush HeadGlowBrush = MakeGlow();
    static readonly Brush MiniOngoing = Frozen(0x30, 0xd1, 0x58);
    static readonly Brush MiniUpcoming = Frozen(0x4d, 0xa3, 0xff);
    static readonly Brush MiniIdle = Frozen(0x8a, 0x8a, 0x8e);
    static readonly Typeface Ui = new("Microsoft YaHei UI, Segoe UI");
    static readonly FontFamily UiFont = new("Microsoft YaHei UI, Segoe UI");

    static Brush MakeGlow()
    {
        var brush = new LinearGradientBrush(
            Color.FromArgb(0x00, 0x96, 0xc8, 0xff),
            Color.FromArgb(0x96, 0xc8, 0xc8, 0xff),
            new Point(0, 0.5), new Point(1, 0.5));
        brush.Freeze();
        return brush;
    }

    static Brush Frozen(byte r, byte g, byte b, byte a = 0xff)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    // ————————————————————— 状态 —————————————————————
    IslandState _state = IslandState.Hidden;
    IslandInput? _input;
    string _text = "";
    string _prevText = "";
    DateTime _textSwitchAt = DateTime.MinValue;
    DateTime _alertUntil = DateTime.MinValue;
    DateTime _alertClock = DateTime.MinValue;
    bool _alertIsStart;
    string _alertText = "";
    bool _wakeupPending;
    DispatcherTimer? _wakeTimer;
    double _topMargin = 6;
    double _alertWidth = 300;
    int _alertHoldMs = 600;
    double _animDurationMs = 220;
    bool _fadeAnimation = true;
    int _countdownSec = 60;
    private double _panelWidth = 0;

    // 目标几何（当前值由 WPF 动画托管，这里只记目标）
    double _tgtW = MiniW, _tgtH = CapsuleH, _tgtX, _tgtY;
    double _lastRatio = -1;          // 圆环进度，变了才重算弧
    bool _marqueeOn;
    bool _fillPending;               // 刚进倒计时：蓝条要做一次长出动画

    public event Action? Clicked;

    /// <summary>点了媒体片段（正文）= 展开媒体卡。</summary>
    public event Action? MediaExpandRequested;

    /// <summary>点了媒体片段上的播放/暂停按钮。</summary>
    public event Action? MediaPlayPauseRequested;
    public event Action<IslandState>? IslandStateChanged;

    public IslandState State => _state;
    public double RightEdgeDip => _tgtX + _tgtW;
    public double TopDip => _tgtY;
    public double HeightDip => _tgtH;

    /// <summary>下拉面板展开时，让主岛宽度与面板总宽保持一致；传 0 恢复原宽。</summary>
    public void SetPanelWidth(double width)
    {
        _panelWidth = width;
        if (_state == IslandState.Compact || _state == IslandState.Countdown)
        {
            // 触发宽度动画重新计算
            AnimateTo(GeometryFor(_state));
        }
    }

    public DynamicIslandWindow()
    {
        InitializeComponent();

        Capsule.Background = CapsuleFill;
        RingTrack.Stroke = RingTrackBrush;
        RingArc.Stroke = RingUpcoming;
        // ArcSegment 从 3 点方向起笔，原版是从 12 点开始 —— 整体转 -90°
        RingArc.RenderTransform = new RotateTransform(-90, RingVisual / 2, RingVisual / 2);
        TextCur.Foreground = TextFill;
        TextPrev.Foreground = TextFill;
        TextCur.FontFamily = UiFont;
        TextCur2.FontFamily = UiFont;
        TextPrev.FontFamily = UiFont;
        TextCur2.Foreground = TextFill;
        CountdownLayer.Background = ProgressFill;
        // 蓝条里的流光/前缘都是矩形元素，必须按圆角裁掉，否则会扫出蓝色区域外
        CountdownLayer.SizeChanged += (_, _) => UpdateBarClip();
        Sheen.Fill = SheenFill;
        HeadGlow.Fill = HeadGlowBrush;
        HeadDot.Fill = HeadFill;

        Left = 0; Top = HiddenYOffset; Width = MiniW; Height = CapsuleH;

        // 媒体片段上的两个热区：正文→展开卡，按钮→播放/暂停
        MediaHost.MouseLeftButtonUp += (_, e) =>
        {
            MediaExpandRequested?.Invoke();
            e.Handled = true;
        };
        MediaButton.MouseLeftButtonUp += (_, e) =>
        {
            MediaPlayPauseRequested?.Invoke();
            e.Handled = true;
        };

        SourceInitialized += (_, _) =>
        {
            Interop.MakeToolWindowNoActivate(this);
            ApplyChrome();
        };
        // SizeChanged 在"窗口几何动画"期间每帧都发：
        //   · ApplyChrome 只是几个属性赋值（圆角/边距/裁切区），便宜，必须每帧跟；
        //   · RefreshMarquee 要重新 MeasureText + 重建一遍关键帧动画 —— 每帧做纯属浪费，
        //     中途重启还会让跑马灯"抖一下"。改成 80ms 防抖，几何落定后再排一次。
        SizeChanged += (_, _) => { ApplyChrome(); ScheduleMarqueeRefresh(); };
        Palette.Changed += OnPaletteChanged;
        // 静态事件会一直抓住这个窗口（连同整棵视觉树）：窗口一旦重建就是硬泄漏
        Closed += (_, _) => Palette.Changed -= OnPaletteChanged;
    }

    /// <summary>调色板变化（具名方法：静态事件必须能退订，lambda 退不掉）。</summary>
    void OnPaletteChanged(object? sender, EventArgs e) => ApplyPalette();


    void ApplyPalette()
    {
        // 岛与面板是固定深色（原版如此），主题切换只刷新文字色
        TextCur.Foreground = TextFill;
        TextPrev.Foreground = TextFill;
        AlertText.Foreground = _alertIsStart ? AlertStartFill : AlertEndFill;
    }

    // ————————————————————— 设置下发 —————————————————————
    public void ApplySettings(Core.Managers.SettingsManager s)
    {
        _topMargin = Math.Clamp(s.Int("island_top_margin"), 0, 200);
        _alertWidth = Math.Clamp(s.Int("island_alert_width"), 160, 900);
        _alertHoldMs = Math.Clamp(s.Int("island_alert_hold_ms"), 200, 10000);
        _animDurationMs = s.Bool("fade_animation") ? Math.Clamp(s.Int("animation_duration_ms"), 0, 2000) : 0;
        _fadeAnimation = s.Bool("fade_animation");
        _countdownSec = Math.Max(5, s.Int("island_countdown_sec"));
        if (_input is not null) Update(_input);
    }

    // ————————————————————— 主循环入口 —————————————————————
    public void Update(IslandInput input)
    {
        _input = input;
        Interop.KeepTopmost(this);
        // 徽标/媒体的宽度都要在算窗口几何之前定下来，否则这一帧胶囊还是旧宽度
        ApplyBadge();
        ApplyMedia();
        ApplyChrome();

        if (!input.Visible) { SetState(IslandState.Hidden); return; }

        // 灾害预警优先：红色倒计时条直接接管胶囊（比课程倒计时更要紧）
        if (input.Emergency is not null)
        {
            if (_state != IslandState.Countdown) EnterCountdown();
            SetState(IslandState.Countdown);
            RefreshLive();
            return;
        }

        if (_alertUntil > DateTime.Now)
        {
            SetState(IslandState.Alert);
            return;
        }

        if (input.HideOnFullscreen && input.IsFullscreen)
        {
            SetState(IslandState.Hidden);
            return;
        }

        if (_wakeupPending) return;

        var countdown = input.Status.IsUpcoming
            && input.Status.UntilSec <= input.CountdownSec
            && !input.IsOfficeForeground;
        var next = countdown ? IslandState.Countdown : IslandState.Compact;
        if (next == IslandState.Countdown && _state != IslandState.Countdown)
            EnterCountdown();
        SetState(next);
        // 状态没变时也要刷新：圆环进度和倒计时条是每秒都在变的
        RefreshLive();
    }

    /// <summary>每个 tick 都要跟的那部分：进度弧 + 倒计时蓝条宽度。</summary>
    void RefreshLive()
    {
        if (_state is IslandState.Hidden or IslandState.Mini or IslandState.Alert) return;
        UpdateRing(_input?.Status ?? ScheduleStatus.Empty);
        if (_state == IslandState.Countdown) UpdateCountdown();
    }

    public void ShowAlert(string text, bool isStart)
    {
        _alertText = text;
        _alertIsStart = isStart;
        _alertClock = DateTime.Now;
        _alertUntil = DateTime.Now.AddMilliseconds(_alertHoldMs);
        SetState(IslandState.Alert);
    }

    public void ClearAlert()
    {
        _alertUntil = DateTime.MinValue;
        if (_input is not null) Update(_input);
    }

    /// <summary>唤醒动画：Hidden →(200ms)→ Mini →(60ms)→ Compact。</summary>
    public void StartWakeup(bool animate)
    {
        // ★ t1（200ms 起步）和 t2（逐格）都必须挂在同一个字段上：
        //   原来只有 t2 记进 _wakeStep，t1 是局部变量 —— 唤醒进行中再调一次本方法，
        //   停不掉旧 t1，两只 t1 各自孵出一只 t2，第二只把 _wakeStep 覆盖掉后
        //   第一只就成了没人管的定时器，动画状态机直接错乱。
        _wakeTimer?.Stop();
        _wakeTimer = null;

        if (!animate) { _wakeupPending = false; SetState(IslandState.Compact); return; }
        _wakeupPending = true;
        SetState(IslandState.Mini);
        var t1 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _wakeTimer = t1;
        t1.Tick += (_, _) =>
        {
            t1.Stop();
            if (!ReferenceEquals(_wakeTimer, t1)) return;   // 已被更新的一次唤醒作废
            if (!_wakeupPending) return;
            var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(StepGapMs) };
            _wakeTimer = t2;
            t2.Tick += (_, _) =>
            {
                t2.Stop();
                if (!ReferenceEquals(_wakeTimer, t2)) return;
                _wakeTimer = null;
                _wakeupPending = false;
                SetState(IslandState.Compact);
            };
            t2.Start();
        };
        t1.Start();
    }

    // ————————————————————— 状态机 —————————————————————
    void SetState(IslandState next)
    {
        EnsureSmoothTimer();   // 幂等：逐帧回调只挂一次（Hidden 时 SmoothTick 自己早退）

        if (next == _state) { AnimateTo(GeometryFor(_state)); return; }
        _state = next;
        ApplyBadge();                 // 状态变了徽标可见性也变，宽度必须在算几何之前定
        AnimateTo(GeometryFor(next));
        IslandStateChanged?.Invoke(next);
        ApplyStateVisuals();
    }

    (double w, double h, double x, double y) GeometryFor(IslandState st)
    {
        var input = _input;
        var work = WindowUtil.WorkAreaDip(this);
        // 居中基准 = 工作区中心（原来用全屏宽的一半，任务栏在左/右时会和按工作区
        // 定位的下拉面板错开半个任务栏宽，面板还可能压到任务栏底下）
        var center = work.Left + work.Width / 2;
        double w = st switch
        {
            IslandState.Mini or IslandState.Hidden => MiniW,
            IslandState.Compact => _panelWidth > 0 ? _panelWidth : CompactBaseW + ExtraTotal,
            IslandState.Countdown => _panelWidth > 0 ? _panelWidth : CountdownW,
            IslandState.Alert => _alertWidth,
            IslandState.Banner => 560 + ExtraTotal,
            _ => MiniW,
        };
        double h = st == IslandState.Banner ? BannerH : CapsuleH;
        double x = Math.Round(center - w / 2);
        double y = st == IslandState.Hidden
            ? HiddenYOffset
            : Math.Round(work.Top + _topMargin - (h - CapsuleH) / 2);
        return (w, h, x, y);
    }

    void AnimateTo((double w, double h, double x, double y) target)
    {
        var changed = Math.Abs(target.w - _tgtW) > 0.5 || Math.Abs(target.x - _tgtX) > 0.5 ||
                      Math.Abs(target.y - _tgtY) > 0.5 || Math.Abs(target.h - _tgtH) > 0.5;
        if (!changed) return;

        _tgtW = target.w; _tgtH = target.h; _tgtX = target.x; _tgtY = target.y;

        if (!_fadeAnimation || _animDurationMs <= 1)
        {
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Width = target.w; Height = target.h; Left = target.x; Top = target.y;
            ApplyChrome();
            return;
        }

        var ms = (int)_animDurationMs;
        this.SlideTo(WidthProperty, target.w, ms);
        this.SlideTo(HeightProperty, target.h, ms);
        this.SlideTo(LeftProperty, target.x, ms);
        this.SlideTo(TopProperty, target.y, ms);
    }

    /// <summary>尺寸/位置变化后同步：胶囊圆角、文字裁切区、圆环与圆点位置。</summary>
    void ApplyChrome()
    {
        var h = ActualHeight;
        if (h <= 0.5) return;
        Capsule.CornerRadius = new CornerRadius(h / 2);
        CountdownLayer.Height = Math.Max(2, h - BarInset * 2);
        CountdownLayer.CornerRadius = new CornerRadius(CountdownLayer.Height / 2);
        Sheen.Height = CountdownLayer.Height;
        HeadGlow.Height = CountdownLayer.Height;
        UpdateBarClip();

        // 文字区：左起 42，右侧让出 10（加长片段另算）
        var left = _state is IslandState.Mini or IslandState.Hidden ? 12 : TextLeft();
        var right = RightPadding + ExtraTotal;
        TextClip.Margin = new Thickness(left, 0, Math.Max(0, right), 0);
        TextClip.Height = h;

        PluginBadgeHost.Margin = new Thickness(0, 0, RightPadding, 0);
        MediaHost.Margin = new Thickness(0, 0, RightPadding, 0);
    }

    /// <summary>胶囊要额外让出的宽度：插件声明 + 徽标实际宽度 + 间距。</summary>
    /// <summary>
    /// 胶囊要额外让出的宽度：插件声明 + 徽标 + 媒体片段。
    /// **灾害预警接管时一律为 0** —— 那时红条要独占整条胶囊，
    /// 再叠加插件的加长段会出现"红条只铺到一半、右边还挂着插件徽标"的错位。
    /// </summary>
    double ExtraTotal => _input?.Emergency is not null
        ? 0
        : (_input?.ExtraWidth ?? 0)
          + (_mediaWidth > 0 ? _mediaWidth + MediaGap : 0)
          + (_badgeWidth > 0 ? _badgeWidth + BadgeGap : 0);

    double _badgeWidth;
    double _mediaWidth;

    /// <summary>
    /// 画插件徽标。宽度用 FormattedText 量（不依赖布局，同一帧就能算出胶囊宽度），
    /// Border 自己按同样的内边距排版，两边不会差出来。
    /// </summary>
    void ApplyBadge()
    {
        var badge = _input?.Badge;
        // 媒体片段占了右端、或灾害预警接管胶囊时，徽标一律让位
        if (badge is null || _input?.Media is not null || _input?.Emergency is not null ||
            _state is IslandState.Mini or IslandState.Hidden or IslandState.Alert)
        {
            if (PluginBadgeHost.Visibility != Visibility.Collapsed)
            {
                PluginBadgeHost.Visibility = Visibility.Collapsed;
                PluginBadgeText.Text = "";
            }
            _badgeWidth = 0;
            return;
        }

        if (!string.Equals(PluginBadgeText.Text, badge.Text, StringComparison.Ordinal))
            PluginBadgeText.Text = badge.Text;

        var (fg, bg) = BadgeColors(badge.Tone);
        PluginBadgeText.Foreground = fg;
        PluginBadgeHost.Background = bg;
        PluginBadgeHost.Visibility = Visibility.Visible;

        _badgeWidth = MeasureBadge(badge.Text);
    }

    /// <summary>
    /// 画媒体片段。宽度取设置里的 audio_max_width（胶囊会跟着加长），
    /// 封面没有时只显示文字，文字太长由 TextTrimming 省略。
    /// </summary>
    void ApplyMedia()
    {
        var media = _input?.Media;
        if (media is null || _state is IslandState.Mini or IslandState.Hidden
            or IslandState.Alert or IslandState.Countdown)
        {
            if (MediaHost.Visibility != Visibility.Collapsed)
            {
                MediaHost.Visibility = Visibility.Collapsed;
                MediaArt.Source = null;
            }
            _mediaWidth = 0;
            return;
        }

        MediaTitle.Text = media.Title;
        MediaArtist.Text = media.Artist;
        MediaArtist.Visibility = string.IsNullOrEmpty(media.Artist) ? Visibility.Collapsed : Visibility.Visible;
        MediaTitle.Foreground = Palette.IslandText;
        MediaArtist.Foreground = Palette.IslandText;
        MediaGlyph.Foreground = Palette.IslandText;

        MediaArt.Source = media.Artwork;
        MediaArtFrame.Visibility = media.Artwork is null ? Visibility.Collapsed : Visibility.Visible;

        // E768 = ▶ / E769 = ⏸
        MediaGlyph.Text = media.Playing ? "\uE769" : "\uE768";

        _mediaWidth = Math.Clamp(media.Width, 120, 400);
        MediaHost.Width = _mediaWidth;
        MediaHost.Visibility = Visibility.Visible;
    }

    double MeasureBadge(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI, Segoe UI"), BadgeFontSize, Brushes.White,
            dpi <= 0 ? 1 : dpi);
        return Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 14;   // Padding 7+7
    }

    /// <summary>徽标配色（深色胶囊上固定用亮色文字 + 半透明底）。</summary>
    static (Brush Foreground, Brush Background) BadgeColors(BadgeTone tone)
    {
        (Color fg, Color bg) = tone switch
        {
            BadgeTone.Accent => (Color.FromRgb(0xA9, 0xD1, 0xFF), Color.FromArgb(0x3A, 0x0A, 0x84, 0xFF)),
            BadgeTone.Success => (Color.FromRgb(0x8B, 0xEA, 0xA8), Color.FromArgb(0x3A, 0x30, 0xD1, 0x58)),
            BadgeTone.Warning => (Color.FromRgb(0xFF, 0xD6, 0x0A), Color.FromArgb(0x3A, 0xFF, 0xB9, 0x00)),
            BadgeTone.Danger => (Color.FromRgb(0xFF, 0x9A, 0x92), Color.FromArgb(0x3A, 0xFF, 0x45, 0x3A)),
            _ => (Color.FromRgb(0xE6, 0xE6, 0xE6), Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
        };
        return (Frozen(fg), Frozen(bg));
    }

    static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    double TextLeft() => RingX + RingSize + TextGapAfterRing;

    double AvailableTextWidth()
    {
        var left = _state is IslandState.Mini or IslandState.Hidden ? 12 : TextLeft();
        var right = RightPadding + ExtraTotal;
        return Math.Max(0, ActualWidth - left - right);
    }

    // ————————————————————— 文案 —————————————————————
    public void SetText(string text) => SetText(text, animate: true);

    /// <summary>
    /// 设置文案。<paramref name="animate"/>=false 用于每秒都在变的灾害倒计时文案 ——
    /// 那种场景每帧做一次"上移淡出"会很闹，红条收缩本身已经是动效了。
    /// </summary>
    public void SetText(string text, bool animate)
    {
        if (string.Equals(text, _text, StringComparison.Ordinal)) return;
        _prevText = _text;
        _text = text;
        _textSwitchAt = DateTime.Now;
        TextPrev.Text = _prevText;
        TextCur.Text = _text;
        TextCur2.Text = _text;

        if (animate)
        {
            PlayTextSwitch();
        }
        else
        {
            // 直接落位：旧文案藏起来，新文案立即可见
            TextPrev.BeginAnimation(OpacityProperty, null);
            TextPrev.Opacity = 0;
            TextCur.BeginAnimation(OpacityProperty, null);
            TextCur.Opacity = 1;
            MarqueeShift.BeginAnimation(TranslateTransform.YProperty, null);
            MarqueeShift.Y = 0;
        }
        RefreshMarquee();
    }

    /// <summary>旧文案上移淡出、新文案从下方推入（对齐原版 DrawText 的两段式）。</summary>
    void PlayTextSwitch()
    {
        var h = ActualHeight;
        var dur = Anim.Span((int)TextSwitchMs);

        // 位移与淡出都用「先快后慢」（EaseOut）的样条：起手迅速离位、末尾缓缓归位，
        // 比线性顺滑得多 —— 倒计时每秒触发一次，曲线不对就会显得一跳一跳。
        var slideOut = new KeySpline(0.16, 0.84, 0.28, 1);
        var slideIn = new KeySpline(0.12, 0.82, 0.24, 1);

        TextPrevShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, -h, dur) { EasingFunction = Anim.Ease() });

        var prevFade = new DoubleAnimationUsingKeyFrames();
        prevFade.KeyFrames.Add(new SplineDoubleKeyFrame(1,
            KeyTime.FromTimeSpan(TimeSpan.Zero), slideOut));
        prevFade.KeyFrames.Add(new SplineDoubleKeyFrame(0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(TextSwitchMs * 0.75)), slideOut));
        prevFade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(dur.TimeSpan)));
        TextPrev.BeginAnimation(OpacityProperty, prevFade);

        MarqueeShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(h, 0, dur) { EasingFunction = Anim.Ease() });
        MarqueeShift.BeginAnimation(TranslateTransform.XProperty, null);

        var curFade = new DoubleAnimationUsingKeyFrames();
        curFade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        curFade.KeyFrames.Add(new SplineDoubleKeyFrame(0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(TextSwitchMs * 0.28)), slideIn));
        curFade.KeyFrames.Add(new SplineDoubleKeyFrame(1,
            KeyTime.FromTimeSpan(dur.TimeSpan), slideIn));
        TextCur.BeginAnimation(OpacityProperty, curFade);
    }

    /// <summary>文字比可视区宽时开跑马灯：滚一整段 + 停 0.9s，无限循环。</summary>
    /// <summary>跑马灯重排的防抖（窗口几何动画期间 SizeChanged 每帧都来）。</summary>
    DispatcherTimer? _marqueeDebounce;

    void ScheduleMarqueeRefresh()
    {
        if (_marqueeDebounce is null)
        {
            _marqueeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _marqueeDebounce.Tick += (_, _) =>
            {
                _marqueeDebounce!.Stop();
                RefreshMarquee();
            };
        }
        _marqueeDebounce.Stop();
        _marqueeDebounce.Start();
    }

    void RefreshMarquee()
    {
        if (_state != IslandState.Compact || TextCur.Text.Length == 0)
        {
            StopMarquee();
            return;
        }
        var w = MeasureText(TextCur.Text);
        var avail = AvailableTextWidth();
        if (w <= avail + 0.5)
        {
            StopMarquee();
            return;
        }

        var span = w + MarqueeGap;
        var runSec = span / MarqueeSpeed;
        var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(-span,
            KeyTime.FromTimeSpan(TimeSpan.FromSeconds(runSec))));
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(-span,
            KeyTime.FromTimeSpan(TimeSpan.FromSeconds(runSec + MarqueeRestSec))));
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0,
            KeyTime.FromTimeSpan(TimeSpan.FromSeconds(runSec + MarqueeRestSec + 0.001))));
        MarqueeShift.BeginAnimation(TranslateTransform.XProperty, anim);
        _marqueeOn = true;
        // 只有跑马灯真的在滚时才显示第二份；否则它会以 +40px 的间距露出头来
        TextCur2.Visibility = Visibility.Visible;
    }

    void StopMarquee()
    {
        if (!_marqueeOn) return;
        MarqueeShift.BeginAnimation(TranslateTransform.XProperty, null);
        MarqueeShift.X = 0;
        _marqueeOn = false;
        TextCur2.Visibility = Visibility.Collapsed;
    }

    double MeasureText(string text)
    {
        if (text.Length == 0) return 0;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Ui, TextSize, TextFill, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace;
    }

    // ————————————————————— 圆环 —————————————————————
    /// <summary>进度弧：12 点方向顺时针，比例 0~1。用 Path + ArcSegment（原生元素）。</summary>
    void UpdateRing(ScheduleStatus status)
    {
        var (brush, ratio, total) = RingSpec(status);
        RingArc.Stroke = brush;

        // 秒级 tick 只更新「基准快照」，真正的推进交给 SmoothTick 逐帧插值，
        // 否则圆环每秒跳一格，看起来就是一卡一卡的。
        _ringStampUtc = DateTime.UtcNow;
        _ringBaseRatio = Math.Clamp(ratio, 0, 1);
        _ringTotalSec = Math.Max(1, total);

        // ★ 这里绝不直接 Render：数据是整秒取整的，硬设就等于每秒把弧"拽"一下。
        //   只在首帧给个初值，之后交给 SmoothTick 逐帧平滑跟随。
        if (_ringShown < 0) { _ringShown = _ringBaseRatio; RenderRing(_ringShown); }
    }

    /// <summary>把圆环画到指定比例（平滑定时器每帧调用）。</summary>
    void RenderRing(double ratio)
    {
        // 这些状态下圆环本来就让位（ApplyStateVisuals 已隐藏），别再抢回可见
        if (_state is IslandState.Hidden or IslandState.Mini or IslandState.Alert) return;
        if (_input?.Emergency is not null) return;

        ratio = Math.Clamp(ratio, 0, 1);
        if (Math.Abs(ratio - _lastRatio) < 0.0015) return;
        _lastRatio = ratio;
        RingArc.Visibility = ratio <= 0.001 ? Visibility.Collapsed : Visibility.Visible;
        RingArc.Data = ArcGeometry(ratio);
    }

    /// <summary>把倒计时蓝条画到指定比例（平滑定时器每帧调用）。</summary>
    void RenderBar(double ratio)
    {
        var layerW = Math.Max(0, Math.Max(0, ActualWidth * Math.Clamp(ratio, 0, 1)) - BarInset);
        if (Math.Abs(layerW - _barLastW) < 0.4) return;   // 亚像素抖动直接跳过
        _barLastW = layerW;
        CountdownLayer.BeginAnimation(WidthProperty, null);
        CountdownLayer.Width = layerW;
        Sheen.Width = Math.Max(40, layerW * 0.5);
        UpdateBarClip();
        UpdateBarHead(layerW);
    }

    /// <summary>
    /// 给蓝条套一个**圆角**裁剪：它内部的流光(Sheen)和光晕(HeadGlow)都是矩形，
    /// 不裁的话会在蓝条两端露出直角/扫到条外（就是用户看到的"浅白色方块扫过来"）。
    /// </summary>
    void UpdateBarClip()
    {
        var w = CountdownLayer.ActualWidth;
        var h = CountdownLayer.ActualHeight;
        if (w <= 0.5 || h <= 0.5) { CountdownLayer.Clip = null; return; }

        var r = Math.Min(h / 2, Math.Min(w / 2, h / 2));
        CountdownLayer.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    /// <summary>彗星前缘（白点 + 光晕）要占约 30px；蓝条比它还窄时藏起来，不然会凸出条外。</summary>
    void UpdateBarHead(double layerW)
    {
        var show = layerW >= 34;
        var v = show ? Visibility.Visible : Visibility.Collapsed;
        if (HeadDot.Visibility != v) HeadDot.Visibility = v;
        if (HeadGlow.Visibility != v) HeadGlow.Visibility = v;
    }

    Geometry ArcGeometry(double ratio)
    {
        var r = RingVisual / 2;
        var center = new Point(r, r);
        var sweep = Math.Min(359.999, 360 * ratio);
        var start = new Point(center.X + r, center.Y);
        var end = new Point(
            center.X + r * Math.Cos(sweep * Math.PI / 180),
            center.Y + r * Math.Sin(sweep * Math.PI / 180));
        var fig = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, sweep > 180,
            SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    /// <summary>环颜色与比例：即将上课=蓝（剩余/窗口），上课中=绿（剩余/时长），其余=灰。</summary>
    (Brush brush, double ratio, double totalSec) RingSpec(ScheduleStatus status)
    {
        if (_state == IslandState.Countdown)
        {
            var input = _input;
            if (input is null) return (RingUpcoming, 0, 1);
            var window = Math.Max(1, input.CountdownSec);
            return (RingUpcoming, Math.Clamp((double)input.Status.UntilSec / window, 0, 1), window);
        }
        if (status.IsUpcoming)
        {
            var total = Math.Max(1, Math.Max(status.AdvanceSec, _countdownSec));
            return (RingUpcoming, Math.Clamp((double)status.UntilSec / total, 0, 1), total);
        }
        if (status.IsOngoing)
        {
            var total = Math.Max(1, status.DurationSec);
            return (RingOngoing, Math.Clamp((double)status.RemainSec / total, 0, 1), total);
        }
        return (RingIdle, 0, 1);
    }

    // ————————————————————— 倒计时 —————————————————————
    void EnterCountdown()
    {
        _lastRatio = -1;
        _fillPending = true;      // 对齐原版 _fillStarted：180ms 内长出
        _sheenOn = false;
        ApplyStateVisuals();
    }

    /// <summary>蓝条宽度跟随剩余比例；进入时有一次 180ms 的长出动画。</summary>
    void UpdateCountdown()
    {
        var input = _input;
        if (input is null || input.CountdownSec <= 0) return;

        // 灾害预警：比例直接由服务算好（剩余/总倒计时），颜色固定红
        var emergency = input.Emergency;
        var ratio = emergency is not null
            ? Math.Clamp(emergency.Ratio, 0, 1)
            : Math.Clamp((double)input.Status.UntilSec / Math.Max(1, input.CountdownSec), 0, 1);
        // 倒计时条底色跟着预估烈度走（蓝/黄/橙/红）；黄底上白字看不清，换深色文字
        if (emergency is not null)
        {
            CountdownLayer.Background = Frozen(emergency.BarColor);
            var fg = emergency.Level == "yellow" ? Frozen(0x1b, 0x1b, 0x1b) : TextFill;
            TextCur.Foreground = fg;
            TextPrev.Foreground = fg;
            TextCur2.Foreground = fg;
        }
        else
        {
            CountdownLayer.Background = ProgressFill;
            TextCur.Foreground = TextFill;
            TextPrev.Foreground = TextFill;
            TextCur2.Foreground = TextFill;
        }
        // 同样只记「基准」：蓝条每秒跳一格会像卡住，逐帧推进交给 SmoothTick
        _barStampUtc = DateTime.UtcNow;
        _barBaseRatio = Math.Clamp(ratio, 0, 1);
        _barTotalSec = Math.Max(1, input.CountdownSec);
        var targetW = Math.Max(0, Math.Max(0, ActualWidth * _barBaseRatio) - BarInset);

        if (_fillPending)
        {
            // 第一次：从 0 长出到目标（OutCubic 180ms），流光等长完再扫
            _fillPending = false;
            CountdownLayer.BeginAnimation(WidthProperty,
                Anim.Slide(targetW, (int)CountdownFillMs));
            _barFillDoneUtc = DateTime.UtcNow.AddMilliseconds(CountdownFillMs + 60);
            _barShown = -1;          // 长完之后重新起算，交给逐帧平滑
        }
        else if (_barShown < 0)
        {
            _barShown = _barBaseRatio;
            RenderBar(_barShown);
        }
        Sheen.Width = Math.Max(40, targetW * 0.5);
        // 光晕是个竖直矩形：宽度压到 ≤ 圆角半径，右端会被蓝条的圆角裁成弧形，
        // 否则它那条笔直的竖边就从白球后面露出来（用户看到的那根"竖杠"）。
        HeadGlow.Width = Math.Min(12.0, Math.Max(6.0, CountdownLayer.Height * 0.33));
        // 右留 14px：球(23px)主体落在圆角内，只让边缘的弧自然过渡，不会被切出直角缺口
        HeadDot.Margin = new Thickness(0, 0, 14, 0);
        UpdateBarHead(targetW);
        UpdateBarClip();

        // 流光：从左外侧扫到右外侧，循环
        if (!_sheenOn)
        {
            var anim = new DoubleAnimation(-Sheen.Width, targetW,
                new Duration(TimeSpan.FromMilliseconds(SheenCycleMs)))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            SheenShift.BeginAnimation(TranslateTransform.XProperty, anim);
            _sheenOn = true;
        }
    }

    bool _sheenOn;

    // ————————————————————— 平滑进度（约 30fps） —————————————————————
    // 数据每秒才来一次；圆环和蓝条若只在 tick 时改值，就是每秒跳一格。
    // 这里保留「快照 + 时刻」，由定时器按流逝时间插值，视觉上连续。
    bool _frameHooked;
    double _ringShown = -1;          // 当前"画出来"的比例（逐帧逼近目标）
    double _barShown = -1;
    DateTime _lastFrameUtc = DateTime.UtcNow;
    DateTime _ringStampUtc = DateTime.UtcNow;
    double _ringBaseRatio;
    double _ringTotalSec = 60;
    DateTime _barStampUtc = DateTime.UtcNow;
    double _barBaseRatio;
    double _barTotalSec = 60;
    DateTime _barFillDoneUtc;
    double _barLastW = -1;

    /// <summary>
    /// 挂上逐帧回调。
    ///
    /// 之前用的是 33ms 的 DispatcherTimer，实测有两个问题：
    /// ① 状态切到 Hidden 就被停表，回来后只补一帧 —— 正好表现成"一秒跳一格"；
    /// ② 定时器和渲染节拍不同步，偶尔会被 UI 线程挤掉几帧。
    /// 换成 CompositionTarget.Rendering：跟着 WPF 的每一帧走，画面要多顺有多顺；
    /// 没有可见内容时（比例没变化、不产生无效化）WPF 自己就不再回调，等于自动省电。
    /// </summary>
    void EnsureSmoothTimer()
    {
        if (_frameHooked) return;
        _frameHooked = true;
        CompositionTarget.Rendering += OnRenderingFrame;
    }

    /// <summary>
    /// 逐帧回调必须用**具名方法**订阅：lambda 无法退订，而 CompositionTarget.Rendering
    /// 是静态事件，不退订会一直持有本窗口（连同整棵可视树），窗口关不掉。
    /// </summary>
    void OnRenderingFrame(object? sender, EventArgs e) => SmoothTick();

    /// <summary>关窗时退订逐帧回调，别让静态事件把窗口钉住。</summary>
    protected override void OnClosed(EventArgs e)
    {
        if (_frameHooked)
        {
            CompositionTarget.Rendering -= OnRenderingFrame;
            _frameHooked = false;
        }
        base.OnClosed(e);
    }
    void SmoothTick()
    {
        if (_state is IslandState.Hidden or IslandState.Mini or IslandState.Alert) return;

        var now = DateTime.UtcNow;

        // 帧间隔 → 一阶低通系数（时间常数 ≈90ms）。
        // 为什么不直接等于目标：数据每秒才来一次，而且 UntilSec 是整秒取整的，
        // 目标值本身就是"台阶"。直接赋值 = 每秒把条硬拽一下（就是"闪一下、跳一下"）；
        // 用指数逼近，台阶会被抹成一段连续的滑动。
        var dt = Math.Clamp((now - _lastFrameUtc).TotalSeconds, 0.001, 0.1);
        _lastFrameUtc = now;
        var alpha = 1 - Math.Exp(-dt / 0.09);

        var ringTarget = _ringBaseRatio - (now - _ringStampUtc).TotalSeconds / _ringTotalSec;

        if (_state == IslandState.Countdown)
        {
            // 蓝条：等"长出"动画结束后再接管，否则会把动画顶掉
            if (now >= _barFillDoneUtc)
            {
                var barTarget = _barBaseRatio - (now - _barStampUtc).TotalSeconds / _barTotalSec;
                _barShown = _barShown < 0 ? barTarget : _barShown + (barTarget - _barShown) * alpha;
                RenderBar(_barShown);
            }

            if (_input?.Emergency is null)
            {
                _ringShown = _ringShown < 0 ? ringTarget : _ringShown + (ringTarget - _ringShown) * alpha;
                RenderRing(_ringShown);
            }
            return;
        }

        _ringShown = _ringShown < 0 ? ringTarget : _ringShown + (ringTarget - _ringShown) * alpha;
        RenderRing(_ringShown);
    }

    // ————————————————————— 各状态可见性 —————————————————————
    void ApplyStateVisuals()
    {
        var mini = _state is IslandState.Mini or IslandState.Hidden;
        var alert = _state == IslandState.Alert;
        var countdown = _state == IslandState.Countdown;

        var emergency = _input?.Emergency is not null;
        MiniDot.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
        // 灾害预警时圆环让位：红色倒计时条要独占整条胶囊
        RingTrack.Visibility = mini || alert || emergency ? Visibility.Collapsed : Visibility.Visible;
        RingArc.Visibility = mini || alert || emergency ? Visibility.Collapsed : Visibility.Visible;
        TextClip.Visibility = mini || alert ? Visibility.Collapsed : Visibility.Visible;
        AlertText.Visibility = alert ? Visibility.Visible : Visibility.Collapsed;
        CountdownLayer.Visibility = countdown ? Visibility.Visible : Visibility.Collapsed;

        if (mini)
        {
            var status = _input?.Status ?? ScheduleStatus.Empty;
            MiniDot.Fill = status.IsOngoing ? MiniOngoing : status.IsUpcoming ? MiniUpcoming : MiniIdle;
        }

        if (alert)
        {
            AlertText.Text = _alertText;
            AlertText.Foreground = _alertIsStart ? AlertStartFill : AlertEndFill;
            AlertText.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(120))));
        }
        else
        {
            AlertText.BeginAnimation(OpacityProperty, null);
            AlertText.Opacity = 0;
        }

        if (countdown) UpdateCountdown();
        else
        {
            _fillPending = false;
            _sheenOn = false;
            SheenShift.BeginAnimation(TranslateTransform.XProperty, null);
        }

        ApplyChrome();
        RefreshMarquee();
        // 圆环进度放最后：RefreshLive 对 mini/alert 会早退，
        // 否则 UpdateRing 会把刚 Collapse 掉的 RingArc 又点亮（提醒态左侧会漏一截蓝弧）
        RefreshLive();
    }

    // ————————————————————— 交互 —————————————————————
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_state is IslandState.Compact or IslandState.Countdown)
            Clicked?.Invoke();
    }
}
