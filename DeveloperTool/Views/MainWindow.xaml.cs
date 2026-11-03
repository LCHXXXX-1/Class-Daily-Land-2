using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using ClassDailyLand2.MarketContract;

using DeveloperTool.Core;
using DeveloperTool.Market;
using DeveloperTool.Updates;

namespace DeveloperTool.Views;

/// <summary>
/// 插件 IDE 主窗口：项目树 + AvalonEdit 编辑器 + 输出控制台。
///
/// 职责边界：界面与交互在这里，**建项目 / 编译 / 打包 / 部署**全在 <see cref="PluginWorkshop"/>，
/// 这样以后要做命令行版或单元测试都不用碰 XAML。
/// </summary>
public partial class MainWindow : Window
{
    PluginProject? _project;
    IdeConfig _config = IdeConfig.Load();
    readonly MarketAuthService _marketAuth = new();
    readonly MarketUploadService _marketUpload;
    string? _openPath;
    bool _dirty;

    /// <summary>正在编译 / 打包 / 上传。更新会直接关掉进程，这类活儿被拦腰截断就会留下半个包或半个上传。</summary>
    bool _busy;
    readonly System.Windows.Threading.DispatcherTimer _autoSaveTimer;
    bool _loading;

    /// <summary>设置窗单例：重复点入口只把它拎到前面，别再开第二个。</summary>
    SettingsWindow? _settingsWindow;

    /// <summary>静默检查到新版本后留着的那条更新信息（点提示时直接用它拉起更新器）。</summary>
    RemoteRelease? _offer;
    bool _updateCheckStarted;

    public MainWindow()
    {
        InitializeComponent();

        _autoSaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoSaveTimer.Tick += (_, _) => { _autoSaveTimer.Stop(); AutoSave(); };
        Editor.Document.UndoStack.SizeLimit = 50;

        _marketUpload = new MarketUploadService(_marketAuth);
        // 市场面板与快捷弹窗共用同一份控件：先绑好凭证（页签切换时才发请求）
        MyPluginsHost.Attach(_marketAuth);
        AdminReviewHost.Attach(_marketAuth);
        _marketAuth.LoadStored();
        SyncMarketUi();
        RefreshRecentUi();
        // 存的 Token 可能已失效（JWT 无撤销也无法预知），后台拉一次 me 校验并刷新用户名
        _ = RestoreMarketLoginAsync();

        // 主题换色后 AvalonEdit 要手动补（它不认 DynamicResource）
        App.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => { App.ThemeChanged -= OnThemeChanged; _autoSaveTimer.Stop(); if (_dirty) SaveOpen(requireLog: false); };
        SyncEditorTheme();

        var contract = PluginWorkshop.FindContract();
        Log("Class Daily Land 2 插件 IDE 就绪。");
        Log(contract is null
            ? "警告：没找到 ClassDailyLand2.Plugin.Abstractions.dll，编译会失败（先编译一次契约工程）。"
            : "契约程序集：" + contract);
        RefreshStatus();

        // 界面画好之后再跑后台检查，别和首帧布局抢线程
        Loaded += (_, _) =>
        {
            if (_updateCheckStarted) return;
            _updateCheckStarted = true;
            _ = CheckUpdateSilentlyAsync();
        };
    }

    // ─────────────────────────── 静默检查更新 ───────────────────────────

    /// <summary>
    /// 启动时的后台检查：断网 / 超时 / JSON 坏掉统统静默跳过（只往输出控制台记一行），
    /// 有新版才把工具栏那条提示亮起来。
    /// </summary>
    async Task CheckUpdateSilentlyAsync()
    {
        try
        {
            var remote = await UpdateLauncher.CheckSilentlyAsync();
            if (remote is null) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _offer = remote;
                var hint = $"🌟 发现新版本 v{remote.Version}，点击更新";
                UpdateHint.Content = hint;
                UpdateHint.Visibility = Visibility.Visible;
                WelcomeUpdateHint.Content = hint;
                WelcomeUpdateHint.Visibility = Visibility.Visible;
                Log($"后台检查发现新版本 v{remote.Version}（本地 v{UpdateLauncher.LocalVersion}）。");
            }));
        }
        catch (Exception ex)
        {
            // 静默检查绝不能影响启动，连弹窗都不要有
            Log("后台检查更新未成功（不影响使用）：" + ex.Message);
        }
    }

    void OnUpdateHintClick(object sender, RoutedEventArgs e)
    {
        var remote = _offer;
        if (remote is null) return;

        var ask = MessageBox.Show(this,
            $"发现新版本 v{remote.Version}（{remote.Source}）\n\n是否立即更新？IDE 会自动关闭，更新完成后重新启动。",
            "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        // 用户答应更新之后、真正拉起更新器之前再拦一道：这时候才关得掉进程，改到这一步才知道会不会白丢东西
        if (!ConfirmBeforeUpdate()) return;

        if (UpdateLauncher.TryStart(remote, out var error))
        {
            // 立刻松手：更新器在轮询本进程是否消失，我们退出后它才开始覆盖文件
            Application.Current.Shutdown();
            return;
        }

        MessageBox.Show(this, error, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// 更新前的最后一道门。更新器会把整个安装目录覆盖掉、并且一定会关掉本进程，
    /// 所以两件事必须先问清楚：手上有没有正在跑、关掉就白费的活儿；有没有没落盘的编辑内容。
    /// 返回 false 表示「这次先别更新」，调用方直接 return。
    /// </summary>
    internal bool ConfirmBeforeUpdate()
    {
        if (_busy)
        {
            MessageBox.Show(this, "正在编译或上传中，请等待当前操作完成后再更新。",
                "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (!_dirty) return true;

        var answer = MessageBox.Show(this,
            "当前文件有未保存的修改。更新会关闭 DeveloperTool，未保存的内容将丢失。是否保存后继续更新？",
            "检查更新", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Cancel) return false;

        // No = 明确放弃修改，照更新；Yes 就必须真的存下去，存失败还继续等于骗用户
        if (answer == MessageBoxResult.Yes && !SaveOpen(requireLog: true))
        {
            MessageBox.Show(this, "保存失败，已取消更新。",
                "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    // ─────────────────────────── 插件市场 ───────────────────────────

    void SyncMarketUi()
    {
        var loggedIn = _marketAuth.IsLoggedIn;
        MarketLoginButton.Content = loggedIn ? "已登录：" + _marketAuth.User!.Username : "登录插件市场";

        // 欢迎页右上角：未登录 = 按钮，已登录 = 首字母圆形头像 + 用户名（后端没有头像字段）
        WelcomeLoginButton.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        WelcomeUserPanel.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;

        // 管理员专属入口：非管理员隐藏（服务端 AdminMiddleware 再校验一次，客户端不自证）
        AdminReviewButton.Visibility =
            loggedIn && _marketAuth.User!.IsAdmin ? Visibility.Visible : Visibility.Collapsed;

        // 市场页签：登录才显示「我的插件」，管理员才显示「管理员审核」。
        // 退出登录时若正停在市场页签，先退回去再隐藏，避免页签被抽走后内容区空白
        var selected = MainTabs.SelectedItem;
        if (!loggedIn && (ReferenceEquals(selected, MyPluginsTab) || ReferenceEquals(selected, AdminReviewTab)))
            MainTabs.SelectedItem = _project is null ? HomeTab : EditorTab;
        MyPluginsTab.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
        AdminReviewTab.Visibility = loggedIn && _marketAuth.User!.IsAdmin ? Visibility.Visible : Visibility.Collapsed;

        if (!loggedIn) return;

        var name = _marketAuth.User!.Username;
        WelcomeUserName.Text = name;
        WelcomeAvatarText.Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
    }

    async Task RestoreMarketLoginAsync()
    {
        await _marketAuth.RefreshStoredAsync().ConfigureAwait(false);
        Dispatcher.BeginInvoke(new Action(SyncMarketUi));
    }

    async void OnMarketLogin(object sender, RoutedEventArgs e)
    {
        // 已登录时这个按钮当「退出登录」用
        if (_marketAuth.IsLoggedIn)
        {
            if (System.Windows.MessageBox.Show(this,
                    $"当前登录为「{_marketAuth.User!.Username}」。\n\n要退出登录吗？（退出后无法发布插件）",
                    "插件市场", System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question)
                != System.Windows.MessageBoxResult.Yes) return;
            _marketAuth.SignOut();
            SyncMarketUi();
            Log("已退出插件市场登录。");
            return;
        }

        var picker = new LoginPlatformWindow { Owner = this };
        if (picker.ShowDialog() != true || picker.Provider is null) { Log("已取消登录。"); return; }
        var provider = picker.Provider;

        try
        {
            var result = await _marketAuth.LoginAsync(provider, Log);
            if (await EnsureUsernameAsync(result)) return;
            SyncMarketUi();
            // 登录成功 → 自动切到「我的插件」页签（切页触发 SelectionChanged → 加载数据）
            MainTabs.SelectedItem = MyPluginsTab;
            Log($"登录成功：{_marketAuth.User?.Username}（{_marketAuth.Provider}）");
        }
        catch (OperationCanceledException)
        {
            Log("登录超时或已取消（3 分钟内没等到浏览器授权回调）。");
        }
        catch (MarketApiException ex)
        {
            _marketAuth.SignOut();
            SyncMarketUi();
            Log("登录失败：" + ex.Message);
            System.Windows.MessageBox.Show(this, ex.Message, "登录失败",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _marketAuth.SignOut();
            SyncMarketUi();
            Log("登录过程出错：" + ex.Message);
            System.Windows.MessageBox.Show(this, ex.Message, "登录失败",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>need_set_username=true 时循环要名字，改成功为止；返回 true = 用户中途放弃（已退回未登录）。</summary>
    async Task<bool> EnsureUsernameAsync(MarketLoginResult result)
    {
        while (result.NeedSetUsername)
        {
            var name = PromptWindow.Ask(this, "设置站内用户名",
                "首次登录需要设置站内用户名。\n2-32 位、全站唯一，发布插件时用作包名前缀（如 张三.我的插件）：",
                result.User?.Username ?? "");
            if (name is null)
            {
                _marketAuth.SignOut();
                SyncMarketUi();
                Log("没有设置用户名，已退出登录（不改名的话市场接口会被后端拦截）。");
                return true;
            }

            try
            {
                result = await _marketAuth.SetUsernameAsync(name);
                Log("用户名已设置：" + name);
            }
            catch (MarketApiException ex) when (ex.StatusCode == 409)
            {
                System.Windows.MessageBox.Show(this, $"「{name}」已被占用，换一个试试。", "设置用户名",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            catch (MarketApiException ex)
            {
                _marketAuth.SignOut();
                SyncMarketUi();
                Log("设置用户名失败：" + ex.Message);
                System.Windows.MessageBox.Show(this, ex.Message, "设置用户名失败",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return true;
            }
        }
        return false;
    }

    async void OnPublishToMarket(object sender, RoutedEventArgs e)
    {
        if (!EnsureProject()) return;
        if (!_marketAuth.IsLoggedIn)
        {
            System.Windows.MessageBox.Show(this, "先点「登录插件市场」登录，再发布。",
                "发布到市场", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        // 先存盘：让清单校验与版本拦截看到编辑器里最新的 plugin.json（版本号多半是刚改的）
        if (!SaveOpen()) return;

        // 发布前先体检 plugin.json：缺字段 / 契约版本不兼容，别等编译上传完才失败
        var project = _project!;
        if (!PluginWorkshop.TryValidateManifest(project, out var manifestError))
        {
            Log("清单校验未通过：" + manifestError);
            System.Windows.MessageBox.Show(this, manifestError ?? "plugin.json 校验未通过。",
                "无法发布：plugin.json 有问题",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        // 版本拦截：本地版本号 <= 线上最新版本号就不上传
        if (await IsVersionStaleAsync(project)) return;

        // 编译 → 打 .cblplugin
        var packagePath = CompileAndPack();
        if (packagePath is null) return;

        // 只护住上传这一段：前面几处 return 都在校验阶段，_busy 设早了就会在早退路径上一直不放，
        // 之后连更新都点不动。编译本身同步占着 UI 线程，用户没机会点更新。
        _busy = true;
        Cursor = Cursors.Wait;
        try
        {
            var file = await _marketUpload.PublishAsync(packagePath, Log);
            System.Windows.MessageBox.Show(this,
                $"「{file.PackageName}」v{file.Version} 已上传。\n\n待管理员审核通过后就会出现在插件市场列表里。",
                "发布成功", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (MarketApiException ex) when (ex.StatusCode == 403)
        {
            // 全局拦截：username_set=false，改名前市场接口一律 403
            System.Windows.MessageBox.Show(this, "后端要求先设置站内用户名。\n点「登录插件市场」按提示改名后再发布。",
                "需要设置用户名", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        catch (MarketApiException ex) when (ex.StatusCode == 409)
        {
            System.Windows.MessageBox.Show(this,
                "这个版本已经上传过（" + ex.Message + "）。\n改一下 plugin.json 里的 version 再发布。",
                "版本重复", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        catch (MarketApiException ex)
        {
            Log("发布失败：" + ex.Message);
            System.Windows.MessageBox.Show(this, "上传失败：" + ex.Message, "发布失败",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            Log("发布过程出错：" + ex.Message);
            System.Windows.MessageBox.Show(this, "发布过程出错：" + ex.Message, "发布失败",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            Cursor = Cursors.Arrow;
            _busy = false;
        }
    }

    /// <summary>
    /// 发布前的版本拦截：本地 plugin.json 版本号 &lt;= 我名下该包线上最新版本号就拦下上传。
    /// 包名按后端规则拼 {用户名}.{插件名}，线上数据取自 GET /api/files/my（带 JWT）；
    /// status=draft（已撤销审核的草稿）不计入线上版本，保证撤销后改完能直接重传。
    /// 查询失败只记日志放行——服务端上传时还有「重复版本 409」兜底。
    /// </summary>
    async Task<bool> IsVersionStaleAsync(PluginProject project)
    {
        var identity = PluginWorkshop.ReadManifestIdentity(project);
        if (identity is null) return false;

        string? online = null;
        try
        {
            var package = (_marketAuth.User?.Username ?? "") + "." + identity.Value.Name;
            var mine = await _marketAuth.Api.GetMyFilesAsync();
            // 草稿（作者已撤销审核）不占用版本号：改完重传同版本必须放行
            online = mine.Owned.Concat(mine.Collab)
                .Where(x => x.Status != "draft")
                .FirstOrDefault(x => string.Equals(x.PackageName, package, StringComparison.OrdinalIgnoreCase))
                ?.Version;
        }
        catch (Exception ex)
        {
            Log("查询线上最新版本失败（不拦截，靠服务端 409 兜底）：" + ex.Message);
            return false;
        }

        if (online is null || PluginWorkshop.CompareVersion(identity.Value.Version, online) > 0)
            return false;

        var message = $"版本号未更新（当前本地: {identity.Value.Version}，线上最新: {online}），请修改 plugin.json 中的版本号后再试";
        Log(message);
        System.Windows.MessageBox.Show(this, message, "版本号未更新",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        return true;
    }

    // ─────────────────────────── 我的插件 / 管理员审核 ───────────────────────────

    void OnMyPlugins(object sender, RoutedEventArgs e)
    {
        if (!_marketAuth.IsLoggedIn)
        {
            System.Windows.MessageBox.Show(this, "先点「登录插件市场」登录，再查看我的插件。",
                "我的插件", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        // 主体在主窗口页签里；切页触发 SelectionChanged → 面板刷新（不再弹独立窗口）
        MainTabs.SelectedItem = MyPluginsTab;
    }

    void OnAdminReview(object sender, RoutedEventArgs e)
    {
        if (_marketAuth.User?.IsAdmin != true)
        {
            Log("当前账号不是管理员，打不开审核界面。");
            return;
        }

        MainTabs.SelectedItem = AdminReviewTab;
    }

    // ─────────────────────────── 主题 ───────────────────────────

    void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        // 已经开着就拎到前面，不重复弹窗
        if (_settingsWindow is { } open)
        {
            open.Activate();
            return;
        }

        var window = new SettingsWindow(_marketAuth) { Owner = this };
        // 设置里绑定成功 / 退出登录后，主界面右上角跟着变
        window.AccountChanged += SyncMarketUi;
        window.Closed += (_, _) =>
        {
            _settingsWindow = null;
            SyncMarketUi();          // 关窗再兜一次：账号在设置里改过、事件没覆盖到的场景也能刷新
        };

        _settingsWindow = window;
        window.Show();
    }

    void OnOpenAbout(object sender, RoutedEventArgs e)
    {
        var window = new AboutWindow { Owner = this };
        window.Show();
    }

    void OnThemeChanged()
    {
        // ThemeChanged 是同步广播；用 BeginInvoke 保证订阅者在自己的界面就绪后才刷
        Dispatcher.BeginInvoke(new Action(SyncEditorTheme));
    }

    /// <summary>AvalonEdit 的背景 / 文字 / 行号色都要跟着主题换，不然深色配深字等于看不见。</summary>
    void SyncEditorTheme()
    {
        Editor.Background = ThemeBrush("EditorBackgroundBrush");
        Editor.Foreground = ThemeBrush("TextBrush");
        Editor.LineNumbersForeground = ThemeBrush("DimTextBrush");
    }

    Brush ThemeBrush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;

    // ─────────────────────────── 工具栏 ───────────────────────────

    void OnNewProject(object sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectWindow(_marketAuth.IsLoggedIn ? _marketAuth.User?.Username : null) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Project is null)
        {
            Log("已取消新建。");
            return;
        }

        var project = dialog.Project;
        try
        {
            Log($"新建项目完成：{project.ProjectDir}（id={project.Id}，type={PluginWorkshop.DefaultEntryType(project)}）");
            ShowProject(project);
        }
        catch (Exception ex)
        {
            Log("创建项目失败：" + ex.Message);
        }
    }

    /// <summary>打开磁盘上现成的插件目录（选到含 plugin.json 的那一层）。</summary>
    void OnOpenProject(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择包含 plugin.json 的插件项目文件夹",
            Multiselect = false,
        };
        if (Directory.Exists(PluginProject.ProjectsRoot)) dialog.InitialDirectory = PluginProject.ProjectsRoot;

        if (dialog.ShowDialog(this) != true) { Log("已取消打开。"); return; }

        try
        {
            var project = PluginWorkshop.OpenFrom(dialog.FolderName, Log);
            if (project is null)
            {
                System.Windows.MessageBox.Show(this,
                    "打开失败：目录里没有可用的 plugin.json。\n\n项目标识以根目录的 plugin.json 为准。",
                    "打开项目", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            ShowProject(project);
        }
        catch (Exception ex)
        {
            Log("打开项目失败：" + ex.Message);
        }
    }

    /// <summary>装载项目 → 切到工作区 → 记进最近列表。新建 / 打开 / 点历史都走这里。</summary>
    void ShowProject(PluginProject project)
    {
        MainTabs.SelectedItem = EditorTab;   // 先切页签：编辑器进了可视树再灌内容
        _project = project;
        OpenFile(project.SourcePath);
        BuildTree();
        RefreshStatus();
        AddRecent(project.ProjectDir);
    }

    // ─────────────────────────── 最近打开的项目 ───────────────────────────

    /// <summary>
    /// 只存目录路径：recent_projects.json，放在 <see cref="WritableData.Dir"/> 下
    /// （优先 %AppData%，被 ACL 拒绝时自动退到 exe 同级）。倒序、最多 10 条。
    /// </summary>
    static readonly string RecentFile = Path.Combine(WritableData.Dir, "recent_projects.json");

    const int RecentMax = 10;

    static List<string> LoadRecent()
    {
        try
        {
            if (!File.Exists(RecentFile)) return new List<string>();
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(RecentFile)) ?? new List<string>();
        }
        catch
        {
            return new List<string>();      // 文件坏了不拦启动，下一次写入会覆盖
        }
    }

    void SaveRecent(List<string> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RecentFile)!);
            File.WriteAllText(RecentFile, JsonSerializer.Serialize(list));
        }
        catch (Exception ex)
        {
            Log("写最近项目列表失败：" + ex.Message);
        }
    }

    /// <summary>去重后插到最前，超过 10 条截尾，然后重画左侧列表。</summary>
    void AddRecent(string projectDir)
    {
        var list = LoadRecent();
        list.RemoveAll(p => string.Equals(p, projectDir, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, projectDir);
        if (list.Count > RecentMax) list.RemoveRange(RecentMax, list.Count - RecentMax);

        SaveRecent(list);
        RefreshRecentUi(list);
    }

    void RefreshRecentUi(List<string>? list = null)
    {
        list ??= LoadRecent();
        RecentList.Items.Clear();

        foreach (var dir in list)
        {
            var missing = !Directory.Exists(dir);
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (name.Length == 0) name = dir;

            RecentList.Items.Add(new ListBoxItem
            {
                Tag = dir,
                ToolTip = dir,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = missing ? name + "（已失效）" : name,
                            Foreground = ThemeBrush(missing ? "DimTextBrush" : "TextBrush"),
                        },
                        new TextBlock
                        {
                            Text = dir,
                            FontSize = 11,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Foreground = ThemeBrush("DimTextBrush"),
                        },
                    },
                },
            });
        }

        RecentEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnRecentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (RecentList.SelectedItem is not ListBoxItem { Tag: string dir }) return;

        // 先清掉选中：同一条目可以再点一次，键盘上下键也不会直接开项目
        RecentList.SelectedItem = null;
        OpenRecent(dir);
    }

    void OpenRecent(string dir)
    {
        if (!Directory.Exists(dir))
        {
            System.Windows.MessageBox.Show(this,
                "这个项目目录已经不存在了：\n" + dir + "\n\n已从「最近打开的项目」里移除。",
                "最近打开的项目", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);

            var list = LoadRecent();
            list.RemoveAll(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase));
            SaveRecent(list);
            RefreshRecentUi(list);
            return;
        }

        try
        {
            var project = PluginWorkshop.OpenFrom(dir, Log);
            if (project is not null)
            {
                ShowProject(project);
            }
            else
            {
                System.Windows.MessageBox.Show(this,
                    "打开失败：这个目录里没有可用的 plugin.json（不是插件项目，或清单读不了）。",
                    "最近打开的项目", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Log("打开项目失败：" + ex.Message);
        }
    }

    void OnSave(object sender, RoutedEventArgs e) => SaveOpen(requireLog: true);

    void OnBuildPack(object sender, RoutedEventArgs e)
    {
        _busy = true;
        try
        {
            if (!EnsureProject()) return;
            if (CompileAndPack() is null) return;
        }
        finally
        {
            _busy = false;
        }
    }

    void OnBuildTest(object sender, RoutedEventArgs e)
    {
        _busy = true;
        try
        {
            if (!EnsureProject()) return;
            if (CompileAndPack() is null) return;
            DeployAndLaunch();
        }
        finally
        {
            _busy = false;
        }
    }

    void OnClearOutput(object sender, RoutedEventArgs e) => OutputBox.Clear();

    // ─────────────────────────── 编辑器 ───────────────────────────

    void OnTextChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        _dirty = true;
        RefreshStatus();
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Tree.SelectedItem is not TreeViewItem { Tag: string path }) return;
        if (Directory.Exists(path)) return;      // 文件夹节点双击不打开文件
        OpenFile(path);
    }

    void OpenFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".cs" or ".json")) { Log("只能打开 .cs / .json：" + Path.GetFileName(path)); return; }
        if (!File.Exists(path)) { Log("文件不存在：" + path); return; }

        try
        {
            _loading = true;
            Editor.Text = File.ReadAllText(path);
            // JSON 没有内置高亮定义，取不到就纯文本显示，别让它抛
            Editor.SyntaxHighlighting = ext == ".cs"
                ? HighlightingManager.Instance.GetDefinition("C#")
                : HighlightingManager.Instance.GetDefinition("JSON");
            _openPath = path;
            _dirty = false;
            Editor.ScrollToHome();
            Log("已打开：" + path);
            RefreshStatus();
        }
        catch (Exception ex)
        {
            Log("读文件失败：" + ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    bool SaveOpen(bool requireLog = false)
    {
        if (_project is null)
        {
            Log("还没有项目，先点「新建项目」。");
            return false;
        }

        var path = _openPath ?? _project.SourcePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Editor.Text);
            _dirty = false;
            _autoSaveTimer.Stop();
            if (requireLog) Log("已保存：" + path);
            RefreshStatus();
            return true;
        }
        catch (Exception ex)
        {
            Log("保存失败：" + ex.Message);
            return false;
        }
    }

    void AutoSave()
    {
        if (!_dirty || _project is null || _openPath is null) return;
        SaveOpen(requireLog: false);
    }

    // ─────────────────────────── 编译 / 打包 / 联调 ───────────────────────────

    /// <summary>存盘 → Roslyn 编译 → 打 .cblplugin。失败返回 null（原因已经打进控制台）。</summary>
    string? CompileAndPack()
    {
        var project = _project;
        if (project is null) return null;

        if (!SaveOpen()) return null;

        Cursor = Cursors.Wait;
        try
        {
            var result = PluginWorkshop.BuildAndPack(project, Log);
            if (!result.Ok) { RefreshStatus("编译失败"); return null; }

            Log($"入口类型：{result.EntryType ?? "（未识别，需在 plugin.json 里手写 type）"}");
            Log($"包：{project.PackagePath}");

            // 清单被重新生成过，正打开着就再读一次，免得用户看到旧内容
            if (_openPath is not null && string.Equals(_openPath, project.ManifestPath, StringComparison.OrdinalIgnoreCase))
                OpenFile(project.ManifestPath);

            BuildTree();
            RefreshStatus("已打包");
            return project.PackagePath;
        }
        catch (Exception ex)
        {
            Log("编译过程出错：" + ex.Message);
            RefreshStatus("编译异常");
            return null;
        }
        finally
        {
            Cursor = Cursors.Arrow;
        }
    }

    void DeployAndLaunch()
    {
        var project = _project;
        if (project is null) return;

        var mainExe = ResolveMainAppPath();
        if (mainExe is null) return;

        if (PluginWorkshop.IsMainAppRunning(mainExe))
        {
            Log("主程序正在运行 —— 插件 dll 会被占用，先退出主程序（托盘右键 → 退出）再试。");
            System.Windows.MessageBox.Show(this,
                "主程序还在运行，插件文件会被锁定。\n\n请先彻底退出主程序（托盘图标右键 → 退出），\n然后再点一次「编译并测试」。",
                "需要先关闭主程序",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        try
        {
            var target = PluginWorkshop.Deploy(project, mainExe, Log);
            Log("已部署（下次启动或「设置 → 插件 → 重新扫描」即可看到）：" + target);
            PluginWorkshop.Launch(mainExe, Log);
            RefreshStatus("已部署并启动");
        }
        catch (Exception ex)
        {
            Log("部署失败：" + ex.Message);
        }
    }

    /// <summary>ide_config.json 里没有（或路径失效）就让用户选一次 exe，选完写回配置。</summary>
    string? ResolveMainAppPath()
    {
        var configured = _config.MainAppPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        if (!string.IsNullOrWhiteSpace(configured))
            Log("配置里的主程序路径已失效，重新选择：" + configured);

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择主程序（Class Daily Land 2.exe）",
            Filter = "应用程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        var guess = GuessMainAppDir();
        if (guess is not null) dialog.InitialDirectory = guess;

        if (dialog.ShowDialog(this) != true)
        {
            Log("已取消：没有选择主程序。");
            return null;
        }

        // 重新 Load 再改：避免用窗口里这份旧快照把主题设置覆盖掉
        var config = IdeConfig.Load() with { MainAppPath = dialog.FileName };
        config.Save();
        _config = config;
        Log("主程序路径已写入 ide_config.json：" + dialog.FileName);
        return dialog.FileName;
    }

    /// <summary>猜主程序输出目录：从 IDE 的 exe 往上找解决方案里的主工程 bin。</summary>
    static string? GuessMainAppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 6 && dir is not null; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Class Daily Land 2", "bin");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ─────────────────────────── 界面辅助 ───────────────────────────

    bool EnsureProject()
    {
        if (_project is not null) return true;
        Log("还没有项目，先点「新建项目」。");
        return false;
    }

    void BuildTree()
    {
        Tree.Items.Clear();
        var project = _project;
        if (project is null) return;

        var root = new TreeViewItem
        {
            Header = project.DisplayName + "（" + project.Id + "）",
            IsExpanded = true,
        };
        AddDirectory(root, project.ProjectDir);
        Tree.Items.Add(root);
    }

    /// <summary>把项目目录整个铺进树：子文件夹递归展开，生成物目录（build/bin/obj）不进树。</summary>
    void AddDirectory(TreeViewItem parent, string dir)
    {
        string[] dirs;
        string[] files;
        try
        {
            dirs = Directory.GetDirectories(dir);
            files = Directory.GetFiles(dir);
        }
        catch (Exception ex)
        {
            Log("列目录失败：" + dir + "：" + ex.Message);
            return;
        }

        foreach (var sub in dirs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(sub);
            if (name is "build" or "bin" or "obj") continue;

            var node = new TreeViewItem { Header = name, Tag = sub };
            AddDirectory(node, sub);
            parent.Items.Add(node);
        }

        foreach (var file in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var node = new TreeViewItem { Header = Path.GetFileName(file), Tag = file };
            node.ContextMenu = BuildFileMenu(node);
            parent.Items.Add(node);
        }
    }

    /// <summary>文件节点右键菜单（目前只有重命名）。</summary>
    ContextMenu BuildFileMenu(TreeViewItem node)
    {
        var rename = new MenuItem { Header = "重命名" };
        rename.Click += (_, _) => RenameFile(node);

        var menu = new ContextMenu();
        menu.Items.Add(rename);
        return menu;
    }

    void RenameFile(TreeViewItem node)
    {
        if (node.Tag is not string path || !File.Exists(path)) return;

        var name = PromptWindow.Ask(this, "重命名文件", "新文件名（含扩展名，如 a.cs）：",
            Path.GetFileName(path), okText: "重命名");
        if (name is null) return;
        name = name.Trim();

        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            System.Windows.MessageBox.Show(this, "文件名不合法。", "重命名",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        var target = Path.Combine(Path.GetDirectoryName(path)!, name);
        if (string.Equals(target, path, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(target))
        {
            System.Windows.MessageBox.Show(this, "同目录下已经有同名文件了。", "重命名",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        try
        {
            File.Move(path, target);

            // 编辑器里正开着这个文件：路径跟着换，否则下次保存会写回旧文件名
            if (string.Equals(_openPath, path, StringComparison.OrdinalIgnoreCase)) _openPath = target;

            BuildTree();
            RefreshStatus();
            Log($"已重命名：{Path.GetFileName(path)} → {name}");
        }
        catch (Exception ex)
        {
            Log("重命名失败：" + ex.Message);
            System.Windows.MessageBox.Show(this, "重命名失败：" + ex.Message, "重命名",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    void RefreshStatus(string? suffix = null)
    {
        var project = _project;
        if (project is null)
        {
            StatusText.Text = "未打开项目";
            return;
        }

        var state = suffix ?? (_dirty ? "未保存" : "已保存");
        var file = _openPath is null ? "" : " · " + Path.GetFileName(_openPath);
        StatusText.Text = $"{project.DisplayName} · id={project.Id} · {state}{file}";
    }

    void Log(string message)
    {
        void Append()
        {
            OutputBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            OutputBox.ScrollToEnd();
        }

        if (Dispatcher.CheckAccess()) Append();
        else Dispatcher.BeginInvoke(Append);
    }
}
