using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace LiquidGlass.Rendering.Wpf;

/// <summary>
/// Liquid Glass 主着色器：squircle 轮廓 + 边缘位移折射 + 色散 + 边缘光 + Regular 乳白填充。
/// </summary>
/// <remarks>
/// 输入是**两张已经模糊过的背景裁片**（frost 与 fill），通过两个采样器属性传入。
/// 分工对应参考实现的滤镜管线：
/// <list type="bullet">
///   <item>frost（s0）：窄模糊，Regular 为 7.33，Clear 为 0。</item>
///   <item>fill（s1）：宽模糊（≈23），只在 Regular 里参与「亮化混合」。</item>
/// </list>
///
/// ★ WPF 的 <c>ShaderEffect</c> 不会把元素自身的渲染自动绑到 s0：只写
/// <c>sampler2D X : register(s0)</c> 而不注册采样器属性时，采样永远返回空（整块黑），
/// 而 uv / 数学运算照常工作 —— 极易误判成着色器写错。必须用
/// <see cref="ShaderEffect.RegisterPixelShaderSamplerProperty"/> 显式注册并在构造函数里
/// <c>UpdateShaderValue</c>。
///
/// ★ 常量寄存器按依赖属性**注册顺序**铺到 c0、c1……，每个 Point4D 占一个寄存器。
/// 因此下面的属性顺序必须与 GlassSurface.hlsl 里的声明顺序严格一致。
/// </remarks>
public sealed class LiquidGlassEffect : ShaderEffect
{
    const string PackUri =
        "pack://application:,,,/LiquidGlass.Rendering.Wpf;component/Shaders/Compiled/GlassSurface.ps";

    static readonly PixelShader Shader = new() { UriSource = new Uri(PackUri) };

    // s0：窄模糊背景裁片
    public static readonly DependencyProperty BackdropProperty =
        RegisterPixelShaderSamplerProperty(nameof(Backdrop), typeof(LiquidGlassEffect), 0);

    // s1：宽模糊背景裁片（Regular 的乳白填充）
    public static readonly DependencyProperty FillBackdropProperty =
        RegisterPixelShaderSamplerProperty(nameof(FillBackdrop), typeof(LiquidGlassEffect), 1);

    // c0: SurfaceW, SurfaceH, HalfShapeW, HalfShapeH   （物理像素）
    public static readonly DependencyProperty SizeParamsProperty = DependencyProperty.Register(
        nameof(SizeParams), typeof(Point4D), typeof(LiquidGlassEffect),
        new UIPropertyMetadata(new Point4D(100, 100, 40, 40), PixelShaderConstantCallback(0)));

    // c1: RadiusPx, 1/BandPx, 1/AaPx, 1/RimWidthPx
    public static readonly DependencyProperty ShapeParamsProperty = DependencyProperty.Register(
        nameof(ShapeParams), typeof(Point4D), typeof(LiquidGlassEffect),
        new UIPropertyMetadata(new Point4D(20, 0.05, 1, 0.8), PixelShaderConstantCallback(1)));

    // c2: 折射 uv 位移（x/y 轴各自），色散，未用
    public static readonly DependencyProperty RefractParamsProperty = DependencyProperty.Register(
        nameof(RefractParams), typeof(Point4D), typeof(LiquidGlassEffect),
        new UIPropertyMetadata(new Point4D(0.2, 0.2, 0.05, 0), PixelShaderConstantCallback(2)));

    // c3: 边缘光强度，未用 ×3
    public static readonly DependencyProperty OpticsParamsProperty = DependencyProperty.Register(
        nameof(OpticsParams), typeof(Point4D), typeof(LiquidGlassEffect),
        new UIPropertyMetadata(new Point4D(1, 0, 0, 0), PixelShaderConstantCallback(3)));

    // c4: FillOpacity, ToneWeight, ShadowLift, 未用
    public static readonly DependencyProperty MaterialParamsProperty = DependencyProperty.Register(
        nameof(MaterialParams), typeof(Point4D), typeof(LiquidGlassEffect),
        new UIPropertyMetadata(new Point4D(0, 0, 0.18, 0), PixelShaderConstantCallback(4)));

    // c5: ToneA, ToneB, ToneQ, 未用
    public static readonly DependencyProperty ToneParamsProperty = DependencyProperty.Register(
        nameof(ToneParams), typeof(Point4D), typeof(LiquidGlassEffect),
        new UIPropertyMetadata(new Point4D(1, 1, 0, 0), PixelShaderConstantCallback(5)));

    public LiquidGlassEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(BackdropProperty);
        UpdateShaderValue(FillBackdropProperty);
        UpdateShaderValue(SizeParamsProperty);
        UpdateShaderValue(ShapeParamsProperty);
        UpdateShaderValue(RefractParamsProperty);
        UpdateShaderValue(OpticsParamsProperty);
        UpdateShaderValue(MaterialParamsProperty);
        UpdateShaderValue(ToneParamsProperty);
    }

    /// <summary>窄模糊背景裁片（ImageBrush）。</summary>
    public Brush? Backdrop
    {
        get => (Brush?)GetValue(BackdropProperty);
        set => SetValue(BackdropProperty, value);
    }

    /// <summary>宽模糊背景裁片（ImageBrush）。Clear 材质直接复用 frost。</summary>
    public Brush? FillBackdrop
    {
        get => (Brush?)GetValue(FillBackdropProperty);
        set => SetValue(FillBackdropProperty, value);
    }

    /// <summary>绘制表面尺寸与被绘制形状的半宽半高（物理像素）。</summary>
    public Point4D SizeParams
    {
        get => (Point4D)GetValue(SizeParamsProperty);
        set => SetValue(SizeParamsProperty, value);
    }

    /// <summary>圆角半径、折射带深度倒数、抗锯齿宽度倒数、描边宽度倒数（均为物理像素）。</summary>
    public Point4D ShapeParams
    {
        get => (Point4D)GetValue(ShapeParamsProperty);
        set => SetValue(ShapeParamsProperty, value);
    }

    /// <summary>uv 空间的折射位移（xy）、色散比例。</summary>
    public Point4D RefractParams
    {
        get => (Point4D)GetValue(RefractParamsProperty);
        set => SetValue(RefractParamsProperty, value);
    }

    /// <summary>边缘光强度。</summary>
    public Point4D OpticsParams
    {
        get => (Point4D)GetValue(OpticsParamsProperty);
        set => SetValue(OpticsParamsProperty, value);
    }

    /// <summary>填充比例、色调曲线权重、阴影提升。</summary>
    public Point4D MaterialParams
    {
        get => (Point4D)GetValue(MaterialParamsProperty);
        set => SetValue(MaterialParamsProperty, value);
    }

    /// <summary>色调曲线二次项系数。</summary>
    public Point4D ToneParams
    {
        get => (Point4D)GetValue(ToneParamsProperty);
        set => SetValue(ToneParamsProperty, value);
    }
}
