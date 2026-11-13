using System;
using System.Windows;
using System.Windows.Media;

namespace LiquidGlass.Controls;

/// <summary>
/// 连续圆角（squircle）几何。
/// </summary>
/// <remarks>
/// Apple 的圆角不是圆弧：它是一条曲率连续的超椭圆，直边到角之间没有曲率的突变点。
/// 用正圆弧时，边框高光会在切点处"折"一下，这是"看着不像苹果"最明显的一处。
///
/// 这里用超椭圆 |x/a|^p + |y/b|^p = 1（p 由 <paramref name="smoothing"/> 决定，
/// 0.85 时 p ≈ 4）逐点采样后连成折线。以 32 段/角逼近，视觉上与参考项目的
/// 贝塞尔连续圆角无法区分。
/// </remarks>
public static class SquircleGeometry
{
    /// <summary>
    /// 生成一个 squircle 路径。
    /// </summary>
    /// <param name="width">宽（DIP）。</param>
    /// <param name="height">高（DIP）。</param>
    /// <param name="radius">圆角半径（DIP）。</param>
    /// <param name="smoothing">连续曲率强度 0~1（0 = 正圆，0.85 ≈ 参考项目）。</param>
    public static Geometry Create(double width, double height, double radius, double smoothing = 0.85)
    {
        if (width <= 0 || height <= 0) return Geometry.Empty;

        var r = Math.Min(Math.Max(radius, 0), Math.Min(width, height) * 0.5);
        if (r <= 0.01) return new RectangleGeometry(new Rect(0, 0, width, height));

        // 平滑度 → 超椭圆指数：0 → 2（椭圆，等价于圆弧角），1 → 6（更方的"方糖"）
        var p = 2 + 4 * Math.Clamp(smoothing, 0, 1);
        const int steps = 24;

        var path = new StreamGeometry();
        using (var ctx = path.Open())
        {
            // 四角圆心
            var topLeft = new Point(r, r);
            var topRight = new Point(width - r, r);
            var bottomRight = new Point(width - r, height - r);
            var bottomLeft = new Point(r, height - r);

            ctx.BeginFigure(new Point(r, 0), isFilled: true, isClosed: true);

            // 上边 → 右上角
            ctx.LineTo(new Point(width - r, 0), true, false);
            AppendCorner(ctx, topRight, r, p, -Math.PI / 2, 0, steps);

            // 右边 → 右下角
            ctx.LineTo(new Point(width, height - r), true, false);
            AppendCorner(ctx, bottomRight, r, p, 0, Math.PI / 2, steps);

            // 下边 → 左下角
            ctx.LineTo(new Point(r, height), true, false);
            AppendCorner(ctx, bottomLeft, r, p, Math.PI / 2, Math.PI, steps);

            // 左边 → 左上角
            ctx.LineTo(new Point(0, r), true, false);
            AppendCorner(ctx, topLeft, r, p, Math.PI, Math.PI * 1.5, steps);
        }

        path.Freeze();
        return path;
    }

    /// <summary>沿超椭圆走一段 1/4 角。</summary>
    static void AppendCorner(StreamGeometryContext ctx, Point center, double r, double p, double from, double to, int steps)
    {
        // 超椭圆参数方程：x = r·sign(cosθ)·|cosθ|^(2/p)，y = r·sign(sinθ)·|sinθ|^(2/p)
        // 当 p = 2 时退化为圆弧；p > 2 时角更"撑"，得到连续曲率。
        var e = 2.0 / p;
        for (var i = 1; i <= steps; i++)
        {
            var a = from + (to - from) * i / steps;
            var c = Math.Cos(a);
            var s = Math.Sin(a);
            var x = center.X + r * Math.Sign(c) * Math.Pow(Math.Abs(c), e);
            var y = center.Y + r * Math.Sign(s) * Math.Pow(Math.Abs(s), e);
            ctx.LineTo(new Point(x, y), true, false);
        }
    }
}
