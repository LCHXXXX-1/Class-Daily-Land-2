using ClassDailyLand2.App.ViewModels;
using ClassDailyLand2.App.Views.Dialogs;
using System.Windows.Controls;

namespace ClassDailyLand2.App.Views.Pages;

/// <summary>
/// 把一个浮层面板（<see cref="DialogPanel"/>）当成**平级设置页**用：顶栏给它一个
/// 自己的标签，内容就是这张面板本身。
///
/// 为什么要这层壳：假期与调休 / 值日生名单 / 临时调课 / 状态测试 原来是「课表与提醒」
/// 列表里点开的推进式二级页（主窗托盘菜单也还走那条浮层路径）。把它们提升成标签后，
/// 面板内容一个字都不用重写 —— 同一张面板换个外壳摆进页面宿主就行。
/// 具体要做的事（新增假期、保存、在灵动岛演示…）仍然写在面板自己身上，
/// 两条入口（浮层 / 页面）共用同一份实现。
/// </summary>
public sealed class DialogPage : UserControl
{
    readonly Func<SettingsViewModel, DialogPanel> _create;
    bool _built;

    public DialogPage(Func<SettingsViewModel, DialogPanel> create)
    {
        _create = create;

        // ★ 两个时机都要挂：Select() 是「先塞 DataContext、再塞进 PageHost」，
        //   所以正常路径下 DataContextChanged 会先到 —— 这时候建面板，
        //   内容在转场动画开始之前就已经在树里了。
        //   Loaded 只当兜底：首帧那次如果只靠 Loaded，页面会「第一次进来是空白、
        //   第二次才正常」（转场用了 BitmapCache，晚挂上去的内容赶不上这一帧）。
        DataContextChanged += (_, _) => Build();
        Loaded += (_, _) => Build();
    }

    void Build()
    {
        if (_built || DataContext is not SettingsViewModel vm) return;
        _built = true;

        var panel = _create(vm);
        panel.HostAsPage();
        Content = panel;
    }
}
