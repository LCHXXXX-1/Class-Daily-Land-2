using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClassDailyLand.App.Theme;
using ClassDailyLand.App.Win32;
using ClassDailyLand.Core;
using ClassDailyLand.Core.Engine;
using ClassDailyLand.Core.Models;

namespace ClassDailyLand.App.Windows;

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
    bool Visible);

/// <summary>
/// 灵动岛：置顶、无边框、不抢焦点，全部 OnRender 手绘。
/// 版式严格对齐原版 PySide6 dynamic_island.py：
///   胶囊底色不透明 #1c1c1e；左侧 28px 见方圆环（灰轨道 #3a3a3c + 彩色进度弧）；
///   文字白色粗体 14px，起点 = 6 + 28 + 8；倒计时时蓝条铺满整个胶囊高度。
/// </summary>
public sealed class DynamicIslandWindow : Window
{
    // ————————————————————— 几何常量（对齐原版） —————————————————————
    const double CapsuleH = 40;
    const double MiniW = 48;
    const double CompactBaseW = 240;
    const double CountdownW = 600;
    const double BannerH = 72;
    const double HiddenYOffset = -64;
    const int StepGapMs = 60;

    const double RingSize = 28;
    const double RingX = 6;
    const double RingStroke = 2;
    const double TextGapAfterRing = 8;
    const double RightPadding = 10;
    const double TextSize = 14;

    const double MarqueeSpeed = 32;    // px/s
    const double MarqueeGap = 40;      // 首尾间隔
    const double MarqueeRestSec = 0.9; // 每轮回起点前停 0.9s
    const double SheenCycleMs = 1400;
    const double BarInset = 1;

    // ————————————————————— 缓存画笔（禁止在 OnRender 里 new） —————————————————————
    static readonly Brush CapsuleFill = Solid(0x1c, 0x1c, 0x1e);
    static readonly Pen RingTrackPen = Pen2(0x3a, 0x3a, 0x3c);
    static readonly Brush RingOngoing = BrushOf(0x30, 0xd1, 0x58);
    static readonly Brush RingUpcoming = BrushOf(0x4d, 0xa3, 0xff);
    static readonly Brush RingIdle = BrushOf(0x8a, 0x8a, 0x8e);
    static readonly Brush TextFill = BrushOf(0xff, 0xff, 0xff);
    static readonly Brush AlertStartFill = BrushOf(0x30, 0xd1, 0x58);
    static readonly Brush AlertEndFill = BrushOf(0xff, 0xff, 0xff);
    static readonly Brush ProgressFill = BrushOf(0x0a, 0x84, 0xff);
    static readonly Brush SheenFill = BrushOf(0xff, 0xff, 0xff, 0x46);
    static readonly Brush HeadFill = BrushOf(0xf5, 0xf5, 0xff, 0xe6);
    static readonly Brush HeadGlow = MakeGlow();
    static readonly Typeface Ui = new("Microsoft YaHei UI, Segoe UI");

    static Brush Solid(byte r, byte g, byte b, byte a = 0xff)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    static Brush BrushOf(byte r, byte g, byte b, byte a = 0xff) => Solid(r, g, b, a);

    static Pen Pen2(byte r, byte g, byte b)
    {
        var pen = new Pen(Solid(r, g, b), RingStroke);
        pen.Freeze();
        return pen;
    }

    static Brush MakeGlow()
    {
        var brush = new LinearGradientBrush(
            Color.FromArgb(0x96, 0xc8, 0xff, 0xff),
            Color.FromArgb(0x00, 0xc8, 0xff, 0xff),
            new Point(0, 0.5), new Point(1, 0.5));
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
    DispatcherTimer? _wakeStep;
    double _topMargin = 6;
    double _alertWidth = 300;
    int _alertHoldMs = 600;
    double _animDurationMs = 220;
    bool _fadeAnimation = true;
    int _countdownSec = 60;

    // 几何动画（from -> to，OutCubic）
    double _curW = MiniW, _curH = CapsuleH, _curX, _curY = HiddenYOffset;
    double _tgtW = MiniW, _tgtH = CapsuleH, _tgtX, _tgtY;
    double _fromW = MiniW, _fromH = CapsuleH, _fromX, _fromY = HiddenYOffset;
    DateTime _geomStart = DateTime.MinValue;

    readonly DispatcherTimer _frameTimer;
    DateTime _lastFrame = DateTime.MinValue;
    double _marqueeOffset;
    DateTime _marqueeRestUntil = DateTime.MinValue;
    DateTime _fillStarted = DateTime.MinValue;

    FormattedText? _ft, _ftPrev;
    double _pixelsPerDip = 1.0;

    public event Action? Clicked;
    public event Action<IslandState>? IslandStateChanged;

    public IslandState State => _state;
    public double RightEdgeDip => _curX + _curW;
    public double TopDip => _curY;
    public double HeightDip => _curH;

    public DynamicIslandWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Focusable = false;
        Left = 0; Top = HiddenYOffset; Width = MiniW; Height = CapsuleH;

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _frameTimer.Tick += (_, _) => StepFrame();

        SourceInitialized += (_, _) =>
        {
            Interop.MakeToolWindowNoActivate(this);
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            RebuildText();
        };
        Palette.Changed += (_, _) => { RebuildText(); InvalidateVisual(); };
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
        RebuildText();
        if (_input is not null) Update(_input);
    }

    // ————————————————————— 主循环入口 —————————————————————
    public void Update(IslandInput input)
    {
        _input = input;
        Interop.KeepTopmost(this);

        if (!input.Visible) { SetState(IslandState.Hidden); return; }

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
            _fillStarted = DateTime.Now;      // 蓝条填充动画起点
        SetState(next);
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
        _wakeStep?.Stop();
        _wakeStep = null;

        if (!animate) { _wakeupPending = false; SetState(IslandState.Compact); return; }
        _wakeupPending = true;
        SetState(IslandState.Mini);
        var t1 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        t1.Tick += (_, _) =>
        {
            t1.Stop();
            if (!_wakeupPending) return;
            var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(StepGapMs) };
            _wakeStep = t2;
            t2.Tick += (_, _) =>
            {
                t2.Stop();
                _wakeStep = null;
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
        if (next == _state) { AnimateTo(GeometryFor(_state)); return; }
        _state = next;
        AnimateTo(GeometryFor(next));
        IslandStateChanged?.Invoke(next);
        EnsureFrames();
    }

    (double w, double h, double x, double y) GeometryFor(IslandState st)
    {
        var input = _input;
        var work = WindowUtil.WorkAreaDip();
        var center = WindowUtil.FullAreaDip().Width / 2;
        double w = st switch
        {
            IslandState.Mini or IslandState.Hidden => MiniW,
            IslandState.Compact => CompactBaseW + (input?.ExtraWidth ?? 0),
            IslandState.Countdown => CountdownW,
            IslandState.Alert => _alertWidth,
            IslandState.Banner => 560 + (input?.ExtraWidth ?? 0),
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

        _fromW = _curW; _fromH = _curH; _fromX = _curX; _fromY = _curY;
        _tgtW = target.w; _tgtH = target.h; _tgtX = target.x; _tgtY = target.y;
        if (!_fadeAnimation || _animDurationMs <= 1)
        {
            _curW = _tgtW; _curH = _tgtH; _curX = _tgtX; _curY = _tgtY;
            _geomStart = DateTime.MinValue;
            ApplyGeometry();
            InvalidateVisual();
            return;
        }
        _geomStart = DateTime.Now;
        EnsureFrames();
    }

    void EnsureFrames()
    {
        if (!_frameTimer.IsEnabled)
        {
            _lastFrame = DateTime.Now;
            _frameTimer.Start();
        }
    }

    void StopFramesIfIdle()
    {
        var animating = Math.Abs(_curW - _tgtW) > 0.5 || Math.Abs(_curX - _tgtX) > 0.5 ||
                        Math.Abs(_curY - _tgtY) > 0.5 || Math.Abs(_curH - _tgtH) > 0.5;
        var switching = (DateTime.Now - _textSwitchAt).TotalMilliseconds < 170;
        var countdown = _state is IslandState.Countdown or IslandState.Alert;
        if (!(NeedsMarquee() || animating || switching || countdown) && _frameTimer.IsEnabled)
            _frameTimer.Stop();
    }

    // ————————————————————— 每帧 —————————————————————
    void StepFrame()
    {
        var now = DateTime.Now;
        var dt = Math.Max(0.001, (now - _lastFrame).TotalSeconds);
        _lastFrame = now;

        var elapsed = (now - _geomStart).TotalMilliseconds;
        var t = _animDurationMs <= 0 ? 1 : Math.Clamp(elapsed / _animDurationMs, 0, 1);
        var k = Easing.OutCubic(t);
        _curW = Lerp(_fromW, _tgtW, k);
        _curH = Lerp(_fromH, _tgtH, k);
        _curX = Lerp(_fromX, _tgtX, k);
        _curY = Lerp(_fromY, _tgtY, k);
        if (t >= 1) { _curW = _tgtW; _curH = _tgtH; _curX = _tgtX; _curY = _tgtY; }
        ApplyGeometry();

        // 跑马灯：滚动 span/32 秒后回起点停 0.9s
        if (NeedsMarquee())
        {
            if (now >= _marqueeRestUntil)
            {
                _marqueeOffset += MarqueeSpeed * dt;
                if (_marqueeOffset >= MarqueeSpan())
                {
                    _marqueeOffset = 0;
                    _marqueeRestUntil = now.AddSeconds(MarqueeRestSec);
                }
            }
        }
        else if (_marqueeOffset != 0) _marqueeOffset = 0;

        if ((now - _textSwitchAt).TotalMilliseconds > 180 && _prevText.Length > 0)
        {
            _prevText = "";
            _ftPrev = null;
        }

        InvalidateVisual();
        StopFramesIfIdle();
    }

    static double Lerp(double a, double b, double t) => a + (b - a) * t;

    void ApplyGeometry()
    {
        if (Math.Abs(Width - _curW) > 0.4 || Math.Abs(Height - _curH) > 0.4 ||
            Math.Abs(Left - _curX) > 0.4 || Math.Abs(Top - _curY) > 0.4)
        {
            Width = _curW;
            Height = _curH;
            Left = _curX;
            Top = _curY;
        }
    }

    // ————————————————————— 文案 —————————————————————
    public void SetText(string text)
    {
        if (string.Equals(text, _text, StringComparison.Ordinal)) return;
        _prevText = _text;
        _text = text;
        _textSwitchAt = DateTime.Now;
        _marqueeOffset = 0;
        _marqueeRestUntil = DateTime.MinValue;
        RebuildText();
        EnsureFrames();
    }

    void RebuildText()
    {
        _ft = Build(_text);
        _ftPrev = string.IsNullOrEmpty(_prevText) ? null : Build(_prevText);
    }

    FormattedText? Build(string text) => string.IsNullOrEmpty(text) ? null : new FormattedText(
        text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Ui, TextSize, TextFill, _pixelsPerDip);

    /// <summary>文字可用宽度：起点 6+28+8，右侧让出 10（加长片段另算）。</summary>
    double TextLeft => RingX + RingSize + TextGapAfterRing;

    double AvailableTextWidth()
    {
        var left = _state is IslandState.Mini or IslandState.Hidden ? 12 : TextLeft;
        var right = RightPadding + (_input?.ExtraWidth ?? 0);
        return Math.Max(0, _curW - left - right);
    }

    bool NeedsMarquee() =>
        _ft is not null && _state == IslandState.Compact &&
        _ft.WidthIncludingTrailingWhitespace > AvailableTextWidth() + 0.5;

    /// <summary>滚动一段的距离 = 文字宽 + 首尾间隔（原版 span）。</summary>
    double MarqueeSpan() => (_ft?.WidthIncludingTrailingWhitespace ?? 0) + MarqueeGap;

    // ————————————————————— 绘制 —————————————————————
    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0.5 || h <= 0.5) return;
        if (_state == IslandState.Hidden && _curY < -h + 2) return;

        var radius = h / 2;
        var rect = new Rect(0, 0, w, h);

        // 原版：胶囊底色纯 #1c1c1e，无描边
        dc.DrawRoundedRectangle(CapsuleFill, null, rect, radius, radius);

        switch (_state)
        {
            case IslandState.Mini:
            case IslandState.Hidden:
                DrawMini(dc, w, h);
                break;
            case IslandState.Alert:
                DrawAlert(dc, w, h);
                break;
            case IslandState.Countdown:
                DrawCountdownFill(dc, w, h, radius);
                DrawRing(dc, h, _input?.Status ?? ScheduleStatus.Empty);
                DrawText(dc, w, h);
                break;
            default:
                DrawRing(dc, h, _input?.Status ?? ScheduleStatus.Empty);
                DrawText(dc, w, h);
                break;
        }
    }

    /// <summary>Mini：居中的实心圆点（原版半径 6），颜色随状态。</summary>
    void DrawMini(DrawingContext dc, double w, double h)
    {
        var status = _input?.Status ?? ScheduleStatus.Empty;
        var brush = status.IsOngoing ? RingOngoing : status.IsUpcoming ? RingUpcoming : RingIdle;
        dc.DrawEllipse(brush, null, new Point(w / 2, h / 2), 6, 6);
    }

    /// <summary>圆环：灰轨道 + 彩色进度弧（从 12 点顺时针），对齐原版 _draw_ring。</summary>
    void DrawRing(DrawingContext dc, double h, ScheduleStatus status)
    {
        var (brush, ratio) = RingSpec(status);
        var box = new Rect(RingX + 2, (CapsuleH - RingSize) / 2 + 2, RingSize - 4, RingSize - 4);
        var center = new Point(box.Left + box.Width / 2, box.Top + box.Height / 2 + (h - CapsuleH) / 2);
        var r = box.Width / 2;

        dc.DrawEllipse(null, RingTrackPen, center, r, r);

        ratio = Math.Clamp(ratio, 0, 1);
        if (ratio <= 0.001) return;
        var pen = new Pen(brush, RingStroke);
        // 圆弧：WPF 起点在 3 点方向，顺时针递减 Y；从 12 点开始
        var sweep = 360 * ratio;
        var startAngle = -90.0;
        dc.PushTransform(new RotateTransform(startAngle, center.X, center.Y));
        dc.DrawGeometry(null, pen, ArcGeometry(center, r, sweep));
        dc.Pop();
    }

    static Geometry ArcGeometry(Point center, double r, double sweepDeg)
    {
        var sweep = Math.Min(359.999, sweepDeg);
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
    (Brush brush, double ratio) RingSpec(ScheduleStatus status)
    {
        if (_state == IslandState.Countdown)
        {
            var input = _input;
            if (input is null) return (RingUpcoming, 0);
            var window = Math.Max(1, input.CountdownSec);
            return (RingUpcoming, Math.Clamp((double)input.Status.UntilSec / window, 0, 1));
        }
        if (status.IsUpcoming)
        {
            var total = Math.Max(1, Math.Max(status.AdvanceSec, _countdownSec));
            return (RingUpcoming, Math.Clamp((double)status.UntilSec / total, 0, 1));
        }
        if (status.IsOngoing)
        {
            var total = Math.Max(1, status.DurationSec);
            return (RingOngoing, Math.Clamp((double)status.RemainSec / total, 0, 1));
        }
        return (RingIdle, 0);
    }

    void DrawAlert(DrawingContext dc, double w, double h)
    {
        var fade = Math.Clamp((DateTime.Now - _alertClock).TotalMilliseconds / 120.0, 0, 1);
        var brush = _alertIsStart ? AlertStartFill : AlertEndFill;
        var ft = new FormattedText(_alertText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Ui, 16, brush, _pixelsPerDip) { TextAlignment = TextAlignment.Center };
        dc.PushOpacity(fade);
        dc.DrawText(ft, new Point(0, (h - ft.Height) / 2));
        ft.MaxTextWidth = w;
        dc.Pop();
    }

    void DrawText(DrawingContext dc, double w, double h)
    {
        if (_ft is null) return;
        var left = TextLeft;
        var avail = Math.Max(0, w - left - RightPadding - (_input?.ExtraWidth ?? 0));
        if (avail <= 2) return;

        dc.PushClip(new RectangleGeometry(new Rect(left, 0, avail, h)));
        var y = (h - _ft.Height) / 2;
        var anim = Math.Clamp((DateTime.Now - _textSwitchAt).TotalMilliseconds / 140.0, 0, 1);
        var k = Easing.OutCubic(anim);
        var te = k;

        if (_ftPrev is not null && anim < 1)
        {
            // 旧文案上移淡出
            dc.PushOpacity(Math.Max(0, 1 - 1.5 * te));
            dc.PushTransform(new TranslateTransform(0, -te * h));
            dc.DrawText(_ftPrev, new Point(left, y));
            dc.Pop();
            dc.Pop();
            // 新文案从下方推入
            dc.PushOpacity(Math.Max(0, 1.5 * te - 0.5));
            dc.PushTransform(new TranslateTransform(0, h - te * h));
            dc.DrawText(_ft, new Point(left, y));
            dc.Pop();
            dc.Pop();
        }
        else if (NeedsMarquee())
        {
            // 无缝循环：画两份，首尾相接
            var span = MarqueeSpan();
            for (var i = 0; i < 2; i++)
            {
                var x = left - _marqueeOffset + i * span;
                if (x > left + avail) break;
                dc.DrawText(_ft, new Point(x, y));
            }
        }
        else
        {
            dc.DrawText(_ft, new Point(left, y));
        }
        dc.Pop();
    }

    /// <summary>倒计时：蓝条铺满胶囊高度，带流光与彗星前缘（对齐原版 _paint_countdown_fill）。</summary>
    void DrawCountdownFill(DrawingContext dc, double w, double h, double radius)
    {
        var input = _input;
        if (input is null || input.CountdownSec <= 0) return;

        var window = Math.Max(1, input.CountdownSec);
        var ratio = Math.Clamp((double)input.Status.UntilSec / window, 0, 1);
        var fillT = _fillStarted == DateTime.MinValue
            ? 1.0
            : Math.Clamp((DateTime.Now - _fillStarted).TotalMilliseconds / 180.0, 0, 1);
        var shown = Math.Min(Easing.OutCubic(fillT), ratio);
        if (shown <= 0) return;

        var barH = Math.Max(2.0, h - BarInset * 2);
        var r = barH / 2;
        var fillW = Math.Max(0, w * shown);
        var barW = Math.Max(0, fillW - BarInset);
        var rr = Math.Min(r, barW / 2);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), radius, radius));
        dc.DrawRoundedRectangle(ProgressFill, null, new Rect(BarInset, BarInset, barW, barH), rr, rr);

        // 流光扫过
        var phase = (DateTime.Now.TimeOfDay.TotalMilliseconds % SheenCycleMs) / SheenCycleMs;
        var sheenW = Math.Max(40, fillW * 0.5);
        var cx = -sheenW + phase * (fillW + 2 * sheenW);
        dc.PushTransform(new TranslateTransform(cx, 0));
        dc.PushClip(new RectangleGeometry(new Rect(BarInset, BarInset, barW, barH), rr, rr));
        var sheen = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(0x00, 0xff, 0xff, 0xff), 0),
            new GradientStop(Color.FromArgb(0x46, 0xff, 0xff, 0xff), 0.5),
            new GradientStop(Color.FromArgb(0x00, 0xff, 0xff, 0xff), 1),
        }, new Point(0, 0.5), new Point(1, 0.5));
        dc.DrawRectangle(sheen, null, new Rect(0, BarInset, sheenW, barH));
        dc.Pop();
        dc.Pop();

        // 前缘光晕 + 亮点
        var headX = BarInset + fillW;
        var glowW = Math.Min(20.0, Math.Max(8.0, barH));
        dc.DrawRectangle(HeadGlow, null, new Rect(Math.Max(BarInset, headX - glowW), BarInset, glowW, barH));
        dc.DrawEllipse(HeadFill, null, new Point(headX - r, h / 2), r * 0.6, r * 0.6);
        dc.Pop();
    }

    // ————————————————————— 交互 —————————————————————
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_state is IslandState.Compact or IslandState.Countdown)
            Clicked?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        _frameTimer.Stop();
        _wakeStep?.Stop();
        base.OnClosed(e);
    }
}