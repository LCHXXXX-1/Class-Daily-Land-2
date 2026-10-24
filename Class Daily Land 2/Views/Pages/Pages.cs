using System.Windows;
using System.Windows.Controls;
using ClassDailyLand.App.Controls;
using ClassDailyLand.App.ViewModels;
using ClassDailyLand.App.Views.Dialogs;

namespace ClassDailyLand.App.Views.Pages;

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
            var win11 = vm.IsWindows11;
            MicaRow.Visibility = win11 ? Visibility.Visible : Visibility.Collapsed;
            MicaUnsupportedRow.Visibility = win11 ? Visibility.Collapsed : Visibility.Visible;
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
        TimetableLink.Click += (_, _) => Show(vm => new TimetableDialog(vm));
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
    public PluginsPage() => InitializeComponent();
}

public partial class AdvancedPage : UserControl
{
    public AdvancedPage()
    {
        InitializeComponent();
        OpenConfigLink.Click += (_, _) => Open(vm => vm.ConfigPath);
        OpenPluginsLink.Click += (_, _) => Open(vm => System.IO.Path.Combine(vm.ConfigPath, "plugins"));
        InstallLocalLink.Click += (_, _) => DialogLauncher.Toast(
            this, InfoBarSeverity.Information, "安装本地插件", "插件宿主在阶段 3 接入，届时这里会走 .cblplugin 安装流程。");
        ExportScheduleLink.Click += (_, _) => Export();
        ImportScheduleLink.Click += (_, _) => Import();
        ResetLink.Click += (_, _) => Reset();
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
        if (System.Windows.MessageBox.Show(Window.GetWindow(this),
                "导入会覆盖当前全部课表（含假期与调休），确定继续吗？", "导入课表",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            var ok = vm.Ctx.Schedule.ImportJson(System.IO.File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8));
            DialogLauncher.Toast(this, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error, "导入课表",
                ok ? "导入完成，灵动岛已按新课表运行。" : "文件内容不是有效的课表 JSON。");
        }
        catch (Exception ex)
        {
            DialogLauncher.Toast(this, InfoBarSeverity.Error, "导入课表", "导入失败：" + ex.Message);
        }
    }

    void Reset()
    {
        if (DataContext is not SettingsViewModel vm) return;
        if (System.Windows.MessageBox.Show(Window.GetWindow(this),
                "所有设置都会回到默认值。已经装好的插件、课表和值日名单不受影响。", "恢复全部默认",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        vm.ResetAll();
        DialogLauncher.Toast(this, InfoBarSeverity.Success, "已恢复默认", "所有设置已回到初始值。");
    }
}

public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is not SettingsViewModel vm) return;
            SourceNotice.Visibility = vm.BlockUpdateOnSource ? Visibility.Visible : Visibility.Collapsed;
            CheckUpdateButton.Click += (_, _) => vm.Ctx.CheckForUpdate();
        };
    }
}
