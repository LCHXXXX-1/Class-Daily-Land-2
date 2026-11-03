using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClassDailyLand2.MarketContract;

using DeveloperTool.Market;

namespace DeveloperTool.Views;

/// <summary>IDE 设置：主题切换 + 插件市场账号（信息展示 / Gitee、GitHub 绑定）。</summary>
public partial class SettingsWindow : Window
{
    readonly MarketAuthService _marketAuth;

    /// <summary>绑定成功 / 登录态变化后通知主界面刷新右上角账号状态。</summary>
    public event Action? AccountChanged;

    /// <summary>构造里选中的那一项会触发 SelectionChanged；初始化完再放行。</summary>
    bool _ready;

    /// <summary>由外部（别的窗口切了主题）回灌下拉框时，不要再触发一次 ApplyTheme。</summary>
    bool _suppress;

    public SettingsWindow(MarketAuthService marketAuth)
    {
        InitializeComponent();
        _marketAuth = marketAuth;

        ThemeCombo.SelectedIndex = App.IsDark ? 0 : 1;
        UpdateHint();
        RefreshAccount();

        // 在别处登录 / 退出后再点回设置窗，账号面板要跟得上
        Activated += (_, _) => RefreshAccount();

        App.ThemeChanged += OnAppThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= OnAppThemeChanged;

        _ready = true;
    }

    void OnAppThemeChanged() => Dispatcher.BeginInvoke(new Action(SyncCombo));

    void SyncCombo()
    {
        var want = App.IsDark ? 0 : 1;
        if (ThemeCombo.SelectedIndex == want) { UpdateHint(); return; }

        _suppress = true;
        ThemeCombo.SelectedIndex = want;
        _suppress = false;
        UpdateHint();
    }

    void OnThemeSelection(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppress) return;

        var dark = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag as string == "dark";
        if (dark != App.IsDark) App.ApplyTheme(dark);
        UpdateHint();
    }

    void UpdateHint() =>
        HintText.Text = "当前：" + (App.IsDark ? "深色主题" : "浅色主题") + "，选择会记住（ide_config.json）。";

    // ─────────────────────────── 账号 ───────────────────────────

    void RefreshAccount()
    {
        var user = _marketAuth.User;
        if (!_marketAuth.IsLoggedIn || user is null)
        {
            AccountNameText.Text = "未登录";
            AccountIdText.Text = "先在主界面点「登录插件市场」。";
            AccountAdminText.Visibility = Visibility.Collapsed;

            GiteeStateText.Text = GithubStateText.Text = "—";
            GiteeBindButton.Content = GithubBindButton.Content = "立即绑定";
            GiteeBindButton.IsEnabled = GithubBindButton.IsEnabled = false;
            return;
        }

        AccountNameText.Text = user.Username;
        AccountIdText.Text = "系统 ID：" + user.Id;
        AccountAdminText.Text = user.IsAdmin ? "管理员" : "普通用户";
        AccountAdminText.Visibility = Visibility.Visible;

        SetBindingRow(GiteeStateText, GiteeBindButton, user.GiteeId);
        SetBindingRow(GithubStateText, GithubBindButton, user.GithubId);
    }

    static void SetBindingRow(TextBlock state, Button button, string? thirdPartyId)
    {
        var bound = !string.IsNullOrEmpty(thirdPartyId);
        state.Text = bound ? "已绑定" : "未绑定";
        button.Content = bound ? "已绑定" : "立即绑定";
        button.IsEnabled = !bound;          // 解绑暂不做
    }

    async void OnBindGitee(object sender, RoutedEventArgs e) => await BindAsync("gitee");

    async void OnBindGithub(object sender, RoutedEventArgs e) => await BindAsync("github");

    async Task BindAsync(string provider)
    {
        if (!_marketAuth.IsLoggedIn)
        {
            AccountHint.Text = "先登录插件市场，再绑定第三方账号。";
            return;
        }

        var label = provider == "gitee" ? "Gitee" : "GitHub";
        Cursor = Cursors.Wait;
        GiteeBindButton.IsEnabled = GithubBindButton.IsEnabled = false;   // 授权期间别让两边同时点
        try
        {
            await _marketAuth.BindAsync(provider,
                msg => Dispatcher.BeginInvoke(new Action(() => AccountHint.Text = msg)));

            // 绑定后 User 已被替换成服务器返回的新对象，重画本窗 + 通知主界面
            RefreshAccount();
            AccountChanged?.Invoke();
            AccountHint.Text = label + " 绑定成功。";
        }
        catch (OperationCanceledException)
        {
            AccountHint.Text = "已取消绑定。";
        }
        catch (MarketApiException ex)
        {
            // 409 = 目标账号已绑两个平台，后端拒绝自动合并
            System.Windows.MessageBox.Show(this, ex.Message, "绑定失败",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            RefreshAccount();          // 取消/失败也要按当前绑定状态恢复按钮
            Cursor = Cursors.Arrow;
        }
    }
}
