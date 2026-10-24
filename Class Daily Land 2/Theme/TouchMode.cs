using System.Windows;
using System.Windows.Controls;

namespace ClassDailyLand.App.Theme;

/// <summary>
/// 触屏支持。分两件互不相干的事：
///
/// <para><b>① 手指滑动滚动 —— 永远开着，不受设置影响。</b>
/// WPF 的 <see cref="ScrollViewer"/> 默认 <c>PanningMode=None</c>，
/// 手指按住往下拖**一点都不会动**（只有滚轮和滚动条能滚），
/// 触摸屏用户会直接觉得"这界面滑不动"。所以只要有滚动条就一律开
/// <see cref="PanningMode.VerticalOnly"/>，鼠标用户完全无感。</para>
///
/// <para><b>② 放大点击目标 —— 受「设置 → 交互 → 触屏优化」控制。</b>
/// Win11 的 32px 控件高度是给鼠标的；手指大约 9mm ≈ 40~48 DIP，
/// 所以整窗放大到 44，设置行 62→82、导航项 40→54。</para>
///
/// 尺寸的实现方式是覆盖**窗口级资源**里的尺寸键：主题模板里这些都是
/// <c>DynamicResource</c>，索引器一赋值整窗立刻重解析，不用重建可视树、也不用重启。
/// 只覆盖窗口资源而不是 <c>Application.Resources</c>，关掉设置窗就还原，
/// 不会把触屏尺寸带到岛和主窗上。
/// </summary>
public static class TouchMode
{
    /// <summary>
    /// 当前是否开着「触屏优化」（**只管点击目标大小**，和能不能滑动无关）。
    /// 由 <see cref="AppController"/> 在启动/设置变更时维护（<see cref="SetGlobal"/>），
    /// 设置窗口打开时 <see cref="Apply(Window,bool)"/> 会一起同步。
    /// </summary>
    public static bool Enabled { get; private set; }

    /// <summary>宿主持有的权威开关（不开设置窗口时也要能知道当前值）。</summary>
    public static void SetGlobal(bool on) => Enabled = on;

    /// <summary>
    /// 让一个滚动区能被手指上下拖。
    ///
    /// **无条件调用**，不判断 <see cref="Enabled"/> —— 触摸滑动是基础能力而不是"优化项"。
    /// 滚轮、滚动条、手指三种输入可以同时用，互不冲突。
    /// </summary>
    public static void EnableTouchScroll(ScrollViewer? viewer)
    {
        if (viewer is null) return;
        viewer.PanningMode = PanningMode.VerticalOnly;
        // 惯性衰减 0.0006（默认 0.001 在长列表上"甩一下只滑一屏"，手感偏肉）
        viewer.PanningDeceleration = 0.0006;
        // 不设 IsManipulationEnabled：ScrollViewer 的 PanningMode 内部已经接管了
        // manipulation，子元素再自己开会把单击吞掉（按钮点不动）。
    }

    // ————————————— 点击目标尺寸 —————————————

    sealed record Metric(string Key, double Normal, double Touch);

    /// <summary>
    /// 要覆盖的尺寸键。左边是鼠标下的原值（必须与 Tokens.Base.xaml 一致），
    /// 右边是触摸下的值：控件 32→44、小控件 28→38、设置行 62→82、导航项 40→54。
    /// </summary>
    static readonly Metric[] Metrics =
    {
        new("ControlHeight", 32, 44),
        new("ControlHeightSm", 28, 38),
        new("RowMinHeight", 62, 82),
        new("NavItemHeight", 40, 54),
    };

    /// <summary>把触屏尺寸写进（或撤出）某个窗口的资源表。</summary>
    public static void Apply(Window window, bool on)
    {
        Enabled = on;
        foreach (var m in Metrics)
            window.Resources[m.Key] = on ? m.Touch : m.Normal;
    }
}
