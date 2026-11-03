using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClassDailyLand2.MarketContract;

namespace DeveloperTool.Market;

/// <summary>
/// 「我的插件」面板：主窗口页签内嵌显示，与快捷弹窗共用同一份 XAML/逻辑。
/// GET /api/files/my 列出插件状态（审核中 / 已发布 / 未通过 / 草稿）与驳回原因；
/// 「审核中」的条目可撤销（退草稿、作废待审记录，且不触发版本拦截）。
/// 凭证复用 <see cref="MarketAuthService"/> 的 JWT；宿主先 <see cref="Attach"/>。
/// </summary>
public partial class MyPluginsPanel : UserControl
{
    MarketAuthService? _auth;

    public MyPluginsPanel()
    {
        InitializeComponent();
        // 唯一的自动加载时机：面板进入可视树（切到本页签就会 Loaded → 刷新）。
        // 不再依赖 MainWindow 的 SelectionChanged 过滤链——那条链任何一环静默 return，
        // 面板就永远停在「正在加载…」且没有任何线索。
        Loaded += (_, _) => _ = RefreshAsync();
    }

    public void Attach(MarketAuthService auth)
    {
        _auth = auth;
        if (IsLoaded) _ = RefreshAsync();   // 防御：万一 Attach 时面板已经显示过
    }

    /// <summary>
    /// 刷新列表。所有分支都必须落成可见的 Hint 文本，绝不静默返回：
    /// 尚未加载 / 正在加载… / 共 N 个 / 暂无数据 / 获取失败：{原因}。
    /// </summary>
    public Task RefreshAsync()
    {
        if (List is null || Hint is null) return Task.CompletedTask;   // XAML 字段没生成：无处可写

        if (_auth is null)
        {
            ShowEmpty("面板未初始化");
            Hint.Text = "面板未初始化（缺少登录凭证），请切出该页签再切回来。";
            return Task.CompletedTask;
        }

        return RefreshCoreAsync();
    }

    async Task RefreshCoreAsync()
    {
        try
        {
            Cursor = Cursors.Wait;
            Hint.Text = "正在加载…";        // 请求在飞：与「尚未加载」区分开
            try
            {
                // 刻意不 ConfigureAwait(false)：await 之后必须回 UI 线程再碰控件
                var mine = await _auth!.Api.GetMyFilesAsync();

                // 服务端哪天返回 null 数组也兜成空列表，别让 .Count 炸
                var owned = mine?.Owned ?? [];
                var collab = mine?.Collab ?? [];

                List.ItemsSource = owned;
                if (owned.Count == 0)
                {
                    ShowEmpty("暂无数据");
                    Hint.Text = "还没有上传过插件。编译打包后点「发布到市场」上传。";
                }
                else
                {
                    HideEmpty();
                    Hint.Text = $"共 {owned.Count} 个插件；参与合作的 {collab.Count} 个不在此列。";
                }
            }
            finally
            {
                Cursor = Cursors.Arrow;
            }
        }
        catch (MarketApiException ex) when (ex.StatusCode == 401)
        {
            // Token 过期（JWT 无撤销）：单独给一句能照做的提示
            Fail("登录已失效（401），请重新登录插件市场。");
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    /// <summary>失败一律落成可见文本：清列表 + 空态「获取失败」+ Hint 带原因，不留白。</summary>
    void Fail(string reason)
    {
        List?.ItemsSource = null;
        ShowEmpty("获取失败");
        Hint?.Text = "获取失败：" + reason;
    }

    void ShowEmpty(string text)
    {
        if (EmptyText is null) return;
        EmptyText.Text = text;
        EmptyText.Visibility = Visibility.Visible;
    }

    void HideEmpty()
    {
        if (EmptyText is not null) EmptyText.Visibility = Visibility.Collapsed;
    }

    void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    /// <summary>
    /// 撤销审核：二次确认后 POST /api/files/{id}/withdraw，状态退为草稿、管理员端待审记录作废。
    /// 撤销后 draft 不占用版本号，改完重新提交不会被版本拦截挡住。
    /// </summary>
    async void OnWithdraw(object sender, RoutedEventArgs e)
    {
        if (_auth is null) return;
        if (sender is not Button { Tag: MarketFileEntry item }) return;

        var owner = Window.GetWindow(this)!;
        var ask = System.Windows.MessageBox.Show(owner,
            "撤销审核？\n\n" + item.PackageName + " v" + item.Version +
            "\n\n撤销后插件退回草稿（管理员端立即不再显示），" +
            "改完后可重新提交；草稿不占用版本号，不会被版本拦截挡住。",
            "撤销审核", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        try
        {
            var message = await _auth.Api.WithdrawFileAsync(item.Id);
            await RefreshAsync();
            Hint.Text = message;
        }
        catch (MarketApiException ex)
        {
            // 404 = 服务端没有这个接口：withdraw 路由是新加的，服务器多半还是旧版本
            System.Windows.MessageBox.Show(owner,
                ex.StatusCode == 404
                    ? "撤销失败：服务端没有这个接口（服务器还是旧版本？请重新部署最新 Server 后重试）。"
                    : "撤销失败：" + ex.Message,
                "撤销审核", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(owner, "撤销失败：" + ex.Message, "撤销审核",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
