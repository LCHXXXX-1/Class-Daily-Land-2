using System.Windows;

namespace DeveloperTool.Market;

/// <summary>
/// 登录平台选择：只负责挑 provider，真正的授权（WebView2）与改名流程由调用方跑，
/// 这样「选择 → 授权 → 首次改名」三步不挤在一个窗口里。
/// </summary>
public partial class LoginPlatformWindow : Window
{
    /// <summary>用户选中的平台（gitee / github）；取消时保持 null。</summary>
    public string? Provider { get; private set; }

    public LoginPlatformWindow() => InitializeComponent();

    void OnGitee(object sender, RoutedEventArgs e) => Pick("gitee");

    void OnGitHub(object sender, RoutedEventArgs e) => Pick("github");

    void Pick(string provider)
    {
        Provider = provider;
        DialogResult = true;        // 先收起选择框，再让调用方弹 WebView2 授权窗
    }
}
