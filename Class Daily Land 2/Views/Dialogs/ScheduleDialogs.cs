using ClassDailyLand2.Core.Models;
using System.Windows;
using System.Windows.Controls;

namespace ClassDailyLand2.App.Views.Dialogs;

/// <summary>假期与调休（对位 web 版 openHolidays）。</summary>
public sealed class HolidaysDialog : DialogPanel
{
    readonly StackPanel _holidayRows = new();
    readonly StackPanel _makeupRows = new();

    public HolidaysDialog(ViewModels.SettingsViewModel vm) : base(vm, "假期与调休",
        "假期按区间生效（含首尾两天）；调休日按所选星期的课表上课。", 720)
    {
        Add(Card("假期", "期间主岛显示「假期中 · 名称」", _holidayRows));
        Add(Gap(6));
        Add(Card("调休补课", "适合周末补班、放假调休", _makeupRows));

        AddButton("关闭", Close);
        var addHoliday = AddButton("新增假期", AddHoliday, primary: true);
        addHoliday.Margin = new Thickness(8, 0, 0, 0);

        Rebuild();
    }

    static FrameworkElement Gap(double h) => new() { Height = h };

    void Rebuild()
    {
        RebuildHolidays();
        RebuildMakeups();
    }

    void RebuildHolidays()
    {
        _holidayRows.Children.Clear();
        var list = Ctx.Schedule.File.Holidays;
        if (list.Count == 0) _holidayRows.Children.Add(Empty("暂无假期。"));

        for (var i = 0; i < list.Count; i++)
        {
            var index = i;
            var h = list[i];
            var start = Input(h.Start, 150);
            var end = Input(h.End, 150);
            var name = Input(h.Name, 180);

            void Save()
            {
                var items = Ctx.Schedule.File.Holidays;
                if (index >= items.Count) return;
                items[index].Start = start.Text.Trim();
                items[index].End = end.Text.Trim();
                items[index].Name = name.Text.Trim();
                Ctx.Schedule.Save();
            }
            start.LostFocus += (_, _) => Save();
            end.LostFocus += (_, _) => Save();
            name.LostFocus += (_, _) => Save();

            _holidayRows.Children.Add(Row(
                start, end, name,
                SmallButton("删除", () =>
                {
                    Ctx.Schedule.RemoveHoliday(index);
                    Rebuild();
                })));
        }
    }

    void RebuildMakeups()
    {
        _makeupRows.Children.Clear();
        var list = Ctx.Schedule.File.MakeupDays;
        if (list.Count == 0) _makeupRows.Children.Add(Empty("暂无调休补课日。"));

        for (var i = 0; i < list.Count; i++)
        {
            var index = i;
            var m = list[i];
            var date = Input(m.Date, 150);
            var weekday = Select(new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" }, m.AsWeekday);

            void Save()
            {
                var items = Ctx.Schedule.File.MakeupDays;
                if (index >= items.Count) return;
                items[index].Date = date.Text.Trim();
                items[index].AsWeekday = weekday.SelectedIndex < 0 ? 0 : weekday.SelectedIndex;
                Ctx.Schedule.Save();
            }
            date.LostFocus += (_, _) => Save();
            weekday.SelectionChanged += (_, _) => Save();

            _makeupRows.Children.Add(Row(
                date, weekday,
                SmallButton("删除", () =>
                {
                    Ctx.Schedule.RemoveMakeup(index);
                    Rebuild();
                })));
        }
    }

    void AddHoliday()
    {
        var today = DateTime.Today;
        if (!Ctx.Schedule.AddHoliday(new Holiday
        {
            Start = today.ToString("yyyy-MM-dd"),
            End = today.ToString("yyyy-MM-dd"),
            Name = "假期",
        }))
        {
            Tell(this, "日期格式无效，应为 yyyy-MM-dd。", "新增假期");
            return;
        }
        Rebuild();
    }
}

/// <summary>周末作息（对位 web 版 openWeekend）。</summary>
public sealed class WeekendDialog : DialogPanel
{
    readonly TextBox _split;
    readonly StackPanel _periodRows = new();
    readonly TextBox _courses;

    public WeekendDialog(ViewModels.SettingsViewModel vm) : base(vm, "周末作息",
        "上午沿用平日课表中开始时间早于分界点的节次；下午使用下面单独配置的节次。", 640)
    {
        var weekend = Ctx.Schedule.File.Weekend;

        _split = TimeInput(weekend.MorningSplit);
        Add(Row(Label("上午分界点："), _split));

        Add(Card("下午节次", "留空表示周末只有上午有课", _periodRows));
        Add(Gap(2));

        _courses = Area(string.Join("\n", weekend.Courses), 140);
        Add(Card("周末课程", "一行一节，与上面的节次顺序对应", _courses));

        AddButton("取消", Close);
        AddButton("保存", Save, primary: true);

        Rebuild();
    }

    static FrameworkElement Gap(double h) => new() { Height = h };

    void Rebuild()
    {
        _periodRows.Children.Clear();
        var periods = Ctx.Schedule.File.Weekend.AfternoonPeriods;
        if (periods.Count == 0) _periodRows.Children.Add(Empty("下午还没有自定义节次。"));

        for (var i = 0; i < periods.Count; i++)
        {
            var index = i;
            var p = periods[i];
            _periodRows.Children.Add(Row(
                Input(p.Name, 110),
                TimeInput(p.Start),
                TimeInput(p.End),
                SmallButton("删除", () =>
                {
                    var w = Ctx.Schedule.File.Weekend;
                    if (index < w.AfternoonPeriods.Count) w.AfternoonPeriods.RemoveAt(index);
                    if (index < w.Courses.Count) w.Courses.RemoveAt(index);
                    Ctx.Schedule.SetWeekend(w);
                    Rebuild();
                })));
        }

        var add = SmallButton("＋ 加一节", () =>
        {
            var w = Ctx.Schedule.File.Weekend;
            w.AfternoonPeriods.Add(new Period($"午{w.AfternoonPeriods.Count + 1}", "13:30", "14:15"));
            Ctx.Schedule.SetWeekend(w);
            Rebuild();
        });
        _periodRows.Children.Add(add);
    }

    void Save()
    {
        var w = Ctx.Schedule.File.Weekend;

        var split = _split.Text.Trim();
        if (split.Length == 4 && split[1] == ':') split = "0" + split;
        if (split.Length != 5 || split[2] != ':')
        {
            Tell(this, "分界点要写成 HH:MM，已回落到 12:00。", "周末作息");
            split = "12:00";
        }
        w.MorningSplit = split;

        // 行内容按当前节次顺序读回（删除过的行不再保留）
        var periods = new List<Period>();
        var courses = new List<string>();
        var rows = _periodRows.Children.OfType<StackPanel>().ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var boxes = rows[i].Children.OfType<TextBox>().ToList();
            if (boxes.Count < 3) continue;      // 「加一节」按钮那一行
            periods.Add(new Period(boxes[0].Text.Trim(), boxes[1].Text.Trim(), boxes[2].Text.Trim()));
        }

        var lines = _courses.Text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < periods.Count; i++)
            courses.Add(i < lines.Length ? lines[i].Trim() : "");

        w.AfternoonPeriods = periods;
        w.Courses = courses;
        Ctx.Schedule.SetWeekend(w);
        Close();
    }
}

/// <summary>值日生名单（对位 web 版 openDuty）。</summary>
public sealed class DutyDialog : DialogPanel
{
    readonly ListBox _list = new() { Height = 260, Style = S("EdList") };
    readonly TextBox _name;
    readonly TextBox _should;
    readonly TextBox _actual;
    readonly TextBlock _current = new() { Style = S("EdHintWarn") };

    public DutyDialog(ViewModels.SettingsViewModel vm) : base(vm, "值日生名单",
        "按顺序每天轮换一位；跨天自动顺延，也可以右键主窗手动切换。", 560)
    {
        var duty = Ctx.Duty;

        Add(Row(Label("当前值日："), _current));
        Add(_list);
        RefreshList();

        _name = Input("", 200, "姓名");
        Add(Gap(8));
        Add(Row(
            _name,
            SmallButton("添加", AddName),
            SmallButton("删除选中", RemoveSelected),
            SmallButton("上移", () => Move(-1)),
            SmallButton("下移", () => Move(1))));

        Add(Gap(12));
        _should = NumberInput(duty.Config.Should);
        _actual = NumberInput(duty.Config.Actual);
        _should.LostFocus += (_, _) => SaveAttendance();
        _actual.LostFocus += (_, _) => SaveAttendance();
        Add(Row(Label("应到"), _should, Label("实到"), _actual));

        AddButton("关闭", Close);
    }

    static FrameworkElement Gap(double h) => new() { Height = h };

    void RefreshList()
    {
        _list.Items.Clear();
        foreach (var n in Ctx.Duty.Names) _list.Items.Add(n);
        if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(Ctx.Duty.CurrentIndex, _list.Items.Count - 1);
        _current.Text = $"今日 {_ctx_DutyToday()} · 下一位 {(_ctx_DutyNext())}";
    }

    string _ctx_DutyToday() => Ctx.Duty.TodayName;
    string _ctx_DutyNext() => Ctx.Duty.NextName.Length == 0 ? "—" : Ctx.Duty.NextName;

    void AddName()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0)
        {
            Tell(this, "姓名不能为空。", "值日生名单");
            return;
        }
        var names = Ctx.Duty.Names.ToList();
        names.Add(name);
        Ctx.Duty.SetNames(names);
        _name.Clear();
        RefreshList();
    }

    void RemoveSelected()
    {
        var index = _list.SelectedIndex;
        if (index < 0) return;
        var names = Ctx.Duty.Names.ToList();
        if (index >= names.Count) return;
        names.RemoveAt(index);
        if (names.Count == 0)
        {
            Tell(this, "名单至少要留一个人。", "值日生名单");
            return;
        }
        Ctx.Duty.SetNames(names);
        RefreshList();
    }

    void Move(int delta)
    {
        var index = _list.SelectedIndex;
        var names = Ctx.Duty.Names.ToList();
        var target = index + delta;
        if (index < 0 || target < 0 || target >= names.Count) return;
        (names[index], names[target]) = (names[target], names[index]);
        Ctx.Duty.SetNames(names);
        RefreshList();
        _list.SelectedIndex = target;
    }

    void SaveAttendance()
    {
        var should = int.TryParse(_should.Text.Trim(), out var a) ? a : Ctx.Duty.Config.ShouldCount;
        var actual = int.TryParse(_actual.Text.Trim(), out var b) ? b : Ctx.Duty.Config.ActualCount;
        Ctx.Duty.SetAttendance(should.ToString(), actual.ToString());
    }
}

/// <summary>临时调课（对位 web 版 schedule.tempAdjust.*）。</summary>
public sealed class TempAdjustDialog : DialogPanel
{
    readonly TextBox _date;
    readonly StackPanel _rows = new();
    readonly TextBlock _note = new() { Style = S("EdEmpty"), Margin = new Thickness(0, 8, 0, 0) };

    public TempAdjustDialog(ViewModels.SettingsViewModel vm) : base(vm, "临时调课",
        "只替换选定这一天的课程（空 = 该节不上），其它天照常。", 560)
    {
        _date = Input(DateTime.Today.ToString("yyyy-MM-dd"), 150);
        _date.LostFocus += (_, _) => Rebuild();
        Add(Row(Label("日期"), _date));

        Add(Card("当天课程", "留空表示该节没有课", _rows));
        Add(_note);

        AddButton("清除当天", Clear, danger: true);
        AddButton("取消", Close);
        AddButton("保存", Save, primary: true);

        Rebuild();
    }

    DateTime PickedDate =>
        TempAdjust.TryParseDate(_date.Text, out var d) ? d.Date : DateTime.Today;

    void Rebuild()
    {
        _rows.Children.Clear();
        var date = PickedDate;
        var periods = Ctx.Schedule.Engine.PeriodsFor(date);
        var baseCourses = Ctx.Schedule.Engine.CoursesFor(date);
        var overrideToday = Ctx.Schedule.File.TempAdjust.Matches(date);
        var current = overrideToday ? Ctx.Schedule.File.TempAdjust.Courses : null;

        for (var i = 0; i < periods.Count; i++)
        {
            var p = periods[i];
            var text = current is not null && i < current.Count
                ? current[i]
                : (i < baseCourses.Count ? baseCourses[i] : "");
            _rows.Children.Add(Row(
                new TextBlock
                {
                    Text = $"{i + 1}. {p.Name}  {p.Start}-{p.End}",
                    Style = S("EdRowLabel"),
                    Width = 210,
                },
                Input(text, 160)));
        }

        if (periods.Count == 0) _rows.Children.Add(Empty("这一天没有节次。"));
        _note.Text = overrideToday ? "当前显示的是已保存的调课内容。" : "当前显示的是原课表，改完点保存生效。";
    }

    List<TextBox> CourseBoxes() =>
        _rows.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<TextBox>()).ToList();

    void Save()
    {
        var date = PickedDate;
        var count = Ctx.Schedule.Engine.PeriodsFor(date).Count;
        var boxes = CourseBoxes();
        var courses = new List<string>();
        for (var i = 0; i < count; i++)
            courses.Add(i < boxes.Count ? boxes[i].Text.Trim() : "");
        Ctx.Schedule.SetTempAdjust(date.ToString("yyyy-MM-dd"), courses);
        _note.Text = "已保存，当天课表即刻生效。";
        Tell(this, "临时调课已保存。", "临时调课");
    }

    void Clear()
    {
        Ctx.Schedule.ClearTempAdjust();
        Rebuild();
    }
}
