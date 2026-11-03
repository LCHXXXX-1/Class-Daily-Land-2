using System.Windows;

namespace DeveloperTool.Market;

/// <summary>
/// 「我的插件」快捷弹窗：内容复用 <see cref="MyPluginsPanel"/>，与主窗口页签同一份逻辑。
/// 主体入口在主窗口页签；这个窗口留给工具栏快速打开。
/// </summary>
public partial class MyPluginsWindow : Window
{
    public MyPluginsWindow(MarketAuthService auth)
    {
        InitializeComponent();
        Panel.Attach(auth);
        Loaded += async (_, _) => await Panel.RefreshAsync();
    }
}
