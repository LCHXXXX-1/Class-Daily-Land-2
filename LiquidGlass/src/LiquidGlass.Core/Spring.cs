using System;

namespace LiquidGlass.Core;

/// <summary>弹簧参数：感知时长（秒）+ 回弹（0 = 临界阻尼，越接近 1 越弹）。</summary>
/// <remarks>
/// 与 iOS / SwiftUI 的 <c>.spring(duration:bounce:)</c>，以及参考项目
/// <c>packages/liquid-glass/src/core/spring.ts</c> 的取值语义完全一致：
/// 刚度由时长决定（ω = 2π / duration），阻尼比 ζ = 1 - bounce。
/// </remarks>
public readonly record struct SpringOptions(double Duration, double Bounce)
{
    /// <summary>刚度系数 k = ω²。</summary>
    public double Stiffness
    {
        get
        {
            var omega = 2 * Math.PI / Duration;
            return omega * omega;
        }
    }

    /// <summary>阻尼系数 c：ζ = 1 - bounce。</summary>
    public double Damping
    {
        get
        {
            var omega = 2 * Math.PI / Duration;
            return Bounce >= 0
                ? 4 * Math.PI * (1 - Bounce) / Duration
                : 4 * Math.PI / (Duration * (1 + Bounce));
        }
    }
}

/// <summary>
/// 命名弹簧：所有玻璃交互共用同一套，动效才会读起来像同一种材料。
/// </summary>
public static class GlassSprings
{
    /// <summary>材料被手指/指针按住时的收缩。</summary>
    public static readonly SpringOptions Press = new(0.24, 0.2);

    /// <summary>松开：凝胶式过冲回落，Q弹的来源。</summary>
    public static readonly SpringOptions Release = new(0.5, 0.42);

    /// <summary>光斑在材料里浮入浮出。</summary>
    public static readonly SpringOptions Glow = new(0.42, 0);

    /// <summary>布局流动（尺寸/位置变化）。</summary>
    public static readonly SpringOptions Layout = new(0.4, 0.16);

    /// <summary>选择框跟随拖动（不弹，跟手）。</summary>
    public static readonly SpringOptions Track = new(0.18, 0);

    /// <summary>拖动中的元素跟手：比 <see cref="Track"/> 略慢一点，避免抖动。</summary>
    public static readonly SpringOptions Drag = new(0.16, 0);
}

/// <summary>
/// 一个阻尼谐振子（closed-form 解）。
/// </summary>
/// <remarks>
/// 数值积分（Euler/Semi-implicit）在掉帧时会发散或变慢，而弹簧是"手感"的核心，
/// 所以这里直接用解析解：无论 dt 多大都不会炸，掉帧也不改变真实时长。
/// 中途改目标保留当前速度 —— 所以任何动效都能被打断、反向、接着跑。
/// </remarks>
public sealed class Spring
{
    double _stiffness;
    double _damping;

    public Spring(double value, SpringOptions options)
    {
        Value = value;
        Target = value;
        Configure(options);
    }

    /// <summary>当前值。</summary>
    public double Value { get; private set; }

    /// <summary>当前速度（单位/秒）。</summary>
    public double Velocity { get; private set; }

    /// <summary>目标值。</summary>
    public double Target { get; set; }

    /// <summary>是否已静止。</summary>
    public bool Settled => Math.Abs(Value - Target) < 1e-6 && Math.Abs(Velocity) < 1e-6;

    /// <summary>换一组弹簧参数（保留当前值与速度）。</summary>
    public Spring Configure(SpringOptions options)
    {
        if (!(options.Duration > 0) || double.IsInfinity(options.Duration))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!(options.Bounce > -1 && options.Bounce < 1))
            throw new ArgumentOutOfRangeException(nameof(options));

        _stiffness = options.Stiffness;
        _damping = options.Damping;
        return this;
    }

    /// <summary>立刻跳到某值并停下。</summary>
    public Spring Jump(double value)
    {
        Value = Target = value;
        Velocity = 0;
        return this;
    }

    /// <summary>按秒推进；解析解对大 dt 稳定。</summary>
    public void Step(double dt)
    {
        if (!(dt > 0)) return;
        if (Math.Abs(Value - Target) < 1e-9 && Math.Abs(Velocity) < 1e-9)
        {
            Value = Target;
            Velocity = 0;
            return;
        }

        var omega = Math.Sqrt(_stiffness);
        var zeta = _damping / (2 * omega);
        var x0 = Value - Target;
        var v0 = Velocity;
        double x, v;

        if (zeta < 1)
        {
            var wd = omega * Math.Sqrt(1 - zeta * zeta);
            var decay = Math.Exp(-zeta * omega * dt);
            var a = x0;
            var b = (v0 + zeta * omega * x0) / wd;
            var cos = Math.Cos(wd * dt);
            var sin = Math.Sin(wd * dt);
            x = decay * (a * cos + b * sin);
            v = decay * ((b * wd - zeta * omega * a) * cos - (a * wd + zeta * omega * b) * sin);
        }
        else if (Math.Abs(zeta - 1) < 1e-9)
        {
            var decay = Math.Exp(-omega * dt);
            var b = v0 + omega * x0;
            x = decay * (x0 + b * dt);
            v = decay * (b - omega * (x0 + b * dt));
        }
        else
        {
            var root = omega * Math.Sqrt(zeta * zeta - 1);
            var r1 = -zeta * omega + root;
            var r2 = -zeta * omega - root;
            var c2 = (v0 - r1 * x0) / (r2 - r1);
            var c1 = x0 - c2;
            var e1 = Math.Exp(r1 * dt);
            var e2 = Math.Exp(r2 * dt);
            x = c1 * e1 + c2 * e2;
            v = c1 * r1 * e1 + c2 * r2 * e2;
        }

        // 收敛判据按量级缩放：小位移也需要走完，大位移不会提前收
        var scale = Math.Max(1, Math.Max(Math.Abs(Target), Math.Abs(x0)));
        if (Math.Abs(x) < 1e-4 * scale && Math.Abs(v) < 1e-3 * scale)
        {
            Value = Target;
            Velocity = 0;
        }
        else
        {
            Value = Target + x;
            Velocity = v;
        }
    }

    /// <summary>
    /// 把「已经走完的时间」一次性推进（用于事件驱动的补帧）。
    /// </summary>
    public static void Advance(Spring spring, double seconds, double frame = 1.0 / 120)
    {
        while (seconds > 0 && !spring.Settled)
        {
            var dt = Math.Min(frame, seconds);
            spring.Step(dt);
            seconds -= dt;
        }
    }
}

/// <summary>iOS 风格的橡皮筋：先跟手，越拖越沉，永远不硬停。</summary>
public static class RubberBand
{
    /// <summary>
    /// 把超出边界的位移折算成有阻尼的偏移。
    /// </summary>
    /// <param name="offset">原始位移（可正可负）。</param>
    /// <param name="limit">允许自由移动的上限。</param>
    /// <param name="coefficient">阻尼系数，默认 0.55。</param>
    public static double Apply(double offset, double limit, double coefficient = 0.55)
    {
        if (limit <= 0) return 0;
        var magnitude = Math.Abs(offset);
        return Math.Sign(offset) * limit * (1 - 1 / (magnitude * coefficient / limit + 1));
    }
}
