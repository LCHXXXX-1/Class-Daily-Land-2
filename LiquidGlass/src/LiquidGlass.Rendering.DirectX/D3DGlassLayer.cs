using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LiquidGlass.Core;

namespace LiquidGlass.Rendering.DirectX;

/// <summary>
/// 液态玻璃层：从 WPF 可视树采背景 → GPU（DX12，失败退回 DX11）三趟渲染 → 写回位图。
/// </summary>
/// <remarks>
/// 显示路径：GPU 渲染 → 回读 BGRA（预乘 alpha）→ <see cref="WriteableBitmap"/>（Pbgra32）。
///
/// ★ 这里**不使用 D3DImage（D3D9Ex 共享表面）**：
///   那条零拷贝路径会把一块硬件表面交给 WPF 合成，和窗口里的 ClearType 文本
///   （重定向表面）打架 —— 玻璃上的标签文字会随机丢字（实测：三个字丢两个字），
///   而且 alpha/DPR 一变还会出黑边。回读一张几十 KB 的位图代价可以忽略，
///   换来的是完全走 WPF 自己的合成，文字永不被覆盖，alpha 也精确。
/// </remarks>
public sealed class D3DGlassLayer : FrameworkElement
{
    /// <summary>玻璃材质参数。</summary>
    public static readonly DependencyProperty MaterialProperty = DependencyProperty.Register(
        nameof(Material), typeof(GlassParameters), typeof(D3DGlassLayer),
        new PropertyMetadata(null, (d, _) => ((D3DGlassLayer)d).InvalidateVisual()));

    /// <summary>背景源（要采样的可视化树节点）。</summary>
    public static readonly DependencyProperty BackdropSourceProperty = DependencyProperty.Register(
        nameof(BackdropSource), typeof(Visual), typeof(D3DGlassLayer),
        new PropertyMetadata(null));

    /// <summary>
    /// 采样时透明处垫的底色（可选，**默认不垫**）。
    /// </summary>
    /// <remarks>
    /// ★ 以前这里垫的是 <c>SolidBackgroundFillColorBaseBrush</c>（#F3F3F3）。
    /// 云母/亚克力下窗口背板由 DWM 绘制，采样到的是全透明像素，垫一块纯灰
    /// 就等于把整个胶囊做成了一块灰板子。现在默认 null：alpha 一路透传，
    /// 玻璃只加自己那层薄白（见 GlassParameters.SurfaceOpacity）。
    /// </remarks>
    public static readonly DependencyProperty BaseBrushProperty = DependencyProperty.Register(
        nameof(BaseBrush), typeof(Brush), typeof(D3DGlassLayer),
        new PropertyMetadata(null, (d, _) => ((D3DGlassLayer)d).InvalidateVisual()));

    public GlassParameters? Material
    {
        get => (GlassParameters?)GetValue(MaterialProperty);
        set => SetValue(MaterialProperty, value);
    }

    public Visual? BackdropSource
    {
        get => (Visual?)GetValue(BackdropSourceProperty);
        set => SetValue(BackdropSourceProperty, value);
    }

    public Brush? BaseBrush
    {
        get => (Brush?)GetValue(BaseBrushProperty);
        set => SetValue(BaseBrushProperty, value);
    }

    /// <summary>
    /// 是否允许用 DirectX 12 渲染（关于页的「DirectX 12」开关）。
    /// </summary>
    /// <remarks>
    /// 关掉 = 直接用 DX11 后端（老路），连 DX12 设备都不尝试创建 ——
    /// 有些机器 DX12 能建设备但创建资源失败/花屏，这个开关是给那种情况的退路。
    /// 改这个值会重建共享管线。
    /// </remarks>
    public static bool PreferDx12
    {
        get => _preferDx12;
        set
        {
            if (_preferDx12 == value) return;
            _preferDx12 = value;
            ResetPipeline();
        }
    }

    static bool _preferDx12 = true;

    /// <summary>丢弃当前共享管线，下次渲染重新选后端（切换开关/异常恢复时用）。</summary>
    public static void ResetPipeline()
    {
        lock (_pipelineLock)
        {
            try { _sharedPipeline?.Dispose(); } catch { }
            _sharedPipeline = null;
            _sharedPipelineForceDx11 = false;
        }
    }

    static IGlassPipeline? _sharedPipeline;
    static string _sharedPipelineError = "";
    static bool _sharedPipelineForceDx11;
    static readonly object _pipelineLock = new();

    int _lastW, _lastH;

    /// <summary>当前渲染后端（DX12 / DX11），第一次渲染后才有值。</summary>
    public string BackendName => _sharedPipeline?.BackendName ?? "未初始化";

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        if (Material is null || ActualWidth < 1 || ActualHeight < 1)
        {
            dc.DrawRectangle(Brushes.Transparent, null, rect);
            return;
        }

        var w = (int)Math.Round(ActualWidth);
        var h = (int)Math.Round(ActualHeight);
        if (w < 1 || h < 1)
        {
            dc.DrawRectangle(Brushes.Transparent, null, rect);
            return;
        }

        try
        {
            var pixels = RenderPixels(w, h);
            if (pixels is null)
            {
                dc.DrawRectangle(Brushes.Transparent, null, rect);
                return;
            }

            // ★ 不要在 OnRender 里改 WriteableBitmap：那是一张"正在被这个视觉绘制"的位图，
            //   在渲染过程中写它会让 WPF 反复失效/重入，实测窗口会直接不再合成（整窗空白）。
            //   每帧生成一张冻结的 BitmapSource 直接画，一次性、无状态。
            var frame = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
            frame.Freeze();
            dc.DrawImage(frame, rect);
        }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lg_render.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace + "\n");
            }
            catch { }
            // 连回退都失败：背景透出来，不画黑块
            dc.DrawRectangle(Brushes.Transparent, null, rect);
        }
    }

    /// <summary>
    /// 渲染一帧像素；DX12 在运行期失败时自动降级到 DX11 并重试一次。
    /// </summary>
    /// <remarks>
    /// 设备/PSO 建得起来、但真正创建资源才报错的情况是存在的（比如这台机器上
    /// D3D12 运行时的 System32 组件版本和系统不匹配：任何纹理都返回 E_INVALIDARG，
    /// buffer 却正常）。所以降级不能只包构造函数，必须包到"第一次真正渲染"。
    /// </remarks>
    byte[]? RenderPixels(int w, int h)
    {
        var bg = CaptureBackdrop(w, h);
        if (bg is null) return null;

        var p = GlassRenderParams.FromMaterial(Material!, w, h);

        var pipeline = EnsurePipeline();
        if (pipeline is null) return null;

        try
        {
            return pipeline.RenderAndReadback(bg, w, h, p);
        }
        catch (Exception ex)
        {
            if (pipeline is not GlassGpuPipeline12) throw;
            _sharedPipelineError = "DX12 运行期失败，已退回 DX11：" + ex.Message;
            FallbackToDx11();
            return _sharedPipeline?.RenderAndReadback(bg, w, h, p);
        }
    }

    static void FallbackToDx11()
    {
        lock (_pipelineLock)
        {
            try { _sharedPipeline?.Dispose(); } catch { }
            _sharedPipeline = null;
            _sharedPipelineForceDx11 = true;
            _sharedPipeline = new GlassPipelineDx11Adapter();
        }
    }

    /// <summary>
    /// 取（并缓存）渲染管线：优先 DX12；设备创建失败退回 DX11。
    /// </summary>
    /// <remarks>
    /// 管线是进程级共享的：设置页里有两块玻璃（侧栏 + 顶部），各自建一套设备既浪费
    /// 又会让 GPU 内存翻倍。尺寸变化由管线内部按需重建资源。
    /// </remarks>
    static IGlassPipeline? EnsurePipeline()
    {
        if (_sharedPipeline is not null) return _sharedPipeline;
        lock (_pipelineLock)
        {
            if (_sharedPipeline is not null) return _sharedPipeline;

            // 诊断/兼容开关：LG_BACKEND=dx11 可强制走旧后端；运行期失败也会把它置 true
            var forced = Environment.GetEnvironmentVariable("LG_BACKEND");
            if (_preferDx12 && !_sharedPipelineForceDx11 &&
                !string.Equals(forced, "dx11", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _sharedPipeline = new GlassGpuPipeline12();
                    return _sharedPipeline;
                }
                catch (Exception ex)
                {
                    _sharedPipelineError = "DX12: " + ex.Message;
                }
            }

            try
            {
                _sharedPipeline = new GlassPipelineDx11Adapter();
            }
            catch (Exception ex)
            {
                _sharedPipelineError = "DX11: " + ex.Message;
                _sharedPipeline = null;
            }
            return _sharedPipeline;
        }
    }

    /// <summary>后端名 + 最近的错误（诊断用）。</summary>
    public static string BackendDiagnostics => $"{_sharedPipeline?.BackendName ?? "none"} | {_sharedPipelineError}";

    /// <summary>
    /// 从 WPF 可视树采样背景像素（预乘 alpha，PBGRA）。
    /// </summary>
    /// <remarks>
    /// ★ 采样时临时隐藏自身（Opacity=0），防止自反馈（玻璃截到自己→白色块）。
    /// ★ 采样源应当**只包含背景、不包含文字**（主程序里是 BackdropLayer）。
    /// ★ 不铺不透明底色：云母/亚克力下 BackdropLayer 是透明的，垫一块纯色
    ///   就会把整个胶囊做成一块灰板（历史 bug）。透明处由着色器补那层薄白。
    /// </remarks>
    byte[]? CaptureBackdrop(int w, int h)
    {
        var source = BackdropSource;
        if (source is null) return null;

        const double expand = 10;

        // 计算元素在源中的位置
        Point origin = new(0, 0);
        if (source is FrameworkElement fe && fe.IsAncestorOf(this))
        {
            try { origin = TransformToAncestor(fe).Transform(new Point(-expand, -expand)); }
            catch { }
        }
        else if (source is Visual v)
        {
            // 兄弟节点：找共同祖先再换算
            try
            {
                var root = FindCommonAncestor((Visual)this, v);
                if (root is not null)
                {
                    var toRoot = TransformToAncestor(root).Transform(new Point(-expand, -expand));
                    var srcToRoot = v.TransformToAncestor(root);
                    origin = srcToRoot.Inverse.Transform(toRoot);
                }
            }
            catch { }
        }

        var sampleRect = new Rect(origin.X, origin.Y, w + expand * 2, h + expand * 2);

        // ★ 采样时隐藏自身，防自反馈
        var savedOpacity = Opacity;
        Opacity = 0;
        try
        {
            var brush = new VisualBrush(source)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = sampleRect,
                Stretch = Stretch.Fill,
            };
            if (brush.CanFreeze) brush.Freeze();

            var host = new Grid { Width = w, Height = h };
            if (BaseBrush is not null)
                host.Children.Add(new System.Windows.Shapes.Rectangle { Fill = BaseBrush });
            host.Children.Add(new System.Windows.Shapes.Rectangle { Width = w, Height = h, Fill = brush });

            host.Measure(new Size(w, h));
            host.Arrange(new Rect(0, 0, w, h));
            host.UpdateLayout();

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            rtb.Freeze();

            foreach (UIElement child in host.Children)
                if (child is System.Windows.Shapes.Rectangle r) { r.Effect = null; r.Fill = null; }

            var pixels = new byte[w * h * 4];
            rtb.CopyPixels(pixels, w * 4, 0);
            return pixels;
        }
        finally
        {
            Opacity = savedOpacity;
        }
    }

    /// <summary>找两个 Visual 的共同祖先。</summary>
    static Visual? FindCommonAncestor(Visual a, Visual b)
    {
        var ancestors = new HashSet<Visual>();
        for (var p = VisualTreeHelper.GetParent(a) as Visual; p is not null; p = VisualTreeHelper.GetParent(p) as Visual)
            ancestors.Add(p);
        for (var p = VisualTreeHelper.GetParent(b) as Visual; p is not null; p = VisualTreeHelper.GetParent(p) as Visual)
            if (ancestors.Contains(p)) return p;
        return null;
    }

    /// <summary>强制重渲染。</summary>
    public void Refresh() => InvalidateVisual();

    /// <summary>释放 GPU 资源（进程退出时调）。</summary>
    public void Shutdown()
    {
        lock (_pipelineLock)
        {
            _sharedPipeline?.Dispose();
            _sharedPipeline = null;
        }
        _lastW = _lastH = 0;
    }
}
