using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using LiquidGlass.Core;

namespace LiquidGlass.Rendering.Wpf;

/// <summary>
/// 玻璃材质本体：把窗口共享的背景裁片送进 <see cref="LiquidGlassEffect"/>，
/// 画出「被折射过的背景 + 边缘光」。
/// </summary>
/// <remarks>
/// 这是整个控件库唯一的渲染原语 —— 所有玻璃控件都是
/// 「GlassLayer + 内容层 + 交互」的组合，不各写一套着色器。
///
/// 两个关键尺寸概念：
/// <list type="bullet">
///   <item><b>控件矩形</b>：调用方给这个元素分配的布局尺寸，玻璃形状就画在这里面。</item>
///   <item><b>绘制表面</b>：元素用负 Margin 向外多占 <see cref="Expand"/>，所以比控件大一圈。
///     多出来的部分用来放圆角外侧的描边，并让边缘抗锯齿有邻居像素可混。</item>
/// </list>
/// 裁片与绘制表面严格 1:1（同一块像素范围），着色器里的 uv 才是纯线性映射 ——
/// 任何"裁片比表面大/小"的做法都会让背景被压缩或放大，表现为整块错位。
/// </remarks>
public sealed class GlassLayer : FrameworkElement
{
    /// <summary>绘制表面比控件四周多出的 DIP。只够放描边与 AA，不做采样外扩。</summary>
    /// <remarks>
    /// 折射方向是**向内**采样（把外侧背景压进边缘一圈），所以不需要向外取邻居内容；
    /// 外扩只服务于描边不被裁掉，10 足够。过大会白白增加着色器的填充面积。
    /// </remarks>
    public const double Expand = 10;

    readonly LiquidGlassEffect _effect = new();

    GlassParameters _material = GlassMaterialPresets.Create();
    WindowBackdrop? _backdrop;

    ImageBrush? _frost;
    ImageBrush? _fill;
    long _frostVersion = -1;
    long _fillVersion = -1;
    Rect _lastCrop = Rect.Empty;

    public GlassLayer()
    {
        Effect = _effect;
        IsHitTestVisible = false;

        // 不要用负 Margin：FrameworkElement.ArrangeCore 会再应用一次 Margin，
        // 与外层显式给的外扩矩形叠加后会错位。外扩由父控件在 Arrange 时给出。
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => Refresh();
    }

    /// <summary>材质参数。</summary>
    public GlassParameters Material
    {
        get => _material;
        set
        {
            _material = value ?? GlassMaterialPresets.Create();
            Refresh();
        }
    }

    /// <summary>背景快照是否已就绪（首个有效帧之后为 true）。</summary>
    public bool HasBackdrop => _frost is not null;

    /// <summary>
    /// 没有背景快照时的回退外观（云母/亚克力的背板在 DWM 里，WPF 采样不到）。
    /// </summary>
    /// <remarks>
    /// 设置后，取不到快照时改画这层半透明填充 + 描边，着色器停用；
    /// 有快照时照常走折射。这样控件在两种窗口里都不会"空掉"。
    /// </remarks>
    public Brush? Fallback
    {
        get => (Brush?)GetValue(FallbackProperty);
        set => SetValue(FallbackProperty, value);
    }

    /// <summary>回退外观的描边（画在形状边缘，模拟玻璃的抛光边）。</summary>
    public Pen? FallbackRim
    {
        get => (Pen?)GetValue(FallbackRimProperty);
        set => SetValue(FallbackRimProperty, value);
    }

    public static readonly DependencyProperty FallbackProperty = DependencyProperty.Register(
        nameof(Fallback), typeof(Brush), typeof(GlassLayer), new PropertyMetadata(null));

    public static readonly DependencyProperty FallbackRimProperty = DependencyProperty.Register(
        nameof(FallbackRim), typeof(Pen), typeof(GlassLayer), new PropertyMetadata(null));

    long _lastRefreshTick;
    bool _refreshPending;

    /// <summary>
    /// 动画期间用：节流到 ~20fps，被跳过的那次会在下一个后台帧补上。
    /// </summary>
    /// <remarks>
    /// 直接逐帧 <see cref="Refresh"/> 会每帧新建两个 CroppedBitmap + 两个画刷 +
    /// 一次着色器常量更新，动画立刻掉帧 —— 表现就是"Q 弹手感没了、切换不流畅"。
    /// 折射内容在快速移动中本来就看不清，滞后 50ms 完全看不出来。
    /// </remarks>
    public void RefreshThrottled()
    {
        var now = Environment.TickCount64;
        if (now - _lastRefreshTick < 50) { _refreshPending = true; return; }
        Refresh();
    }

    /// <summary>后台帧里补上被节流跳过的刷新（由背景服务统一调度）。</summary>
    internal void FlushPendingRefresh()
    {
        if (!_refreshPending) return;
        _refreshPending = false;
        Refresh();
    }

    /// <summary>
    /// 重新计算 uniform 与背景裁片。
    /// </summary>
    /// <remarks>
    /// 拖动/弹性动画期间每帧都会调用：背景位图本身不变（窗口背景是静态的），
    /// 只是裁切窗口跟着元素移动，所以这里只做 CroppedBitmap（视图对象，不拷贝像素）。
    /// </remarks>
    public void Refresh()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        if (_backdrop is { } service)
        {
            var frost = service.Snapshot;
            var fill = service.FillSnapshot;
            if (frost is not null) ApplySnapshot(frost, fill ?? frost);
        }

        // 没有快照就摘掉 Effect：着色器吃不到输入只会输出黑块
        Effect = _frost is null ? null : _effect;

        ApplyUniforms();
        InvalidateVisual();
        _lastRefreshTick = Environment.TickCount64;
        _refreshPending = false;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        _backdrop = GlassBackdrop.ForElement(this);
        _backdrop?.Subscribe(this);
        _backdrop?.Invalidate();
        Refresh();
    }

    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _backdrop?.Unsubscribe(this);
        _backdrop = null;
        _effect.Backdrop = null;
        _effect.FillBackdrop = null;
        _frost = _fill = null;
        _frostVersion = _fillVersion = -1;
    }

    /// <summary>背景服务通知：快照内容变了（尺寸/主题/背景动画）。</summary>
    internal void OnBackdropChanged(BackdropSnapshot frost, BackdropSnapshot fill)
    {
        if (frost.Version == _frostVersion && fill.Version == _fillVersion && _frost is not null)
            return;

        ApplySnapshot(frost, fill);
        ApplyUniforms();
        InvalidateVisual();
    }

    void ApplySnapshot(BackdropSnapshot frost, BackdropSnapshot fill)
    {
        var rect = ScreenRect();

        // 快照没变、只是位置动了 → 不重建任何对象，只挪 Viewbox。
        // 这是动画不卡的关键：每帧 new CroppedBitmap + new ImageBrush + Freeze
        // 会把 UI 线程吃满，表现就是"滑动一顿一顿"。改用整张快照 + Absolute Viewbox
        // 之后，每帧只有两次矩形赋值，零分配。
        if (_frost is not null && _fill is not null
            && frost.Version == _frostVersion && fill.Version == _fillVersion)
        {
            SetViewbox(_frost, frost, rect);
            SetViewbox(_fill, fill, rect);
            _lastCrop = rect;
            return;
        }

        _frostVersion = frost.Version;
        _fillVersion = fill.Version;
        _lastCrop = rect;

        _frost = BuildBrush(frost, rect);
        _fill = BuildBrush(fill, rect);
        _effect.Backdrop = _frost;
        _effect.FillBackdrop = _fill;
    }

    /// <summary>
    /// 把「元素在快照里的那块」映到元素表面：整张快照常驻，只改 Viewbox 矩形。
    /// </summary>
    static void SetViewbox(ImageBrush? brush, BackdropSnapshot snapshot, Rect dipRect)
    {
        if (brush is null) return;
        brush.Viewbox = PixelRect(snapshot, dipRect);
    }

    static ImageBrush BuildBrush(BackdropSnapshot snapshot, Rect dipRect)
    {
        // 不冻结：动画期间要改 Viewbox
        return new ImageBrush(snapshot.Image)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = PixelRect(snapshot, dipRect),
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
    }

    /// <summary>DIP 矩形 → 快照图像上的像素矩形（Absolute Viewbox 的单位）。</summary>
    static Rect PixelRect(BackdropSnapshot snapshot, Rect dipRect)
    {
        var scale = snapshot.Scale;
        return new Rect(
            (dipRect.X - snapshot.Bounds.X) * scale,
            (dipRect.Y - snapshot.Bounds.Y) * scale,
            Math.Max(1, dipRect.Width * scale),
            Math.Max(1, dipRect.Height * scale));
    }

    /// <summary>
    /// 本元素在屏幕 DIP 空间里的矩形（含 RenderTransform 的位移与缩放）。
    /// </summary>
    /// <remarks>
    /// 动画期间玻璃会被平移/拉伸，裁片必须跟着变成"拉伸后"的矩形，否则
    /// 背景不动而形状在动，会看成玻璃在滑一块静止的照片。
    /// </remarks>
    Rect ScreenRect()
    {
        if (ResolveBackdropSource() is not { } src) return new Rect(0, 0, ActualWidth, ActualHeight);

        try
        {
            var root = GetCommonAncestor(this, src) as Visual;
            if (root is null) return new Rect(0, 0, ActualWidth, ActualHeight);

            var toRoot = TransformToAncestor(root);
            var originInSource = src.TransformToAncestor(root).Inverse;

            var p0 = originInSource.Transform(toRoot.Transform(new Point(0, 0)));
            var p1 = originInSource.Transform(toRoot.Transform(new Point(ActualWidth, ActualHeight)));

            var w = Math.Abs(p1.X - p0.X);
            var h = Math.Abs(p1.Y - p0.Y);
            if (w <= 0 || h <= 0) return new Rect(0, 0, ActualWidth, ActualHeight);

            return new Rect(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), w, h);
        }
        catch (InvalidOperationException)
        {
            return new Rect(0, 0, ActualWidth, ActualHeight);
        }
    }

    /// <summary>找两个 Visual 最近的共同祖先。</summary>
    static DependencyObject? GetCommonAncestor(DependencyObject a, DependencyObject b)
    {
        var chain = new System.Collections.Generic.HashSet<DependencyObject>();
        for (var node = a; node is not null; node = VisualTreeHelper.GetParent(node))
            chain.Add(node);

        for (var node = b; node is not null; node = VisualTreeHelper.GetParent(node))
            if (chain.Contains(node)) return node;

        return null;
    }

    Visual? ResolveBackdropSource() => GlassBackdrop.ResolveSource(this);

    /// <summary>材质参数 → 着色器 uniform（每控件常量在 CPU 预计算，省 shader 指令）。</summary>
    void ApplyUniforms()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;

        var surfaceW = w * dpi;
        var surfaceH = h * dpi;

        // 控件本体 = 绘制表面向内收缩 Expand
        var halfW = Math.Max(surfaceW * 0.5 - Expand * dpi, 0.5);
        var halfH = Math.Max(surfaceH * 0.5 - Expand * dpi, 0.5);

        var radius = Math.Min(_material.CornerRadius * dpi, Math.Min(halfW, halfH));

        // 折射带必须 <= 圆角半径：着色器用的是"超椭圆场 + 半径"，没有真实内部距离，
        // 带宽超过半径时面板中心会被误判成仍在边缘带里（整块开始折射）。
        var band = Math.Min(_material.RefractionBand * dpi, radius);
        band = Math.Max(band, 1.0);

        var aa = Math.Max(1.0, dpi);

        _effect.SizeParams = new Point4D(surfaceW, surfaceH, halfW, halfH);
        _effect.ShapeParams = new Point4D(
            radius,
            1.0 / band,
            1.0 / aa,
            1.0 / Math.Max(_material.RimWidth * dpi, 0.25));

        var refr = _material.RefractionPixels * dpi;
        _effect.RefractParams = new Point4D(
            refr / surfaceW,
            refr / surfaceH,
            _material.Dispersion,
            0);

        _effect.OpticsParams = new Point4D(_material.RimStrength, 0, 0, 0);
        _effect.MaterialParams = new Point4D(
            _material.FillOpacity,
            _material.ToneWeight,
            _material.ShadowLift,
            0);
        _effect.ToneParams = new Point4D(_material.ToneA, _material.ToneB, _material.ToneQ, 0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_frost is null && Fallback is not null)
        {
            // 取不到背景快照（典型：云母/亚克力的背板不在 WPF 视觉树里）。
            // 着色器没有输入只会画出一团黑，这里改画调用方给的回退外观。
            var r = Math.Min(_material.CornerRadius, Math.Min(ActualWidth, ActualHeight) * 0.5);
            dc.DrawRoundedRectangle(Fallback, FallbackRim,
                new Rect(0, 0, ActualWidth, ActualHeight), r, r);
            return;
        }

        // 着色器负责画全部内容（含圆角裁切与描边）。这里只给一个透明载体触发求值。
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
    }
}
