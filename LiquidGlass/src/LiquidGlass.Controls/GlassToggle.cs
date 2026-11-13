using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiquidGlass.Core;

namespace LiquidGlass.Controls;

/// <summary>
/// iOS 26 风格的液态玻璃开关。
/// </summary>
/// <remarks>
/// 观感对标参考项目 <c>react/value-controls.tsx</c> + <c>styles.css</c> 的 .lg-switch：
/// <list type="bullet">
///   <item>胶囊轨道：半透明玻璃（有背景快照时真折射，取不到时退回分层玻璃外观）。</item>
///   <item>大圆钮：白色玻璃球，带投影与抛光边。</item>
///   <item>位移用**开关专用弹簧**（参考实现 <c>advanceSwitchPosition</c> 的
///     response 0.47 / damping 0.7 → 这里的 duration 0.47 / bounce 0.3），中途可打断、反向。</item>
///   <item>按下时圆钮长大一点（press 弹簧），松手回落；按住拖动会跟手，超过半程即翻转。</item>
/// </list>
///
/// 模板里的视觉层全部走 <see cref="GlassLayer"/> 与半透明描边，不依赖宿主主题的画刷键，
/// 因此在浅色/深色/彩色背景上都成立；宿主可以用 <see cref="AccentBrush"/> 换强调色。
/// </remarks>
public class GlassToggle : CheckBox
{
    static GlassToggle()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(GlassToggle),
            new FrameworkPropertyMetadata(typeof(GlassToggle)));
    }

    public GlassToggle()
    {
        _x = new Spring(0, SwitchSpring);
        _press = new Spring(0, GlassSprings.Press);
        Unloaded += (_, _) => { if (_active) { _active = false; Ticker.Remove(Step); } };
    }

    /// <summary>开关位移弹簧：参考实现的响应 0.47s / 阻尼 0.7 → ζ = 0.7 → bounce 0.3。</summary>
    public static readonly SpringOptions SwitchSpring = new(0.47, 0.3);

    // ── 依赖属性 ────────────────────────────────────────────────────────────

    /// <summary>圆钮横向位移（由弹簧驱动，模板绑定到 TranslateTransform.X）。</summary>
    public static readonly DependencyProperty ThumbOffsetProperty = DependencyProperty.Register(
        nameof(ThumbOffset), typeof(double), typeof(GlassToggle), new PropertyMetadata(0.0));

    /// <summary>圆钮缩放（按下时 &gt;1，模板绑定到 ScaleTransform）。</summary>
    public static readonly DependencyProperty KnobScaleProperty = DependencyProperty.Register(
        nameof(KnobScale), typeof(double), typeof(GlassToggle), new PropertyMetadata(1.0));

    /// <summary>开关打开时圆钮的行程（DIP）。与轨道宽度/圆钮直径匹配，见主题样式。</summary>
    public static readonly DependencyProperty TravelProperty = DependencyProperty.Register(
        nameof(Travel), typeof(double), typeof(GlassToggle), new PropertyMetadata(20.0));

    /// <summary>强调色（打开态的轨道染色）。默认 iOS 绿。</summary>
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(GlassToggle),
        new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))));

    /// <summary>圆钮横向位移。</summary>
    public double ThumbOffset
    {
        get => (double)GetValue(ThumbOffsetProperty);
        set => SetValue(ThumbOffsetProperty, value);
    }

    /// <summary>圆钮缩放。</summary>
    public double KnobScale
    {
        get => (double)GetValue(KnobScaleProperty);
        set => SetValue(KnobScaleProperty, value);
    }

    /// <summary>行程。</summary>
    public double Travel
    {
        get => (double)GetValue(TravelProperty);
        set => SetValue(TravelProperty, value);
    }

    /// <summary>强调色。</summary>
    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    // ── 弹簧 ────────────────────────────────────────────────────────────────

    readonly Spring _x;
    readonly Spring _press;
    bool _active;
    bool _dragging;
    double _pressX;

    void Activate()
    {
        if (_active) return;
        _active = true;
        Ticker.Add(Step);
    }

    void Retarget()
    {
        _x.Configure(SwitchSpring).Target = IsChecked == true ? Travel : 0;
        Activate();
    }

    protected override void OnToggle()
    {
        base.OnToggle();
        Retarget();
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        _pressX = e.GetPosition(this).X;
        _press.Configure(GlassSprings.Press).Target = 1;
        Activate();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        // 按住拖动：圆钮跟手，越过中点即翻转状态（与系统开关一致）
        var p = e.GetPosition(this).X;
        var offset = Math.Clamp(ThumbOffset + (p - _pressX), 0, Travel);
        _pressX = p;
        _x.Jump(offset);
        var shouldCheck = offset >= Travel * 0.5;
        if (shouldCheck != (IsChecked == true)) SetCurrentValue(IsCheckedProperty, shouldCheck);
        ThumbOffset = offset;
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging)
        {
            _dragging = false;
            _press.Configure(GlassSprings.Release).Target = 0;
            Retarget();
        }
    }

    /// <summary>推进一帧；返回是否还需要下一帧。</summary>
    bool Step(double dt)
    {
        _x.Step(dt);
        _press.Step(dt);

        if (Math.Abs(ThumbOffset - _x.Value) > 0.002) ThumbOffset = _x.Value;

        // 按下长大：参考实现 grow = min(0.16, 8/max(w,h)) * press
        var grow = Math.Min(0.16, 8.0 / Math.Max(ActualWidth, 1)) * _press.Value;
        var scale = 1 + grow;
        if (Math.Abs(KnobScale - scale) > 0.0005) KnobScale = scale;

        var active = _dragging || !_x.Settled || !_press.Settled;
        if (!active)
        {
            ThumbOffset = _x.Value;
            KnobScale = 1 + Math.Min(0.16, 8.0 / Math.Max(ActualWidth, 1)) * _press.Value;
        }
        return active;
    }

    /// <summary>极简帧调度：所有开关共用一个 CompositionTarget 钩子。</summary>
    static class Ticker
    {
        static readonly List<Func<double, bool>> Live = new();
        static bool _hooked;
        static long _last;

        public static void Add(Func<double, bool> step)
        {
            if (!Live.Contains(step)) Live.Add(step);
            if (_hooked) return;
            _hooked = true;
            _last = 0;
            CompositionTarget.Rendering += OnFrame;
        }

        public static void Remove(Func<double, bool> step) => Live.Remove(step);

        static void OnFrame(object? sender, EventArgs e)
        {
            var now = Stopwatch.GetTimestamp();
            var dt = _last == 0 ? 1.0 / 60 : (now - _last) / (double)Stopwatch.Frequency;
            _last = now;
            dt = Math.Clamp(dt, 0.001, 0.05);

            for (var i = Live.Count - 1; i >= 0; i--)
                if (!Live[i](dt)) Live.RemoveAt(i);
        }
    }
}
