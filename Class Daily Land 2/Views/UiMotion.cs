using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClassDailyLand2.App.Views;

/// <summary>
/// 弹窗的统一入场动画：**从窗口底边下面升上来**（不淡入、不做透明度）。
///
///   · 起点：整块在窗口下沿之外（窗口内的确认框会把这个位移算准传进来），
///     所以读起来是"从底下弹上来"，而不是"原地晃一下"；
///   · 缓动：QuarticEase.EaseOut —— 起步最猛、后半程越来越慢、末端停住，没有回弹；
///   · 收起是同一套缓动的反方向（往下滑出），进出对得上；
///   · **不做透明度动画**：① 确认框不应该半透明；② 带落影的卡片每帧改 opacity 会让
///     整个 DropShadowEffect 重新栅格化，那正是"动画有点卡"的来源；
///   · 位移期间套一层 <see cref="BitmapCache"/>，落定立刻撤掉（否则文字会一直发虚）——
///     和设置页整页平移用的是同一招。
/// </summary>
public static class UiMotion
{
    /// <summary>
    /// 动画时长的来源（由 AppController 注入，读设置里的 <c>animation_duration_ms</c>）。
    /// 返回 &lt;= 0 表示用户关了动画 —— 一律直接落位。
    /// </summary>
    public static Func<int>? DurationProvider { get; set; }

    /// <summary>
    /// 让 <paramref name="target"/> 从下方升上来。
    /// </summary>
    /// <param name="target">要动的元素。</param>
    /// <param name="maxDistance">
    /// 不指定 <paramref name="fromBottom"/> 时的最大位移（高内容只升一段，免得空窗太久）。
    /// </param>
    /// <param name="durationMs">-1 = 用设置里的时长。</param>
    /// <param name="fromBottom">
    /// 明确的起始位移（像素）。窗口内的确认框会算成"整块刚好在窗口下沿之外"，用这个。
    /// </param>
    public static void SlideUpIn(FrameworkElement target, double maxDistance = 420,
                                 double durationMs = -1, double fromBottom = -1)
    {
        if (target is null) return;

        // 不做透明度动画：从头到尾都是不透明的
        target.BeginAnimation(UIElement.OpacityProperty, null);
        target.Opacity = 1;

        var transform = target.RenderTransform as TranslateTransform ?? new TranslateTransform();
        target.RenderTransform = transform;

        var ms = durationMs >= 0 ? durationMs : DurationProvider?.Invoke() ?? 320;

        var height = target.ActualHeight;
        var distance = fromBottom > 0
            ? fromBottom
            : Math.Clamp(double.IsNaN(height) || height <= 0 ? 120 : height, 48, maxDistance);

        // 关了动画：直接停在最终位置
        if (ms <= 0)
        {
            target.CacheMode = null;
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = 0;
            return;
        }

        // 位移期间用位图缓存：卡片带 DropShadowEffect，不缓存的话每帧都要重画阴影
        target.CacheMode = new BitmapCache();

        // QuarticEase 的 EaseOut：起步最猛，后半程越来越慢，末端几乎是"贴"上去的
        // —— 也就是"先快、然后慢慢变慢、最后停下"，没有任何回弹。
        var travel = new DoubleAnimation(distance, 0, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
        };

        travel.Completed += (_, _) =>
        {
            // 落定后撤掉缓存：位图缓存会按当前缩放栅格化文字，留着会发虚
            target.CacheMode = null;
        };

        transform.BeginAnimation(TranslateTransform.YProperty, travel, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// 收起：从当前位置往下滑出窗口底部。
    /// 缓动和进场同一套（先快、再慢、最后停），只是方向相反 —— 进出对得上才不别扭。
    /// </summary>
    /// <param name="target">要动的元素。</param>
    /// <param name="distance">往下滑多远（一般就是进场时的位移，滑回窗口外）。</param>
    /// <param name="done">滑完（或动画关闭时立刻）回调，用来把遮罩收掉。</param>
    public static void SlideDownOut(FrameworkElement target, double distance,
                                    Action? done = null, double durationMs = -1)
    {
        if (target is null)
        {
            done?.Invoke();
            return;
        }

        var transform = target.RenderTransform as TranslateTransform ?? new TranslateTransform();
        target.RenderTransform = transform;

        // 收起比进场快一点，不拖沓
        var ms = durationMs >= 0
            ? durationMs
            : Math.Max(150, (DurationProvider?.Invoke() ?? 320) * 2 / 3);

        if (ms <= 0 || distance <= 0)
        {
            target.CacheMode = null;
            done?.Invoke();
            return;
        }

        target.CacheMode = new BitmapCache();

        var travel = new DoubleAnimation(transform.Y, distance, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
        };

        travel.Completed += (_, _) =>
        {
            target.CacheMode = null;
            done?.Invoke();
        };

        transform.BeginAnimation(TranslateTransform.YProperty, travel, HandoffBehavior.SnapshotAndReplace);
    }
}
