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
/// 设置中心里的「课表编辑」页（替代原来的独立浮层 TimetableDialog）。
///
/// 编辑的是一份**本地副本**：改格子只动内存，"保存"时才组装回 <see cref="Timetable"/>
/// 并交给 <see cref="Core.Managers.ScheduleManager.Save"/>；"取消"直接丢弃重读。
/// 这样"改一半不想改"不会污染 schedule.json（旧浮层是失焦即存）。
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
        public string Sat { get; set; } = "";
        public string Sun { get; set; } = "";

        public string[] Courses => new[] { Mon, Tue, Wed, Thu, Fri, Sat, Sun };

        /// <summary>拼回 "HH:mm" 交给 <see cref="Period"/>（保存时才用）。</summary>
        public string Start => $"{StartHour}:{StartMinute}";
        public string End => $"{EndHour}:{EndMinute}";
    }

    readonly ObservableCollection<PeriodRow> _rows = new();

    string _activeName = "";
    bool _loadedOnce;

    public TimetableEditorPage()
    {
        InitializeComponent();

        RowsGrid.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            // DataContext 是父级（SettingsWindow）给的，Loaded 之前拿不到
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

        SubtitleText.Text = $"课表：{_activeName} · 双击格子改课程；时间用开始时 / 开始分 / 结束时 / 结束分 选择";
        UpdateCount();
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
        schedule.Save();

        Reload();
        StatusText.Text = "已保存。";
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

    void UpdateCount() => CountText.Text = $"共 {_rows.Count} 节";

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
