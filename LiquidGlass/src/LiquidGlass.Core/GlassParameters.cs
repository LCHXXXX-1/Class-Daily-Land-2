using System;

namespace LiquidGlass.Core;

/// <summary>
/// 材质种类。与参考项目 <c>core/materials.ts</c> 的 <c>clear | regular</c> 一一对应。
/// </summary>
public enum GlassMaterial
{
    /// <summary>通透玻璃：背景几乎不糊，只靠边缘折射。小控件、浮层用。</summary>
    Clear = 0,

    /// <summary>标准玻璃：轻磨砂 + 乳白填充 + 边缘折射。系统面板用。</summary>
    Regular = 1,
}

/// <summary>浅色 / 深色外观。决定色调曲线、边缘光的强弱对比。</summary>
public enum GlassAppearance
{
    Light = 0,
    Dark = 1,
}

/// <summary>
/// 一套完整的玻璃光学参数。
/// </summary>
/// <remarks>
/// 数值全部对标参考项目（<c>core/materials.ts</c> + <c>gpu/maps.wgsl</c>），
/// 只是把「SVG 滤镜管线里的常量」翻译成了「着色器直接能吃的物理量」：
///
/// * 参考项目用 feDisplacementMap，位移量以 CSS 像素给出（refraction: 60）；
///   这里同样是像素，<see cref="RefractionPixels"/> 就是边缘处的最大位移。
/// * 参考项目的折射带深度是**固定的物理像素**（maps.wgsl 里 <c>depth = sat(-d*0.05)</c>，
///   即 20 物理像素），不随控件尺寸缩放 —— 所以小控件整个都是一片透镜。
///   这里保留这个语义，用 <see cref="RefractionBand"/> 表达。
/// * Regular 材质是「两路模糊（frost + fill）亮化混合」，见 <see cref="FillSigma"/>。
/// </remarks>
public sealed class GlassParameters
{
    /// <summary>材质种类。</summary>
    public GlassMaterial Material { get; set; } = GlassMaterial.Clear;

    /// <summary>外观。</summary>
    public GlassAppearance Appearance { get; set; } = GlassAppearance.Light;

    /// <summary>连续圆角半径（DIP）。</summary>
    public double CornerRadius { get; set; } = 28;

    /// <summary>
    /// 连续曲率强度：0 = 正圆角，1 = 最"苹果"的超椭圆。
    /// </summary>
    /// <remarks>
    /// 参考项目用 0.8125 的 corner smoothing（squircle）。这里是同一条曲线的
    /// 超椭圆近似：0.85 时指数约 4，与参考几乎一致。
    /// </remarks>
    public double CornerSmoothing { get; set; } = 0.85;

    /// <summary>
    /// 边缘最大折射位移（DIP）。
    /// </summary>
    /// <remarks>
    /// 参考项目默认 60（大面板），滑块 5、开关拇指 7.75。这个值**不随控件尺寸缩放**，
    /// 所以小控件要显式给小值，否则整块会被吸变形。
    /// </remarks>
    public double RefractionPixels { get; set; } = 40;

    /// <summary>
    /// 折射带深度（DIP）：从边缘向内多少距离内发生弯折。
    /// </summary>
    /// <remarks>
    /// 参考实现固定 20 物理像素。太窄（&lt;8）只剩一条白边，看不出"液态"；
    /// 太宽（&gt;控件短边的一半）整个面板都会扭曲，文字发飘。
    /// </remarks>
    public double RefractionBand { get; set; } = 20;

    /// <summary>色散：RGB 三通道位移量的相对差（0.02~0.10 自然）。</summary>
    public double Dispersion { get; set; } = 0.055;

    /// <summary>frost 模糊的 sigma（DIP）。Clear = 0，Regular ≈ 7.3。</summary>
    public double BlurSigma { get; set; }

    /// <summary>fill 模糊的 sigma（DIP）：宽模糊，用于 Regular 的乳白填充。</summary>
    public double FillSigma { get; set; } = 23;

    /// <summary>fill 混合比例：越大越乳白、越不透明。</summary>
    public double FillOpacity { get; set; } = 0.34;

    /// <summary>阴影提升：把暗部抬起来，玻璃下方的内容不会发黑。</summary>
    public double ShadowLift { get; set; } = 0.18;

    /// <summary>高光滚降：压住过亮的区域，避免边缘糊成一片白。</summary>
    public double HighlightRolloff { get; set; }

    /// <summary>色调曲线：输出 = a + b·v + q·v²，按 <see cref="ToneWeight"/> 与原始值混合。</summary>
    public double ToneA { get; set; } = 1;

    /// <inheritdoc cref="ToneA" />
    public double ToneB { get; set; } = 1;

    /// <inheritdoc cref="ToneA" />
    public double ToneQ { get; set; }

    /// <summary>色调曲线权重（0 = 完全不用曲线，1 = 完全用曲线）。</summary>
    public double ToneWeight { get; set; }

    /// <summary>饱和度系数（1 = 不变，&gt;1 更艳）。</summary>
    public double Chroma { get; set; } = 1;

    /// <summary>边缘光强度：白色高光 + 深色侧边的整体倍率。</summary>
    public double RimStrength { get; set; } = 1;

    /// <summary>边缘光宽（DIP）。参考实现的描边约 1~1.5px。</summary>
    public double RimWidth { get; set; } = 1.2;

    /// <summary>色膜颜色（#AARRGGBB）。为空/白色表示不加色膜。</summary>
    public string? TintColor { get; set; }

    /// <summary>色膜强度（0~1）。</summary>
    public double TintOpacity { get; set; }

    /// <summary>背景快照的降采样比例（0.5 = 半分辨率）。</summary>
    /// <remarks>
    /// 折射会把这块图放大着用，采样率太低时弯折处会看到马赛克。0.6~0.75 比较稳。
    /// </remarks>
    public double BackdropScale { get; set; } = 0.75;

    /// <summary>
    /// 玻璃本体的薄白不透明度（0~0.3）。
    /// </summary>
    /// <remarks>
    /// 云母 / 亚克力下背板由 DWM 绘制，WPF 采样到的是透明像素（拿到像素就等于
    /// 截了桌面壁纸，这是明确禁止的）。此时玻璃不能自己造一块底色，只能以
    /// "一层薄白 + 透传背景 alpha"的方式存在 —— 这个值就是那层薄白的浓度。
    /// 背景不透明（纯色 / 图片）时它仍会叠在折射结果上，保持与周围卡片同一档白度。
    /// </remarks>
    public double SurfaceOpacity { get; set; } = 0.14;

    /// <summary>
    /// 折射带内额外的本体不透明度（0~0.3），用来读出"玻璃有厚度"的边缘。
    /// </summary>
    /// <remarks>给 0 时胶囊内外完全均匀；给 0.06~0.12 时边缘会有一道柔和的加白，
    /// 应该看得出来但绝不能读成一条白描边。</remarks>
    public double EdgeOpacity { get; set; } = 0.08;

    /// <summary>
    /// 接管背板的程度（0 = 普通半透明玻璃，1 = 玻璃本身就是背板）。
    /// </summary>
    /// <remarks>
    /// 整窗「液态玻璃背景板」用 1：折射后的背景以不透明形式输出，
    /// 背后的清晰壁纸/图片不会从半透明处漏出来，整块板才像一块真玻璃。
    /// </remarks>
    public double Coverage { get; set; } = 0;

    /// <summary>深拷贝。</summary>
    public GlassParameters Clone() => (GlassParameters)MemberwiseClone();

    /// <summary>夹到安全范围，防止调参到极端值时黑屏或 NaN。</summary>
    public GlassParameters Sanitized()
    {
        CornerRadius = Math.Max(0, CornerRadius);
        CornerSmoothing = Math.Clamp(CornerSmoothing, 0, 1);
        RefractionPixels = Math.Clamp(RefractionPixels, 0, 200);
        RefractionBand = Math.Clamp(RefractionBand, 2, 120);
        Dispersion = Math.Clamp(Dispersion, 0, 0.3);
        BlurSigma = Math.Clamp(BlurSigma, 0, 60);
        FillSigma = Math.Clamp(FillSigma, 0, 80);
        FillOpacity = Math.Clamp(FillOpacity, 0, 1);
        ShadowLift = Math.Clamp(ShadowLift, 0, 0.6);
        HighlightRolloff = Math.Clamp(HighlightRolloff, 0, 0.6);
        ToneWeight = Math.Clamp(ToneWeight, 0, 1);
        Chroma = Math.Clamp(Chroma, 0, 2);
        RimStrength = Math.Clamp(RimStrength, 0, 3);
        RimWidth = Math.Clamp(RimWidth, 0.4, 6);
        TintOpacity = Math.Clamp(TintOpacity, 0, 1);
        BackdropScale = Math.Clamp(BackdropScale, 0.2, 1.0);
        SurfaceOpacity = Math.Clamp(SurfaceOpacity, 0, 0.5);
        EdgeOpacity = Math.Clamp(EdgeOpacity, 0, 0.5);
        Coverage = Math.Clamp(Coverage, 0, 1);
        return this;
    }
}
