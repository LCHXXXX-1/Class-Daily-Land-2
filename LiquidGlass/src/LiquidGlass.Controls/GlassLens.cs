using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Shapes;
using System.Windows.Threading;
using LiquidGlass.Core;
using LiquidGlass.Rendering.Wpf;

namespace LiquidGlass.Controls;

/// <summary>
/// 一块可以左右拖动的液态玻璃。
/// </summary>
/// <remarks>
/// 交互与动效严格照参考项目（<c>dom/interaction.ts</c>）的模型实现：
///
/// <list type="number">
///   <item><b>按下</b>：材料"长大"一点（<c>grow = min(0.16, 8 / max(w,h)) * press</c>，
///     越小的玻璃长得越多），同时指针处亮起一块柔光。</item>
///   <item><b>拖动</b>：玻璃用 track 弹簧追指针，落后量决定横向拉伸
///     （<c>ex = min(cap, 0.5·lag/w + 0.00007·|v|)</c>），纵向等体积收缩，看起来像被"拽"着走。
///     越过轨道两端时用橡皮筋阻尼，绝不硬停。</item>
///   <item><b>松开</b>：<see cref="GlassSprings.Release"/>（0.5s / bounce 0.42）回位或吸附到最近的档位，
///     过冲再回来 —— 这就是"Q弹"。</item>
///   <item><b>单击</b>：直接弹到下一个档位（或另一端），全程同一个 release 弹簧。</item>
/// </list>
///
/// 所有弹簧都是解析解（<see cref="Spring"/>），掉帧不会让动画变慢或发散；
/// 每一帧都会让 <see cref="GlassLayer"/> 重新裁一次背景，所以拖动过程中
/// 透过玻璃看到的背景是实时更新、跟着一起弯折的。
/// </remarks>
[ContentProperty(nameof(Content))]
public sealed class GlassLens : Panel
{
    // ── 依赖属性 ────────────────────────────────────────────────────────────

    /// <summary>材质参数。</summary>
    public static readonly DependencyProperty MaterialProperty = DependencyProperty.Register(
        nameof(Material), typeof(GlassParameters), typeof(GlassLens),
        new PropertyMetadata(null, (d, _) => ((GlassLens)d).ApplyMaterial()));

    /// <summary>可左右移动的总距离（DIP）。0 表示只能"顶"一下（按下时的微位移）。</summary>
    public static readonly DependencyProperty TravelProperty = DependencyProperty.Register(
        nameof(Travel), typeof(double), typeof(GlassLens),
        new PropertyMetadata(0.0, (d, _) => ((GlassLens)d).ClampToTravel()));

    /// <summary>档位数量。&gt;1 时松开吸附到最近档位，单击跳到下一档；0 表示自由拖动。</summary>
    public static readonly DependencyProperty SnapDivisionsProperty = DependencyProperty.Register(
        nameof(SnapDivisions), typeof(int), typeof(GlassLens),
        new PropertyMetadata(0, (d, _) => ((GlassLens)d).ClampToTravel()));

    /// <summary>内容内边距。</summary>
    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(GlassLens),
        new FrameworkPropertyMetadata(new Thickness(18, 10, 18, 10),
            FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>当前所在档位（<see cref="SnapDivisions"/> &gt; 1 时有意义）。</summary>
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(GlassLens),
        new PropertyMetadata(0, (d, e) => ((GlassLens)d).OnSelectedIndexChanged((int)e.NewValue)));

    // ── 视觉树 ──────────────────────────────────────────────────────────────

    readonly GlassLayer _glass = new();
    readonly Rectangle _glow;
    readonly Border _debug;
    readonly ContentPresenter _content = new();
    readonly ScaleTransform _scale = new(1, 1);
    readonly TranslateTransform _translate = new();

    // ── 弹簧状态 ────────────────────────────────────────────────────────────

    readonly Spring _x;
    readonly Spring _press;
    readonly Spring _glowAmount;

    bool _placed;
    bool _dragging;
    bool _registered;
    double _grabOffset;      // 按下时 指针 - 玻璃当前偏移
    double _pointerX;
    double _dragDistance;    // 用于区分"单击"和"拖动"
    SpringOptions _follow = GlassSprings.Drag;
    double _lastWidth;

    /// <summary>诊断：画出元素矩形（洋红）与控件本体矩形（青），用于核对布局。</summary>
    public static readonly DependencyProperty ShowDebugBoundsProperty = DependencyProperty.Register(
        nameof(ShowDebugBounds), typeof(bool), typeof(GlassLens),
        new PropertyMetadata(false, (d, _) => ((GlassLens)d)._debug.Visibility =
            ((GlassLens)d).ShowDebugBounds ? Visibility.Visible : Visibility.Collapsed));

    /// <summary>档位变化事件（拖动吸附或单击触发）。</summary>
    public event EventHandler<int>? SelectedIndexChanged;

    public GlassLens()
    {
        _x = new Spring(0, GlassSprings.Drag);
        _press = new Spring(0, GlassSprings.Press);
        _glowAmount = new Spring(0, GlassSprings.Glow);

        _glow = new Rectangle
        {
            IsHitTestVisible = false,
            Opacity = 0,
            Fill = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF), 0),
                    new GradientStop(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 0.45),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1),
                },
            },
        };

        _content.HorizontalAlignment = HorizontalAlignment.Center;
        _content.VerticalAlignment = VerticalAlignment.Center;
        _content.IsHitTestVisible = false;

        _debug = new Border
        {
            BorderBrush = Brushes.Magenta,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = new Border
            {
                BorderBrush = Brushes.Cyan,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(GlassLayer.Expand),
            },
        };

        Children.Add(_glass);
        Children.Add(_glow);
        Children.Add(_content);
        Children.Add(_debug);

        RenderTransformOrigin = new Point(0.5, 0.5);
        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_translate);
        RenderTransform = group;

        Cursor = Cursors.Hand;
        Material = GlassMaterialPresets.Create();

        SizeChanged += OnSizeChanged;
        Unloaded += (_, _) =>
        {
            if (!_registered) return;
            _registered = false;
            Animated.Remove(this);
        };
    }

    // ── 公开属性 ────────────────────────────────────────────────────────────

    /// <summary>材质。</summary>
    public GlassParameters Material
    {
        get => (GlassParameters)GetValue(MaterialProperty);
        set => SetValue(MaterialProperty, value);
    }

    /// <summary>内容。</summary>
    public object? Content
    {
        get => _content.Content;
        set => _content.Content = value;
    }

    /// <summary>可移动的总距离。</summary>
    public double Travel
    {
        get => (double)GetValue(TravelProperty);
        set => SetValue(TravelProperty, value);
    }

    /// <summary>档位数量（0/1 = 自由拖动）。</summary>
    public int SnapDivisions
    {
        get => (int)GetValue(SnapDivisionsProperty);
        set => SetValue(SnapDivisionsProperty, value);
    }

    /// <summary>内边距。</summary>
    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    /// <summary>显示布局诊断框。</summary>
    public bool ShowDebugBounds
    {
        get => (bool)GetValue(ShowDebugBoundsProperty);
        set => SetValue(ShowDebugBoundsProperty, value);
    }

    /// <summary>当前档位。</summary>
    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>当前横向偏移（已含动画）。</summary>
    public double Offset => _x.Value;

    // ── 布局 ────────────────────────────────────────────────────────────────

    /// <summary>Panel 默认不绘制任何内容，命中测试会穿透；画一个透明矩形即可参与命中。</summary>
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var pad = Padding;
        var innerW = double.IsNaN(Width) ? Math.Max(0, availableSize.Width - pad.Left - pad.Right) : Math.Max(0, Width - pad.Left - pad.Right);
        var innerH = double.IsNaN(Height) ? Math.Max(0, availableSize.Height - pad.Top - pad.Bottom) : Math.Max(0, Height - pad.Top - pad.Bottom);

        _content.Measure(new Size(innerW, innerH));

        var w = double.IsNaN(Width) ? _content.DesiredSize.Width + pad.Left + pad.Right : Width;
        var h = double.IsNaN(Height) ? _content.DesiredSize.Height + pad.Top + pad.Bottom : Height;
        var size = new Size(Math.Max(1, w), Math.Max(1, h));

        _glass.Measure(new Size(size.Width + GlassLayer.Expand * 2, size.Height + GlassLayer.Expand * 2));
        _glow.Measure(size);
        _debug.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!_placed)
        {
            // 第一次布局就把指示器放到当前档位，否则会从中间滑进来
            _placed = true;
            _x.Jump(PositionOf(SelectedIndex));
            _translate.X = _x.Value;
        }

        // 玻璃层比控件大一圈（外扩 10），这样描边与抗锯齿不会贴到元素边界上。
        _glass.Arrange(new Rect(
            -GlassLayer.Expand, -GlassLayer.Expand,
            finalSize.Width + GlassLayer.Expand * 2,
            finalSize.Height + GlassLayer.Expand * 2));

        _glow.Arrange(new Rect(finalSize));
        _debug.Arrange(new Rect(finalSize));

        var pad = Padding;
        _content.Arrange(new Rect(
            pad.Left, pad.Top,
            Math.Max(0, finalSize.Width - pad.Left - pad.Right),
            Math.Max(0, finalSize.Height - pad.Top - pad.Bottom)));

        return finalSize;
    }

    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 尺寸变了：squircle 裁剪路径要重建，弹簧的追踪时长也随尺寸微调
        var m = Material;
        var radius = m?.CornerRadius ?? 28;
        var smoothing = m?.CornerSmoothing ?? 0.85;
        _glow.Clip = SquircleGeometry.Create(ActualWidth, ActualHeight, radius, smoothing);

        // 参考实现：光斑直径 max(36, min(180, max(w,h)*0.9))
        if (_glow.Fill is RadialGradientBrush rb && ActualWidth > 0 && ActualHeight > 0)
        {
            var light = Math.Max(36, Math.Min(180, Math.Max(ActualWidth, ActualHeight) * 0.9));
            rb.RadiusX = light / ActualWidth;
            rb.RadiusY = light / ActualHeight;
        }

        _lastWidth = ActualWidth;
        ApplyMaterial();
    }

    void ApplyMaterial()
    {
        var m = Material;
        if (m is null) return;
        m.Sanitized();
        _glass.Material = m;
        _glow.Clip = SquircleGeometry.Create(ActualWidth, ActualHeight, m.CornerRadius, m.CornerSmoothing);
    }

    void OnSelectedIndexChanged(int index)
    {
        if (SnapDivisions <= 1) return;
        var target = PositionOf(index);
        _x.Configure(GlassSprings.Release).Target = target;
        Activate();
    }

    // ── 几何辅助 ────────────────────────────────────────────────────────────

    double Half => Travel * 0.5;

    /// <summary>第 i 个档位的偏移量。</summary>
    double PositionOf(int index)
    {
        if (SnapDivisions <= 1) return 0;
        var clamped = Math.Clamp(index, 0, SnapDivisions - 1);
        return -Half + Travel * clamped / (SnapDivisions - 1);
    }

    int NearestIndex(double position)
    {
        if (SnapDivisions <= 1) return 0;
        var step = Travel / (SnapDivisions - 1);
        return Math.Clamp((int)Math.Round((position + Half) / step), 0, SnapDivisions - 1);
    }

    /// <summary>轨道内自由移动，越界后转为橡皮筋阻尼。</summary>
    double ApplyBounds(double raw)
    {
        if (Travel <= 0) return RubberBand.Apply(raw, 14);
        var limit = Half;
        if (raw > limit) return limit + RubberBand.Apply(raw - limit, limit * 0.4);
        if (raw < -limit) return -limit + RubberBand.Apply(raw + limit, limit * 0.4);
        return raw;
    }

    void ClampToTravel()
    {
        var target = ApplyBounds(_x.Value);
        if (Math.Abs(target - _x.Value) > 0.01)
        {
            _x.Configure(GlassSprings.Release).Target = target;
            Activate();
        }
    }

    // ── 指针交互 ────────────────────────────────────────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (!CaptureMouse()) return;

        _dragging = true;
        _dragDistance = 0;
        _pointerX = e.GetPosition(this).X;
        _grabOffset = e.GetPosition(this).X - _x.Value;

        // 大玻璃更"重"：跟随时间随尺寸增加（参考实现同款公式）
        var size = Math.Max(ActualWidth, ActualHeight);
        _follow = new SpringOptions(
            GlassSprings.Track.Duration + 0.0005 * size,
            GlassSprings.Track.Bounce);

        _press.Configure(GlassSprings.Press).Target = 1;
        _glowAmount.Configure(GlassSprings.Press).Target = 1;
        SetGlowCenter(e.GetPosition(this));

        e.Handled = true;
        Activate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var p = e.GetPosition(this);

        if (!_dragging)
        {
            // 悬停：只亮一点光，不位移
            if (_glowAmount.Target < 0.3)
            {
                SetGlowCenter(p);
                _glowAmount.Configure(GlassSprings.Glow).Target = 0.35;
                Activate();
            }
            else
            {
                SetGlowCenter(p);
            }
            return;
        }

        _dragDistance += Math.Abs(p.X - _pointerX);
        _pointerX = p.X;
        SetGlowCenter(p);

        _x.Configure(_follow).Target = ApplyBounds(_pointerX - _grabOffset);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!_dragging) return;
        ReleaseMouseCapture();
        _dragging = false;

        _press.Configure(GlassSprings.Release).Target = 0;
        _glowAmount.Configure(GlassSprings.Glow).Target = IsMouseOver ? 0.35 : 0;

        if (SnapDivisions > 1)
        {
            // 拖动结束吸附到最近档位；单击（几乎没动）则前进一档
            var index = _dragDistance < 4
                ? (NearestIndex(_x.Value) + 1) % SnapDivisions
                : NearestIndex(_x.Value);

            SelectedIndex = index;
            _x.Configure(GlassSprings.Release).Target = PositionOf(index);
        }
        else
        {
            // 自由模式：单击弹到另一端，拖动则回弹到轨道内
            var target = _dragDistance < 4
                ? (_x.Value > 0 ? -Half : Half)
                : ApplyBounds(_x.Value);
            _x.Configure(GlassSprings.Release).Target = target;
        }

        Activate();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_dragging)
        {
            _glowAmount.Configure(GlassSprings.Glow).Target = 0;
            Activate();
        }
    }

    void SetGlowCenter(Point p)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (_glow.Fill is not RadialGradientBrush brush) return;

        var x = Math.Clamp(p.X / ActualWidth, 0, 1);
        var y = Math.Clamp(p.Y / ActualHeight, 0, 1);
        if (Math.Abs(brush.Center.X - x) < 0.002 && Math.Abs(brush.Center.Y - y) < 0.002) return;

        brush.Center = new Point(x, y);
        brush.GradientOrigin = new Point(x, y);
    }

    // ── 帧驱动 ──────────────────────────────────────────────────────────────

    static readonly List<GlassLens> Animated = new();
    static bool _hooked;
    static long _lastTimestamp;

    void Activate()
    {
        if (_registered) return;
        _registered = true;
        Animated.Add(this);
        HookTicker();
    }

    static void HookTicker()
    {
        if (_hooked) return;
        _hooked = true;
        _lastTimestamp = 0;
        CompositionTarget.Rendering += OnFrame;
    }

    static void UnhookTicker()
    {
        if (Animated.Count > 0 || !_hooked) return;
        _hooked = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    static void OnFrame(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = _lastTimestamp == 0 ? 1.0 / 60 : (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;

        // 掉帧时不要一步跳太远（弹簧是解析解，稳定性没问题，但视觉上会"闪"）
        dt = Math.Clamp(dt, 0.001, 0.05);

        for (var i = Animated.Count - 1; i >= 0; i--)
        {
            if (!Animated[i].Step(dt)) Animated.RemoveAt(i);
        }
    }

    /// <summary>推进一帧。返回是否还需要下一帧。</summary>
    bool Step(double dt)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return false;

        _x.Step(dt);
        _press.Step(dt);
        _glowAmount.Step(dt);

        // 指针领先玻璃多少 —— 这个落后量就是"被拽住"的拉伸来源
        var lag = _dragging ? ApplyBounds(_pointerX - _grabOffset) - _x.Value : 0;

        var size = Math.Max(w, h);
        var grow = Math.Min(0.16, 8 / Math.Max(size, 1)) * _press.Value;

        // 0.5·lag/w：位移拉伸；0.00007·|v|：速度拉伸（快速甩动时更明显）
        var cap = size <= 72 ? 0.20 : 0.12;
        var ex = Math.Min(cap, 0.5 * Math.Abs(lag) / w + 0.00007 * Math.Abs(_x.Velocity));
        const double ey = 0.0;

        // 等体积：横向拉长多少，纵向就压扁多少（凝胶感的关键）
        var sx = (1 + grow) * (1 + ex) / Math.Sqrt(1 + ey);
        var sy = (1 + grow) * (1 + ey) / Math.Sqrt(1 + ex);

        var moved = Math.Abs(_translate.X - _x.Value) > 0.005;
        var scaled = Math.Abs(_scale.ScaleX - sx) > 0.0005 || Math.Abs(_scale.ScaleY - sy) > 0.0005;

        if (moved) _translate.X = _x.Value;
        if (scaled)
        {
            _scale.ScaleX = sx;
            _scale.ScaleY = sy;
        }

        _glow.Opacity = _glowAmount.Value;

        // 位置/形状变了就让着色器重新裁一次背景（拖动时每帧一次）
        if (moved || scaled) _glass.Refresh();

        var active = _dragging || !_x.Settled || !_press.Settled || !_glowAmount.Settled;
        if (!active)
        {
            // 停下来时做一次收尾对齐，避免累积的亚像素偏移
            if (Math.Abs(_translate.X - _x.Value) > 0.001) _translate.X = _x.Value;
            if (Math.Abs(_scale.ScaleX - sx) > 1e-4 || Math.Abs(_scale.ScaleY - sy) > 1e-4)
            {
                _scale.ScaleX = sx;
                _scale.ScaleY = sy;
            }
            _glow.Opacity = _glowAmount.Value;
            _glass.Refresh();
        }

        return active;
    }
}
