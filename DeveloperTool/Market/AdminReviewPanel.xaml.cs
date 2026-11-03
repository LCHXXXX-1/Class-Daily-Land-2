using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClassDailyLand2.MarketContract;

using DeveloperTool.Views;

namespace DeveloperTool.Market;

/// <summary>
/// 管理员审核面板：主窗口页签内嵌显示，与快捷弹窗共用同一份 XAML/逻辑。
/// 待审核列表 + 预览信息 + 通过 / 驳回（驳回在弹窗里必填理由）。
/// 作者撤销后这条记录已不是 pending：服务端回 409，这里捕获后立即刷新，把过期条目从列表摘掉。
/// 入口处按 <see cref="MarketUser.IsAdmin"/> 过滤，服务端 AdminMiddleware 再校验一次，客户端不自证。
/// </summary>
public partial class AdminReviewPanel : UserControl
{
    MarketAuthService? _auth;

    public AdminReviewPanel()
    {
        InitializeComponent();
        // 唯一的自动加载时机：面板进入可视树（切到本页签就会 Loaded → 刷新）。
        // 不再依赖 MainWindow 的 SelectionChanged 过滤链——那条链任何一环静默 return，
        // 面板就永远停在「尚未加载」且没有任何线索。
        Loaded += (_, _) => _ = RefreshAsync();
    }

    public void Attach(MarketAuthService auth)
    {
        _auth = auth;
        if (IsLoaded) _ = RefreshAsync();   // 防御：万一 Attach 时面板已经显示过
    }

    /// <summary>
    /// 刷新待审核列表。所有分支都落成可见的 Hint 文本，绝不静默返回：
    /// 尚未加载 / 正在加载… / 共 N 个待审核 / 暂无待审核插件 / 获取失败：{原因}。
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
                var pending = await _auth!.Api.GetPendingAsync() ?? [];

                List.ItemsSource = pending;
                DetailText.Text = "选择左侧插件查看详细信息。";

                if (pending.Count == 0)
                {
                    ShowEmpty("暂无待审核插件");
                    Hint.Text = "没有待审核的插件。";
                }
                else
                {
                    HideEmpty();
                    Hint.Text = $"共 {pending.Count} 个待审核。选中左侧插件查看预览信息。";
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

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List is null || DetailText is null) return;

        if (List.SelectedItem is not MarketFileEntry item)
        {
            DetailText.Text = "选择左侧插件查看详细信息。";
            return;
        }

        DetailText.Text =
            "包名：" + item.PackageName + "\n" +
            "插件名：" + item.PluginName + "\n" +
            "版本：" + item.Version + "\n" +
            "文件名：" + item.Filename + "\n" +
            "大小：" + item.SizeText + "\n" +
            "上传者 ID：" + item.UploaderId + "\n" +
            "SHA256：" + item.Sha256 + "\n" +
            "上传时间：" + item.TimeText;
    }

    void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    async void OnApprove(object sender, RoutedEventArgs e)
    {
        if (_auth is null) return;
        if (List is null) return;
        if (List.SelectedItem is not MarketFileEntry item)
        {
            Hint.Text = "先在左边选一个插件。";
            return;
        }

        var owner = Window.GetWindow(this)!;
        var ask = System.Windows.MessageBox.Show(owner,
            "通过并发布？\n\n" + item.PackageName + " v" + item.Version +
            "\n\n通过后会推到 GitHub 并刷新市场列表。",
            "审核通过", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        try
        {
            var message = await _auth.Api.ApproveFileAsync(item.Id);
            await RefreshAsync();
            Hint.Text = message;
        }
        catch (MarketApiException ex)
        {
            // 409 = 作者已撤销（记录不是 pending 了）：立即刷新，把过期条目从列表摘掉
            await RefreshAsync();
            Hint.Text = ex.Message;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(owner, "审核失败：" + ex.Message, "审核通过",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    async void OnReject(object sender, RoutedEventArgs e)
    {
        if (_auth is null) return;
        if (List is null) return;
        if (List.SelectedItem is not MarketFileEntry item)
        {
            Hint.Text = "先在左边选一个插件。";
            return;
        }

        var owner = Window.GetWindow(this)!;

        // 理由在弹窗里输入（PromptWindow 空内容时确定按钮不可点）
        var reason = PromptWindow.Ask(owner,
            "驳回：" + item.PackageName + " v" + item.Version,
            "驳回理由（作者会在「我的插件」里看到）：", "", okText: "驳回");
        if (string.IsNullOrEmpty(reason)) return;

        try
        {
            var message = await _auth.Api.RejectFileAsync(item.Id, reason);
            await RefreshAsync();
            Hint.Text = message;
        }
        catch (MarketApiException ex)
        {
            // 同上：作者已撤销就回 409，刷新摘掉过期条目
            await RefreshAsync();
            Hint.Text = ex.Message;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(owner, "驳回失败：" + ex.Message, "审核拒绝",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
