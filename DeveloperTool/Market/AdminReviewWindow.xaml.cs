using System.Windows;

namespace DeveloperTool.Market;

/// <summary>
/// 管理员审核快捷弹窗：内容复用 <see cref="AdminReviewPanel"/>，与主窗口页签同一份逻辑。
/// 主体入口在主窗口页签（仅管理员可见）；这个窗口留给工具栏快速打开。
/// </summary>
public partial class AdminReviewWindow : Window
{
    public AdminReviewWindow(MarketAuthService auth)
    {
        InitializeComponent();
        Panel.Attach(auth);
        Loaded += async (_, _) => await Panel.RefreshAsync();
    }
}
