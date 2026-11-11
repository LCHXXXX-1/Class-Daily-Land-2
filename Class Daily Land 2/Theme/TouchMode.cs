using System.Windows.Controls;

namespace ClassDailyLand2.App.Theme;

/// <summary>
/// 触屏支持：**只剩"手指能上下拖"这一件事，且永远开着**，没有开关、没有尺寸放大。
///
/// WPF 的 <see cref="ScrollViewer"/> 默认 <c>PanningMode=None</c>，
/// 手指按住往下拖**一点都不会动**（只有滚轮和滚动条能滚），触摸屏用户会直接觉得"这界面滑不动"。
/// 所以只要有滚动条就一律开 <see cref="PanningMode.VerticalOnly"/>，鼠标用户完全无感。
/// </summary>
public static class TouchMode
{
    /// <summary>
    /// 让一个滚动区能被手指上下拖。滚轮、滚动条、手指三种输入可以同时用，互不冲突。
    /// </summary>
    public static void EnableTouchScroll(ScrollViewer? viewer)
    {
        if (viewer is null) return;
        viewer.PanningMode = PanningMode.VerticalOnly;
        // 惯性衰减统一 0.001（与 XAML 里 <ScrollViewer PanningDeceleration="0.001"> 保持一致）
        viewer.PanningDeceleration = 0.001;
        // 不设 IsManipulationEnabled：ScrollViewer 的 PanningMode 内部已经接管了
        // manipulation，子元素再自己开会把单击吞掉（按钮点不动）。
    }
}
