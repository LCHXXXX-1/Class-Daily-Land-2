using ClassDailyLand2.App.ViewModels;
using ClassDailyLand2.Core;
using ClassDailyLand2.Core.Models;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClassDailyLand2.App.Views.Pages;

/// <summary>
/// 设置中心「课表与提醒 › 课表编辑」子页面。
///
/// · 上方是周一到周五的课表；
/// · 顶部开关「自定义周六 / 周日作息」打开后，下方出现周末课表（周六、周日共用一套，
///   整天节次都在这张表里排，不再有上午 / 下午分界点）。
///
/// 编辑的是**本地副本**：改格子只动内存，"保存"时才写回 schedule.json；"取消"丢弃重读。
/// </summary>
public partial class TimetableEditorPage : UserControl
{
    /// <summary>时间下拉的候选值："00".."23" / "00".."59"（XAML 用 x:Static 直接取）。</summary>
    public static readonly IReadOnlyList<string> Hours =
        Enumerable.Range(0, 24).Select(h => h.ToString("D2")).ToList();

    public static readonly IReadOnlyList<string> Minutes =
        Enumerable.Range(0, 60).Select(m => m.ToString("D2")).ToList();

    /// <summary>一行 = 一个节次 + 它 7 天各自的课程。时间拆成小时/分钟四个字段给下拉框绑定。</summary>
    public sealed class PeriodRow
    {
        public string Name { get; set; } = "";

        public string StartHour { get; set; } = "08";
        public string StartMinute { get; set; } = "00";
        public string EndHour { get; set; } = "08";
        public string EndMinute { get; set; } = "45";

        public string Mon { get; set; } = "";
        public string Tue { get; set; } = "";
        public string Wed { get; set; } = "";
        public string Thu { get; set; } = "";
        public string Fri { get; set; } = "";

        /// <summary>周六一列现在只在"未开启周末自定义"时保留旧数据，界面上不展示也不再改。</summary>
        public string Sat { get; set; } = "";
        public string Sun { get; set; } = "";

        public string[] Courses => new[] { Mon, Tue, Wed, Thu, Fri, Sat, Sun };

        /// <summary>拼回 "HH:mm" 交给 <see cref="Period"/>（保存时才用）。</summary>
        public string Start => $"{StartHour}:{StartMinute}";
        public string End => $"{EndHour}:{EndMinute}";
    }

    /// <summary>周末课表的一行：节次 + 时间 + 课程（周六周日共用）。</summary>
    public sealed class WeekendRow
    {
        public string Name { get; set; } = "";
        public string Start { get; set; } = "13:30";
        public string End { get; set; } = "14:15";
        public string Course { get; set; } = "";
    }

    readonly ObservableCollection<PeriodRow> _rows = new();
    readonly ObservableCollection<WeekendRow> _weekendRows = new();

    string _activeName = "";
    bool _loadedOnce;
    bool _loadingWeekend;

    /// <summary>测试课表的原始备份（JSON）与磁盘文件；有值 = 当前有未恢复的测试课表。</summary>
    string? _testScheduleBackup;
    string? _testScheduleBackupFile;
    const string TestScheduleBackupName = "schedule.timetable-backup.json";

    public TimetableEditorPage()
    {
        InitializeComponent();

        RowsGrid.ItemsSource = _rows;
        WeekendGrid.ItemsSource = _weekendRows;

        // 表格会把滚轮事件"吃掉"（DataGrid 内层 ScrollViewer 无条件标记 Handled），
        // 页面里表格高度是随内容撑开的，自己没有可滚区间，于是鼠标停在课表上就滚不动页面。
        EnableWheelBubbling(RowsGrid);
        EnableWheelBubbling(WeekendGrid);

        Loaded += (_, _) =>
        {
            // DataContext 是父级给的，Loaded 之前拿不到
            if (_loadedOnce) return;
            _loadedOnce = true;
            Reload();
        };
    }

    // ————————————————————— 滚轮：表格滚得动就自己滚，滚不动就交给页面 —————————————————————

    /// <summary>
    /// DataGrid 内部的 ScrollViewer 会把 MouseWheel 标记为 Handled，哪怕它自己没得滚。
    /// 页面里的课表是按内容撑开的（没有可滚区间），所以事件被吞掉后外层 ScrollViewer
    /// 永远收不到 → 鼠标停在课表上滚不动。这里在**隧道**阶段（内层还没处理）先判一次：
    /// 表格自己滚得动就放行，滚不动（或已到边界）就把这一格滚轮转给外层滚动容器。
    /// </summary>
    static void EnableWheelBubbling(UIElement element)
    {
        element.PreviewMouseWheel += (_, e) =>
        {
            if (FindAncestor<ScrollViewer>(element) is not { } outer) return;
            if (FindChild<ScrollViewer>(element) is { } inner && CanScroll(inner, e.Delta)) return;

            // 一格滚轮 120 → 48px，和系统默认"3 行"的手感接近
            var step = e.Delta / 120.0 * 48;
            outer.ScrollToVerticalOffset(outer.VerticalOffset - step);
            e.Handled = true;
        };
    }

    static bool CanScroll(ScrollViewer viewer, int delta)
    {
        if (viewer.ScrollableHeight <= 0.5) return false;   // 没有可滚区间（内容撑开时就是这种）
        return delta > 0 ? viewer.VerticalOffset > 0 : viewer.VerticalOffset < viewer.ScrollableHeight;
    }

    static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current is not null)
        {
            if (current is T hit) return hit;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    SettingsViewModel? Vm => DataContext as SettingsViewModel;
    Core.Managers.ScheduleManager? Schedule => Vm?.Ctx.Schedule;

    // ————————————————————— 读入 —————————————————————

    void Reload()
    {
        var schedule = Schedule;
        if (schedule is null)
        {
            StatusText.Text = "课表尚未加载。";
            return;
        }

        _activeName = schedule.File.Active;
        var table = schedule.Active;

        _rows.Clear();
        for (var i = 0; i < table.Periods.Count; i++)
        {
            var period = table.Periods[i];
            var (startHour, startMinute) = SplitTime(period.Start);
            var (endHour, endMinute) = SplitTime(period.End);

            _rows.Add(new PeriodRow
            {
                Name = period.Name ?? "",
                StartHour = startHour,
                StartMinute = startMinute,
                EndHour = endHour,
                EndMinute = endMinute,
                Mon = table.CourseAt(i, 0),
                Tue = table.CourseAt(i, 1),
                Wed = table.CourseAt(i, 2),
                Thu = table.CourseAt(i, 3),
                Fri = table.CourseAt(i, 4),
                Sat = table.CourseAt(i, 5),
                Sun = table.CourseAt(i, 6),
            });
        }

        LoadWeekend();
        SubtitleText.Text = $"课表：{_activeName} · 双击格子改课程；时间用开始时 / 开始分 / 结束时 / 结束分 选择";
        UpdateCount();
        LoadTestBackupState();
        UpdateTestUi();
    }

    void LoadWeekend()
    {
        var schedule = Schedule;
        if (schedule is null) return;

        _loadingWeekend = true;

        var weekend = schedule.File.Weekend;
        _weekendRows.Clear();

        // 周末表是**一整天**的完整节次（不再分上午 / 下午）：
        //   · morning_split == "00:00" → 已经是完整表，按存的顺序原样显示；
        //   · 否则是老配置（上午沿用平日课表 + 下午自定义），把两段合并成一张完整表，
        //     用户在这一张表上改，保存后就固化成完整表。
        var main = schedule.Active;
        var selfContained = weekend.MorningSplitMinutes == 0;

        if (selfContained && weekend.AfternoonPeriods.Count > 0)
        {
            for (var i = 0; i < weekend.AfternoonPeriods.Count; i++)
            {
                var period = weekend.AfternoonPeriods[i];
                _weekendRows.Add(new WeekendRow
                {
                    Name = period.Name ?? $"第{i + 1}节",
                    Start = period.Start,
                    End = period.End,
                    Course = i < weekend.Courses.Count ? weekend.Courses[i] ?? "" : "",
                });
            }
        }
        else
        {
            // 老配置：先把平日课表整天的节次搬进来（周末列优先，空则回落周一列）
            for (var i = 0; i < main.Periods.Count; i++)
            {
                var period = main.Periods[i];
                if (!period.IsValid) continue;

                var course = main.CourseAt(i, 5);
                if (string.IsNullOrWhiteSpace(course)) course = main.CourseAt(i, 0);

                _weekendRows.Add(new WeekendRow
                {
                    Name = string.IsNullOrWhiteSpace(period.Name) ? $"第{_weekendRows.Count + 1}节" : period.Name,
                    Start = period.Start,
                    End = period.End,
                    Course = course ?? "",
                });
            }

            // 再把之前单独配过的午后节次接在后面
            for (var i = 0; i < weekend.AfternoonPeriods.Count; i++)
            {
                var period = weekend.AfternoonPeriods[i];
                if (!period.IsValid) continue;

                _weekendRows.Add(new WeekendRow
                {
                    Name = string.IsNullOrWhiteSpace(period.Name) ? $"第{_weekendRows.Count + 1}节" : period.Name,
                    Start = period.Start,
                    End = period.End,
                    Course = i < weekend.Courses.Count ? weekend.Courses[i] ?? "" : "",
                });
            }
        }

        var on = Vm?.Ctx.Settings.Bool("weekend_custom_schedule") ?? false;
        WeekendToggle.IsChecked = on;
        WeekendHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        _loadingWeekend = false;
    }

    // ————————————————————— 周末开关 —————————————————————

    void WeekendToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingWeekend) return;

        var on = WeekendToggle.IsChecked == true;
        WeekendHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        try { Vm?.Ctx.Settings.Set("weekend_custom_schedule", on); }
        catch { /* 设置写失败不影响本次编辑 */ }

        StatusText.Text = on
            ? "已展开周末课表（周六、周日共用）· 记得点「保存」。"
            : "已收起周末课表 · 此时保存不会改动周末设置。";
    }

    // ————————————————————— 节次操作 —————————————————————

    void AddPeriod_Click(object sender, RoutedEventArgs e)
    {
        CommitEdits();

        var row = new PeriodRow
        {
            Name = $"第{_rows.Count + 1}节",
            StartHour = _rows.Count > 0 ? _rows[^1].EndHour : "08",
            StartMinute = _rows.Count > 0 ? _rows[^1].EndMinute : "00",
        };

        (row.EndHour, row.EndMinute) = AddMinutes(row.StartHour, row.StartMinute, 45);

        _rows.Add(row);
        UpdateCount();

        RowsGrid.SelectedItem = row;
        RowsGrid.ScrollIntoView(row);
        StatusText.Text = "已新增一节（记得点「保存」）。";
    }

    void DeletePeriod_Click(object sender, RoutedEventArgs e)
    {
        CommitEdits();

        if (RowsGrid.SelectedItem is not PeriodRow row)
        {
            StatusText.Text = "先在表格里点选一行，再删除。";
            return;
        }

        _rows.Remove(row);
        UpdateCount();
        StatusText.Text = "已删除选中行（记得点「保存」）。";
    }

    void AddWeekend_Click(object sender, RoutedEventArgs e)
    {
        CommitWeekendEdits();

        // ★ 上一行的「结束」是用户可自由编辑的文本框：清空 / 敲了非数字都会走到这里。
        //   原来直接 Split(':')[1] + int.Parse，越界或格式错就是一次崩溃。
        //   现在解析不出来就退回 08:00，并在状态栏说清楚为什么不是接着上一节排。
        var previousEnd = _weekendRows.Count > 0 ? _weekendRows[^1].End?.Trim() ?? "" : "08:00";
        var startText = NormalizeClock(previousEnd);
        var guessed = startText is null;

        var row = new WeekendRow
        {
            Name = $"第{_weekendRows.Count + 1}节",
            Start = startText ?? "08:00",
        };

        var (h, m) = AddMinutes(row.Start[..2], row.Start[3..], 45);
        row.End = $"{h}:{m}";

        _weekendRows.Add(row);
        StatusText.Text = guessed
            ? "上一节的结束时间读不出来，这一节按 08:00 起排——请手动改一下（记得点「保存」）。"
            : "已新增一节周末课（记得点「保存」）。";
    }

    void DeleteWeekend_Click(object sender, RoutedEventArgs e)
    {
        CommitWeekendEdits();

        if (WeekendGrid.SelectedItem is not WeekendRow row)
        {
            StatusText.Text = "先在周末表格里点选一行，再删除。";
            return;
        }

        _weekendRows.Remove(row);
        StatusText.Text = "已删除该周末节次（记得点「保存」）。";
    }

    // ————————————————————— 导入 / 导出 —————————————————————

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var schedule = Schedule;
        if (schedule is null) return;

        CommitEdits();

        var dialog = new SaveFileDialog
        {
            Title = "导出课表 JSON",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"课表_{DateTime.Today:yyyyMMdd}.json",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, schedule.ExportJson(), Encoding.UTF8);
            StatusText.Text = "已导出：" + dialog.FileName;
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var schedule = Schedule;
        if (schedule is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "导入课表 JSON",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var json = File.ReadAllText(dialog.FileName, Encoding.UTF8);

            if (!schedule.ImportJson(json))
            {
                StatusText.Text = "文件内容不是有效的课表 JSON。";
                return;
            }

            Reload();
            StatusText.Text = "导入完成（已覆盖全部课表与假期设置）。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "导入失败：" + ex.Message;
        }
    }

    // ————————————————————— 保存 / 取消 —————————————————————

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!PersistEditor()) return;

        Reload();
        StatusText.Text = WeekendToggle.IsChecked == true ? "已保存（含周末课表）。" : "已保存。";
    }

    /// <summary>
    /// 把编辑器里的课表写回 schedule.json（不负责 Reload / 提示）。
    /// 测试课表插入前也先调它：保证备份的是用户当前正在编辑的版本，而不是上一次保存的版本。
    /// </summary>
    bool PersistEditor()
    {
        var schedule = Schedule;
        if (schedule is null) return false;

        CommitEdits();
        CommitWeekendEdits();

        var table = new Timetable();

        foreach (var row in _rows)
        {
            table.Periods.Add(new Period(row.Name.Trim(), row.Start, row.End));
            table.SetRow(table.Periods.Count - 1, row.Courses);
        }

        if (string.IsNullOrWhiteSpace(_activeName) || !schedule.File.Timetables.ContainsKey(_activeName))
        {
            StatusText.Text = "找不到正在编辑的课表，已取消保存。";
            return false;
        }

        schedule.File.Timetables[_activeName] = table;

        // 周末只在开关打开时写回：关掉时保持原数据，避免误清空
        if (WeekendToggle.IsChecked == true)
        {
            var weekend = schedule.File.Weekend;

            // 周末表就是整天的全部节次，不存在上午 / 下午分界：
            // morning_split = "00:00" 表示"不从平日课表继承任何节次"，
            // 引擎按 start < morning_split 取上午，取到空集后整天都由下面这张表决定。
            weekend.MorningSplit = "00:00";
            weekend.AfternoonPeriods = _weekendRows
                .Select(r => new Period(r.Name.Trim(), NormalizeTime(r.Start, "08:00"), NormalizeTime(r.End, "08:45")))
                .ToList();
            weekend.Courses = _weekendRows.Select(r => r.Course.Trim()).ToList();
            schedule.SetWeekend(weekend);
        }

        schedule.Save();
        return true;
    }

    // ————————————————————— 测试课表 —————————————————————

    void TestClassStart_Click(object sender, RoutedEventArgs e) => InsertTestTimetable(startNow: true);
    void TestClassEnd_Click(object sender, RoutedEventArgs e) => InsertTestTimetable(startNow: false);
    void TestClassRestore_Click(object sender, RoutedEventArgs e) => RestoreTestTimetable();

    /// <summary>
    /// 插入测试课表（先备份原课表）：
    ///   上课状态：现在 = 第 1 节上课，40 分钟后下课，课间 10 分钟，再 40 分钟……
    ///   下课状态：现在 - 40 分钟 = 第 1 节上课，现在 = 第 1 节下课，+10 分钟后第 2 节……
    /// 课程一律只写「自习」，重名编号交给引擎（自习(1)、自习(2)…）。
    ///
    /// ★ 直接把**当前正在用的课表**顶掉（不另建一张测试表），
    ///   测试期间课表编辑器/灵动岛读到的都是这套测试数据；点「恢复原课表」才整份写回。
    /// </summary>
    void InsertTestTimetable(bool startNow)
    {
        var schedule = Schedule;
        if (schedule is null || Vm is null) return;

        // 先把编辑器里正在改的内容落盘，再备份 —— 否则备份的是上一次保存的旧课表
        if (!PersistEditor()) return;

        if (_testScheduleBackup is null)
        {
            _testScheduleBackup = schedule.ExportJson();
            _testScheduleBackupFile = WriteTestBackup(Vm.ConfigPath, _testScheduleBackup);
        }

        var targetName = schedule.File.Active;
        if (string.IsNullOrWhiteSpace(targetName) || !schedule.File.Timetables.ContainsKey(targetName))
        {
            StatusText.Text = "找不到当前课表，无法插入测试课表。";
            return;
        }

        var now = DateTime.Now;
        var table = BuildTestTable(now, startNow);

        // 实打实顶掉原来的课表：名字还是原来的名字，内容整份换成测试课表
        schedule.File.Timetables[targetName] = table;
        schedule.File.Active = targetName;

        // 周末 / 假期 / 临时调课都会影响"今天到底上不上这套课"，测试期间先绕开（恢复时整份写回）
        schedule.File.Weekend ??= new WeekendConfig();
        schedule.File.Weekend.MorningSplit = "23:59";
        schedule.File.Weekend.AfternoonPeriods.Clear();
        schedule.File.Weekend.Courses.Clear();
        schedule.File.Holidays.Clear();
        schedule.File.TempAdjust = new TempAdjust();

        schedule.SetAdvance(0);
        schedule.SetOffset(0);
        schedule.Save();     // 写 schedule.json，灵动岛立刻按新表算

        Reload();
        StatusText.Text = startNow
            ? $"已把「{targetName}」整份替换成测试课表：现在开始上第 1 节，40 分钟后下课。"
            : $"已把「{targetName}」整份替换成测试课表：第 1 节现在下课，10 分钟后上第 2 节。";
    }

    /// <summary>按 40 分钟上课 + 10 分钟课间，从指定起点一直排到当天 23:59。</summary>
    static Timetable BuildTestTable(DateTime now, bool startNow)
    {
        var table = new Timetable();
        var dayEnd = now.Date.AddHours(23).AddMinutes(59).AddSeconds(59);

        // 下课状态：把"现在"当成第 1 节的下课时刻；留 2 秒余量，让灵动岛先看到
        // "上课中 → 下课"这次跳变，才会弹「下课了！」。
        var anchor = startNow ? now : now.AddSeconds(2);

        // 下课状态要往前推 40 分钟：刚过零点时会跨天，这里夹到当天 00:00
        var t = startNow ? anchor : anchor.AddMinutes(-40);
        if (t < now.Date) t = now.Date;

        var index = 0;
        while (t < dayEnd && index < 60)
        {
            var start = t;
            var end = start.AddMinutes(40);
            if (end > dayEnd) end = dayEnd;
            if (end <= start) break;

            table.Periods.Add(new Period($"第{index + 1}节", FormatClock(start), FormatClock(end)));
            // 只写「自习」两个字；重复编号由 ScheduleEngine 读取时统一处理
            for (var day = 0; day < 7; day++) table.SetCourse(index, day, "自习");

            index++;
            t = end.AddMinutes(10);
        }
        return table;
    }

    static string FormatClock(DateTime t) =>
        t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    void RestoreTestTimetable()
    {
        var schedule = Schedule;
        if (schedule is null || _testScheduleBackup is null) return;

        if (!schedule.ImportJson(_testScheduleBackup))
        {
            StatusText.Text = "恢复失败：备份内容无法读取。";
            return;
        }

        DeleteTestBackup();
        _testScheduleBackup = null;
        Reload();
        StatusText.Text = "已恢复测试前的课表。";
    }

    /// <summary>页面重新打开 / 切回来时，如果数据目录里还有测试备份，把「恢复原课表」按钮找回来。</summary>
    void LoadTestBackupState()
    {
        if (_testScheduleBackup is not null || Vm is null) return;

        var path = Path.Combine(Vm.ConfigPath, TestScheduleBackupName);
        if (!File.Exists(path)) return;

        try
        {
            _testScheduleBackup = File.ReadAllText(path);
            _testScheduleBackupFile = path;
        }
        catch (Exception ex)
        {
            Logger.Current?.Warn("[测试] 读取测试课表备份失败：" + ex.Message);
        }
    }

    void UpdateTestUi()
    {
        var hasBackup = _testScheduleBackup is not null;

        TestClassRestoreButton.Visibility = hasBackup ? Visibility.Visible : Visibility.Collapsed;
        TimetableTestStatus.Visibility = hasBackup ? Visibility.Visible : Visibility.Collapsed;
        TimetableTestStatus.Text = hasBackup
            ? $"当前课表「{_activeName}」已被测试课表整份替换（课程只写「自习」，编号由系统生成）；点「结束测试并恢复」还原。"
            : "";
    }

    string? WriteTestBackup(string folder, string json)
    {
        try
        {
            var path = Path.Combine(folder, TestScheduleBackupName);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex)
        {
            Logger.Current?.Warn("[测试] 写课表备份失败：" + ex.Message);
            return null;
        }
    }

    void DeleteTestBackup()
    {
        if (_testScheduleBackupFile is null) return;
        try { File.Delete(_testScheduleBackupFile); } catch { }
        _testScheduleBackupFile = null;
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Reload();
        StatusText.Text = "已放弃本次修改。";
    }

    // ————————————————————— 小工具 —————————————————————

    /// <summary>把还在编辑中的单元格写回对象，否则点「保存」会丢掉最后敲的那一格。</summary>
    void CommitEdits()
    {
        RowsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RowsGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    void CommitWeekendEdits()
    {
        WeekendGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        WeekendGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    void UpdateCount() => CountText.Text = $"共 {_rows.Count} 节";

    /// <summary>"8:30" / "08:30" → "08:30"；解析不出来就回落到兜底值。</summary>
    static string NormalizeTime(string? text, string fallback)
    {
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var hour)
            && int.TryParse(parts[1], out var minute)
            && hour is >= 0 and <= 23
            && minute is >= 0 and <= 59)
        {
            return $"{hour:D2}:{minute:D2}";
        }
        return fallback;
    }

    /// <summary>"08:30" → ("08","30")；解析不出来时回落到 08:00，免得下拉框空着。</summary>
    static (string Hour, string Minute) SplitTime(string? text)
    {
        var parts = (text ?? "").Trim().Split(':');

        if (parts.Length >= 2
            && int.TryParse(parts[0], out var hour)
            && int.TryParse(parts[1], out var minute)
            && hour is >= 0 and <= 23
            && minute is >= 0 and <= 59)
        {
            return (hour.ToString("D2"), minute.ToString("D2"));
        }

        return ("08", "00");
    }

    /// <summary>
    /// 把用户敲的时间规整成 <c>HH:mm</c>；认不出来返回 null（调用方自己决定兜底值）。
    /// 容忍 "8:5"、"8：05"（全角冒号）、"08"、"0805" 这类常见手误。
    /// </summary>
    static string? NormalizeClock(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.Trim().Replace('：', ':').Replace('．', '.').Replace('.', ':');

        string hh, mm;
        var parts = text.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) { hh = parts[0]; mm = parts[1]; }
        else if (parts.Length == 1 && parts[0].Length is 3 or 4)
        {
            hh = parts[0][..^2];
            mm = parts[0][^2..];
        }
        else return null;

        if (!int.TryParse(hh, out var h) || !int.TryParse(mm, out var m)) return null;
        if (h is < 0 or > 23 || m is < 0 or > 59) return null;
        return $"{h:D2}:{m:D2}";
    }

    /// <summary>在新一节的结束时间上顺延，跨午夜时对 24 小时取模。</summary>
    static (string Hour, string Minute) AddMinutes(string hour, string minute, int delta)
    {
        var total = (int.Parse(hour) * 60 + int.Parse(minute) + delta) % (24 * 60);
        if (total < 0) total += 24 * 60;
        return ((total / 60).ToString("D2"), (total % 60).ToString("D2"));
    }
}
