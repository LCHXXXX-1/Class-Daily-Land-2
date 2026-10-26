using ClassDailyLand2.App.Controls;
using ClassDailyLand2.App.Integrations.Audio;
using ClassDailyLand2.App.Integrations.Disaster;
using ClassDailyLand2.App.Plugins;
using ClassDailyLand2.App.Updates;
using ClassDailyLand2.App.ViewModels;
using ClassDailyLand2.App.Views.Dialogs;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClassDailyLand2.App.Views.Pages;

// ============================================================================
//  各页的代码后置：XAML 负责版式，这里只做「需要跑代码才能接上」的少量交互
//  （DataContext 由外壳在创建后赋值，所以取 VM 的时机一律放在 Loaded）。
// ============================================================================

public partial class GeneralPage : UserControl
{
    public GeneralPage() => InitializeComponent();
}

public partial class AppearancePage : UserControl
{
    public AppearancePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is not SettingsViewModel vm) return;
            // Win11 → 云母，Win10 1803+ → 亚克力；更老的系统才置灰
            var supported = vm.SupportsBackdrop;
            MicaRow.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
            MicaUnsupportedRow.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;
        };
    }
}

public partial class InteractionPage : UserControl
{
    public InteractionPage() => InitializeComponent();
}

public partial class IslandPage : UserControl
{
    public IslandPage() => InitializeComponent();
}

public partial class SchedulePage : UserControl
{
    public SchedulePage()
    {
        InitializeComponent();
        WeekendLink.Click += (_, _) => Show(vm => new WeekendDialog(vm));
        HolidaysLink.Click += (_, _) => Show(vm => new HolidaysDialog(vm));
        DutyLink.Click += (_, _) => Show(vm => new DutyDialog(vm));
        TempAdjustLink.Click += (_, _) => Show(vm => new TempAdjustDialog(vm));
        StatusTestLink.Click += (_, _) => Show(vm => new StatusTestDialog(vm));
    }

    void Show(Func<SettingsViewModel, FrameworkElement> create)
    {
        if (DataContext is not SettingsViewModel vm) return;
        DialogLauncher.Show(this, create(vm));
    }
}

public partial class HomeworkPage : UserControl
{
    bool _built;

    public HomeworkPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_built || DataContext is not SettingsViewModel vm) return;
            _built = true;
            // 内嵌模式：改动即时生效，没有取消 / 确认按钮
            EditorHost.Content = new HomeworkEditor(vm, embedded: true);
            OpenFolderButton.Click += (_, _) => DialogLauncher.OpenFolder(vm.ConfigPath);
        };
    }
}

public partial class PluginsPage : UserControl
{
    PluginManager? _manager;
    bool _hooked;

    public PluginsPage()
    {
        InitializeComponent();

        InstallPackageLink.Click += (_, _) => InstallPackage();
        InstallFolderLink.Click += (_, _) => InstallFolder();
        RescanLink.Click += (_, _) =>
        {
            _manager?.Refresh();
            Refresh();
            DialogLauncher.Toast(this, InfoBarSeverity.Information, "重新扫描", "已按插件目录的当前内容重新加载。");
        };
        OpenFolderLink.Click += (_, _) =>
        {
            if (_manager is not null) DialogLauncher.OpenFolder(_manager.PluginsRoot);
        };
        ClearLogLink.Click += (_, _) =>
        {
            _manager?.ClearLog();
            UpdateLog();
        };

        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    void Attach()
    {
        if (_hooked || DataContext is not SettingsViewModel vm) return;
        _hooked = true;
        _manager = vm.Ctx.Plugins;
        if (_manager is not null) _manager.LogAppended += OnLogAppended;
        Refresh();
    }

    void Detach()
    {
        if (_manager is not null) _manager.LogAppended -= OnLogAppended;
        _hooked = false;
    }

    void OnLogAppended()
    {
        // 日志可能来自插件自己的线程，切回 UI 线程再动控件
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(UpdateLog));
            return;
        }
        UpdateLog();
    }

    // ————————————————————— 刷新 —————————————————————

    void Refresh()
    {
        if (_manager is null) return;

        var plugins = _manager.Plugins;
        var loaded = plugins.Count(p => p.Loaded);
        var failed = plugins.Count(p => p.Error is not null);

        NoPluginNotice.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginHostPanel.Visibility = plugins.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        PluginHostPanel.Children.Clear();
        foreach (var info in plugins) PluginHostPanel.Children.Add(BuildRow(info));

        OpenFolderLink.Hint = _manager.PluginsRoot;
        SummaryText.Text = $"共 {plugins.Count} 个插件 · 已加载 {loaded} 个" +
                           (failed > 0 ? $" · 失败 {failed} 个" : "") +
                           $" · 契约 v{PluginManager.ApiVersion} · 插件目录：{_manager.PluginsRoot}";
        UpdateLog();
    }

    void UpdateLog()
    {
        var lines = _manager?.RecentLog;
        if (lines is null || lines.Count == 0)
        {
            LogBox.Text = "（还没有日志）";
            return;
        }
        LogBox.Text = string.Join(Environment.NewLine, lines);
        LogBox.ScrollToEnd();
    }

    // ————————————————————— 单个插件一行 —————————————————————

    FrameworkElement BuildRow(PluginInfo info)
    {
        var content = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };

        // 第一行：名称 + 版本 + 状态徽标
        var head = new WrapPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = info.Name,
            FontSize = (double)FindResource("FontSizeBody"),
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(MakeBadge("v" + info.Version, BadgeVariant.Neutral));
        head.Children.Add(info.Loaded
            ? MakeBadge("已加载", BadgeVariant.Ok)
            : info.Enabled
                ? MakeBadge("未加载", BadgeVariant.Warn)
                : MakeBadge("已禁用", BadgeVariant.Neutral));
        if (!string.IsNullOrWhiteSpace(info.Author)) head.Children.Add(MakeBadge(info.Author, BadgeVariant.Neutral));
        content.Children.Add(head);

        if (!string.IsNullOrWhiteSpace(info.Description))
            content.Children.Add(new TextBlock
            {
                Text = info.Description,
                Margin = new Thickness(0, 3, 0, 0),
                FontSize = (double)FindResource("FontSizeCaption"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
            });

        // 出错的行把原因原样摆出来 —— 这是插件系统最有价值的一行信息
        if (!string.IsNullOrWhiteSpace(info.Error))
            content.Children.Add(new TextBlock
            {
                Text = "加载失败：" + info.Error,
                Margin = new Thickness(0, 4, 0, 0),
                FontSize = (double)FindResource("FontSizeCaption"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Theme.Palette.Danger,
            });

        var meta = new List<string> { "契约 v" + info.ApiVersion };
        if (info.Loaded) meta.Add($"注册 {info.Registrations} 个显示位");
        if (!string.IsNullOrWhiteSpace(info.Homepage)) meta.Add(info.Homepage!);
        content.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", meta),
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = (double)FindResource("FontSizeCaption"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
        });

        // 右侧操作区
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var toggle = new FluentToggle { Label = "启用", IsChecked = info.Enabled, VerticalAlignment = VerticalAlignment.Center };
        toggle.Click += (_, _) =>
        {
            var want = toggle.IsChecked == true;
            if (_manager is null) return;
            if (!_manager.SetEnabled(info.Id, want))
            {
                toggle.IsChecked = !want;      // 起不来就把开关拨回去，别骗用户
                DialogLauncher.Toast(this, InfoBarSeverity.Error, "没有生效",
                    $"{info.Name} 没能" + (want ? "启用" : "禁用") + "，原因见运行日志。");
            }
            Refresh();
        };
        actions.Children.Add(toggle);

        actions.Children.Add(MakeButton("重载", () =>
        {
            _manager?.Reload(info.Id);
            Refresh();
        }));
        actions.Children.Add(MakeButton("目录", () => DialogLauncher.OpenFolder(info.Directory)));
        actions.Children.Add(MakeButton("卸载", () =>
            DialogLauncher.Confirm(this, "卸载插件",
                $"确定卸载「{info.Name}」吗？插件目录与它的数据都会被删除，无法恢复。",
                () =>
                {
                    var result = _manager?.Uninstall(info.Id);
                    if (result is { Ok: false })
                        DialogLauncher.Toast(this, InfoBarSeverity.Error, "卸载失败", result.Error ?? "");
                    Refresh();
                }, okText: "卸载")));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(content, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(content);
        grid.Children.Add(actions);

        return new Border
        {
            Padding = new Thickness(18, 12, 18, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = (Brush)FindResource("DividerStrokeColorDefaultBrush"),
            Child = grid,
        };
    }

    Badge MakeBadge(string text, BadgeVariant variant) => new()
    {
        Text = text,
        Variant = variant,
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    Button MakeButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource("SubtleButton"),
            Padding = new Thickness(10, 3, 10, 4),
            Margin = new Thickness(8, 0, 0, 0),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    // ————————————————————— 安装 —————————————————————

    void InstallPackage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择插件包",
            Filter = "插件包 (*.cblplugin;*.zip)|*.cblplugin;*.zip|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        Install(dlg.FileName, overwrite: false);
    }

    void InstallFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择插件文件夹（含 plugin.json）" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        Install(dlg.FolderName, overwrite: false);
    }

    void Install(string path, bool overwrite)
    {
        if (_manager is null) return;
        var result = _manager.Install(path, overwrite);
        if (result.Ok)
        {
            DialogLauncher.Toast(this, InfoBarSeverity.Success, "已安装",
                $"「{result.PluginId}」已经装好并启用。");
            Refresh();
            return;
        }

        // 已装过：问一句再覆盖，不要直接失败也不要静默覆盖
        if (!overwrite && result.PluginId is not null)
        {
            DialogLauncher.Confirm(this, "覆盖安装",
                $"已经装过「{result.PluginId}」了。用这个包覆盖它吗？（插件自己的数据目录会保留）",
                () => Install(path, overwrite: true), okText: "覆盖");
            return;
        }

        DialogLauncher.Toast(this, InfoBarSeverity.Error, "安装失败", result.Error ?? "未知原因");
        Refresh();
    }
}

public partial class AdvancedPage : UserControl
{
    public AdvancedPage()
    {
        InitializeComponent();
        OpenConfigLink.Click += (_, _) => Open(vm => vm.ConfigPath);
        OpenPluginsLink.Click += (_, _) => Open(vm => vm.Ctx.Plugins.PluginsRoot);
        InstallLocalLink.Click += (_, _) => InstallPlugin(vm: DataContext as SettingsViewModel);
        ExportScheduleLink.Click += (_, _) => Export();
        ImportScheduleLink.Click += (_, _) => Import();
        ResetLink.Click += (_, _) => Reset();
    }

    /// <summary>高级页的「安装本地插件…」：和插件页走同一条流程，装完跳到插件页看结果。</summary>
    void InstallPlugin(SettingsViewModel? vm)
    {
        if (vm is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择插件包",
            Filter = "插件包 (*.cblplugin;*.zip)|*.cblplugin;*.zip|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        var result = vm.Ctx.Plugins.Install(dlg.FileName, overwrite: false);
        if (result.Ok)
        {
            DialogLauncher.Toast(this, InfoBarSeverity.Success, "已安装", $"「{result.PluginId}」已经装好并启用。");
            return;
        }
        if (result.PluginId is not null)
        {
            DialogLauncher.Confirm(this, "覆盖安装",
                $"已经装过「{result.PluginId}」了。用这个包覆盖它吗？（插件自己的数据目录会保留）",
                () =>
                {
                    var retry = vm.Ctx.Plugins.Install(dlg.FileName, overwrite: true);
                    DialogLauncher.Toast(this,
                        retry.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                        retry.Ok ? "已安装" : "安装失败",
                        retry.Ok ? $"「{retry.PluginId}」已经装好并启用。" : retry.Error ?? "");
                }, okText: "覆盖");
            return;
        }
        DialogLauncher.Toast(this, InfoBarSeverity.Error, "安装失败", result.Error ?? "未知原因");
    }

    void Open(Func<SettingsViewModel, string> path)
    {
        if (DataContext is SettingsViewModel vm) DialogLauncher.OpenFolder(path(vm));
    }

    void Export()
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出课表 JSON",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"课表_{DateTime.Today:yyyyMMdd}.json",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        System.IO.File.WriteAllText(dlg.FileName, vm.Ctx.Schedule.ExportJson(), System.Text.Encoding.UTF8);
        DialogLauncher.Toast(this, InfoBarSeverity.Success, "已导出", dlg.FileName);
    }

    void Import()
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入课表 JSON",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        DialogLauncher.Confirm(this, "导入课表",
            "导入会覆盖当前全部课表（含假期与调休），确定继续吗？", () =>
        {
            try
            {
                var ok = vm.Ctx.Schedule.ImportJson(
                    System.IO.File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8));
                DialogLauncher.Toast(this, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error, "导入课表",
                    ok ? "导入完成，灵动岛已按新课表运行。" : "文件内容不是有效的课表 JSON。");
            }
            catch (Exception ex)
            {
                DialogLauncher.Toast(this, InfoBarSeverity.Error, "导入课表", "导入失败：" + ex.Message);
            }
        });
    }

    void Reset()
    {
        if (DataContext is not SettingsViewModel vm) return;
        DialogLauncher.Confirm(this, "恢复全部默认",
            "所有设置都会回到默认值。已经装好的插件、课表和值日名单不受影响。", () =>
        {
            vm.ResetAll();
            DialogLauncher.Toast(this, InfoBarSeverity.Success, "已恢复默认", "所有设置已回到初始值。");
        }, okText: "恢复");
    }
}

public partial class AboutPage : UserControl
{
    SettingsViewModel? _vm;
    bool _hooked;
    bool _spinning;

    public AboutPage()
    {
        InitializeComponent();

        // ★ 状态订阅只挂一次：页面会被 SettingsWindow 缓存，之前写在 Loaded 里
        //   每次切回关于页都挂一次 Click，连点 5 次会发 5 个请求（见 AGENTS 坑清单）。
        DataContextChanged += (_, _) => Hook();
        Loaded += (_, _) => { Hook(); Render(); };
    }

    void Hook()
    {
        if (_hooked) return;
        if (DataContext is not SettingsViewModel vm) return;
        _vm = vm;
        _hooked = true;

        ChannelHint.Text = vm.UpdateChannel == "beta" ? "测试版可能不稳定" : "";

        vm.Ctx.Updates.StatusChanged += OnStatusChanged;

        // 通道切换后要重新检查（同一个源的 stable/beta 是不同的块）
        ChannelBar.SelectedChanged += _ =>
        {
            ChannelHint.Text = vm.UpdateChannel == "beta" ? "测试版可能不稳定" : "";
            vm.Ctx.CheckForUpdate();
        };
    }

    void OnStatusChanged(UpdateStatus status)
    {
        // CheckAsync 里 ConfigureAwait(false)，回调可能在后台线程
        Dispatcher.BeginInvoke(() => Render(status));
    }

    void OnCheckUpdateClick(object sender, RoutedEventArgs e) => _vm?.Ctx.CheckForUpdate();

    /// <summary>
    /// 「立即更新」：直接拉起更新器，不过确认框（用户明确要求的交互：
    /// 检查更新过后 = 再次检查 + 立即更新，点了立即更新就把更新器拉起来）。
    /// 下载期间程序照常运行，只在替换文件前由更新器关闭，设置与插件不受影响。
    /// </summary>
    void OnUpdateInstallClick(object sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;

        var offer = vm.Ctx.Updates.Status.App;
        if (offer is null) return;

        // ApplyUpdateAsync：启动预复制到 %Temp% 的更新器 → 主程序继续在后台运行
        _ = vm.Ctx.ApplyUpdateAsync(offer);
    }

    void Render() => Render(_vm?.Ctx.Updates.Status ?? UpdateStatus.Idle);

    void Render(UpdateStatus status)
    {
        if (status.Stage == UpdateStage.Checking)
        {
            CheckUpdateButton.IsEnabled = false;
            CheckUpdateText.Text = "正在检查…";
            StartSpin();
        }
        else
        {
            CheckUpdateButton.IsEnabled = true;
            // 检查跑完过一轮（无论有没有新版本）就变成"再次检查"
            var checkedBefore = status.Stage is UpdateStage.UpToDate or UpdateStage.Available;
            CheckUpdateText.Text = checkedBefore ? "再次检查" : "检查更新";
            StopSpin();
        }

        // 有新版本才亮出「立即更新」；同时把检查按钮降级成次要样式，突出主操作
        var hasUpdate = status.App is not null;
        UpdateInstallButton.Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdateButton.Style = (Style)FindResource(hasUpdate ? "SubtleButton" : "AccentButton");

        if (status.Stage is UpdateStage.Idle or UpdateStage.Checking)
        {
            UpdateBar.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateBar.Visibility = Visibility.Visible;
        switch (status.Stage)
        {
            case UpdateStage.UpToDate:
                UpdateBar.Severity = InfoBarSeverity.Information;
                UpdateBar.Title = "已是最新";
                UpdateBar.Message = status.Message;
                break;

            case UpdateStage.Available:
                UpdateBar.Severity = status.App?.IsMandatory == true
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success;
                UpdateBar.Title = status.App?.IsMandatory == true ? "发现新版本（强制）" : "发现新版本";
                // 更新内容直接贴在状态条上（没有确认框了，用户一眼能看到改了什么）
                var preview = status.App is { Changelog.Count: > 0 } app
                    ? "  · " + string.Join("  · ", app.Changelog.Take(3))
                    : "";
                UpdateBar.Message = (status.Message ?? "") + preview;
                break;

            case UpdateStage.Failed:
                UpdateBar.Severity = InfoBarSeverity.Warning;
                UpdateBar.Title = "检查更新失败";
                UpdateBar.Message = string.IsNullOrWhiteSpace(status.Error) ? status.Message : status.Error;
                break;
        }
    }

    void StartSpin()
    {
        if (_spinning) return;
        _spinning = true;

        var transform = new RotateTransform();
        CheckIcon.RenderTransform = transform;
        CheckIcon.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        transform.BeginAnimation(RotateTransform.AngleProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            });
    }

    void StopSpin()
    {
        if (!_spinning) return;
        _spinning = false;
        CheckIcon.RenderTransform = null;
    }
}

// ============================================================================
//  媒体（内置集成）：状态行每秒刷新一次，其余都是普通绑定
// ============================================================================

public partial class MediaPage : UserControl
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _loading;

    public MediaPage()
    {
        InitializeComponent();
        InjectButton.Click += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm) vm.Ctx.Audio?.InjectTestMedia();
            UpdateStatus();
        };
        ClearInjectButton.Click += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm) vm.Ctx.Audio?.ClearTestMedia();
            UpdateStatus();
        };
        Loaded += (_, _) => { Init(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += (_, _) => UpdateStatus();
    }

    void Init()
    {
        UnavailableNotice.Visibility = AudioService.Available ? Visibility.Collapsed : Visibility.Visible;
        if (DataContext is not SettingsViewModel vm) return;

        _loading = true;
        foreach (var item in ShowOnBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals((string?)item.Tag, vm.AudioShowOn, StringComparison.OrdinalIgnoreCase))
            {
                ShowOnBox.SelectedItem = item;
                break;
            }
        }
        _loading = false;
        UpdateStatus();
    }

    void UpdateStatus()
    {
        if (DataContext is SettingsViewModel vm)
            StatusText.Text = vm.Ctx.Audio?.StatusText ?? "";
    }

    void OnShowOnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        if (ShowOnBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            vm.AudioShowOn = tag;
    }
}

// ============================================================================
//  灾害预警（内置集成）：省市县三级联动 + 状态行 + 演练
// ============================================================================

public partial class DisasterPage : UserControl
{
    const string AnyCity = "（不限）";
    const string AnyDistrict = "（不限）";

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _loading;

    public DisasterPage()
    {
        InitializeComponent();
        ApplyCoordButton.Click += (_, _) => ApplyCoord();
        DrillButton.Click += (_, _) => Drill();
        ClearDrillButton.Click += (_, _) => ClearDrill();

        Loaded += (_, _) => { Init(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += (_, _) => UpdateStatus();
    }

    void Init()
    {
        if (DataContext is not SettingsViewModel vm) return;
        _loading = true;

        // 省份（最后加一个"不获取气象"）
        ProvinceBox.Items.Clear();
        foreach (var p in Cities.Provinces) ProvinceBox.Items.Add(p);
        ProvinceBox.Items.Add(Cities.NoWeather);
        Select(ProvinceBox, string.IsNullOrEmpty(vm.DisasterProvince) ? "四川省" : vm.DisasterProvince);

        FillCities(vm.DisasterCity);
        FillDistricts(vm.DisasterDistrict);

        foreach (var item in ThresholdBox.Items.OfType<ComboBoxItem>())
        {
            if (int.TryParse((string?)item.Tag, out var v) && v == vm.DisasterThresholdX10)
            {
                ThresholdBox.SelectedItem = item;
                break;
            }
        }
        if (ThresholdBox.SelectedItem is null) ThresholdBox.SelectedIndex = 1;

        LatBox.Text = vm.DisasterLat;
        LonBox.Text = vm.DisasterLon;

        _loading = false;
        UpdateStatus();
    }

    static void Select(ComboBox box, string value)
    {
        foreach (var item in box.Items)
        {
            if (string.Equals(item?.ToString(), value, StringComparison.Ordinal))
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    void FillCities(string keep)
    {
        var province = ProvinceBox.SelectedItem?.ToString() ?? "";
        CityBox.Items.Clear();
        CityBox.Items.Add(AnyCity);
        foreach (var c in Cities.CitiesOf(province)) CityBox.Items.Add(c);
        Select(CityBox, string.IsNullOrEmpty(keep) ? AnyCity : keep);
    }

    void FillDistricts(string keep)
    {
        var province = ProvinceBox.SelectedItem?.ToString() ?? "";
        var city = CityBox.SelectedItem?.ToString() ?? AnyCity;
        DistrictBox.Items.Clear();
        DistrictBox.Items.Add(AnyDistrict);
        foreach (var d in Cities.DistrictsOf(province, city == AnyCity ? "" : city))
            DistrictBox.Items.Add(d);
        Select(DistrictBox, string.IsNullOrEmpty(keep) ? AnyDistrict : keep);
    }

    string Province => ProvinceBox.SelectedItem?.ToString() ?? "";
    string CityValue => CityBox.SelectedItem?.ToString() == AnyCity ? "" : CityBox.SelectedItem?.ToString() ?? "";
    string DistrictValue => DistrictBox.SelectedItem?.ToString() == AnyDistrict ? "" : DistrictBox.SelectedItem?.ToString() ?? "";

    void OnProvinceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        _loading = true;
        FillCities("");
        FillDistricts("");
        _loading = false;

        vm.SetDisasterLocation(Province, "", "");
        LatBox.Text = "";
        LonBox.Text = "";
        ApplyCoord();
    }

    void OnCityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        _loading = true;
        FillDistricts("");
        _loading = false;

        vm.SetDisasterLocation(Province, CityValue, "");
        LatBox.Text = "";
        LonBox.Text = "";
        ApplyCoord();
    }

    void OnDistrictChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        vm.SetDisasterLocation(Province, CityValue, DistrictValue);
        LatBox.Text = "";
        LonBox.Text = "";
        ApplyCoord();
    }

    void OnThresholdChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        if (ThresholdBox.SelectedItem is ComboBoxItem item && int.TryParse((string?)item.Tag, out var v))
            vm.DisasterThresholdX10 = v;
    }

    /// <summary>把（可能空着的）经纬度写回设置：空 = 按省市查表。</summary>
    void ApplyCoord()
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.SetDisasterCoord(LatBox.Text?.Trim() ?? "", LonBox.Text?.Trim() ?? "");
        UpdateStatus();
    }

    void Drill()
    {
        if (DataContext is not SettingsViewModel vm) return;
        var result = vm.Ctx.Disaster?.StartDrill();
        DialogLauncher.Toast(this, InfoBarSeverity.Warning, "地震演练", result ?? "服务未就绪");
        UpdateStatus();
    }

    void ClearDrill()
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.Ctx.Disaster?.ClearDrill();
        UpdateStatus();
    }

    void UpdateStatus()
    {
        if (DataContext is SettingsViewModel vm)
            StatusText.Text = vm.Ctx.Disaster?.StatusText ?? "";
    }
}
