using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClassDailyLand.App.Win32;
using ClassDailyLand.Core;
using ClassDailyLand.Core.Engine;

namespace ClassDailyLand.App.Windows;

/// <summary>
/// 单个下拉面板：从高度 0（+6px）展开到目标高度（OutCubic），收起反向。
/// 视觉对齐原版 panel_window.py：底色 #1c1c1e、圆角 8、文字 13px #e6e6e6、边距 12。
/// </summary>
public sealed class PanelWindow : Window
{
    static readonly Brush Bg = MakeSolid(0x1c, 0x1c, 0x1e);
    static readonly Brush Fg = MakeSolid(0xe6, 0xe6, 0xe6);
    static readonly Typeface Tf = new("Microsoft YaHei UI");

    const double MarginAll = 12;
    const double Radius = 8;

    readonly List<TextChunk> _chunks = new();          // 预排版文字（避免每帧 new）
    readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(16) };
    readonly bool _fade;
    readonly int _durationMs;

    double _baseX, _baseY;          // 落位（收起后回这里）
    double _curH = 0;               // 当前视觉高度（动画用）
    double _targetH;
    double _scroll;                 // 内容滚动偏移
    bool _open;
    bool _closed;
    DateTime _animStart;

    public bool Idle => !_anim.IsEnabled;

    public PanelWindow(double x, double yTop, double width, double height, double durationMs, bool fade)
    {
        _baseX = x;
        _baseY = yTop;
        _targetH = Math.Max(24, height);
        _durationMs = Math.Max(80, (int)durationMs);
        _fade = fade;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        Topmost = true;
        ShowInTaskbar = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;

        Width = width;
        Height = 1;
        Left = x;
        Top = yTop;

        SnapsToDevicePixels = true;
        _anim.Tick += (_, _) => StepFrame();

        SourceInitialized += (_, _) => Interop.MakeToolWindowNoActivate(this);
        MouseWheel += (_, e) => Scroll(-e.Delta / 3.0);
    }

    static Brush MakeSolid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>设置纯文本内容（\n 换行时按行高 22 预排版）。</summary>
    public void SetLines(IEnumerable<string> lines)
    {
        _chunks.Clear();
        foreach (var line in lines)
        {
            var text = line ?? "";
            var ft = new FormattedText(
                text, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, Tf, 13, Fg, 1.0);
            _chunks.Add(new TextChunk(ft, text));
        }
        InvalidateVisual();
    }

    readonly record struct TextChunk(FormattedText Formatted, string Source);

    /// <summary>展开动画。</summary>
    public void AnimateIn()
    {
        if (_closed) return;
        _open = true;
        SetHeight(0);
        _animStart = DateTime.Now;
        _anim.Start();
    }

    public void AnimateOut()
    {
        _open = false;
        _animStart = DateTime.Now;
        _anim.Start();
    }

    void StepFrame()
    {
        var t = Math.Clamp((DateTime.Now - _animStart).TotalMilliseconds / _durationMs, 0, 1);
        var k = Easing.OutCubic(t);
        if (_open)
        {
            SetHeight(Lerp(0, _targetH, k));
            if (t >= 1) _anim.Stop();
        }
        else
        {
            SetHeight(Lerp(_targetH, 0, k));
            if (t >= 1) { _anim.Stop(); CloseIfDone(); }
        }
    }

    static double Lerp(double a, double b, double t) => a + (b - a) * t;

    void SetHeight(double h)
    {
        _curH = h;
        Height = Math.Max(1, Math.Round(h));
        Top = Math.Round(_baseY + 6 - h);   // 对齐原版：起点在 y+6，向上长出 6px 更顺势
    }

    void CloseIfDone()
    {
        if (_closed) return;
        _closed = true;
        ClosedByAnim?.Invoke();
        Close();
    }

    public event Action? ClosedByAnim;

    void Scroll(double delta)
    {
        var maxScroll = Math.Max(0, ContentHeight() - _targetH + MarginAll);
        var before = _scroll;
        _scroll = Math.Clamp(_scroll - delta, 0, maxScroll);
        if (Math.Abs(_scroll - before) > 0.5) InvalidateVisual();
    }

    double ContentHeight() => MarginAll + _chunks.Count * 22 + 8;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (h <= 1.5) return;

        var rect = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(Bg, null, rect, Radius, Radius);

        dc.PushClip(new RectangleGeometry(rect, Radius, Radius));
        var y = MarginAll - _scroll;
        foreach (var chunk in _chunks)
        {
            if (y > h) break;
            if (y + chunk.Formatted.Height > 0)
                dc.DrawText(chunk.Formatted, new Point(MarginAll, y));
            y += 22;
        }

        // 内容超高时画底部渐隐（提示继续滚动）
        var maxScroll = Math.Max(0, ContentHeight() - _targetH + MarginAll);
        if (_scroll < maxScroll && y > h)
        {
            dc.PushOpacity(0.85);
            var grad = new LinearGradientBrush(
                Color.FromArgb(0, 28, 28, 30),
                Color.FromArgb(0xff, 28, 28, 30),
                new Point(0, 0), new Point(0, 1));
            dc.DrawRectangle(grad, null, new Rect(0, h - 22, w, 22));
            dc.Pop();
        }
        dc.Pop();
    }
}

/// <summary>
/// 主岛下拉面板宿主：点击主岛时展开「今日课程」面板（展开进度随原版 70ms 交错），
/// 3 秒无操作自动收起；再点主岛立即收起。阶段 3 插件可在 specs 中加面板。
/// </summary>
public sealed class IslandPanelHost
{
    const double PanelGap = 4;
    const int StaggerMs = 70;
    const int HoldMs = 3000;
    const double SchedulePanelWidth = 240;
    const double MaxHeightRatio = 1.0 / 3.0;

    readonly AppController _ctx;
    readonly List<PanelWindow> _panels = new();
    DispatcherTimer? _hold;
    bool _open;
    int _generation;

    public IslandPanelHost(AppController ctx) => _ctx = ctx;

    public void Toggle()
    {
        if (_open) { Collapse(); return; }
        PullDown();
    }

    void PullDown()
    {
        var specs = BuildSpecs().ToList();
        if (specs.Count == 0) return;

        // 总宽若超过屏幕 80% 则收窄/丢弃（对齐原版 MAX_TOTAL_RATIO）
        var work = WindowUtil.WorkAreaDip();
        var maxTotal = work.Width * 0.8;
        var total = specs.Sum(s => s.Width) + PanelGap * (specs.Count - 1);
        for (var i = specs.Count - 1; i > 0 && total > maxTotal; i--)
        {
            total -= specs[i].Width + PanelGap;
            specs.RemoveAt(i);
        }

        var startX = Math.Round((work.Width - total) / 2);
        var yTop = Math.Round(work.Top + _ctx.Island!.TopDip + _ctx.Island.HeightDip + PanelGap);

        _open = true;
        _generation++;
        var gen = _generation;

        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var x = startX;
            startX += (int)spec.Width + (int)PanelGap;
            var pw = new PanelWindow(x, yTop, spec.Width, spec.Height, 320, fade: true);
            pw.SetLines(spec.Lines);
            pw.Closed += (_, _) => { _panels.Remove(pw); if (gen == _generation && _panels.Count == 0) _open = false; };
            _panels.Add(pw);

            // 交错展开（70ms 间隔）
            var delay = i * StaggerMs;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (gen == _generation) pw.AnimateIn();
            };
            timer.Start();
        }

        // 3 秒无操作自动收起
        _hold?.Stop();
        _hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldMs) };
        _hold.Tick += (_, _) => { _hold!.Stop(); if (_open) Collapse(); };
        _hold.Start();
    }

    public void Collapse()
    {
        _open = false;
        _generation++;
        _hold?.Stop();
        // 拷贝一份再遍历：面板收起/关闭时 Closed 回调会从 _panels 里移除自己，
        // 直接 foreach 原集合会抛 InvalidOperationException。
        foreach (var p in _panels.ToList()) p.AnimateOut();
    }

    record PanelSpec(double Width, double Height, IReadOnlyList<string> Lines);

    /// <summary>MVP：只有中间的「今日课程」面板（与原版无插件时一致）。</summary>
    IEnumerable<PanelSpec> BuildSpecs()
    {
        var sm = _ctx.Schedule;
        var today = DateTime.Today;
        var status = _ctx.CurrentStatus;
        var items = sm.Engine.CoursesOfDay(today);

        var lines = new List<string>();
        foreach (var (index, period, course) in items)
            lines.Add($"{(status.IsOngoing && status.Index == index ? "▸ " : "   ")}{course}  {Shift(period.Start)}");

        if (status.Status == ScheduleEngine.StatusHoliday)
        {
            var name = status.Name ?? "";
            lines.Clear();
            lines.Add(string.IsNullOrEmpty(name) ? "假期中" : $"假期中 · {name}");
        }
        else if (lines.Count == 0)
        {
            lines.Add("今天没有课");
        }

        var work = WindowUtil.WorkAreaDip();
        var maxH = work.Height * MaxHeightRatio;
        var height = Math.Min(20 + lines.Count * 22 + 14, maxH);
        yield return new PanelSpec(SchedulePanelWidth, height, lines);
    }

    /// <summary>显示用的开始时间要叠加全局时间偏移（原版 offset_time_str）。</summary>
    string Shift(string hhm)
    {
        var offset = _sm_offsetSeconds();
        try
        {
            var parts = (hhm ?? "").Trim().Split(':');
            if (parts.Length >= 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m))
            {
                var total = Math.Clamp(h * 60 + m + offset / 60, 0, 1440);
                return $"{total / 60:D2}:{total % 60:D2}";
            }
        }
        catch { }
        return hhm ?? "";
    }

    int _sm_offsetSeconds() => _ctx.Schedule.OffsetSeconds;
}
