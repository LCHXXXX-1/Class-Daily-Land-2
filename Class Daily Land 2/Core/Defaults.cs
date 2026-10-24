using ClassDailyLand.Core.Models;

namespace ClassDailyLand.Core;

/// <summary>首次运行时写入的默认数据（与原 PySide6 版的初始课表形态一致）。</summary>
public static class Defaults
{
    public const string DefaultTimetableName = "默认课表";

    /// <summary>8 节正课 + 晚自习的时间表。</summary>
    public static readonly string[,] SampleCourses =
    {
        { "语文", "数学", "英语", "物理", "化学", "生物", "" },
        { "数学", "英语", "语文", "化学", "物理", "数学", "" },
        { "英语", "语文", "数学", "英语", "语文", "英语", "" },
        { "物理", "化学", "体育", "数学", "体育", "物理", "" },
        { "化学", "体育", "物理", "语文", "数学", "化学", "" },
        { "体育", "数学", "化学", "生物", "英语", "语文", "" },
        { "语文", "英语", "生物", "物理", "化学", "体育", "" },
        { "数学", "物理", "英语", "化学", "生物", "数学", "" },
    };

    public static List<Period> CreatePeriods() => new()
    {
        new Period("早读", "07:30", "08:10"),
        new Period("第1节", "08:20", "09:00"),
        new Period("第2节", "09:10", "09:50"),
        new Period("第3节", "10:00", "10:40"),
        new Period("第4节", "10:50", "11:30"),
        new Period("第5节", "14:30", "15:10"),
        new Period("第6节", "15:20", "16:00"),
        new Period("第7节", "16:10", "16:50"),
    };

    public static Timetable CreateTimetable()
    {
        var t = new Timetable { Periods = CreatePeriods() };
        for (var p = 0; p < t.Periods.Count; p++)
        {
            var row = new List<string>();
            for (var d = 0; d < 7; d++)
                row.Add(SampleCourses[p % SampleCourses.GetLength(0), d]);
            t.SetRow(p, row);
        }
        return t;
    }

    public static ScheduleFile CreateSchedule()
    {
        var sf = new ScheduleFile
        {
            Active = DefaultTimetableName,
            AdvanceMinutes = 2,
            TimeOffsetSeconds = 0,
        };
        sf.Timetables[DefaultTimetableName] = CreateTimetable();
        sf.Weekend = new WeekendConfig
        {
            MorningSplit = "12:00",
            AfternoonPeriods = new List<Period>(),
            Courses = new List<string>(),
        };
        return sf.Normalize();
    }

    public static List<string> CreateNames() => new()
    {
        "张三", "李四", "王五", "赵六",
    };

    public static List<HomeworkItem> CreateHomework() => new()
    {
        new HomeworkItem { Subject = "语文", Content = "完成第 3 课课后练习", Date = "" },
        new HomeworkItem { Subject = "数学", Content = "习题 1.2 全部", Date = "" },
    };

    public static DutyConfig CreateDuty() => new()
    {
        CurrentIndex = 0,
        LastDate = "",
        Should = "40",
        Actual = "0",
    };
}
