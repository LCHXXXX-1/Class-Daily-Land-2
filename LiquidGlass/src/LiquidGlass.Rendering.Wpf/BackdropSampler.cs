using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace LiquidGlass.Rendering.Wpf;

/// <summary>
/// 把一段真实可视化内容采样成位图（可选先做模糊）。
/// </summary>
/// <remarks>
/// 为什么用 <see cref="VisualBrush"/> + 一个临时 <see cref="Rectangle"/> 离屏渲染：
/// WPF 没有任何 API 能"读取某个元素背后画了什么"。标准做法是让玻璃层自己去画一遍
/// 背景 —— VisualBrush 就是那张"背景的实时照片"，随后对这张照片做模糊与折射，
/// 得到的效果与"透过玻璃看背景"等价。
///
/// 离屏 Rectangle 必须真的带上 Effect 才会在 RenderTargetBitmap 里生效
/// （已验证：RenderTargetBitmap 会走软件光栅化，Effect 会被应用）。
/// </remarks>
public static class BackdropSampler
{
    /// <summary>
    /// 采样一块 DIP 区域并模糊。
    /// </summary>
    /// <param name="source">背景视觉（通常是壁纸层/背景 Grid）。</param>
    /// <param name="dipRect">要采样的区域，坐标相对 <paramref name="source"/>。</param>
    /// <param name="scale">降采样比例（0.25 = 1/4 线性尺寸）。</param>
    /// <param name="blurRadius">模糊半径（DIP）。0 表示不模糊。</param>
    /// <param name="baseColor">
    /// 采样区里"透明处"垫的底色 —— 玻璃身下的窗口背景（云母/亚克力）不在 WPF 视觉树里，
    /// 直接截会得到透明像素，着色器采样到就是黑。垫一层主题底色，让折射有内容可折。
    /// </param>
    public static BitmapSource Capture(Visual source, Rect dipRect, double scale, double blurRadius,
        Brush? baseBrush = null)
    {
        if (dipRect.Width <= 0 || dipRect.Height <= 0)
            return CreateEmpty();

        scale = Math.Clamp(scale, 0.05, 1.0);
        var pxW = Math.Max(1, (int)Math.Round(dipRect.Width * scale));
        var pxH = Math.Max(1, (int)Math.Round(dipRect.Height * scale));

        // VisualBrush 用 Absolute Viewbox 精确裁出目标区域，Stretch=Fill 把它铺满
        var brush = new VisualBrush(source)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = dipRect,
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
        // VisualBrush 默认不冻结，但 Freeze 后能显著降低重复光栅化的开销
        if (brush.CanFreeze) brush.Freeze();

        // 底层 + 内容层：先铺不透明底（云母/亚克力取不到像素时用壁纸/底色兜底），
        // 再把内容画上去 —— 内容里的透明区域会透出底层，玻璃折射的就是这张合成图。
        var host = new Grid { Width = pxW, Height = pxH };
        if (baseBrush is not null)
            host.Children.Add(new Rectangle { Fill = baseBrush });
        host.Children.Add(new Rectangle { Width = pxW, Height = pxH, Fill = brush });

        if (blurRadius > 0.01)
        {
            host.Effect = new BlurEffect
            {
                // 半径按降采样比换算：位图小了，同样的视觉模糊只需要更小的像素半径
                Radius = Math.Max(0.5, blurRadius * scale),
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Performance,
            };
        }

        host.Measure(new Size(pxW, pxH));
        host.Arrange(new Rect(0, 0, pxW, pxH));
        host.UpdateLayout();

        var rtb = new RenderTargetBitmap(pxW, pxH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        rtb.Freeze();

        // 分离掉临时元素，避免它一直被渲染资源引用
        foreach (UIElement child in host.Children)
            if (child is Rectangle r) { r.Effect = null; r.Fill = null; }

        return rtb;
    }

    static BitmapSource CreateEmpty()
    {
        var bmp = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[4], 4);
        bmp.Freeze();
        return bmp;
    }
}
