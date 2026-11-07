using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClassDailyLand2.App.Views;

/// <summary>
/// 提示框的统一入场动画：**从底边加速升上来 + 淡入**。
///
///   · 起点在窗口底边之下（位移 = 内容自身高度），所以是"从底下升上来"而不是"原地晃一下"
///   · 前 2/3 行程**加速**（AccelerationRatio 0.65），最后 1/3 收住（DecelerationRatio 0.35）
///     —— 越升越快、到顶不硬撞
///   · 只动内容的 RenderTransform 与 Opacity，不挪窗口本身：
///     带系统边框的窗口挪起来会跳帧，自绘圆角窗口也会跟着抖
/// </summary>
public static class UiMotion
{
    /// <summary>
    /// 动画时长的来源（由 AppController 注入，读设置里的 <c>animation_duration_ms</c>）。
    /// 返回 &lt;= 0 表示用户关了动画 —— 一律直接落位，不闪一下透明度再补。
    /// </summary>
    public static Func<int>? DurationProvider { get; set; }

    /// <summary>
    /// 让 <paramref name="target"/> 从窗口底边之下升上来。
    /// <paramref name="maxDistance"/> 限制最大位移（高窗只升一段，免得空窗太久）。
    /// </summary>
    public static void SlideUpIn(FrameworkElement target, double maxDistance = 420, double durationMs = -1)
    {
        if (target is null) return;

        var ms = durationMs >= 0
            ? durationMs
            : (DurationProvider?.Invoke() ?? 320);

        var height = target.ActualHeight;
        var distance = double.IsNaN(height) || height <= 0
            ? 120
            : Math.Clamp(height, 48, maxDistance);

        // 关了动画：直接停在最终位置
        if (ms <= 0)
        {
            target.BeginAnimation(UIElement.OpacityProperty, null);
            target.Opacity = 1;
            if (target.RenderTransform is TranslateTransform t)
            {
                t.BeginAnimation(TranslateTransform.YProperty, null);
                t.Y = 0;
            }
            return;
        }

        var transform = target.RenderTransform as TranslateTransform ?? new TranslateTransform();
        target.RenderTransform = transform;
        target.Opacity = 0;

        // 加速升上来：0.65 的时间在加速，0.35 的时间减速停稳
        var travel = new DoubleAnimation
        {
            From = distance,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(ms),
            AccelerationRatio = 0.65,
            DecelerationRatio = 0.35,
        };

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            // 淡入略快于位移：内容先"看得见"，再稳稳落到位置
            Duration = TimeSpan.FromMilliseconds(ms * 0.6),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        transform.BeginAnimation(TranslateTransform.YProperty, travel, HandoffBehavior.SnapshotAndReplace);
        target.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }
}
