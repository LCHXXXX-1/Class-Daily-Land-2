using ClassDailyLand2.Core.Engine;
using ClassDailyLand2.Core.Models;
using Xunit;

namespace ClassDailyLand2.Core.Tests;

/// <summary>
/// ScheduleEngine 覆盖：上班/未到/下班/无课/假期/调休/周末/偏移/提前提醒/跨午夜/临时调课。
/// 约定：2026-10-19 是周一，10-24 周六，10-25 周日。
/// </summary>
public class ScheduleEngineTests
{
    static DateTime Monday(int h, int m = 0, int s = 0) => new(2026, 10, 19, h, m, s);
    static DateTime Saturday(int h, int m = 0, int s = 0) => new(2026, 10, 24, h, m, s);
    static DateTime Sunday(int h, int m = 0, int s = 0) => new(2026, 10, 25, h, m, s);

    /// <summary>两张节次：08:00-08:40 语文、09:00-09:40 数学（周六/周日为空）。</summary>
    static ScheduleFile Basic()
    {
        var sf = new ScheduleFile { Active = "默认课表" };
        var t = new Timetable
        {
            Periods =
            {
                new Period("第1节", "08:00", "08:40"),
                new Period("第2节", "09:00", "09:40"),
            }
        };
        t.SetRow(0, new[] { "语文", "数学", "英语", "物理", "化学", "", "" });
        t.SetRow(1, new[] { "数学", "英语", "语文", "化学", "物理", "", "" });
        sf.Timetables["默认课表"] = t;
        return sf.Normalize();
    }

    static ScheduleEngine Engine(ScheduleFile sf) => new(sf);

    // ——— 正在上课 ———

    [Fact]
    public void Ongoing_WithAdvance_StartsEarlyAndReportsRemain()
    {
        var s = Engine(Basic()).Compute(Monday(8, 20), advanceMinutes: 2);
        Assert.Equal("ongoing", s.Status);
        Assert.Equal("语文", s.Course);
        Assert.Equal("第1节", s.Period);
        Assert.Equal(20 * 60, s.RemainSec);
        Assert.Equal(20, s.RemainMin);
        Assert.Equal(2 * 60, s.AdvanceSec);
        // 提前 2 分钟 -> 有效时长 42 分钟（07:58 起算）
        Assert.Equal(42 * 60, s.DurationSec);
        Assert.Equal(0, s.Index);
    }

    [Fact]
    public void Ongoing_WithinAdvanceWindow_StillOngoing()
    {
        // 07:59 距离 08:00 还有 60 秒 < 提前 120 秒
        var s = Engine(Basic()).Compute(Monday(7, 59), advanceMinutes: 2);
        Assert.Equal("ongoing", s.Status);
        Assert.Equal("语文", s.Course);
    }

    [Fact]
    public void AdvanceWindow_IsIncludedInCountdownTarget()
    {
        // 提前提醒改变“下节课何时算开始”，文案里的分钟数据此计算
        var s = Engine(Basic()).Compute(Monday(7, 50), advanceMinutes: 5);
        Assert.Equal("upcoming", s.Status);
        Assert.Equal(0, s.UntilSec % 60);
        Assert.Equal(5, s.UntilMin);   // 07:50 -> 07:55(=08:00-5min)
        Assert.Equal("下节 语文 · 5分钟后", ScheduleText.Upcoming(s));
    }

    // ——— 未到 ———

    [Fact]
    public void Upcoming_FirstNonEmptyPeriod_Wins()
    {
        var s = Engine(Basic()).Compute(Monday(7, 0), advanceMinutes: 2);
        Assert.Equal("upcoming", s.Status);
        Assert.Equal("语文", s.Course);
        Assert.Equal(58 * 60, s.UntilSec);   // 到 07:58
        Assert.Equal(58, s.UntilMin);
        Assert.Equal(0, s.RemainSec);
    }

    [Fact]
    public void Upcoming_BetweenPeriods_ReturnsNextPeriod()
    {
        var s = Engine(Basic()).Compute(Monday(8, 45), advanceMinutes: 2);
        Assert.Equal("upcoming", s.Status);
        Assert.Equal("数学", s.Course);
        Assert.Equal(1, s.Index);
        Assert.Equal(13 * 60, s.UntilSec);
    }

    [Fact]
    public void Upcoming_CeilsMinutes()
    {
        var s = Engine(Basic()).Compute(Monday(7, 55, 30), advanceMinutes: 2);
        Assert.Equal(150, s.UntilSec);
        Assert.Equal(3, s.UntilMin);   // 2.5 分钟向上取整为 3
    }

    // ——— 下班 / 无课 ———

    [Fact]
    public void Done_AfterLastPeriod()
    {
        var s = Engine(Basic()).Compute(Monday(10, 0), advanceMinutes: 2);
        Assert.Equal("done", s.Status);
        Assert.Equal("数学", s.Course);
        Assert.Equal(1, s.Index);
    }

    [Fact]
    public void None_WhenAllCoursesEmpty()
    {
        var sf = Basic();
        sf.ActiveTimetable.SetRow(0, new[] { "", "", "", "", "", "", "" });
        sf.ActiveTimetable.SetRow(1, new[] { "", "", "", "", "", "", "" });
        var s = Engine(sf).Compute(Monday(9, 0));
        Assert.Equal("none", s.Status);
        Assert.Equal("今日无课", ScheduleText.Core(s));
    }

    // ——— 假期 ———

    [Fact]
    public void Holiday_WithinRange()
    {
        var sf = Basic();
        sf.Holidays.Add(new Holiday { Start = "2026-10-01", End = "2026-10-07", Name = "国庆节" });
        var inside = Engine(sf).Compute(new DateTime(2026, 10, 3, 9, 0, 0));
        Assert.Equal("holiday", inside.Status);
        Assert.Equal("国庆节", inside.Name);
        Assert.Equal("假期中 · 国庆节", ScheduleText.Holiday(inside));

        var outside = Engine(sf).Compute(Monday(8, 20));
        Assert.Equal("ongoing", outside.Status);
    }

    [Fact]
    public void Holiday_MakeupDay_Overrides()
    {
        var sf = Basic();
        sf.Holidays.Add(new Holiday { Start = "2026-10-24", End = "2026-10-25", Name = "调休补班" });
        sf.MakeupDays.Add(new MakeupDay { Date = "2026-10-24", AsWeekday = 0 });   // 周六按周一上课
        var s = Engine(sf).Compute(Saturday(8, 20), advanceMinutes: 2);
        Assert.Equal("ongoing", s.Status);
        Assert.Equal("语文", s.Course);
    }

    // ——— 调休 ———

    [Fact]
    public void MakeupDay_UsesSpecifiedWeekdayCourses()
    {
        var sf = Basic();
        sf.MakeupDays.Add(new MakeupDay { Date = "2026-10-25", AsWeekday = 4 });   // 周日按周五（化学/物理）
        var s = Engine(sf).Compute(Sunday(8, 20), advanceMinutes: 2);
        Assert.Equal("ongoing", s.Status);
        Assert.Equal("化学", s.Course);
    }

    // ——— 周末 ———

    [Fact]
    public void Weekend_MorningFollowsWorkday_AfternoonCustom()
    {
        var sf = Basic();
        sf.Weekend = new WeekendConfig
        {
            MorningSplit = "12:00",
            AfternoonPeriods = { new Period("下午1", "14:00", "15:00") },
            Courses = { "社团活动" },
        };
        var eng = Engine(sf);

        var morning = eng.Compute(Saturday(8, 20), advanceMinutes: 2);
        Assert.Equal("ongoing", morning.Status);
        Assert.Equal("语文", morning.Course);     // 周六列为空 -> 回落周一列

        var afternoon = eng.Compute(Saturday(14, 30), advanceMinutes: 2);
        Assert.Equal("ongoing", afternoon.Status);
        Assert.Equal("社团活动", afternoon.Course);

        var none = eng.Compute(Saturday(16, 0), advanceMinutes: 2);
        Assert.Equal("done", none.Status);
    }

    [Fact]
    public void Weekend_NoPeriodsBeforeSplit_IsNone()
    {
        var sf = Basic();
        sf.Weekend = new WeekendConfig { MorningSplit = "07:00" };
        var s = Engine(sf).Compute(Sunday(9, 0));
        Assert.Equal("none", s.Status);
    }

    /// <summary>
    /// 「课表编辑」保存周末表时写 morning_split = "00:00"，表示整天都由周末表决定、
    /// 不再从平日课表继承任何节次（周末表没有上午 / 下午分界点）。
    /// </summary>
    [Fact]
    public void Weekend_SelfContainedTable_DoesNotInheritWeekdayPeriods()
    {
        var sf = Basic();
        sf.Weekend = new WeekendConfig
        {
            MorningSplit = "00:00",
            AfternoonPeriods =
            {
                new Period("周1", "10:00", "10:40"),
                new Period("周2", "11:00", "11:40"),
            },
            Courses = { "自习", "社团活动" },
        };
        var eng = Engine(sf);

        // 平日 08:00 / 09:00 的节次不该被搬过来
        var periods = eng.PeriodsFor(Saturday(8, 20));
        Assert.Equal(2, periods.Count);
        Assert.Equal("10:00", periods[0].Start);

        // 08:20 时下一节是周末表的 10:00（平日 08:00 那节已不存在）
        var before = eng.Compute(Saturday(8, 20));
        Assert.Equal("upcoming", before.Status);
        Assert.Equal("周1", before.Period);

        var first = eng.Compute(Saturday(10, 10));
        Assert.Equal("ongoing", first.Status);
        Assert.Equal("自习", first.Course);

        // 周六、周日共用同一张表
        var second = eng.Compute(Sunday(11, 10));
        Assert.Equal("ongoing", second.Status);
        Assert.Equal("社团活动", second.Course);
    }

    // ——— 时间偏移 ———

    [Fact]
    public void OffsetSeconds_ShiftsEffectiveStart()
    {
        var eng = Engine(Basic());
        var baseS = eng.Compute(Monday(8, 10), advanceMinutes: 0);
        Assert.Equal("ongoing", baseS.Status);

        // 推迟 15 分钟：08:10 时还没上课
        var late = eng.Compute(Monday(8, 10), advanceMinutes: 0, offsetSeconds: 15 * 60);
        Assert.Equal("upcoming", late.Status);
        Assert.Equal(5 * 60, late.UntilSec);

        // 提前 10 分钟：08:10 已在上节课中（本节有效结束 08:30）
        var early = eng.Compute(Monday(8, 10), advanceMinutes: 0, offsetSeconds: -10 * 60);
        Assert.Equal("ongoing", early.Status);
        Assert.Equal(20 * 60, early.RemainSec);
    }

    [Fact]
    public void OffsetSeconds_AppliesToPeriodDisplayTimes()
    {
        var s = Engine(Basic()).Compute(Monday(7, 30), advanceMinutes: 2, offsetSeconds: 300);
        Assert.Equal("upcoming", s.Status);
        Assert.Equal("08:00", s.Start);   // 展示仍是课表原始时间
        Assert.Equal(33 * 60, s.UntilSec); // 有效开始 08:05 - 2min = 08:03
    }

    // ——— 跨午夜 ———

    [Fact]
    public void CrossMidnight_PeriodSpansTwoDays()
    {
        var sf = new ScheduleFile { Active = "夜校" };
        var t = new Timetable { Periods = { new Period("晚自习", "23:50", "00:40") } };
        t.SetRow(0, new[] { "数学", "", "", "", "", "", "" });
        sf.Timetables["夜校"] = t;

        var s = Engine(sf.Normalize()).Compute(Monday(23, 55), advanceMinutes: 2);
        Assert.Equal("ongoing", s.Status);
        Assert.Equal(45 * 60, s.RemainSec);    // 到次日 00:40
        Assert.Equal(52 * 60, s.DurationSec);  // 含 2 分钟提前量
    }

    // ——— 临时调课 ———

    [Fact]
    public void TempAdjust_ReplacesCoursesOfTheDay()
    {
        var sf = Basic();
        sf.TempAdjust = new TempAdjust { Date = "2026-10-19", Courses = { "音乐", "美术" } };
        var s = Engine(sf).Compute(Monday(8, 20), advanceMinutes: 2);
        Assert.Equal("音乐", s.Course);
        Assert.Equal("ongoing", s.Status);
    }

    [Fact]
    public void TempAdjust_OtherDateIgnored()
    {
        var sf = Basic();
        sf.TempAdjust = new TempAdjust { Date = "2026-10-20", Courses = { "音乐", "美术" } };
        var s = Engine(sf).Compute(Monday(8, 20), advanceMinutes: 2);
        Assert.Equal("语文", s.Course);
    }

    // ——— 文案 ———

    [Theory]
    [InlineData("ongoing", "本节：语文 还剩 20分钟")]
    public void TextFormats(string status, string expected)
    {
        var s = Engine(Basic()).Compute(Monday(8, 20), advanceMinutes: 2);
        Assert.Equal(status, s.Status);
        Assert.Equal(expected, ScheduleText.Core(s));
        Assert.Equal(expected + " · 插播", ScheduleText.WithPlugin(ScheduleText.Core(s), "插播"));
        Assert.Equal(expected, ScheduleText.WithPlugin(expected, null));
    }

    [Fact]
    public void DayTable_MarksCurrentAndPast()
    {
        var rows = Engine(Basic()).DayTable(Monday(8, 45), advanceMinutes: 2);
        Assert.Equal(2, rows.Count);
        Assert.Equal("past", rows[0].State);
        Assert.Equal("future", rows[1].State);
        var now = Engine(Basic()).DayTable(Monday(8, 20), advanceMinutes: 2);
        Assert.Equal("now", now[0].State);
    }

    [Fact]
    public void Text_UsesSecondsInsideFinalMinute()
    {
        // 本节还剩 5 秒
        var ongoing = Engine(Basic()).Compute(Monday(8, 39, 55), advanceMinutes: 0);
        Assert.Equal("本节：语文 还剩 5秒", ScheduleText.Core(ongoing));

        // 距离下节还有 5 秒
        var upcoming = Engine(Basic()).Compute(Monday(8, 59, 55), advanceMinutes: 0);
        Assert.Equal("下节 数学 · 5秒后", ScheduleText.Core(upcoming));
    }

    [Fact]
    public void ActiveTimetable_FallsBackToFirst()
    {
        var sf = Basic();
        sf.Active = "不存在的课表";
        Assert.NotNull(sf.ActiveTimetable);
        Assert.Equal("默认课表", sf.Active);
    }

    // ——— 秒级课表 + 重名课程编号（测试按钮依赖这两条） ———

    [Fact]
    public void DuplicateCourseNames_AreNumberedOnlyWhenRepeated()
    {
        var sf = new ScheduleFile { Active = "测试" };
        var t = new Timetable
        {
            Periods =
            {
                new Period("第1节", "10:00:00", "10:00:10"),
                new Period("第2节", "10:00:20", "10:00:30"),
                new Period("第3节", "10:00:40", "10:00:50"),
            }
        };
        t.SetRow(0, new[] { "自习", "自习", "自习", "自习", "自习", "自习", "自习" });
        t.SetRow(1, new[] { "自习", "自习", "自习", "自习", "自习", "自习", "自习" });
        t.SetRow(2, new[] { "数学自习", "数学自习", "数学自习", "数学自习", "数学自习", "数学自习", "数学自习" });
        sf.Timetables["测试"] = t;
        sf.Normalize();

        var courses = Engine(sf).CoursesOfDay(Monday(10, 0, 5)).Select(c => c.Course).ToList();

        // 「自习、自习、数学自习」→ 自习(1)、自习(2)、数学自习（名字不同不加序号）
        Assert.Equal(new[] { "自习(1)", "自习(2)", "数学自习" }, courses);
    }

    [Fact]
    public void SecondPrecisionPeriod_RunsAndEndsInTenSeconds()
    {
        var sf = new ScheduleFile { Active = "测试" };
        var t = new Timetable { Periods = { new Period("第1节", "10:00:00", "10:00:10") } };
        t.SetRow(0, new[] { "自习", "自习", "自习", "自习", "自习", "自习", "自习" });
        sf.Timetables["测试"] = t;
        sf.Normalize();

        var engine = Engine(sf);
        var ongoing = engine.Compute(Monday(10, 0, 5), advanceMinutes: 0);
        Assert.Equal("ongoing", ongoing.Status);
        Assert.Equal(5, ongoing.RemainSec);

        var done = engine.Compute(Monday(10, 0, 11), advanceMinutes: 0);
        Assert.Equal("done", done.Status);
    }
}
