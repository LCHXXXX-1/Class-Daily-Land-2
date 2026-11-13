using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LiquidGlass.Core;
using LiquidGlass.Rendering;

namespace LiquidGlass.Rendering.Wpf;

/// <summary>
/// 全窗口共享的背景采样服务。
/// </summary>
/// <remarks>
/// 一个窗口里所有玻璃控件共用同一份背景快照，每个控件只是从里面裁自己那一块。
/// 10 个玻璃控件也只做一次背景渲染，而不是 10 次 —— 这是能跑满 60fps 的前提。
///
/// 快照有两份：
/// <list type="bullet">
///   <item><b>frost</b>：窄模糊，Clear 材质直接用原图（sigma = 0）。</item>
///   <item><b>fill</b>：宽模糊（≈23），Regular 材质用它做乳白填充。</item>
/// </list>
/// 两份都只在「背景真的变了」时重建；拖动玻璃不触发重采样（背景是静态的），
/// 只是各控件重新裁切同一张图。
/// </remarks>
public static class GlassBackdrop
{
    /// <summary>背景视觉。通常指向窗口里的壁纸层/背景 Grid。</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(Visual), typeof(GlassBackdrop),
        new FrameworkPropertyMetadata(null, OnSourceChanged));

    public static void SetSource(DependencyObject element, Visual? value) => element.SetValue(SourceProperty, value);
    public static Visual? GetSource(DependencyObject element) => (Visual?)element.GetValue(SourceProperty);

    static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        foreach (var svc in Services.Values) svc.Invalidate();
    }

    /// <summary>
    /// 采样时"透明处"垫的底层（通常是壁纸或主题底色）。
    /// </summary>
    /// <remarks>
    /// 云母/亚克力的背板在 DWM 里、WPF 截不到像素；不垫底的话玻璃会折射到一片透明
    /// （着色器采样到就是黑）。垫上真实壁纸后，即使窗口是云母，玻璃也在折射桌面壁纸。
    /// </remarks>
    public static readonly DependencyProperty BaseBrushProperty = DependencyProperty.RegisterAttached(
        "BaseBrush", typeof(Brush), typeof(GlassBackdrop),
        new PropertyMetadata(null, OnSourceChanged));

    public static void SetBaseBrush(DependencyObject element, Brush? value) => element.SetValue(BaseBrushProperty, value);
    public static Brush? GetBaseBrush(DependencyObject element) => (Brush?)element.GetValue(BaseBrushProperty);

    /// <summary>
    /// 解析「玻璃下面那一层」的可视化。
    /// </summary>
    /// <remarks>
    /// 查找顺序：
    /// 1. 自身 / 祖先链上直接设了 Source 的（最常用：根布局容器上设一次）；
    /// 2. 整棵可视树里第一个设了 Source 的元素（Source 常挂在某个兄弟层，如背景 Canvas）；
    /// 3. 窗口内容本身（兜底）。
    /// ★ 绝不能返回「包含玻璃控件的祖先」—— 那样玻璃会截到自己，表现为面板里一片黑。
    /// 调用方（<see cref="WindowBackdrop"/> 与 GlassLayer）必须走同一个解析，
    /// 否则采样区域和裁切区域会各自算各自的，错位。
    /// </remarks>
    public static Visual? ResolveSource(DependencyObject anchor)
    {
        if (Window.GetWindow(anchor) is not { } window) return null;

        for (var node = anchor; node is not null; node = ParentOf(node))
        {
            if (GetSource(node) is { } direct) return direct;
        }

        // 找整棵可视树里第一个显式设了 Source 的元素
        if (FindSourceInTree(window, anchor) is { } found) return found;

        // 退回到窗口内容（可能就是 anchor 的祖先）——这是"玻璃折射窗口内 UI"的正确语义。
        // 自反馈由采样时临时隐藏所有玻璃层解决（见 WindowBackdrop.Flush）。
        return window.Content as Visual;
    }

    static DependencyObject? ParentOf(DependencyObject node)
        => node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    /// <summary>判断 <paramref name="maybeAncestor"/> 是否在 <paramref name="node"/> 的祖先链上。</summary>
    static bool IsAncestorOf(DependencyObject maybeAncestor, DependencyObject node)
    {
        for (var p = ParentOf(node); p is not null; p = ParentOf(p))
            if (ReferenceEquals(p, maybeAncestor)) return true;
        return false;
    }

    static Visual? FindSourceInTree(DependencyObject root, DependencyObject anchor)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (GetSource(node) is { } src && !IsAncestorOf(src, anchor)) return src;

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }

        return null;
    }

    static readonly Dictionary<Window, WindowBackdrop> Services = new();

    /// <summary>取（或为窗口创建）背景服务。</summary>
    public static WindowBackdrop For(Window window)
    {
        if (!Services.TryGetValue(window, out var svc))
        {
            svc = new WindowBackdrop(window);
            Services[window] = svc;
            window.Closed += (_, _) =>
            {
                svc.Dispose();
                Services.Remove(window);
            };
        }
        return svc;
    }

    /// <summary>从任意元素向上找窗口，然后取该窗口的背景服务。</summary>
    public static WindowBackdrop? ForElement(DependencyObject element)
        => Window.GetWindow(element) is { } w ? For(w) : null;
}

/// <summary>
/// 单个窗口的背景快照管理器：负责采样、缓存、失效与帧节流。
/// </summary>
public sealed class WindowBackdrop : IGlassRenderBackend
{
    readonly Window _window;
    readonly BackdropCache _cache = new();
    readonly DispatcherTimer _timer;
    readonly HashSet<GlassLayer> _subscribers = new();

    bool _dirty = true;
    long _samples;
    long _lastMergeSamples;
    long _lastMergeTick;
    bool _disposed;

    internal WindowBackdrop(Window window)
    {
        _window = window;

        // 33ms ≈ 30fps：背景不是每帧都变，30 次/秒足够跟上动画。
        // 优先级必须是 Background：RenderTargetBitmap 一帧要几十毫秒，
        // 挂在 Render 优先级上会把滑动动画的帧直接吃掉（切换就不流畅了）。
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    /// <inheritdoc />
    public string Id => "wpf";

    /// <inheritdoc />
    public GlassBackendCapabilities Capabilities { get; } = new()
    {
        Name = "WPF ShaderEffect",
        SupportsRefraction = true,
        SupportsDispersion = true,
        SupportsSharedBackdrop = true,
        MaxPixelShaderModel = 2,
        IsHardwareAccelerated = true,
    };

    /// <inheritdoc />
    public bool NeedsBackdropUpdate => _dirty;

    /// <inheritdoc />
    public long BackdropSampleCount => _samples;

    /// <inheritdoc />
    public long CacheBytes => _cache.Bytes;

    /// <summary>当前 frost 快照（未就绪时为 null）。</summary>
    public BackdropSnapshot? Snapshot { get; private set; }

    /// <summary>当前 fill 快照（宽模糊，未就绪时为 null）。</summary>
    public BackdropSnapshot? FillSnapshot { get; private set; }

    internal void Subscribe(GlassLayer layer) => _subscribers.Add(layer);

    internal void Unsubscribe(GlassLayer layer) => _subscribers.Remove(layer);

    /// <summary>请求重新采样（背景动画、窗口缩放、主题切换后调用）。</summary>
    public void Invalidate()
    {
        _dirty = true;
        _cache.Invalidate();
    }

    /// <summary>标记一块区域需要刷新。WPF 后端整窗重采一次，脏区域只用于统计。</summary>
    public void Invalidate(int x, int y, int width, int height) => Invalidate();

    /// <summary>立刻重采样一次（不等下一帧）。</summary>
    public BackdropSnapshot? SampleNow()
    {
        Flush(force: true);
        return Snapshot;
    }

    void Flush(bool force = false)
    {
        if (_disposed) return;
        if (!force && !_dirty) return;

        var source = GlassBackdrop.ResolveSource(_window);
        if (source is not FrameworkElement fe || fe.ActualWidth <= 0 || fe.ActualHeight <= 0)
            return;

        // 取订阅者里最小的降采样比与最大的模糊半径：一张快照要同时满足所有控件，
        // 否则会出现"有的控件清晰有的糊"的割裂感
        double scale = 0.75, frost = 0, fill = 0;
        if (_subscribers.Count > 0)
        {
            scale = 1;
            foreach (var layer in _subscribers)
            {
                scale = Math.Min(scale, Math.Max(0.2, layer.Material.BackdropScale));
                frost = Math.Max(frost, layer.Material.BlurSigma);
                fill = Math.Max(fill, layer.Material.FillSigma);
            }
        }

        var bounds = new Rect(0, 0, fe.ActualWidth, fe.ActualHeight);
        var root = $"{_window.GetHashCode()}:{bounds.Width:F0}x{bounds.Height:F0}";
        var baseBrush = GlassBackdrop.GetBaseBrush(fe);

        // ★ 玻璃层不能出现在自己的背景里（自反馈 = 边缘内侧一圈幽灵轮廓）。
        // 采样是同步的，隐藏/恢复之间不会有帧，用户看不到闪烁。
        var restore = new List<(GlassLayer Layer, double Opacity)>();
        foreach (var layer in _subscribers)
        {
            restore.Add((layer, layer.Opacity));
            layer.Opacity = 0;
        }

        BackdropSnapshot frostSnap, fillSnap;
        try
        {
            frostSnap = _cache.GetOrCreate($"frost:{root}", scale, bounds, (s, rect) =>
            {
                _samples++;
                return BackdropSampler.Capture(source, rect, s, frost, baseBrush);
            });

            fillSnap = _cache.GetOrCreate($"fill:{root}", scale, bounds, (s, rect) =>
            {
                _samples++;
                return BackdropSampler.Capture(source, rect, s, fill, baseBrush);
            });
        }
        finally
        {
            foreach (var (layer, opacity) in restore) layer.Opacity = opacity;
        }

        var changed = Snapshot is null
            || frostSnap.Version != Snapshot.Version
            || FillSnapshot is null
            || fillSnap.Version != FillSnapshot.Version;

        Snapshot = frostSnap;
        FillSnapshot = fillSnap;
        _dirty = false;

        // 调试：LG_DUMP_SNAP=1 时把合成图导出来看（采样到底截到了什么）
        if (Environment.GetEnvironmentVariable("LG_DUMP_SNAP") == "1")
        {
            try
            {
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frostSnap.Image));
                using var fs = System.IO.File.Create(System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "lg-snap.png"));
                enc.Save(fs);
            }
            catch { }
        }

        if (changed)
        {
            foreach (var layer in _subscribers.ToArray())
                layer.OnBackdropChanged(frostSnap, fillSnap);
        }

        // 补上动画期间被节流掉的刷新（尾帧也要落到准确位置）
        foreach (var layer in _subscribers.ToArray()) layer.FlushPendingRefresh();

        var tick = Environment.TickCount64;
        if (_lastMergeTick == 0) _lastMergeTick = tick;
        if (tick - _lastMergeTick >= 1000)
        {
            _lastMergeSamples = _samples;
            _lastMergeTick = tick;
        }
    }

    /// <summary>本秒内的采样次数（性能面板用）。</summary>
    public long SamplesThisSecond
    {
        get
        {
            var tick = Environment.TickCount64;
            if (_lastMergeTick == 0 || tick - _lastMergeTick >= 1000) return 0;
            return _samples - _lastMergeSamples;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _subscribers.Clear();
        _cache.Dispose();
        Snapshot = null;
        FillSnapshot = null;
    }
}
