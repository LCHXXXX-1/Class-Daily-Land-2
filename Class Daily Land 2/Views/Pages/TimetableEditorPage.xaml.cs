using ClassDailyLand2.App.ViewModels;
using ClassDailyLand2.Core.Models;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace ClassDailyLand2.App.Views.Pages;

/// <summary>
/// 设置中心「课表与提醒 › 课表编辑」子页面。
///
/// · 上方是周一到周五的课表；
/// · 顶部开关「自定义周六 / 周日作息」打开后，下方出现周末课表（周六、周日共用一套，
///   上午沿用平日课表中早于分界点的节次，下午用这里配置的自定义节次）。
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

    public TimetableEditorPage()
    {
        InitializeComponent();

        RowsGrid.ItemsSource = _rows;
        WeekendGrid.ItemsSource = _weekendRows;

        Loaded += (_, _) =>
        {
            // DataContext 是父级给的，Loaded 之前拿不到
            if (_loadedOnce) return;
            _loadedOnce = true;
            Reload();
        };
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
    }

    void LoadWeekend()
    {
        var schedule = Schedule;
        if (schedule is null) return;

        _loadingWeekend = true;

        var weekend = schedule.File.Weekend;
        SplitBox.Text = string.IsNullOrWhiteSpace(weekend.MorningSplit) ? "12:00" : weekend.MorningSplit;

        _weekendRows.Clear();
        for (var i = 0; i < weekend.AfternoonPeriods.Count; i++)
        {
            var period = weekend.AfternoonPeriods[i];
            _weekendRows.Add(new WeekendRow
            {
                Name = period.Name ?? $"午{i + 1}",
                Start = period.Start,
                End = period.End,
                Course = i < weekend.Courses.Count ? weekend.Courses[i] ?? "" : "",
            });
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
            : "已收起周末课表 · 周末将沿用平日课表的上午节次。";
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

        var row = new WeekendRow
        {
            Name = $"午{_weekendRows.Count + 1}",
            Start = _weekendRows.Count > 0 ? _weekendRows[^1].End : "13:30",
        };

        var (h, m) = AddMinutes(row.Start.Split(':')[0], row.Start.Split(':')[1], 45);
        row.End = $"{h}:{m}";

        _weekendRows.Add(row);
        StatusText.Text = "已新增一节周末课（记得点「保存」）。";
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
        var schedule = Schedule;
        if (schedule is null) return;

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
            return;
        }

        schedule.File.Timetables[_activeName] = table;

        // 周末只在开关打开时写回：关掉时保持原数据，避免误清空
        if (WeekendToggle.IsChecked == true)
        {
            var weekend = schedule.File.Weekend;
            weekend.MorningSplit = NormalizeTime(SplitBox.Text, "12:00");
            weekend.AfternoonPeriods = _weekendRows
                .Select(r => new Period(r.Name.Trim(), NormalizeTime(r.Start, "13:30"), NormalizeTime(r.End, "14:15")))
                .ToList();
            weekend.Courses = _weekendRows.Select(r => r.Course.Trim()).ToList();
            schedule.SetWeekend(weekend);
        }

        schedule.Save();

        Reload();
        StatusText.Text = WeekendToggle.IsChecked == true ? "已保存（含周末课表）。" : "已保存。";
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

    /// <summary>在新一节的结束时间上顺延，跨午夜时对 24 小时取模。</summary>
    static (string Hour, string Minute) AddMinutes(string hour, string minute, int delta)
    {
        var total = (int.Parse(hour) * 60 + int.Parse(minute) + delta) % (24 * 60);
        if (total < 0) total += 24 * 60;
        return ((total / 60).ToString("D2"), (total % 60).ToString("D2"));
    }
}
