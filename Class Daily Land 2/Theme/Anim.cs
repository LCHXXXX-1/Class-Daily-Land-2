using System.Windows;
using System.Windows.Media.Animation;

namespace ClassDailyLand2.App.Theme;

/// <summary>
/// 动画统一入口：缓动曲线与时长**全部从 Themes/Tokens.Base.xaml 取**，
/// 改一处令牌，所有窗口一起变。
///
/// 背景：原生动画散在各窗口里 —— 主窗自己 new QuinticEase、副岛/岛各写一份
/// 33ms 帧循环做 OutCubic 插值、面板再写一份 16ms 的。曲线和时长四套，
/// 改一个动效要翻四个文件。现在收敛到这里：
///   · 窗口几何、宽高、透明度 → <see cref="Slide(double, int, string)"/>
///   · 需要自定义关键帧的（跑马灯、流光）→ 仍就地写 DoubleAnimationUsingKeyFrames，
///     但缓动/时长同样走这里的令牌。
/// </summary>
public static class Anim
{
    /// <summary>取令牌里的缓动函数（EaseOutCubic / EaseDecelerate / EaseLiquid…）。</summary>
    public static IEasingFunction? Ease(string key = "EaseOutCubic")
        => Application.Current?.TryFindResource(key) as IEasingFunction;

    /// <summary>取令牌里的时长（DurationFast / DurationNormal / DurationSurface…）。</summary>
    public static Duration Span(string key = "DurationNormal")
    {
        var raw = Application.Current?.TryFindResource(key) as string;
        return TimeSpan.TryParse(raw, out var ts)
            ? new Duration(ts)
            : new Duration(System.TimeSpan.FromMilliseconds(250));
    }

    /// <summary>毫秒时长（设置项 animation_duration_ms 驱动时用这个）。</summary>
    public static Duration Span(int ms)
        => new(System.TimeSpan.FromMilliseconds(Math.Max(0, ms)));

    /// <summary>滑到目标值的 DoubleAnimation；ms &lt;= 0 表示「不动画」。</summary>
    public static DoubleAnimation? Slide(double to, int ms, string easeKey = "EaseOutCubic")
    {
        if (ms <= 0) return null;
        return new DoubleAnimation(to, Span(ms)) { EasingFunction = Ease(easeKey) };
    }

    /// <summary>滑到目标值的 DoubleAnimation，时长取令牌。</summary>
    public static DoubleAnimation Slide(double to, string durationKey = "DurationNormal",
                                        string easeKey = "EaseOutCubic")
        => new(to, Span(durationKey)) { EasingFunction = Ease(easeKey) };

    /// <summary>把动画挂到依赖属性上；ms &lt;= 0 时直接落到位（等于关掉动画）。</summary>
    public static void SlideTo(this UIElement element, DependencyProperty dp, double to,
                               int ms, string easeKey = "EaseOutCubic")
    {
        var anim = Slide(to, ms, easeKey);
        if (anim is null)
        {
            element.BeginAnimation(dp, null);
            element.SetValue(dp, to);
            return;
        }
        element.BeginAnimation(dp, anim);
    }
}
