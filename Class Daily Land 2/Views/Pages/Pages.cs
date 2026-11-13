using ClassDailyLand2.App.Controls;
using ClassDailyLand2.App.DeveloperTool;
using ClassDailyLand2.App.Integrations.Audio;
using ClassDailyLand2.App.Integrations.Disaster;
using ClassDailyLand2.App.Market;
using ClassDailyLand2.App.Plugins;
using ClassDailyLand2.App.Theme;
using ClassDailyLand2.App.Updates;
using ClassDailyLand2.App.ViewModels;
using ClassDailyLand2.App.Views.Dialogs;
using ClassDailyLand2.Core;
using ClassDailyLand2.Core.Engine;
using ClassDailyLand2.Core.Managers;
using ClassDailyLand2.Core.Market;
using ClassDailyLand2.Core.Models;
using ClassDailyLand2.Core.Storage;
using Microsoft.Win32;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClassDailyLand2.App.Views.Pages;

// ============================================================================
//  各页的代码后置：XAML 负责版式，这里只做「需要跑代码才能接上」的少量交互
//  （DataContext 由外壳在创建后赋值，所以取 VM 的时机一律放在 Loaded）。
// ============================================================================

/// <summary>
/// 页面自己往顶部胶囊提供的子标签。设置中心优先使用页面提供的标签
/// （例如插件页的「已安装 / 插件市场」），而不是把整个页面当成唯一子项。
/// </summary>
public interface ISettingsPageTabs
{
    IReadOnlyList<SettingsPageTab> Tabs { get; }
    string CurrentTabId { get; }
    void SelectTab(string id);
}

public sealed record SettingsPageTab(string Id, string Label);

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
            // 纯色 / 图片 / 云母 / 亚克力四选一（互斥，绑 SettingsBackground）。
            // 老系统没有半透明背板时**不再整条藏掉**：纯色和图片仍然要能选，
            // 只在旁边挂一条"当前系统不支持半透明背板"的说明。
            MicaUnsupportedRow.Visibility = vm.SupportsBackdrop ? Visibility.Collapsed : Visibility.Visible;

            BuildThemeSwatches();
            BuildBackgroundSwatches();
            SyncBackgroundRows(vm);
            BuildBoardSwatches();
            SyncBoardRows(vm);

            vm.PropertyChanged += OnAppearanceChanged;
            Unloaded += (_, _) => vm.PropertyChanged -= OnAppearanceChanged;
        };
    }

    SettingsViewModel? Vm => DataContext as SettingsViewModel;

    void OnAppearanceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Vm is not { } vm) return;

        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.SettingsBackground):
                SyncBackgroundRows(vm);
                break;
            case nameof(SettingsViewModel.SettingsBackgroundColor):
                BuildBackgroundSwatches();
                break;
            case nameof(SettingsViewModel.SettingsBackgroundImage):
                SyncBackgroundRows(vm);
                break;
            case nameof(SettingsViewModel.BoardBackground):
                SyncBoardRows(vm);
                break;
            case nameof(SettingsViewModel.BoardBackgroundImage):
                SyncBoardRows(vm);
                break;
            case nameof(SettingsViewModel.ThemeMode):
                // 深浅一变，纯色预设跟着换一套（浅色主题里给深色底会读不清字）
                BuildBackgroundSwatches();
                BuildBoardSwatches();
                break;
        }
    }

    // ————————————— 窗口背景 —————————————

    /// <summary>纯色预设：按当前深浅给两套，避免"浅色主题配深底"看不清。</summary>
    static readonly (string Hex, string Name)[] LightBackgrounds =
    {
        ("#F3F3F3", "纸白"), ("#E9EEF6", "雾蓝"), ("#EAF3EC", "浅苔"),
        ("#F7F0E4", "暖砂"), ("#F2EAF5", "薄紫"), ("#E8F2F4", "淡青"),
    };

    static readonly (string Hex, string Name)[] DarkBackgrounds =
    {
        ("#1A1A1C", "石墨"), ("#191C22", "深海"), ("#181F1B", "墨绿"),
        ("#221D18", "深棕"), ("#1F1A24", "夜紫"), ("#101418", "纯黑"),
    };

    void SyncBackgroundRows(SettingsViewModel vm)
    {
        var mode = vm.SettingsBackground;
        BgColorRow.Visibility = mode == "solid" ? Visibility.Visible : Visibility.Collapsed;
        BgImageRow.Visibility = mode == "image" ? Visibility.Visible : Visibility.Collapsed;

        var path = vm.SettingsBackgroundImage;
        BgImageText.Text = string.IsNullOrWhiteSpace(path) ? "未选择图片" : System.IO.Path.GetFileName(path);
    }

    void BuildBackgroundSwatches()
    {
        var dark = ThemeService.IsDark;
        BuildColorStrip(BgColorStrip, dark ? DarkBackgrounds : LightBackgrounds,
            Vm?.SettingsBackgroundColor ?? "", hex =>
            {
                if (Vm is not { } vm) return;
                vm.SettingsBackgroundColor = hex;
                vm.SettingsBackground = "solid";     // 点颜色即切纯色模式，少一步
            });
    }

    // ————————————— 作业区域 —————————————

    /// <summary>作业区色板：浅色/深色各一组，深底会自动把文字换浅。</summary>
    static readonly (string Hex, string Name)[] BoardBackgrounds =
    {
        ("#FFFFFF", "白"), ("#F4F5F8", "浅灰"), ("#EDF3FF", "淡蓝"), ("#EDF7EE", "淡绿"),
        ("#FFF6E9", "暖黄"), ("#2B2B31", "深灰"), ("#1F2430", "深蓝"), ("#241F2E", "深紫"),
    };

    void SyncBoardRows(SettingsViewModel vm)
    {
        var mode = vm.BoardBackground;
        BoardColorRow.Visibility = mode == "color" ? Visibility.Visible : Visibility.Collapsed;
        BoardImageRow.Visibility = mode == "image" ? Visibility.Visible : Visibility.Collapsed;

        var path = vm.BoardBackgroundImage;
        BoardImageText.Text = string.IsNullOrWhiteSpace(path) ? "未选择图片" : System.IO.Path.GetFileName(path);
    }

    void BuildBoardSwatches()
    {
        BuildColorStrip(BoardColorStrip, BoardBackgrounds,
            Vm?.BoardBackgroundColor ?? "", hex =>
            {
                if (Vm is not { } vm) return;
                vm.BoardBackgroundColor = hex;
                vm.BoardBackground = "color";
            });
    }

    void OnPickBoardImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择「班级日常」窗口背景图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        if (Vm is { } vm)
        {
            vm.BoardBackgroundImage = dialog.FileName;
            vm.BoardBackground = "image";
            SyncBoardRows(vm);
        }
    }

    void OnClearBoardImage(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.BoardBackgroundImage = "";
        SyncBoardRows(vm);
    }

    // ————————————— 通用色块条 —————————————

    /// <summary>
    /// 一排圆形色块：选中项带强调色环与对勾。两个页面（窗口背景 / 作业区）共用，
    /// 只是传入的预设和"点下去写哪个设置"不同。
    /// </summary>
    void BuildColorStrip(WrapPanel target, (string Hex, string Name)[] presets, string current, Action<string> picked)
    {
        target.Children.Clear();

        var dark = ThemeService.IsDark;
        var now = (current ?? "").Trim().ToUpperInvariant();

        foreach (var (hex, name) in presets)
        {
            var selected = string.Equals(hex, now, StringComparison.OrdinalIgnoreCase);

            var dot = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.FindResource("ControlStrokeColorDefaultBrush"),
            };

            var check = new TextBlock
            {
                Text = "✓",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = selected ? Visibility.Visible : Visibility.Collapsed,
                Foreground = dark ? Brushes.White : Brushes.Black,
            };

            var ring = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                BorderThickness = new Thickness(2),
                BorderBrush = (Brush)Application.Current.FindResource("AccentFillColorDefaultBrush"),
                Visibility = selected ? Visibility.Visible : Visibility.Collapsed,
            };

            var inner = new Grid { Width = 26, Height = 26 };
            inner.Children.Add(dot);
            inner.Children.Add(check);

            var halo = new Grid { Width = 34, Height = 34 };
            halo.Children.Add(ring);
            halo.Children.Add(new Border
            {
                Width = 26,
                Height = 26,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = inner,
            });

            var button = new Button
            {
                Style = (Style)Application.Current.FindResource("SubtleButton"),
                Width = 34,
                Height = 34,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Content = halo,
                ToolTip = name,
            };
            button.Click += (_, _) =>
            {
                picked(hex);
                BuildBackgroundSwatches();
                BuildBoardSwatches();
            };

            target.Children.Add(button);
        }

        // 最后一颗去掉右边距，整排右缘和行内其它控件对齐
        if (target.Children.Count > 0 && target.Children[target.Children.Count - 1] is FrameworkElement tail)
            tail.Margin = new Thickness(0);
    }

    void OnPickBackgroundImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        if (Vm is { } vm)
        {
            vm.SettingsBackgroundImage = dialog.FileName;
            vm.SettingsBackground = "image";
            SyncBackgroundRows(vm);
        }
    }

    void OnClearBackgroundImage(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.SettingsBackgroundImage = "";
        SyncBackgroundRows(vm);
    }

    /// <summary>
    /// 主题色色块。用代码建视觉元素而不是 XAML 模板 + ItemsControl：
    /// 每颗色球要叠"径向渐变球体 + 左上镜面高光 + 选中环 + 对勾"四层，
    /// 写成模板要三套 DataTemplate 加转换器，还是这里直白。
    /// </summary>
    void BuildThemeSwatches()
    {
        ThemeStrip.Children.Clear();

        var current = ColorThemes.Get(Vm?.ColorTheme).Key;
        foreach (var theme in ColorThemes.All)
            ThemeStrip.Children.Add(CreateSwatch(theme, theme.Key == current));

        // 最后一颗不带右边距：否则整排色球比下面选择的右缘缩进 10px，看着没对齐
        if (ThemeStrip.Children.Count > 0 && ThemeStrip.Children[ThemeStrip.Children.Count - 1] is FrameworkElement tail2)
            tail2.Margin = new Thickness(0);

        // 「当前用哪套」放左侧说明：色球下面再挂一排小字会让这一行比别的高一截
        var hit = ColorThemes.Get(current);
        ThemeRow.Hint = $"当前：{hit.Name} · {hit.Hint}";
    }

    FrameworkElement CreateSwatch(ColorTheme theme, bool selected)
    {
        var orb = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = BuildOrb(theme.Seed),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = theme.Seed,
                Opacity = 0.38,
                BlurRadius = 12,
                ShadowDepth = 2,
                Direction = 270,
            },
        };

        // 左上镜面高光：让色球像玻璃珠，而不是一块纯色圆
        var specular = new System.Windows.Shapes.Ellipse
        {
            Width = 14,
            Height = 9,
            Margin = new Thickness(5, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Fill = new LinearGradientBrush(
                Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
                90),
            IsHitTestVisible = false,
        };

        var check = new TextBlock
        {
            Text = "✓",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = selected ? Visibility.Visible : Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        var inner = new Grid { Width = 34, Height = 34 };
        inner.Children.Add(orb);
        inner.Children.Add(specular);
        inner.Children.Add(check);

        var ring = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(21),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(theme.Seed),
            Visibility = selected ? Visibility.Visible : Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        // 42 而不是 44：行内上下各 13 的 padding，44 会把这一行撑到 70（别的行是 68）
        var halo = new Grid { Width = 42, Height = 42 };
        halo.Children.Add(ring);
        halo.Children.Add(new Border
        {
            Width = 34,
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = inner,
        });

        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("SubtleButton"),
            Width = 42,
            Height = 42,
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = halo,
            ToolTip = $"{theme.Name} · {theme.Hint}",
        };
        button.Click += (_, _) =>
        {
            if (Vm is { } vm) vm.ColorTheme = theme.Key;
            BuildThemeSwatches();
        };

        // 名字只在悬停提示里给：球下面再挂一行小字会把这一行撑高，和别的行对不齐
        return button;
    }

    /// <summary>玻璃色球：左上提亮、主体种子色、右下压暗。</summary>
    static Brush BuildOrb(Color seed)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.30, 0.24),
            Center = new Point(0.34, 0.28),
            RadiusX = 0.95,
            RadiusY = 0.95,
        };
        brush.GradientStops.Add(new GradientStop(ColorThemes.Lighten(seed, 0.42), 0));
        brush.GradientStops.Add(new GradientStop(seed, 0.52));
        brush.GradientStops.Add(new GradientStop(Darken(seed, 0.28), 1));
        brush.Freeze();
        return brush;
    }

    static Color Darken(Color c, double t) => Color.FromRgb(
        (byte)(c.R * (1 - t)),
        (byte)(c.G * (1 - t)),
        (byte)(c.B * (1 - t)));
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
    // 课表编辑 / 假期与调休 / 值日生名单 / 临时调课 / 状态测试 原来是这个页面里的列表入口，
    // 现在都提升成「教学」大项下的平级标签（见 SettingsWindow 的 _groups），
    // 所以这里不再需要任何点击接线。
    public SchedulePage() => InitializeComponent();
}

public partial class HomeworkPage : UserControl
{
    bool _built;
    bool _hooked;
    bool _historyLoading;
    SettingsViewModel? _vm;

    // —— 提交时段自检的临时状态 ——
    System.Windows.Threading.DispatcherTimer? _dueTestTimer;
    List<HomeworkItem>? _dueTestBackup;     // 测试前的作业（深拷贝）
    string? _dueTestScheduleBackup;         // 测试前的 schedule.json（原样 JSON）
    string? _dueTestScheduleBackupFile;     // 同一份 JSON 的磁盘备份（崩溃后可恢复）
    string? _dueTestHomeworkBackupFile;     // 测试前的 homework.json 磁盘备份
    string? _dueTestSettingsBackupFile;     // 测试前的 settings.json 磁盘备份
    int _dueTestRemaining;

    public HomeworkPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is not SettingsViewModel vm) return;

            if (!_built)
            {
                _built = true;
                // 内嵌模式：改动即时生效，没有取消 / 确认按钮
                EditorHost.Content = new HomeworkEditor(vm, embedded: true);
                OpenFolderButton.Click += (_, _) => DialogLauncher.OpenFolder(vm.ConfigPath);
                DueTestButton.Click += (_, _) => StartDueTest();
                DueTestAbortButton.Click += (_, _) => EndDueTest(aborted: true);
            }

            Hook(vm);
            SyncHistoryDays();
        };
        Unloaded += (_, _) =>
        {
            Unhook();
            // 页面被卸载（关设置窗口等）时测试还没结束：先把数据恢复，别把测试作业留在文件里
            if (_dueTestBackup is not null) EndDueTest(aborted: true, toast: false);
        };
    }

    void Hook(SettingsViewModel vm)
    {
        if (_hooked) return;
        _hooked = true;
        _vm = vm;
        // 「恢复本页默认」会广播 null；之外只关心保留天数这一项
        vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    void Unhook()
    {
        if (!_hooked || _vm is null) return;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _hooked = false;
    }

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(SettingsViewModel.HomeworkHistoryDays))
            SyncHistoryDays();
    }

    /// <summary>
    /// 到点提醒 + 提交时段 + 灵动岛自检：
    ///
    /// 1. **先备份**测试前的作业与 schedule.json；
    /// 2. 往 schedule.json 里真写 5 节测试课（每节 10 秒上课 + 10 秒课间），课程名故意重名
    ///    （自习、自习、数学自习、自习、自习），由引擎统一编号成 自习(1)/自习(2)/数学自习…
    /// 3. 往 homework.json 里写每节课对应的作业：内容 = 这节课的名字，提交时间 = 下课时间，
    ///    行尾显示「时间 + 自习(1)」；
    /// 4. 让每秒的看门狗/课表引擎自己跑，灵动岛与作业区都按真实数据变化；
    /// 5. 约 100 秒后（或点「中断测试」）把作业与课表原样写回。
    ///
    /// <para>★ 不直接调通知、也不直接告诉灵动岛"你在上课"：全部走数据文件 → 引擎读取。</para>
    /// </summary>
    void StartDueTest()
    {
        if (_dueTestBackup is not null) return;        // 已经在测试
        if (_vm is not SettingsViewModel vm) return;

        var now = DateTime.Now;
        var today = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        static string Stamp(DateTime t) =>
            t.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        // ★ 课表 / 设置 / 作业都先备份，测试结束原样恢复。
        //   不往 homework.json 里写任何「测试」标记 —— 这样看到的行为完全由真实数据决定。
        _dueTestScheduleBackup = vm.Ctx.Schedule.ExportJson();
        _dueTestScheduleBackupFile = WriteDueTestBackup(vm.ConfigPath, "schedule.test-backup.json", _dueTestScheduleBackup);
        _dueTestSettingsBackupFile = CopyDueTestBackup(vm.ConfigPath, "settings.json", "settings.test-backup.json");

        // ★ 作业备份必须在改课表之前：写测试课表会触发"按节次同步"，
        //   可能把原作业的提交时间改成测试课表的时间；先抓原始快照才不会被带跑。
        var backup = vm.Ctx.Homework.Items.Select(CloneHomework).ToList();
        _dueTestBackup = backup;
        _dueTestHomeworkBackupFile = WriteDueTestBackup(vm.ConfigPath, "homework.test-backup.json",
            JsonSerializer.Serialize(backup, AtomicJson.Options));

        var (periodItems, totalSeconds) = BuildDueTestData(now, vm.Ctx.Schedule);

        var tests = new List<HomeworkItem>(periodItems)
        {
            // 对照组：不交 → 正常段，应在「已过提交时间」之上
            new()
            {
                Subject = "音乐",
                Content = "音乐：练唱《歌唱祖国》，下节课检查",
                Date = today,
                DueKind = HomeworkDueKinds.None,
            },
            // 对照组：补交 → 正常作业之后，但仍在「已过提交时间」之上
            new()
            {
                Subject = "美术",
                Content = "美术：完成静物素描一张",
                Date = today,
                DueKind = HomeworkDueKinds.Late,
            },
            // 对照组：自定义文本 → 10 分钟后，仍属正常段
            new()
            {
                Subject = "信息",
                Content = "信息：完成 Python 循环练习 1-5 题",
                Date = today,
                Due = Stamp(now.AddMinutes(10)),
                DueKind = HomeworkDueKinds.Custom,
                DueLabel = $"自定义文本 · {HomeworkDue.Describe(now.AddMinutes(10), now)}",
            },
        };

        var withTests = new List<HomeworkItem>(backup);
        withTests.AddRange(tests);
        vm.Ctx.Homework.ReplaceAll(withTests);
        RefreshEditor();

        _dueTestRemaining = totalSeconds;
        _dueTestTimer?.Stop();
        _dueTestTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _dueTestTimer.Tick += OnDueTestTick;
        _dueTestTimer.Start();

        SetDueTestRunning(true);
        SetNavigationLocked(true);

        DialogLauncher.Toast(this, InfoBarSeverity.Information, "测试开始",
            $"课表已写入 4 节测试课 · 约 {totalSeconds} 秒后自动结束");
    }

    void OnDueTestTick(object? sender, EventArgs e)
    {
        if (--_dueTestRemaining > 0) return;
        EndDueTest(aborted: false);
    }

    /// <summary>结束测试：把开始时的备份原样写回去（自然结束 / 中断 / 页面被卸载都走这里）。</summary>
    void EndDueTest(bool aborted, bool toast = true)
    {
        _dueTestTimer?.Stop();
        if (_dueTestTimer is not null) _dueTestTimer.Tick -= OnDueTestTick;
        _dueTestTimer = null;

        var backup = _dueTestBackup;
        _dueTestBackup = null;
        var scheduleBackup = _dueTestScheduleBackup;
        _dueTestScheduleBackup = null;
        var vm = _vm;

        if (backup is not null && vm is not null)
        {
            // 测试期间原作业万一正好到点，due_fired 可能已被写进文件；
            // 回滚时按「内容 + 时间」把这层标记合并回去，避免恢复后重复提醒。
            var fired = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in vm.Ctx.Homework.Items)
            {
                if (string.IsNullOrEmpty(item.DueFired)) continue;
                fired[HomeworkDue.KeyOf(item)] = item.DueFired;
            }
            // 只把「备份里本来就有」的条目按 key 合并回来；测试期间新写的作业不会进备份。
            foreach (var item in backup)
                if (fired.TryGetValue(HomeworkDue.KeyOf(item), out var stamp))
                    item.DueFired = stamp;

            vm.Ctx.Homework.ReplaceAll(backup);
            RefreshEditor();
            DeleteDueTestBackup(ref _dueTestHomeworkBackupFile);
        }

        // 课表也原样写回：临时课表 / 偏移 / 周末配置 / 假期都是这里恢复的
        if (scheduleBackup is not null && vm is not null)
        {
            if (vm.Ctx.Schedule.ImportJson(scheduleBackup))
            {
                Logger.Current?.Info("[测试] 已恢复测试前的课表");
                DeleteDueTestBackup(ref _dueTestScheduleBackupFile);
            }
            else
            {
                // 恢复失败就把磁盘备份留着：下次启动会再试一次
                Logger.Current?.Warn("[测试] 恢复临时课表失败，已保留备份供下次启动恢复");
            }
        }

        // 设置也原样写回（测试期间改过的话恢复成测试前；没改就只删备份）
        if (vm is not null) RestoreDueTestSettings(vm);

        SetDueTestRunning(false);
        SetNavigationLocked(false);

        if (toast)
            DialogLauncher.Toast(this, InfoBarSeverity.Success,
                aborted ? "测试已中断" : "测试结束",
                "已恢复测试前的作业、设置与课表。");
    }

    void SetDueTestRunning(bool running)
    {
        DueTestButton.Content = running ? "测试中" : "开始测试";
        DueTestButton.IsEnabled = !running;
        DueTestAbortButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>测试期间锁住设置窗口的导航，避免切页丢掉"测试中"状态、把测试数据留在文件里。</summary>
    void SetNavigationLocked(bool locked)
    {
        if (Window.GetWindow(this) is SettingsWindow window) window.NavigationLocked = locked;
    }

    /// <summary>
    /// 测试开始/结束时 homework.json 是被外部整体替换的，内嵌编辑器不会自己感知；
    /// 手动重建一次，免得它拿着一批过期的对象、下一次编辑把测试数据/恢复结果又盖回去。
    /// </summary>
    void RefreshEditor()
    {
        if (EditorHost.Content is HomeworkEditor editor) editor.Rebuild();
    }

    /// <summary>把一份 JSON 写到数据目录做磁盘备份；失败只记日志，不影响测试继续跑。</summary>
    static string? WriteDueTestBackup(string folder, string name, string content)
    {
        try
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
            return path;
        }
        catch (Exception ex)
        {
            Logger.Current?.Warn($"[测试] 写备份 {name} 失败：{ex.Message}");
            return null;
        }
    }

    static void DeleteDueTestBackup(ref string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch { }
        path = null;
    }

    /// <summary>把数据目录里的文件复制一份做磁盘备份。</summary>
    static string? CopyDueTestBackup(string folder, string sourceName, string backupName)
    {
        try
        {
            var source = Path.Combine(folder, sourceName);
            if (!File.Exists(source)) return null;
            var target = Path.Combine(folder, backupName);
            File.Copy(source, target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            Logger.Current?.Warn($"[测试] 备份 {sourceName} 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>测试结束时恢复 settings.json；内容没变就只删备份，避免无谓地重载设置。</summary>
    void RestoreDueTestSettings(SettingsViewModel vm)
    {
        var backup = _dueTestSettingsBackupFile;
        if (backup is null) return;

        var target = Path.Combine(vm.ConfigPath, "settings.json");
        try
        {
            if (!FilesEqual(target, backup))
            {
                File.Copy(backup, target, overwrite: true);
                vm.Ctx.Settings.Reload();   // 让内存里的设置和文件保持一致，并广播刷新
                Logger.Current?.Info("[测试] 已恢复测试前的设置");
            }
            DeleteDueTestBackup(ref _dueTestSettingsBackupFile);
        }
        catch (Exception ex)
        {
            // 恢复失败就把磁盘备份留着：下次启动会再试一次
            Logger.Current?.Warn("[测试] 恢复设置失败，已保留备份供下次启动恢复：" + ex.Message);
        }
    }

    static bool FilesEqual(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 往 schedule.json 里写入 4 节测试课（每节 10 秒上课 + 10 秒课间），并生成对应的作业：
    ///   课程名故意重名（自习、自习、数学自习、自习），引擎会编号成
    ///   自习(1)、自习(2)、数学自习、自习(3)；
    ///   第 1/2/3/4 节下课分别有 2/3/5/5 个科目交；
    ///   **同一科目的多条作业错开到不同节次**（语文 3 条分别在第 1/2/3 节下课交），
    ///   用来观察"同一板块里一条到点了、另一条还没到"会不会打架；
    ///   行尾显示「时间 + 这节课的名字」（如「10:00:10 自习(1)」）。
    ///
    /// ★ 全部通过写文件完成：灵动岛照常读 schedule.json 自己算，不是代码里塞一个假状态。
    /// 原始 schedule.json 已整份备份，测试结束时 <see cref="ScheduleManager.ImportJson"/> 原样恢复。
    /// </summary>
    (List<HomeworkItem> Items, int TotalSeconds) BuildDueTestData(DateTime now, ScheduleManager sched)
    {
        // 4 节测试课：每节 10 秒上课 + 10 秒课间
        string[] raw = { "自习", "自习", "数学自习", "自习" };
        var display = ScheduleEngine.DecorateDuplicates(raw);

        // (科目, 作业内容, 第几节下课交)
        var plan = new (string Subject, string Content, int Period)[]
        {
            ("语文", "背诵《岳阳楼记》全文，默写第 3 段", 0),
            ("语文", "完成《同步练习》第 3 单元 P45-P48，并把错题抄到错题本上，明天早读检查", 1),
            ("语文", "预习第 4 课，标出不会读的字词", 2),

            ("数学", "习题 3.2 第 1-8 题，写清步骤", 0),

            ("英语", "背诵 Unit 4 单词表，家长签字", 1),
            ("英语", "完成 Workbook P32-P34 的阅读和完形填空", 2),

            ("物理", "完成《课时练》第 2 章第 3 节，重点看电路图那两道大题，不会的圈出来", 1),

            ("化学", "整理实验室制取氧气的步骤", 2),
            ("化学", "完成练习册 P18-P19", 3),

            ("生物", "画一张细胞结构图，标出各部分的名称", 2),

            ("政治", "背熟第三课知识点", 2),
            ("政治", "完成《同步导学》P22 的材料分析题", 3),

            ("历史", "整理隋唐时期的大事年表，至少写 8 条，每条后面用一句话说明影响", 3),
            ("地理", "完成地图册 P12 的气候类型填图", 3),
            ("体育", "明天带跳绳，课间操后测 1 分钟跳绳", 3),
        };

        static string Clock(DateTime t) =>
            t.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        static string Stamp(DateTime t) =>
            t.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        var today = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var firstStart = now.AddSeconds(-2);       // 第一节课已经开始，岛立刻是"上课中"
        var starts = new DateTime[raw.Length];
        var table = new Timetable();

        for (var i = 0; i < raw.Length; i++)
        {
            starts[i] = firstStart.AddSeconds(i * 20);
            table.Periods.Add(new Period($"第{i + 1}节", Clock(starts[i]), Clock(starts[i].AddSeconds(10))));
            // 七天都排上，周末列 / 调休列也能取到课
            for (var day = 0; day < 7; day++) table.SetCourse(i, day, raw[i]);
        }

        const string name = "【测试】提交时段";
        sched.File.Timetables.Remove(name);
        sched.File.Timetables[name] = table;
        sched.File.Active = name;

        // 周末 / 假期 / 临时调课都会走另一套取课规则，测试期间先绕开（备份里原样恢复）
        sched.File.Weekend ??= new WeekendConfig();
        sched.File.Weekend.MorningSplit = "23:59";
        sched.File.Weekend.AfternoonPeriods.Clear();
        sched.File.Weekend.Courses.Clear();
        sched.File.Holidays.Clear();
        sched.File.TempAdjust = new TempAdjust();

        sched.SetAdvance(0);
        sched.SetOffset(0);
        sched.Save();      // 写 schedule.json

        var items = new List<HomeworkItem>(plan.Length);
        foreach (var (subject, content, period) in plan)
        {
            var due = starts[period].AddSeconds(10);
            // 提交时段 = [本节下课, 下一节上课)；最后一节没有下一节，补 10 秒
            var dueEnd = period + 1 < raw.Length ? starts[period + 1] : due.AddSeconds(10);

            items.Add(new HomeworkItem
            {
                Subject = subject,          // 同一科目可能有多条，错开到不同节次
                Content = content,          // 有长有短，方便看换行 / 多行
                Date = today,
                Due = Stamp(due),
                DueEnd = Stamp(dueEnd),
                DueName = display[period],  // 行尾「时间 + 自习(1)」
                DuePeriod = period,         // 选的是第几节（课表改了会跟着重算）
                DueKind = HomeworkDueKinds.Period,
                DueLabel = $"{display[period]} · {Clock(due)} 下课",
            });
        }

        var lastEnd = starts[^1].AddSeconds(10);
        var total = (int)Math.Ceiling((lastEnd.AddSeconds(10) - now).TotalSeconds) + 2;
        total = Math.Max(20, total);

        var perPeriod = Enumerable.Range(0, raw.Length)
            .Select(i => plan.Count(p => p.Period == i) + " 科");
        Logger.Current?.Info(
            $"[测试] 课表已写盘：{raw.Length} 节，每节 10 秒上课 + 10 秒课间；" +
            $"课程 {string.Join("、", display)}；" +
            $"作业 {string.Join(" / ", perPeriod)}（共 {items.Count} 条，同科错开）；" +
            $"约 {total} 秒后结束");
        return (items, total);
    }

    static HomeworkItem CloneHomework(HomeworkItem item) => new()
    {
        Subject = item.Subject,
        Content = item.Content,
        Date = item.Date,
        Due = item.Due,
        DueEnd = item.DueEnd,
        DueName = item.DueName,
        DuePeriod = item.DuePeriod,
        DueKind = item.DueKind,
        DueLabel = item.DueLabel,
        DueFired = item.DueFired,
    };

    /// <summary>把设置值回填到下拉框（不触发写回）。</summary>
    void SyncHistoryDays()
    {
        if (DataContext is not SettingsViewModel vm) return;

        _historyLoading = true;
        try
        {
            ComboBoxItem? match = null;
            foreach (var item in HistoryDaysBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is string tag && int.TryParse(tag, out var days) && days == vm.HomeworkHistoryDays)
                {
                    match = item;
                    break;
                }
            }

            if (match is not null) HistoryDaysBox.SelectedItem = match;
            else if (HistoryDaysBox.SelectedItem is null && HistoryDaysBox.Items.Count > 1) HistoryDaysBox.SelectedIndex = 1;
        }
        finally
        {
            _historyLoading = false;
        }
    }

    void OnHistoryDaysChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_historyLoading || DataContext is not SettingsViewModel vm) return;
        if (HistoryDaysBox.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            int.TryParse(tag, out var days))
            vm.HomeworkHistoryDays = days;
    }
}

public partial class PluginsPage : UserControl, ISettingsPageTabs
{
    static readonly SettingsPageTab[] PageTabs =
    {
        new("installed", "已安装"),
        new("market", "插件市场"),
    };

    PluginManager? _manager;
    MarketService? _market;
    bool _hooked;
    bool _installing;
    bool _syncing;
    bool _applyingSource;   // 正在把设置里的值写回选择器：此时不触发刷新

    // —— 顶部胶囊的「已安装 / 插件市场」 ——
    public IReadOnlyList<SettingsPageTab> Tabs => PageTabs;

    public string CurrentTabId =>
        string.Equals(PluginTabs.SelectedValue, "market", StringComparison.Ordinal) ? "market" : "installed";

    /// <summary>
    /// 顶部胶囊切换子标签：选择器本身是隐藏的（UI 已搬到顶栏），
    /// 所以这里同步值后手动调用一次 SwitchTab —— 代码设值不会触发 SelectedChanged。
    /// </summary>
    public void SelectTab(string id)
    {
        var value = string.Equals(id, "market", StringComparison.Ordinal) ? "market" : "installed";
        if (string.Equals(PluginTabs.SelectedValue, value, StringComparison.Ordinal)) return;
        PluginTabs.SelectedValue = value;
        SwitchTab(value);
    }

    /// <summary>市场里比本机新的插件（id → 视图），用来在已安装行上亮「可更新」。</summary>
    readonly Dictionary<string, MarketView> _updates = new(StringComparer.OrdinalIgnoreCase);

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
        // 运行日志已独立成左侧「日志」页，这里只做跳转
        LogLink.Click += (_, _) =>
        {
            if (Window.GetWindow(this) is SettingsWindow window) window.NavigateTo("logs");
        };

        // ── 插件市场 ──
        // 两段标签已经搬到设置窗口顶部的胶囊里，切换由 SelectTab 手动调 SwitchTab；
        // 这里不再挂 SelectedChanged，避免隐藏控件和顶部胶囊各触发一次。
        MarketSyncButton.Click += (_, _) => _ = SyncMarketAsync();
        MarketSourceBar.SelectedChanged += OnMarketSourceChanged;
        MarketSearch.TextChanged += (_, _) => RenderMarket();

        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    void Attach()
    {
        if (_hooked || DataContext is not SettingsViewModel vm) return;
        _hooked = true;
        _manager = vm.Ctx.Plugins;
        _market = vm.Ctx.Market;
        _market.Changed += OnMarketChanged;

        // 把设置里的更新源回填到选择器（不触发刷新）
        _applyingSource = true;
        MarketSourceBar.SelectedValue = vm.MarketSource;
        _applyingSource = false;

        Refresh();

        // 静默拉一次市场：缓存未过期不会发请求。拿到「可更新」才能在这里提示，
        // 不然用户只会看到"怎么还是旧版、怎么没有设置按钮"
        _ = RefreshMarketAsync(force: false);

        // 页面被缓存，切回来时若正停在整个市场页，顺手补一次（有缓存就不发请求）
        if (string.Equals(PluginTabs.SelectedValue, "market", StringComparison.Ordinal))
            _ = RefreshMarketAsync(force: false);
    }

    void Detach()
    {
        if (_market is not null) _market.Changed -= OnMarketChanged;
        _hooked = false;
    }

    // ————————————————————— 刷新 —————————————————————

    void Refresh()
    {
        if (_manager is null) return;

        var plugins = _manager.Plugins;
        var loaded = plugins.Count(p => p.Loaded);
        var failed = plugins.Count(p => p.Error is not null);

        // 市场里比本机新的那些（纯内存，不联网）
        _updates.Clear();
        if (_market is not null)
            foreach (var view in _market.Views)
                if (view.State == MarketState.Update) _updates[view.Entry.Id] = view;

        NoPluginNotice.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginHostPanel.Visibility = plugins.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        UpdateNotice.Visibility = plugins.Count > 0 && _updates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_updates.Count > 0)
        {
            var names = string.Join("、", _updates.Values.Select(v => v.Entry.Name).Take(3));
            var more = _updates.Count > 3 ? $" 等 {_updates.Count} 个" : "";
            UpdateNotice.Message = $"{names}{more}有新版本，可在「插件市场」里更新（新版可能带来新的设置项）。";
        }

        PluginHostPanel.Children.Clear();
        foreach (var info in plugins) PluginHostPanel.Children.Add(BuildRow(info));

        OpenFolderLink.Hint = _manager.PluginsRoot;
        SummaryText.Text = $"共 {plugins.Count} 个插件 · 已加载 {loaded} 个" +
                           (failed > 0 ? $" · 失败 {failed} 个" : "") +
                           $" · 契约 v{PluginManager.ApiVersion} · 插件目录：{_manager.PluginsRoot}";
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
        if (_updates.TryGetValue(info.Id, out var pending))
            head.Children.Add(MakeBadge($"可更新 v{pending.Entry.Version}", BadgeVariant.Accent));
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
        // 插件自己算的一句话（如「2029 届 · 还有 218 天」），比描述更有用
        if (info.Loaded && info.HasSettings &&
            _manager?.SettingsSummary(info.Id) is { Length: > 0 } settingsSummary)
            meta.Add(settingsSummary);
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

        // 市场里有新版：直接给一个「更新」，省得用户切到市场再找一遍
        if (_updates.TryGetValue(info.Id, out var pendingUpdate))
        {
            var updateButton = new Button
            {
                Content = "更新",
                Style = (Style)FindResource("AccentButton"),
                Padding = new Thickness(12, 3, 12, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = !_installing,
            };
            updateButton.Click += (_, _) => _ = InstallMarketAsync(pendingUpdate);
            actions.Children.Add(updateButton);
        }

        // 只有插件主动声明过设置页，这里才有「设置」—— 空设置页比没有更糟
        if (info.Loaded && info.HasSettings)
            actions.Children.Add(MakeButton("设置", () => OpenPluginSettings(info)));

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

    /// <summary>打开插件自己声明的设置页（声明式渲染，插件不碰任何 WPF 类型）。</summary>
    void OpenPluginSettings(PluginInfo info)
    {
        if (_manager is null || DataContext is not SettingsViewModel vm) return;

        var spec = _manager.SettingsPageOf(info.Id);
        if (spec is null)
        {
            DialogLauncher.Toast(this, InfoBarSeverity.Warning, "没有设置页",
                $"「{info.Name}」没有向宿主要求设置页。");
            return;
        }

        DialogLauncher.Show(this, new PluginSettingsPanel(vm, _manager, info, spec));
    }

    // =====================================================================
    //  插件市场
    //  -------------------------------------------------------------------
    //  页面只读 MarketService 的 Status / Views，不自己发请求：
    //  拉索引、下载、哈希校验、调用 PluginInstaller 全在服务层。
    // =====================================================================

    void SwitchTab(string value)
    {
        var market = string.Equals(value, "market", StringComparison.Ordinal);
        InstalledPanel.Visibility = market ? Visibility.Collapsed : Visibility.Visible;
        MarketPanel.Visibility = market ? Visibility.Visible : Visibility.Collapsed;

        // 顶部胶囊左右滑动的同步动作：新面板从对应方向轻轻滑入（不淡化）
        var incoming = market ? (FrameworkElement)MarketPanel : InstalledPanel;
        var shift = new TranslateTransform(market ? 26 : -26, 0);
        incoming.RenderTransform = shift;
        var slide = Anim.Slide(0, 180);
        if (slide is not null) shift.BeginAnimation(TranslateTransform.XProperty, slide);
        else incoming.RenderTransform = Transform.Identity;

        if (market) _ = RefreshMarketAsync(force: false);
        else Refresh();
    }

    async Task<bool> RefreshMarketAsync(bool force)
    {
        var market = _market;
        if (market is null) return false;
        return await market.RefreshAsync(force);
    }

    /// <summary>
    /// 「立即同步」：绕过 10 分钟缓存强制拉一次索引，并把结果直接告诉用户
    /// （成功给插件数与来源，失败给每个源各自的原因 —— 与自动刷新不同，点了按钮就一定要有回音）。
    /// </summary>
    async Task SyncMarketAsync()
    {
        var market = _market;
        if (market is null || _syncing) return;

        _syncing = true;
        MarketSyncButton.IsEnabled = false;
        MarketSyncButton.Content = "同步中…";

        try
        {
            var ok = await market.RefreshAsync(force: true);
            var status = market.Status;

            if (!ok && status.Stage == MarketStage.Loading)
            {
                DialogLauncher.Toast(this, InfoBarSeverity.Information, "正在同步",
                    "上一次同步还没结束，稍等一下再看。");
                return;
            }

            DialogLauncher.Toast(this,
                ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                ok ? "市场已同步" : "同步失败",
                ok
                    ? $"{market.Views.Count} 个插件 · {(string.IsNullOrWhiteSpace(status.FreshnessText) ? "刚刚" : status.FreshnessText)}"
                    : (string.IsNullOrWhiteSpace(status.Message) ? "两个源都没拉到，检查网络后再试。" : status.Message));

            RenderMarket();
            Refresh();   // 让已安装行的「可更新」徽标跟上
        }
        finally
        {
            _syncing = false;
            MarketSyncButton.Content = "立即同步";
            MarketSyncButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 切换插件市场的更新源：写进设置并立刻重拉一次索引。
    /// 缓存里记着上次用的是哪个源，换源后不重拉的话页面会继续显示旧源的数据。
    /// </summary>
    void OnMarketSourceChanged(string value)
    {
        if (_applyingSource) return;
        if (DataContext is not SettingsViewModel vm) return;
        if (string.Equals(vm.MarketSource, value, StringComparison.OrdinalIgnoreCase)) return;

        vm.MarketSource = value;

        var label = value switch
        {
            "github" => "只走 GitHub",
            "gitee" => "只走 Gitee",
            _ => "自动（GitHub 优先，失败换 Gitee）",
        };
        DialogLauncher.Toast(this, InfoBarSeverity.Information, "已切换更新源", label);

        _ = RefreshMarketAsync(force: true);
    }

    void OnMarketChanged() => Dispatcher.BeginInvoke(new Action(() =>
    {
        RenderMarket();
        // 市场数据到位后，已安装列表的「可更新」徽标要跟着出来
        if (_market?.Status.Stage == MarketStage.Ready) Refresh();
    }));

    void RenderMarket()
    {
        var market = _market;
        if (market is null) return;

        var status = market.Status;
        MarketBar.Severity = status.Stage switch
        {
            MarketStage.Failed => status.FromCache ? InfoBarSeverity.Warning : InfoBarSeverity.Error,
            MarketStage.Loading => InfoBarSeverity.Information,
            _ => status.Warnings.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
        };
        MarketBar.Title = status.Stage == MarketStage.Failed && status.FromCache
            ? "无法连接市场（显示缓存）"
            : status.Stage == MarketStage.Failed ? "无法连接插件市场" : "插件市场";
        MarketBar.Message = BuildMarketMessage(status);

        var keyword = (MarketSearch.Text ?? "").Trim();
        var views = market.Views.Where(v => Matches(v, keyword)).ToList();

        MarketHost.Children.Clear();
        foreach (var view in views) MarketHost.Children.Add(BuildMarketRow(view));
        MarketEmpty.Visibility = views.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MarketEmpty.Text = market.Views.Count == 0 ? "市场里还没有插件" : "没有匹配的插件";

        MarketSummary.Text = market.Views.Count == 0
            ? (status.FetchedAt is null ? "" : "数据：" + status.FreshnessText)
            : $"共 {market.Views.Count} 个插件" +
              (market.UpdateCount > 0 ? $" · {market.UpdateCount} 个可更新" : "") +
              (status.FetchedAt is null ? "" : $" · 数据：{status.FreshnessText}");
    }

    static string BuildMarketMessage(MarketStatus status) => status.Stage switch
    {
        MarketStage.Loading => string.IsNullOrWhiteSpace(status.Message) ? "正在获取插件市场…" : status.Message!,
        MarketStage.Failed => string.IsNullOrWhiteSpace(status.Message)
            ? "无法连接插件市场，检查网络后点「刷新市场」。"
            : status.Message,
        _ => "安装包会做 sha256 校验，并核对包内的 id / 版本；装完立即生效，不用重启。",
    };

    static bool Matches(MarketView view, string keyword)
    {
        if (keyword.Length == 0) return true;
        var entry = view.Entry;
        return Contains(entry.Name, keyword)
               || Contains(entry.Summary, keyword)
               || Contains(entry.Author, keyword)
               || Contains(entry.Id, keyword)
               || (entry.Tags?.Any(tag => Contains(tag, keyword)) ?? false);
    }

    static bool Contains(string? text, string keyword) =>
        !string.IsNullOrEmpty(text) && text.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);

    FrameworkElement BuildMarketRow(MarketView view)
    {
        var entry = view.Entry;
        var content = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };

        // 第一行：名称 + 版本 + 信任级别 + 已装版本
        var head = new WrapPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = (double)FindResource("FontSizeBody"),
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(MakeBadge("v" + entry.Version, BadgeVariant.Neutral));
        head.Children.Add(entry.TrustOrDefault switch
        {
            "official" => MakeBadge("官方", BadgeVariant.Ok),
            "verified" => MakeBadge("认证", BadgeVariant.Accent),
            _ => MakeBadge("社区", BadgeVariant.Neutral),
        });
        if (view.State == MarketState.Update && !string.IsNullOrWhiteSpace(view.InstalledVersion))
            head.Children.Add(MakeBadge($"已装 v{view.InstalledVersion}", BadgeVariant.Accent));
        if (view.State == MarketState.Revoked) head.Children.Add(MakeBadge("已下架", BadgeVariant.Warn));
        content.Children.Add(head);

        if (!string.IsNullOrWhiteSpace(entry.Summary))
            content.Children.Add(new TextBlock
            {
                Text = entry.Summary,
                Margin = new Thickness(0, 3, 0, 0),
                FontSize = (double)FindResource("FontSizeCaption"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
            });

        // 元信息：作者 · 分类 · 体积 · 上架日期 ·（不兼容时的原因）
        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Author)) meta.Add(entry.Author);
        meta.Add(CategoryName(entry.CategoryOrDefault));
        if (entry.Size > 0) meta.Add($"{entry.Size / 1024.0:0.#} KB");
        if (!string.IsNullOrWhiteSpace(entry.PublishedAt)) meta.Add(entry.PublishedAt);
        if (!string.IsNullOrWhiteSpace(view.Reason)) meta.Add(view.Reason);
        content.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", meta),
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = (double)FindResource("FontSizeCaption"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
        });

        // 右侧操作区
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (view.CanInstall)
        {
            var button = new Button
            {
                Content = view.ActionText,
                Style = (Style)FindResource(view.State == MarketState.Update ? "AccentButton" : "SubtleButton"),
                Padding = new Thickness(12, 3, 12, 4),
                IsEnabled = !_installing,
            };
            button.Click += (_, _) => _ = InstallMarketAsync(view);
            actions.Children.Add(button);
        }
        else
        {
            actions.Children.Add(new TextBlock
            {
                Text = view.ActionText,
                FontSize = (double)FindResource("FontSizeCaption"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
            });
        }

        if (!string.IsNullOrWhiteSpace(entry.Repo)) actions.Children.Add(MakeButton("源码", () => OpenUrl(entry.Repo!)));
        if (!string.IsNullOrWhiteSpace(entry.Homepage)) actions.Children.Add(MakeButton("主页", () => OpenUrl(entry.Homepage!)));

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

    static string CategoryName(string category) => category switch
    {
        "timetable" => "课表",
        "weather" => "天气",
        "reminder" => "提醒",
        "tools" => "工具",
        "media" => "媒体",
        "theme" => "外观",
        "data" => "数据",
        _ => "其它",
    };

    /// <summary>
    /// 装之前的分级确认：降级要问、社区插件要问（插件与主程序同进程，没有沙箱）。
    /// 官方 / 认证插件直接装，别让用户每装一个都点两次。
    /// </summary>
    async Task InstallMarketAsync(MarketView view)
    {
        if (_market is null || _installing) return;
        var entry = view.Entry;

        if (view.State == MarketState.Downgrade)
        {
            DialogLauncher.Confirm(this, "降级安装",
                $"「{entry.Name}」本机是 v{view.InstalledVersion}，市场上是 v{entry.Version}（更低）。确定降级吗？插件自己的数据会保留。",
                () => _ = RunInstallAsync(view), okText: "降级");
            return;
        }

        if (entry.TrustOrDefault == "community")
        {
            var changelog = entry.Changelog is { Count: > 0 }
                ? "\n\n更新内容：" + string.Join("；", entry.Changelog.Take(3))
                : "";
            var hash = entry.Sha256.Length >= 12 ? entry.Sha256[..12] : entry.Sha256;
            DialogLauncher.Confirm(this, "安装社区插件",
                $"「{entry.Name}」v{entry.Version} 由 {entry.Author} 提供。插件以完整权限运行在主程序进程里，请只装你信任的来源。\n\n包已做 sha256 校验：{hash}…{changelog}",
                () => _ = RunInstallAsync(view), okText: "安装");
            return;
        }

        await RunInstallAsync(view);
    }

    async Task RunInstallAsync(MarketView view)
    {
        var market = _market;
        if (market is null) return;

        _installing = true;
        MarketSyncButton.IsEnabled = false;

        // 下载进度就地反馈在状态条上，不弹模态框（包通常只有几十 KB）
        var progress = new Progress<double>(p =>
        {
            MarketBar.Severity = InfoBarSeverity.Information;
            MarketBar.Title = "正在安装";
            MarketBar.Message = $"正在下载「{view.Entry.Name}」… {p * 100:0}%";
        });

        try
        {
            var (ok, message) = await market.InstallAsync(view, progress);
            DialogLauncher.Toast(this,
                ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                ok ? "已安装" : "安装失败",
                message);
            if (ok) Refresh();
        }
        finally
        {
            _installing = false;
            MarketSyncButton.IsEnabled = true;
            RenderMarket();
        }
    }

    static void OpenUrl(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* 打不开链接不影响任何功能 */ }
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
    bool _applyingDev;   // 正在把设置回填到开关：此时不写回设置

    public AboutPage()
    {
        InitializeComponent();

        // ★ 状态订阅只挂一次：页面会被 SettingsWindow 缓存，之前写在 Loaded 里
        //   每次切回关于页都挂一次 Click，连点 5 次会发 5 个请求（见 AGENTS 坑清单）。
        DataContextChanged += (_, _) => Hook();
        Loaded += (_, _) => { Hook(); Render(); };

        // ★ 控件自有事件只在构造里挂一次：Hook 会在 Loaded/Unloaded 循环里反复进出，
        //   放在 Hook 里会随 _hooked 重置而重复订阅（连点一次发多次请求）。
        DeveloperToggle.Checked += (_, _) => RequestDeveloperMode();
        DeveloperToggle.Unchecked += (_, _) => ApplyDeveloperMode(false);

        // 通道切换后要重新检查（同一个源的 stable/beta 是不同的块）
        ChannelBar.SelectedChanged += _ =>
        {
            ChannelHint.Text = _vm?.UpdateChannel == "beta" ? "测试版可能不稳定" : "";
            _vm?.Ctx.CheckForUpdate();
        };

        // ★ 页面卸载（关设置窗口 / 切走）必须退订：Updates 是进程常驻对象，
        //   不退订的话每开一次设置窗口就永久泄漏一棵 AboutPage 可视树，
        //   而且状态回调还会往已不可见的页面 BeginInvoke。
        Unloaded += (_, _) => Unhook();
    }

    void Unhook()
    {
        if (!_hooked) return;
        if (_vm is not null) _vm.Ctx.Updates.StatusChanged -= OnStatusChanged;
        _vm = null;
        _hooked = false;
    }

    void Hook()
    {
        if (_hooked) return;
        if (DataContext is not SettingsViewModel vm) return;
        _vm = vm;
        _hooked = true;

        ChannelHint.Text = vm.UpdateChannel == "beta" ? "测试版可能不稳定" : "";

        vm.Ctx.Updates.StatusChanged += OnStatusChanged;

        // 开发者选项：开关状态回填 + 只有打开时才露出调试入口与插件开发工具
        _applyingDev = true;
        DeveloperToggle.IsChecked = vm.DeveloperMode;
        _applyingDev = false;
        var devVisible = vm.DeveloperMode ? Visibility.Visible : Visibility.Collapsed;
        TestErrorRow.Visibility = devVisible;
        ErrorCodeRow.Visibility = devVisible;
        PluginDeveloperToolRow.Visibility = devVisible;
    }

    void OnStatusChanged(UpdateStatus status)
    {
        // CheckAsync 里 ConfigureAwait(false)，回调可能在后台线程
        Dispatcher.BeginInvoke(() => Render(status));
    }

    void OnCheckUpdateClick(object sender, RoutedEventArgs e) => _vm?.Ctx.CheckForUpdate();

    /// <summary>打开 GitHub 仓库，方便用户点 Star（打不开也不影响任何功能）。</summary>
    void OnOpenStarClick(object sender, RoutedEventArgs e)
    {
        const string url = "https://github.com/LCHXXXX-1/Class-Daily-Land-2";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* 打不开链接不影响功能 */ }
    }

    /// <summary>
    /// 打开开发者选项前先确认一次。
    ///
    /// ★ 顺序很重要：`Checked` 是**开关已经切成"开"之后**才发的，所以这里第一件事是把开关拨回去，
    ///   等用户在确认框里点「开启」再真正打开。否则看起来就是"开关先亮、弹窗后到"，先斩后奏。
    /// </summary>
    void RequestDeveloperMode()
    {
        if (_applyingDev || _vm is null) return;

        // 先还原视觉状态（_applyingDev 挡住这次写回设置）
        SetDeveloperToggle(false);

        // 正常都在设置窗口里；拿不到窗口就直接生效（不至于因为弹不出确认而打不开）
        if (Window.GetWindow(this) is not SettingsWindow window)
        {
            SetDeveloperToggle(true);
            ApplyDeveloperMode(true);
            return;
        }

        window.Confirm(
            "开启开发者选项",
            "你正在开启开发者选项。此模式包含面向开发与调试的入口，其中部分操作可能影响程序的正常运行。" +
            "如果你不清楚这些设置项的作用，请不要随意调整。",
            onOk: () =>
            {
                SetDeveloperToggle(true);
                ApplyDeveloperMode(true);
            },
            okText: "开启",
            cancelText: "取消");
        // 取消不用额外处理：开关刚才已经拨回去了
    }

    /// <summary>拨动开关但不触发"写回设置"（由 _applyingDev 挡住事件里的逻辑）。</summary>
    void SetDeveloperToggle(bool on)
    {
        _applyingDev = true;
        DeveloperToggle.IsChecked = on;
        _applyingDev = false;
    }

    void ApplyDeveloperMode(bool on)
    {
        if (_applyingDev || _vm is null) return;
        _vm.DeveloperMode = on;
        var visibility = on ? Visibility.Visible : Visibility.Collapsed;
        TestErrorRow.Visibility = visibility;
        ErrorCodeRow.Visibility = visibility;
        PluginDeveloperToolRow.Visibility = visibility;
    }

    /// <summary>错误码清单：非模态打开，方便对照着报错窗看。</summary>
    void OnShowErrorCodesClick(object sender, RoutedEventArgs e)
    {
        var window = new ErrorCodeListWindow();
        if (Window.GetWindow(this) is Window owner) window.Owner = owner;
        window.Show();
    }

    /// <summary>
    /// 测试报错：直接**结束主窗口**，模拟最真实的异常退出。
    /// 报信进程立即弹「关键进程异常退出」，下次启动再补报「上次异常退出」—— 两层兜底一次验证。
    /// </summary>
    void OnTestErrorClick(object sender, RoutedEventArgs e) => _vm?.Ctx.DevTestTerminateMain();

    void OnOpenPluginDeveloperToolClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        var result = _vm.Ctx.OpenPluginDeveloperTool(out var message);
        if (result == DeveloperToolOpenResult.Launched)
        {
            DialogLauncher.Toast(this, InfoBarSeverity.Success, "插件开发工具已打开", message);
            return;
        }

        if (result == DeveloperToolOpenResult.Failed)
        {
            DialogLauncher.Toast(this, InfoBarSeverity.Warning, "无法打开插件开发工具", message);
            return;
        }

        // NotInstalled：DeveloperTool 已独立分发，问一句要不要现在下载在线安装器
        DialogLauncher.Confirm(this, "需要安装插件开发工具",
            "未检测到已安装的插件开发工具（DeveloperTool 已从主程序分离，需单独安装）。" +
            "是否下载在线安装器？下载完成后会自动启动安装向导。",
            () =>
            {
                var window = new DeveloperToolSetupWindow();
                if (Window.GetWindow(this) is Window owner) window.Owner = owner;
                window.Show();
            },
            okText: "下载", cancelText: "以后再说");
    }

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

    /// <summary>
    /// 重新安装最新版本：不比较版本号，直接重新下载并替换程序文件。
    /// 程序文件疑似损坏、更新到一半断了时的自救入口；会先确认，因为安装时要关程序。
    /// </summary>
    void OnReinstallClick(object sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;

        DialogLauncher.Confirm(this, "重新安装最新版本",
            "会重新下载最新版并替换程序文件，安装时会自动关闭程序。设置与插件会保留。",
            () => _ = vm.Ctx.ReinstallLatestAsync(),
            okText: "重新安装");
    }

    /// <summary>重启软件：走正常关闭流程（清运行标记、卸插件），再启动新实例。</summary>
    void OnRestartClick(object sender, RoutedEventArgs e)
    {
        var vm = _vm;
        if (vm is null) return;

        DialogLauncher.Confirm(this, "重启软件",
            "关闭并重新启动程序吗？",
            () => vm.Ctx.Restart(),
            okText: "重启");
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
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _loading;

    public DisasterPage()
    {
        InitializeComponent();
        DrillButton.Click += (_, _) => Drill();
        DrillWeatherButton.Click += (_, _) => DrillWeather();
        ClearDrillButton.Click += (_, _) => ClearDrill();

        Loaded += (_, _) => { Init(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += (_, _) =>
        {
            UpdateStatus();
            UpdateLocationSummary();   // 位置由天气页在管，这里只跟着显示
            UpdateDrillPreview();      // 位置一变，"距你多少公里 / 几秒到达"也要跟着变
        };
    }

    void Init()
    {
        if (DataContext is not SettingsViewModel vm) return;
        _loading = true;

        foreach (var item in ThresholdBox.Items.OfType<ComboBoxItem>())
        {
            if (int.TryParse((string?)item.Tag, out var v) && v == vm.DisasterThresholdX10)
            {
                ThresholdBox.SelectedItem = item;
                break;
            }
        }
        if (ThresholdBox.SelectedItem is null) ThresholdBox.SelectedIndex = 1;

        _loading = false;
        InitDrill();
        UpdateStatus();
        UpdateLocationSummary();
    }

    // ————————————————————— 演练 —————————————————————

    const string DrillDefault = "（默认）";

    /// <summary>演练用的震中/震级选择器初始化。默认全选「（默认）」= 往西 120km。</summary>
    void InitDrill()
    {
        _loading = true;

        DrillProvinceBox.Items.Clear();
        DrillProvinceBox.Items.Add(DrillDefault);
        foreach (var p in Cities.Provinces) DrillProvinceBox.Items.Add(p);
        DrillProvinceBox.SelectedIndex = 0;

        FillDrillCities();
        FillDrillDistricts();

        DrillMagBox.SelectedIndex = 3;     // 6.5（默认）
        DrillSignalBox.SelectedIndex = 0;  // 暴雨
        DrillLevelBox.SelectedIndex = 0;   // 橙色

        _loading = false;
        UpdateDrillPreview();
    }

    void OnDrillEpicenterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        _loading = true;
        if (ReferenceEquals(sender, DrillProvinceBox))
        {
            FillDrillCities();
            FillDrillDistricts();
        }
        else if (ReferenceEquals(sender, DrillCityBox))
        {
            FillDrillDistricts();
        }
        _loading = false;

        UpdateDrillPreview();
    }

    void FillDrillCities()
    {
        var province = DrillProvinceBox.SelectedItem?.ToString() ?? DrillDefault;

        DrillCityBox.Items.Clear();
        DrillCityBox.Items.Add(DrillDefault);
        if (province != DrillDefault)
            foreach (var c in Cities.CitiesOf(province)) DrillCityBox.Items.Add(c);
        DrillCityBox.SelectedIndex = 0;
    }

    void FillDrillDistricts()
    {
        var province = DrillProvinceBox.SelectedItem?.ToString() ?? DrillDefault;
        var city = DrillCityBox.SelectedItem?.ToString() ?? DrillDefault;

        DrillDistrictBox.Items.Clear();
        DrillDistrictBox.Items.Add(DrillDefault);
        if (province != DrillDefault && city != DrillDefault)
            foreach (var d in Cities.DistrictsOf(province, city)) DrillDistrictBox.Items.Add(d);
        DrillDistrictBox.SelectedIndex = 0;
    }

    /// <summary>把三个下拉框解析成震中坐标；全选「默认」时返回 null（服务端按往西 120km 处理）。</summary>
    (double? Lat, double? Lon, string Name) ResolveDrillEpicenter()
    {
        var province = DrillProvinceBox.SelectedItem?.ToString() ?? DrillDefault;
        if (province == DrillDefault) return (null, null, "默认（往西约 120km）");

        var city = DrillCityBox.SelectedItem?.ToString() ?? DrillDefault;
        var district = DrillDistrictBox.SelectedItem?.ToString() ?? DrillDefault;

        var coord = Cities.CoordOf(
            province,
            city == DrillDefault ? "" : city,
            district == DrillDefault ? "" : district);

        var name = district != DrillDefault ? district
                 : city != DrillDefault ? city
                 : province;

        return (coord?.Lat, coord?.Lon, name);
    }

    double DrillMagnitude() =>
        DrillMagBox.SelectedItem is ComboBoxItem item &&
        double.TryParse((string?)item.Tag, out var v) ? v : 6.5;

    /// <summary>
    /// 选完震中立刻把"距你多远 / 几秒到达 / 预估烈度"算出来 —— 不用真的注入就能对比不同震中。
    /// 和真实评估走的是同一套 <see cref="QuakeCalc"/>，所以预览和真注入不会有出入。
    /// </summary>
    void UpdateDrillPreview()
    {
        if (DataContext is not SettingsViewModel vm) return;
        if (DrillPreviewText is null) return;

        var d = vm.Ctx.Disaster;
        if (d?.Lat is null || d.Lon is null)
        {
            DrillPreviewText.Text = "你的位置还没有坐标 —— 先在上面「位置」里选好省市";
            return;
        }

        var (lat, lon, name) = ResolveDrillEpicenter();
        var evLat = lat ?? d.Lat.Value + 0.6;
        var evLon = lon ?? d.Lon.Value - 1.1;

        var mag = DrillMagnitude();
        var depth = DrillDepthBox.Value;
        var dist = QuakeCalc.HaversineKm(d.Lat.Value, d.Lon.Value, evLat, evLon);
        var cd = QuakeCalc.CountdownSeconds(depth, dist);
        var inten = QuakeCalc.IntensityAt(mag, dist);
        var level = QuakeCalc.LevelOf(inten);

        DrillPreviewText.Text =
            $"震中 {name} · 距你 {dist:0} km · 横波约 {cd:0} 秒后到达 · " +
            $"预估烈度 {inten:0.#}（{QuakeCalc.LevelWord(level)}）" +
            (inten > d.Threshold ? "" : " · 低于阈值，主岛不会弹红条");
    }

    void OnThresholdChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        if (ThresholdBox.SelectedItem is ComboBoxItem item && int.TryParse((string?)item.Tag, out var v))
            vm.DisasterThresholdX10 = v;
    }

    /// <summary>位置已经搬到「设置 → 天气」，这里只做跳转。</summary>
    void OnGoWeatherLocation(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.Ctx.ShowSettings(AppSettings.PageWeather);
    }

    /// <summary>只读摘要：当前到底用的哪套坐标（自动定位 / 手选省市 / 手填经纬度）。</summary>
    void UpdateLocationSummary()
    {
        if (DataContext is not SettingsViewModel vm) return;
        if (LocationText is null) return;

        var d = vm.Ctx.Disaster;
        if (d is null || d.Lat is null || d.Lon is null)
        {
            LocationText.Text = "未设置 · 去「天气」里选省市";
            return;
        }

        var parts = new[] { d.Province, d.City, d.District }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var name = string.Join(" / ", parts);
        LocationText.Text = $"{(name.Length > 0 ? name : "—")} · {d.Lat:0.##},{d.Lon:0.##}（{d.LocationSource}）";
    }

    void Drill()
    {
        if (DataContext is not SettingsViewModel vm) return;

        var (lat, lon, name) = ResolveDrillEpicenter();
        var result = vm.Ctx.Disaster?.StartDrill(
            DrillMagnitude(),
            DrillDepthBox.Value,
            lat, lon,
            lat is null ? null : name);   // 没选震中就交给服务端按默认处理

        DialogLauncher.Toast(this, InfoBarSeverity.Warning, "地震演练", result ?? "服务未就绪");
        UpdateStatus();
    }

    void DrillWeather()
    {
        if (DataContext is not SettingsViewModel vm) return;

        var signal = (DrillSignalBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "暴雨";
        var level = (DrillLevelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "orange";

        var result = vm.Ctx.Disaster?.StartWeatherDrill(signal, level);
        DialogLauncher.Toast(this, InfoBarSeverity.Information, "气象演练", result ?? "服务未就绪");
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
