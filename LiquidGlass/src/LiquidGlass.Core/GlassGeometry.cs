using System;

namespace LiquidGlass.Core;

/// <summary>
/// 圆角矩形 SDF（有符号距离场）与法线计算。
/// </summary>
/// <remarks>
/// 这里是**着色器公式的 C# 镜像**：CPU 侧用它算命中测试、阴影轮廓和光斑位置，
/// GPU 侧（GlassRefraction.hlsl）用同一套公式算边缘折射。
/// 两边必须保持一致，否则「视觉边缘」和「可点击边缘」会错位半像素以上。
/// </remarks>
public static class GlassGeometry
{
    /// <summary>
    /// 圆角矩形有符号距离：内部为负、边缘为 0、外部为正。单位是 DIP。
    /// </summary>
    /// <param name="px">相对矩形中心点的 x。</param>
    /// <param name="py">相对矩形中心点的 y。</param>
    /// <param name="halfWidth">半宽。</param>
    /// <param name="halfHeight">半高。</param>
    /// <param name="radius">圆角半径（会被自动夹到不超过短边一半）。</param>
    public static double RoundedRectSdf(double px, double py, double halfWidth, double halfHeight, double radius)
    {
        var r = Math.Min(radius, Math.Min(halfWidth, halfHeight));
        var qx = Math.Abs(px) - halfWidth + r;
        var qy = Math.Abs(py) - halfHeight + r;
        var outsideX = Math.Max(qx, 0);
        var outsideY = Math.Max(qy, 0);
        return Math.Sqrt(outsideX * outsideX + outsideY * outsideY)
               + Math.Min(Math.Max(qx, qy), 0) - r;
    }

    /// <summary>点是否落在圆角矩形内。</summary>
    public static bool HitTest(double px, double py, double width, double height, double radius)
        => RoundedRectSdf(px - width / 2, py - height / 2, width / 2, height / 2, radius) <= 0;

    /// <summary>
    /// 边缘法线（指向外部，单位向量）。折射方向由它决定：
    /// 只有贴着边缘的那一圈（法线有明显方向）才会把背景"掰弯"。
    /// </summary>
    public static (double X, double Y) EdgeNormal(double px, double py, double halfWidth, double halfHeight, double radius)
    {
        var r = Math.Min(radius, Math.Min(halfWidth, halfHeight));
        var qx = Math.Abs(px) - halfWidth + r;
        var qy = Math.Abs(py) - halfHeight + r;

        double nx, ny;
        if (qx > 0 && qy > 0)
        {
            // 圆角段：法线沿圆心方向
            var len = Math.Sqrt(qx * qx + qy * qy);
            if (len < 1e-6) return (0, 0);
            nx = qx / len * Math.Sign(px == 0 ? 1 : px);
            ny = qy / len * Math.Sign(py == 0 ? 1 : py);
        }
        else if (qx > qy)
        {
            nx = Math.Sign(px == 0 ? 1 : px);
            ny = 0;
        }
        else
        {
            nx = 0;
            ny = Math.Sign(py == 0 ? 1 : py);
        }

        var mag = Math.Sqrt(nx * nx + ny * ny);
        return mag < 1e-6 ? (0, 0) : (nx / mag, ny / mag);
    }

    /// <summary>
    /// 边缘权重：0 = 远离边缘（中心区），1 = 正好在边缘上。
    /// 玻璃的折射与边光都乘这个权重，得到"边缘强、中心弱"的光学分布。
    /// </summary>
    /// <param name="sdf">该点的 SDF 值（内部为负）。</param>
    /// <param name="band">边缘过渡带宽度（DIP）。</param>
    public static double EdgeWeight(double sdf, double band)
    {
        if (band <= 1e-6) return sdf <= 0 ? 1 : 0;
        var t = 1 - Math.Abs(sdf) / band;   // |sdf| 越小越接近边缘
        return Math.Clamp(t, 0, 1);
    }

    /// <summary>
    /// 按光照方向算一圈边缘的亮度分布，用于阴影/高光的分区绘制。
    /// </summary>
    /// <param name="normalX">外法线 x。</param>
    /// <param name="normalY">外法线 y。</param>
    /// <param name="lightAngleDeg">光照方向（度，0=右，90=上）。</param>
    public static double Lambert(double normalX, double normalY, double lightAngleDeg)
    {
        var rad = lightAngleDeg * Math.PI / 180.0;
        var lx = Math.Cos(rad);
        var ly = -Math.Sin(rad);            // 屏幕坐标 y 向下
        return Math.Clamp(normalX * lx + normalY * ly, -1, 1);
    }
}
