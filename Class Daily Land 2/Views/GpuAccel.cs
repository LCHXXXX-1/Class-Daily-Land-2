using ClassDailyLand2.Core;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClassDailyLand2.App.Views;

/// <summary>
/// GPU 加速的统一入口。做两件事：
///
/// <list type="number">
///   <item><b>锁死硬件渲染路径</b> —— 进程级渲染模式显式设成 <see cref="RenderMode.Default"/>
///         并记录当前渲染层（Tier），避免某些环境（远程桌面 / 驱动异常）悄无声息地掉到
///         软件渲染上而没人发现。</item>
///   <item><b>运动期间挂位图缓存</b> —— 滚动 / 位移这类"内容不变、只换位置"的动作，
///         每帧重新栅格化文字非常浪费。给滚动内容挂一层 <see cref="BitmapCache"/> 之后，
///         一帧只需要在显存里平移一张位图（GPU 合成），停下来 160ms 再撤掉，
///         免得缓存一直留着把文字压得发虚。</item>
/// </list>
///
/// <para><b>为什么不用更多 DX12 自绘</b>：设置中心的胶囊玻璃走的是
/// <c>LiquidGlass.Rendering.DirectX</c>（DX12 优先、DX11 兜底、GPU 计算着色器 + 回读）。
/// 其余界面元素是 WPF 原生控件，它们的合成由 WPF 自己的 D3D 渲染线程完成 ——
/// 这部分没法"换成 DX12"，能做的是：别掉到软件渲染、别每帧重画、别用软件特效。</para>
/// </summary>
public static class GpuAccel
{
    static bool _warmed;

    /// <summary>进程启动时调一次：锁定硬件渲染并记录渲染层。</summary>
    public static void WarmUp()
    {
        if (_warmed) return;
        _warmed = true;

        // Default = 有可用 GPU 就走硬件；显式设一次，防止别处（或旧代码）把它改成 SoftwareOnly
        RenderOptions.ProcessRenderMode = RenderMode.Default;

        var tier = RenderCapability.Tier >> 16;      // 0 / 1 / 2
        var desc = tier switch
        {
            2 => "Tier 2 · 完整硬件加速",
            1 => "Tier 1 · 部分硬件加速",
            _ => "Tier 0 · 软件渲染（没有拿到 GPU）",
        };

        Logger.Current?.Info($"[GPU] 渲染层：{desc}（RenderMode=Default）");
    }

    /// <summary>当前渲染层描述（诊断 / 关于页显示用）。</summary>
    public static string Describe()
    {
        var tier = RenderCapability.Tier >> 16;
        var gpu = LiquidGlass.Rendering.DirectX.D3DGlassLayer.BackendDiagnostics;
        return $"渲染层 Tier {tier} · 玻璃后端 {gpu}";
    }

    // ————————————————————— 运动期间挂缓存 —————————————————————

    /// <summary>每个元素一个"空闲计时器"：连续滚动时不断续命，停手 160ms 后自动撤缓存。</summary>
    static readonly Dictionary<UIElement, DispatcherTimer> IdleTimers = new();

    /// <summary>
    /// 告诉 GPU 层"这个元素正在动"：挂上位图缓存并续命空闲计时器。
    /// 滚动类动画每帧调一次即可（重复调用是廉价的）。
    /// </summary>
    /// <param name="element">运动中的元素（要传滚动内容，不能传 ScrollViewer 本身）。</param>
    /// <param name="idleMs">停止调用多久后撤掉缓存。</param>
    public static void BeginMotion(UIElement? element, int idleMs = 160)
    {
        if (element is null) return;

        // 1:1 光栅化。放大系数固定为 1，避免 4K / 125% 缩放下被反复重采样
        if (element.CacheMode is null)
            element.CacheMode = new BitmapCache(1.0);

        if (!IdleTimers.TryGetValue(element, out var timer))
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(idleMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                // 停下来就把缓存撤掉：位图缓存是按当前缩放栅格化的，长期留着文字会发虚
                element.CacheMode = null;
            };
            IdleTimers[element] = timer;
        }

        timer.Stop();
        timer.Start();
    }

    /// <summary>立刻结束运动：撤掉缓存（比如列表内容要重排前）。</summary>
    public static void EndMotion(UIElement? element)
    {
        if (element is null) return;
        if (IdleTimers.TryGetValue(element, out var timer))
        {
            timer.Stop();
            IdleTimers.Remove(element);
        }
        element.CacheMode = null;
    }
}
