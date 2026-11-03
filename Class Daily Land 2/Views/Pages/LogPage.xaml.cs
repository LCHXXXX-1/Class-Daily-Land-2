using ClassDailyLand2.App.Controls;
using ClassDailyLand2.App.ViewModels;
using ClassDailyLand2.Core;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ClassDailyLand2.App.Views.Pages;

/// <summary>
/// 独立日志页（左侧导航「日志」）：
/// 实时订阅 <see cref="Logger.LineAppended"/>，配合级别 / 关键字过滤。
///
/// 页面会被 SettingsWindow 缓存，所以订阅放在 Loaded、退订放在 Unloaded，
/// 避免离开页面后还在往可视树上追加（那会让页面永远被引用住）。
/// </summary>
public partial class LogPage : UserControl
{
    /// <summary>界面这一份缓存的上限，跟 Logger.Capacity（500~3000）保持一致。</summary>
    int _keepLines = Logger.DefaultCapacity;

    /// <summary>界面当前显示的行（ObservableCollection：实时追加走它，不会和 ItemsSource 打架）。</summary>
    readonly ObservableCollection<string> _view = new();

    readonly List<string> _all = new();
    Logger? _log;
    bool _hooked;
    bool _applyingCapacity;

    public LogPage()
    {
        InitializeComponent();
        // ItemsSource 只设这一次；之后所有增删都走 _view，列表会自己刷新
        LogList.ItemsSource = _view;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    void Attach()
    {
        if (_hooked || DataContext is not SettingsViewModel vm) return;
        _hooked = true;

        _log = vm.Ctx.Log;
        if (_log is null) return;

        // 先用内存环形缓冲填满（日志页打开前发生的事也看得到）
        _all.Clear();
        _all.AddRange(_log.RecentLines);
        _log.LineAppended += OnLineAppended;

        _keepLines = Math.Max(Logger.MinCapacity, _log.Capacity);
        SyncCapacityBox(_log.Capacity);

        Refresh();
        ScrollToEnd();
    }

    /// <summary>把当前上限回填到下拉框（不触发写回）。</summary>
    void SyncCapacityBox(int capacity)
    {
        _applyingCapacity = true;
        try
        {
            foreach (var obj in CapacityBox.Items)
            {
                if (obj is ComboBoxItem item && item.Tag?.ToString() == capacity.ToString())
                {
                    CapacityBox.SelectedItem = item;
                    return;
                }
            }
        }
        finally
        {
            _applyingCapacity = false;
        }
    }

    /// <summary>
    /// 改「内存保留条数」：写进设置（<c>AppController</c> 会同步 <see cref="Logger.Capacity"/>），
    /// 同时立刻裁掉本页自己缓存的多余旧行 —— 否则界面还是旧的，用户会以为没生效。
    /// </summary>
    void OnCapacityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingCapacity) return;
        if (DataContext is not SettingsViewModel vm) return;
        if (CapacityBox.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Tag?.ToString(), out var value)) return;

        vm.Ctx.Settings.Set("log_max_lines", value);

        _keepLines = value;
        if (_all.Count > _keepLines)
        {
            _all.RemoveRange(0, _all.Count - _keepLines);
            while (_view.Count > _keepLines) _view.RemoveAt(0);
        }

        Refresh();
        ScrollToEnd();
    }

    void Detach()
    {
        if (_log is not null) _log.LineAppended -= OnLineAppended;
        _log = null;
        _hooked = false;
    }

    // ————————————————————— 实时追加 —————————————————————

    void OnLineAppended(string line)
    {
        // 写日志的可能是后台线程（插件 / 灾害轮询 / SMTC 桥），切回 UI 线程再动控件
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnLineAppended(line)));
            return;
        }

        _all.Add(line);
        if (_all.Count > _keepLines)
        {
            var drop = _all.Count - _keepLines;
            _all.RemoveRange(0, drop);
            // 界面上同步丢掉最旧的几行，否则长时间开着页面会越堆越多
            for (var i = 0; i < drop && _view.Count > 0; i++) _view.RemoveAt(0);
        }

        if (Matches(line))
        {
            _view.Add(line);
            if (AutoScrollToggle.IsChecked == true) LogList.ScrollIntoView(line);
        }
        UpdateCount();
    }

    // ————————————————————— 过滤 —————————————————————

    void OnFilterChanged(object sender, RoutedEventArgs e) => Refresh();

    void OnFilterChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    bool Matches(string line)
    {
        if (!LevelOk(line)) return false;

        var key = FilterBox?.Text?.Trim() ?? "";
        if (key.Length == 0) return true;
        return line.Contains(key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>下拉框 Tag：0 全部 / 1 INFO+ / 2 WARN+ / 3 仅 ERROR。</summary>
    bool LevelOk(string line) => (LevelBox?.SelectedIndex ?? 0) switch
    {
        1 => !line.Contains("[TRACE]", StringComparison.Ordinal),
        2 => line.Contains("[WARN]", StringComparison.Ordinal) || line.Contains("[ERROR]", StringComparison.Ordinal),
        3 => line.Contains("[ERROR]", StringComparison.Ordinal),
        _ => true,
    };

    /// <summary>按当前过滤条件重建显示内容（筛选变化 / 首次进入时用）。</summary>
    void Refresh()
    {
        if (LogList is null) return;   // XAML 初始化期间 SelectionChanged 会先于控件就绪触发

        _view.Clear();
        foreach (var line in _all)
            if (Matches(line)) _view.Add(line);

        UpdateCount(_view.Count);
        if (AutoScrollToggle?.IsChecked == true) ScrollToEnd();
    }

    void UpdateCount(int? shown = null)
    {
        if (CountText is null) return;
        var visible = shown ?? LogList.Items.Count;
        CountText.Text = $"共 {_all.Count} 行 · 当前显示 {visible} 行 · 实时刷新中 · app.log（约 {FileSizeText()}）";
    }

    string FileSizeText()
    {
        try
        {
            var path = (DataContext as SettingsViewModel)?.Ctx?.Paths?.Log;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "尚未写入";
            var length = new FileInfo(path).Length;
            return length < 1024 ? $"{length} B"
                : length < 1024 * 1024 ? $"{length / 1024.0:0.#} KB"
                : $"{length / 1024.0 / 1024.0:0.#} MB";
        }
        catch { return "未知"; }
    }

    void ScrollToEnd()
    {
        if (_view.Count == 0) return;
        LogList.ScrollIntoView(_view[^1]);
    }

    // ————————————————————— 工具按钮 —————————————————————

    void OnClear(object sender, RoutedEventArgs e)
    {
        _log?.ClearRecent();
        _all.Clear();
        _view.Clear();     // 不碰 ItemsSource：绑定的集合清空，列表自己就空了
        UpdateCount(0);
    }

    /// <summary>
    /// 清除日志：删掉 app.log 与所有自动保存的报错快照（error-*.log），同时清空页面内容。
    /// 不可恢复，所以必须先确认；删完会往新日志里补一条记录，免得"日志突然空了"变成谜。
    /// </summary>
    void OnClearLogs(object sender, RoutedEventArgs e)
    {
        var log = _log ?? (DataContext as SettingsViewModel)?.Ctx.Log;
        if (log is null) return;

        DialogLauncher.Confirm(this, "清除日志",
            "会删除 app.log 和所有自动保存的报错日志（error-*.log）。删除后无法恢复。",
            () =>
            {
                var (files, bytes) = log.ClearAll();

                _all.Clear();
                _view.Clear();
                UpdateCount(0);

                DialogLauncher.Toast(this, InfoBarSeverity.Success, "已清除日志",
                    $"删除 {files} 个文件，释放 {bytes / 1024.0:0.#} KB");
            },
            okText: "清除");
    }

    void OnOpenFile(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var path = vm.Ctx.Paths.Log;
        try { vm.Ctx.OpenDirectory(path); }
        catch (Exception ex) { DialogLauncher.Toast(this, InfoBarSeverity.Error, "打不开日志文件", ex.Message); }
    }

    void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出日志",
            Filter = "文本文件 (*.txt)|*.txt|日志文件 (*.log)|*.log",
            FileName = $"ClassDailyLand2-日志-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        File.WriteAllText(dlg.FileName, string.Join(Environment.NewLine, _view), System.Text.Encoding.UTF8);
        DialogLauncher.Toast(this, InfoBarSeverity.Success, "已导出", $"{_view.Count} 行 → {dlg.FileName}");
    }
}
