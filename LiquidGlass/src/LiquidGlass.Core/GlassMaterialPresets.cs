using System;

namespace LiquidGlass.Core;

/// <summary>
/// 材质预设。数值直接取自参考项目 <c>core/materials.ts</c> 与
/// <c>dom/material-graph.ts</c>，不是凭感觉调的。
/// </summary>
public static class GlassMaterialPresets
{
    /// <summary>取一套材质参数。</summary>
    /// <param name="material">clear / regular。</param>
    /// <param name="appearance">浅色 / 深色。</param>
    /// <param name="refractionPixels">
    /// 边缘折射位移（DIP）。参考实现的默认值是 60，但那是给大面板的；
    /// 小控件应当给 5~10，否则整块会被吸到变形。
    /// </param>
    public static GlassParameters Create(
        GlassMaterial material = GlassMaterial.Clear,
        GlassAppearance appearance = GlassAppearance.Light,
        double refractionPixels = 40)
    {
        var p = new GlassParameters
        {
            Material = material,
            Appearance = appearance,
            RefractionPixels = refractionPixels,
        };

        var dark = appearance == GlassAppearance.Dark;

        if (material == GlassMaterial.Regular)
        {
            // Regular：frost 只轻糊，fill 是很宽的模糊，两者 max 混合后按 fillOpacity
            // 压回 frost —— 这就是系统面板那种"乳白但能看见轮廓"的来源。
            p.BlurSigma = 7.33;
            p.FillSigma = dark ? 21.8 : 23;
            p.FillOpacity = dark ? 0.65 : 0.34;
            p.ShadowLift = 0;

            // 色调曲线：线性 + 二次项，按 0.8 / 0.9 权重与原始值混合
            p.ToneA = dark ? 0.105 : 0.36;
            p.ToneB = dark ? 0.83 : 0.67;
            p.ToneQ = dark ? -0.2091 : -0.1113;
            p.ToneWeight = dark ? 0.90 : 0.80;
            p.Chroma = dark ? 1.0648 : 1.1031;

            // 深色下玻璃要比背景亮一点才看得出边界，但不能发白
            p.TintOpacity = dark ? 0.10 : 0.0;
            p.TintColor = dark ? "#FFFFFFFF" : null;
        }
        else
        {
            // Clear：完全不糊。只抬暗部（shadowLift），保留中间调与高光。
            p.BlurSigma = 0;
            p.FillSigma = 0;
            p.FillOpacity = 0;
            p.ShadowLift = dark ? 0.15 : 0.18;
            p.HighlightRolloff = dark ? 0.20 : 0;
            p.ToneWeight = 0;
            p.ToneA = 1;
            p.ToneB = 1;
            p.ToneQ = 0;
            p.Chroma = 1;

            // 参考实现还留了一层极淡的色膜（浅色 6% 白 / 深色 14% 暗）
            p.TintColor = dark ? "#FF1A1A20" : "#FFFFFFFF";
            p.TintOpacity = dark ? 0.14 : 0.06;
        }

        // 边缘光：原实现的描边是 ~1px，深色下要更亮才"发得出来"
        p.RimWidth = dark ? 1.0 : 1.2;
        p.RimStrength = dark ? 1.15 : 1.0;

        return p.Sanitized();
    }

    /// <summary>按尺寸推荐一个折射值（小控件用小位移，大面板用大位移）。</summary>
    /// <remarks>
    /// 参考实现是每个组件写死（滑块 5 / 开关 7.75 / 面板 60）。这里给一条连续经验公式，
    /// 让控件库不必逐个枚举：短边 24 → 6px，短边 60 → 14px，短边 180 → 40px 左右。
    /// </remarks>
    public static double RecommendRefraction(double shortSideDip)
        => Math.Round(Math.Clamp(shortSideDip * 0.22, 4, 60), 2);
}
