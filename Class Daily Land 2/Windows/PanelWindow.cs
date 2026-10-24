using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClassDailyLand.App.Theme;
using ClassDailyLand.App.Win32;
using ClassDailyLand.Core;
using ClassDailyLand.Core.Engine;
using ClassDailyLand.Core.Plugins;

namespace ClassDailyLand.App.Windows;

/// <summary>
/// 单个下拉面板：从高度 0（+6px）展开到目标高度（OutCubic），收起反向。
/// 视觉对齐原版 panel_window.py：底色 #1c1c1e、圆角 8、文字 13px #e6e6e6、边距 12。
///
/// **原生化**：视觉树在 PanelWindow.xaml（Border 卡片 + ScrollViewer + ItemsControl），
/// 本文件只留展开/收起动画与内容装载。滚动交给原生 ScrollViewer，
/// 底部渐隐是原生 Rectangle + 渐变。
/// </summary>
public sealed partial class PanelWindow : Window
{
    const double MarginAll = 12;
    const double Radius = 8;
    const double LineHeight = 22;

    readonly bool _fade;
    readonly int _durationMs;

    double _baseX, _baseY;          // 落位（收起后回这里）
    double _targetH;
    bool _closed;
    bool _busy;                     // 展开/收起动画进行中

    public bool Idle => !_busy;

    public PanelWindow(double x, double yTop, double width, double height, double durationMs, bool fade)
    {
        InitializeComponent();

        _baseX = x;
        _baseY = yTop;
        _targetH = Math.Max(24, height);
        _durationMs = Math.Max(80, (int)durationMs);
        _fade = fade;

        Card.Background = Palette.PanelBg;
        Lines.Foreground = Palette.PanelText;
        BottomFade.Visibility = Visibility.Collapsed;

        Width = width;
        Height = 1;
        Left = x;
        Top = yTop;

        SourceInitialized += (_, _) => Interop.MakeToolWindowNoActivate(this);
        MouseWheel += (_, e) =>
        {
            Scroll.ScrollToVerticalOffset(Scroll.VerticalOffset - e.Delta / 3.0);
            UpdateFade();
        };
        Scroll.ScrollChanged += (_, _) => UpdateFade();
        // 手指上下滑：无条件开（默认 PanningMode=None，手指按住是一点都不动的）
        TouchMode.EnableTouchScroll(Scroll);
    }

    /// <summary>设置纯文本内容（一行一项）。</summary>
    public void SetLines(IEnumerable<string> lines)
    {
        Lines.ItemsSource = lines.Select(l => l ?? "").ToList();
        UpdateFade();
    }

    /// <summary>展开动画：高度 0 → 目标，Top 同步下移，视觉上「向上长出」。</summary>
    public void AnimateIn()
    {
        if (_closed) return;
        BeginAnimation(HeightProperty, null);
        BeginAnimation(TopProperty, null);
        Height = 0;
        Top = Math.Round(_baseY + 6);
        UpdateFade();
        Play(_targetH, Math.Round(_baseY + 6 - _targetH));
    }

    public void AnimateOut()
    {
        BeginAnimation(HeightProperty, null);
        BeginAnimation(TopProperty, null);
        Play(0, Math.Round(_baseY + 6), closeWhenDone: true);
    }

    /// <summary>高度与 Top 用同一条缓动同步跑（时长/曲线来自 Anim 统一入口）。</summary>
    void Play(double h, double top, bool closeWhenDone = false)
    {
        _busy = true;
        // 设置里关掉动画时 durationMs 传 0，Anim.Slide 返回 null → 直接落位
        var anim = Anim.Slide(h, _fade ? _durationMs : 0);
        if (anim is null)
        {
            Height = h;
            Top = top;
            _busy = false;
            UpdateFade();
            if (closeWhenDone) CloseIfDone();
            return;
        }
        anim.Completed += (_, _) =>
        {
            _busy = false;
            UpdateFade();
            if (closeWhenDone) CloseIfDone();
        };
        BeginAnimation(HeightProperty, anim);
        BeginAnimation(TopProperty, Anim.Slide(top, _fade ? _durationMs : 0)!);
    }

    void CloseIfDone()
    {
        if (_closed) return;
        _closed = true;
        ClosedByAnim?.Invoke();
        Close();
    }

    public event Action? ClosedByAnim;

    /// <summary>内容比视口高、且还没滚到底时，底部渐隐提示。</summary>
    void UpdateFade()
    {
        if (Scroll.ExtentHeight <= 0 || ActualHeight <= 1.5)
        {
            BottomFade.Visibility = Visibility.Collapsed;
            return;
        }
        var more = Scroll.ExtentHeight - Scroll.ViewportHeight - Scroll.VerticalOffset;
        BottomFade.Visibility = more > 1 ? Visibility.Visible : Visibility.Collapsed;
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
    const double LineHeight = 22;      // 与 XAML 里 TextBlock 的行高一致

    readonly AppController _ctx;
    readonly List<PanelWindow> _panels = new();
    DispatcherTimer? _hold;
    bool _open;
    int _generation;

    public IslandPanelHost(AppController ctx) => _ctx = ctx;

    /// <summary>插件面板（排在「今日课程」右側；总宽超限时先丢它们）。</summary>
    public Func<IReadOnlyList<PanelRender>>? PluginPanelsProvider { get; set; }

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
        var height = Math.Min(20 + lines.Count * LineHeight + 14, maxH);
        yield return new PanelSpec(SchedulePanelWidth, height, lines);

        // 插件面板接在课程面板右边（和原版一样：内置在中间，插件从右往左排）
        IReadOnlyList<PanelRender> extra;
        try { extra = PluginPanelsProvider?.Invoke() ?? Array.Empty<PanelRender>(); }
        catch { yield break; }

        foreach (var item in extra)
        {
            var ratio = item.Spec.MaxHeightRatio <= 0
                ? MaxHeightRatio
                : Math.Clamp(item.Spec.MaxHeightRatio, 0.1, 0.6);
            var h = Math.Min(20 + item.Lines.Count * LineHeight + 14, work.Height * ratio);
            yield return new PanelSpec(item.Spec.Width, h, item.Lines);
        }
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
