using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

namespace DeveloperIDE;

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
    string? _openPath;
    bool _dirty;
    bool _loading;

    /// <summary>静默检查到新版本后留着的那条更新信息（点提示时直接用它拉起更新器）。</summary>
    RemoteRelease? _offer;
    bool _updateCheckStarted;

    public MainWindow()
    {
        InitializeComponent();

        // 主题换色后 AvalonEdit 要手动补（它不认 DynamicResource）
        App.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= OnThemeChanged;
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
                UpdateHint.Content = $"🌟 发现新版本 v{remote.Version}，点击更新";
                UpdateHint.Visibility = Visibility.Visible;
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

        if (UpdateLauncher.TryStart(remote, out var error))
        {
            // 立刻松手：更新器在等我们退出，它自己还会再等 1.5 秒
            Application.Current.Shutdown();
            return;
        }

        MessageBox.Show(this, error, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ─────────────────────────── 主题 ───────────────────────────

    void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow { Owner = this };
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
        var dialog = new NewProjectWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Project is null)
        {
            Log("已取消新建。");
            return;
        }

        var project = dialog.Project;
        try
        {
            _project = project;
            Log($"新建项目完成：{project.ProjectDir}（id={project.Id}，type={PluginWorkshop.DefaultEntryType(project)}）");
            OpenFile(project.SourcePath);
            BuildTree();
            RefreshStatus();
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
            if (project is null) return;

            _project = project;
            OpenFile(project.SourcePath);
            BuildTree();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            Log("打开项目失败：" + ex.Message);
        }
    }

    void OnSave(object sender, RoutedEventArgs e) => SaveOpen(requireLog: true);

    void OnBuildPack(object sender, RoutedEventArgs e)
    {
        if (!EnsureProject()) return;
        if (CompileAndPack() is null) return;
    }

    void OnBuildTest(object sender, RoutedEventArgs e)
    {
        if (!EnsureProject()) return;
        if (CompileAndPack() is null) return;
        DeployAndLaunch();
    }

    void OnClearOutput(object sender, RoutedEventArgs e) => OutputBox.Clear();

    // ─────────────────────────── 编辑器 ───────────────────────────

    void OnTextChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        _dirty = true;
        RefreshStatus();
    }

    void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Tree.SelectedItem is not TreeViewItem { Tag: string path }) return;
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

        AddNode(root, project.SourceFileName, project.SourcePath);
        AddNode(root, "plugin.json", project.ManifestPath);
        AddNode(root, Path.GetFileName(project.PackagePath), project.PackagePath);
        AddNode(root, "build/Plugin.dll", project.DllPath);

        Tree.Items.Add(root);
    }

    static void AddNode(TreeViewItem parent, string header, string path)
    {
        if (!File.Exists(path)) return;
        parent.Items.Add(new TreeViewItem { Header = header, Tag = path });
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
