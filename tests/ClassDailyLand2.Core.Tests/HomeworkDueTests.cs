using ClassDailyLand2.Core;
using ClassDailyLand2.Core.Engine;
using ClassDailyLand2.Core.Models;
using Xunit;

namespace ClassDailyLand2.Core.Tests;

/// <summary>
/// 作业「提交时段」的排位 + 到点触发。
///
/// 规则（用户原话的落点）：
///   · 提交时段 = [本节下课, 下一节上课)
///   · 时段内         → 整个科目板块顶到最前
///   · 还没到 / 没设  → 按原顺序
///   · 补交           → 正常作业之后
///   · 时段结束       → 整块沉到最底（已过提交时间）
/// </summary>
public class HomeworkDueTests
{
    static readonly DateTime Now = new(2026, 11, 7, 10, 41, 0);   // 本地时间：周六 10:41

    static HomeworkItem Item(string due, string kind = HomeworkDueKinds.Period, string fired = "", string dueEnd = "") =>
        new() { Subject = "数学", Content = "习题", Due = due, DueEnd = dueEnd, DueKind = kind, DueFired = fired };

    // ——— 排位：没有时段的旧数据 / 自定义时间 ———

    [Fact]
    public void NoDue_StaysNormal()
    {
        Assert.Equal(HomeworkSlot.Normal, HomeworkDue.SlotOf(Item("", HomeworkDueKinds.None), Now));
    }

    [Fact]
    public void FutureDue_StaysNormal()
    {
        Assert.Equal(HomeworkSlot.Normal, HomeworkDue.SlotOf(Item("2026-11-07 11:30"), Now));
    }

    [Fact]
    public void DueButNotFired_WithNoWindow_SinksToBottom()
    {
        // 没有时段终点（老数据 / 自定义时间）：过了点就是「已过提交时间」
        Assert.Equal(HomeworkSlot.Expired, HomeworkDue.SlotOf(Item("2026-11-07 10:00"), Now));
    }

    [Fact]
    public void DueAndFired_WithNoWindow_MovesToTop()
    {
        // 自定义时间仍沿用旧规则：程序看着它到点就置顶
        Assert.Equal(HomeworkSlot.Top,
            HomeworkDue.SlotOf(Item("2026-11-07 10:40", fired: "2026-11-07 10:40"), Now));
    }

    [Fact]
    public void FiredYesterday_SinksToday()
    {
        Assert.Equal(HomeworkSlot.Expired,
            HomeworkDue.SlotOf(Item("2026-11-06 15:00", fired: "2026-11-06 15:00"), Now));
    }

    [Fact]
    public void Late_RanksAboveExpired_ButBelowNormal()
    {
        Assert.Equal(HomeworkSlot.Late, HomeworkDue.SlotOf(Item("", HomeworkDueKinds.Late), Now));
        Assert.Equal(HomeworkSlot.Late, HomeworkDue.SlotOf(Item("2026-11-09 09:00", HomeworkDueKinds.Late), Now));

        // 排序值：Normal < Late < Expired
        Assert.True(HomeworkSlot.Normal < HomeworkSlot.Late);
        Assert.True(HomeworkSlot.Late < HomeworkSlot.Expired);
    }

    // ——— 排位：提交时段 [due, dueEnd) ———

    [Fact]
    public void BeforeWindow_StaysNormal()
    {
        Assert.Equal(HomeworkSlot.Normal,
            HomeworkDue.SlotOf(Item("2026-11-07 10:41", dueEnd: "2026-11-07 10:55"), Now.AddMinutes(-1)));
    }

    [Fact]
    public void InsideWindow_MovesToTop()
    {
        var item = Item("2026-11-07 10:41", dueEnd: "2026-11-07 10:55");
        Assert.Equal(HomeworkSlot.Top, HomeworkDue.SlotOf(item, Now));
        Assert.Equal(HomeworkSlot.Top, HomeworkDue.SlotOf(item, Now.AddMinutes(13)));
    }

    [Fact]
    public void WindowEnd_SinksToBottom()
    {
        var item = Item("2026-11-07 10:41", dueEnd: "2026-11-07 10:55");
        Assert.Equal(HomeworkSlot.Expired, HomeworkDue.SlotOf(item, Now.AddMinutes(14)));
    }

    // ——— 板块排位：取组内最靠上的一条 ———

    [Fact]
    public void GroupSlot_AnyItemInWindow_PutsWholeBlockOnTop()
    {
        var items = new List<HomeworkItem>
        {
            Item("2026-11-07 10:41", dueEnd: "2026-11-07 10:55"),   // 时段内
            Item("2026-11-07 11:30", dueEnd: "2026-11-07 11:40"),   // 还没到
        };
        Assert.Equal(HomeworkSlot.Top, HomeworkDue.GroupSlot(items, Now));
    }

    [Fact]
    public void GroupSlot_FirstExpiredSecondPending_ReturnsToNormal()
    {
        // 用户描述：第 1 样过完、第 2 样还没到 → 整个物理板块回原位
        var items = new List<HomeworkItem>
        {
            Item("2026-11-07 10:35", dueEnd: "2026-11-07 10:40"),   // 已过
            Item("2026-11-07 10:45", dueEnd: "2026-11-07 10:50"),   // 还没到
        };
        Assert.Equal(HomeworkSlot.Normal, HomeworkDue.GroupSlot(items, Now));
    }

    [Fact]
    public void GroupSlot_AllExpired_SinksWholeBlock()
    {
        var items = new List<HomeworkItem>
        {
            Item("2026-11-07 10:35", dueEnd: "2026-11-07 10:40"),
            Item("2026-11-07 10:38", dueEnd: "2026-11-07 10:39"),
        };
        Assert.Equal(HomeworkSlot.Expired, HomeworkDue.GroupSlot(items, Now));
    }

    [Fact]
    public void GroupSlot_LateAndExpired_UsesLate()
    {
        var items = new List<HomeworkItem>
        {
            Item("", HomeworkDueKinds.Late),
            Item("2026-11-07 10:35", dueEnd: "2026-11-07 10:40"),
        };
        Assert.Equal(HomeworkSlot.Late, HomeworkDue.GroupSlot(items, Now));
    }

    // ——— 到点触发 ———

    [Fact]
    public void Watcher_FiresOnce_WithinCatchUpWindow()
    {
        var watcher = new HomeworkDueWatcher();
        var items = new List<HomeworkItem> { Item("2026-11-07 10:40", fired: "2026-11-07 10:40") };

        // 已经触发过的不会再报
        Assert.Empty(watcher.Tick(items, Now));

        var fresh = new List<HomeworkItem> { Item("2026-11-07 10:40") };
        var fired = watcher.Tick(fresh, Now);
        Assert.Single(fired);
        Assert.Same(fresh[0], fired[0]);
    }

    [Fact]
    public void Watcher_IgnoresLongMissedDue()
    {
        var watcher = new HomeworkDueWatcher();
        // 30 分钟前就该交的：当"错过了"，不再补通知
        var items = new List<HomeworkItem> { Item("2026-11-07 10:10") };
        Assert.Empty(watcher.Tick(items, Now));
    }

    [Fact]
    public void Watcher_ReportsLayoutChange_WhenSlotFlips()
    {
        var watcher = new HomeworkDueWatcher();
        var items = new List<HomeworkItem> { Item("2026-11-07 11:30", dueEnd: "2026-11-07 11:40") };

        watcher.Tick(items, Now);
        Assert.False(watcher.LayoutChanged);       // 第一拍只是建立基线

        watcher.Tick(items, new DateTime(2026, 11, 7, 11, 31, 0));
        Assert.True(watcher.LayoutChanged);        // 进入提交时段 → 排位变了
    }

    // ——— 小字提示 ———

    [Fact]
    public void Hint_CoversTheStates()
    {
        Assert.Equal("补交 · 排最后", HomeworkDue.Hint(Item("", HomeworkDueKinds.Late), Now));
        Assert.Equal("11:30 截止", HomeworkDue.Hint(Item("2026-11-07 11:30"), Now));
        Assert.Equal("已过提交时间", HomeworkDue.Hint(Item("2026-11-07 10:00"), Now));
        Assert.Equal("已过提交时间",
            HomeworkDue.Hint(Item("2026-11-07 10:41", dueEnd: "2026-11-07 10:55"), Now));
    }

    // ——— 解析 ———

    [Theory]
    [InlineData("2026-11-07 15:30")]
    [InlineData("2026/11/7 15:30")]
    [InlineData("2026.11.7 15:30:00")]
    [InlineData("2026-11-07")]
    public void TryParse_AcceptsCommonFormats(string text)
    {
        Assert.True(HomeworkDue.TryParse(text, out _));
    }

    [Fact]
    public void TryParse_RejectsGarbage()
    {
        Assert.False(HomeworkDue.TryParse("明天下午", out _));
        Assert.False(HomeworkDue.TryParse("", out _));
    }

    /// <summary>老 homework.json 没有这几个键：读出来是空串，排位保持原样。</summary>
    [Fact]
    public void LegacyItem_WithoutNewFields_BehavesAsBefore()
    {
        var legacy = new HomeworkItem { Subject = "语文", Content = "抄写", Date = "" };
        Assert.Null(legacy.DueTime);
        Assert.Null(legacy.DueEndTime);
        Assert.False(legacy.IsLate);
        Assert.Equal(HomeworkSlot.Normal, HomeworkDue.SlotOf(legacy, Now));
        Assert.Equal("", HomeworkDue.Hint(legacy, Now));
    }

    /// <summary>下拉框的「时间 + 名字」去重规则用到的拼时刻工具。</summary>
    [Fact]
    public void TimeOnDate_UsesPeriodEnd()
    {
        Assert.Equal(new DateTime(2026, 11, 7, 10, 40, 0),
            HomeworkDue.TimeOnDate(Now, "10:40"));
        Assert.Equal(new DateTime(2026, 11, 7, 23, 59, 0),
            HomeworkDue.TimeOnDate(Now, "乱写"));
    }

    // ——— 下拉框文案：晚自习 / 重名 / 时段 ———

    static List<(int Index, Period Period, string Course)> Slots(params (string Name, string Start, string End, string Course)[] rows)
    {
        var list = new List<(int, Period, string)>();
        for (var i = 0; i < rows.Length; i++)
            list.Add((i, new Period(rows[i].Name, rows[i].Start, rows[i].End), rows[i].Course));
        return list;
    }

    [Fact]
    public void PeriodOptions_IncludesPeriodsWithoutCourse()
    {
        // 晚自习通常不排课程名，但它一定得出现在下拉框里，否则根本选不到"晚自习交"
        var options = HomeworkDue.PeriodOptions(Slots(
            ("第10节", "21:00", "21:40", ""),
            ("晚自习", "22:00", "22:40", "")), Now);

        Assert.Equal(2, options.Count);
        Assert.Equal("第10节 · 21:40 下课", options[0].Text);
        Assert.Equal("晚自习 · 22:40 下课", options[1].Text);
        Assert.Equal(new DateTime(2026, 11, 7, 22, 40, 0), options[1].Due);
    }

    [Fact]
    public void PeriodOptions_WindowRunsUntilNextPeriodStart()
    {
        var options = HomeworkDue.PeriodOptions(Slots(
            ("第3节", "10:00", "10:40", "数学"),
            ("第4节", "10:50", "11:30", "语文")), Now);

        Assert.Equal(new DateTime(2026, 11, 7, 10, 40, 0), options[0].Due);
        Assert.Equal(new DateTime(2026, 11, 7, 10, 50, 0), options[0].End);   // 下一节上课
        Assert.Equal(new DateTime(2026, 11, 7, 11, 30, 0), options[1].Due);
        Assert.Equal(new DateTime(2026, 11, 7, 23, 59, 0), options[1].End);   // 最后一节到当天结束
    }

    [Fact]
    public void PeriodOptions_DisambiguatesDuplicateCourseNames()
    {
        // 下午三节都是数学：时间打头，避免选串
        var options = HomeworkDue.PeriodOptions(Slots(
            ("第5节", "14:30", "15:10", "数学"),
            ("第6节", "15:20", "16:00", "数学"),
            ("第7节", "16:10", "16:50", "英语")), Now);

        Assert.Equal("15:10 数学 · 第5节 下课", options[0].Text);
        Assert.Equal("16:00 数学 · 第6节 下课", options[1].Text);
        Assert.Equal("第7节 英语 · 16:50 下课", options[2].Text);
    }

    [Fact]
    public void PeriodOptions_DisambiguatesDuplicatePeriodNames()
    {
        // 「晚自习」两节同名且都没排课：用时间 + 节次序号区分
        var options = HomeworkDue.PeriodOptions(Slots(
            ("晚自习", "19:00", "19:50", ""),
            ("晚自习", "20:00", "20:50", "")), Now);

        Assert.Equal("19:50 晚自习 · 第1节 下课", options[0].Text);
        Assert.Equal("20:50 晚自习 · 第2节 下课", options[1].Text);
        Assert.NotEqual(options[0].Due, options[1].Due);
    }

    [Fact]
    public void PeriodOptions_EmptyScheduleReturnsNothing()
    {
        Assert.Empty(HomeworkDue.PeriodOptions(new List<(int, Period, string)>(), Now));
    }

    [Fact]
    public void PeriodOptions_ReturnsPeriodIndexes()
    {
        var options = HomeworkDue.PeriodOptions(Slots(
            ("第1节", "10:00", "10:40", "数学"),
            ("第2节", "10:50", "11:30", "语文")), Now);

        Assert.Equal(0, options[0].Index);
        Assert.Equal(1, options[1].Index);
    }

    // ——— 课表改了，按节次选的提交时间要跟着走 ———

    static ScheduleEngine EngineWith(params (string Name, string Start, string End, string Course)[] rows)
    {
        var sf = new ScheduleFile { Active = "测试" };
        var t = new Timetable();
        for (var i = 0; i < rows.Length; i++)
        {
            t.Periods.Add(new Period(rows[i].Name, rows[i].Start, rows[i].End));
            t.SetRow(i, Enumerable.Repeat(rows[i].Course, 7).ToArray());
        }
        sf.Timetables["测试"] = t;
        sf.Normalize();
        return new ScheduleEngine(sf);
    }

    [Fact]
    public void RefreshPeriodDue_FollowsPeriodTimeChange()
    {
        var date = new DateTime(2026, 11, 9);   // 周一
        var item = new HomeworkItem
        {
            Subject = "数学",
            Content = "习题",
            DueKind = HomeworkDueKinds.Period,
            DuePeriod = 0,
            Due = "2026-11-09 10:40",
            DueEnd = "2026-11-09 10:50",
            DueName = "数学",
            DueLabel = "第1节 数学 · 10:40 下课",
        };

        var before = EngineWith(("第1节", "10:00", "10:40", "数学"),
                                ("第2节", "10:50", "11:30", "语文"));
        Assert.False(HomeworkDue.RefreshPeriodDue(item, before, date.AddHours(10)));

        // 第一节改成 10:05 ~ 10:45，第二节不变
        var after = EngineWith(("第1节", "10:05", "10:45", "数学"),
                               ("第2节", "10:50", "11:30", "语文"));
        Assert.True(HomeworkDue.RefreshPeriodDue(item, after, date.AddHours(10)));

        Assert.Equal("2026-11-09 10:45", item.Due);
        Assert.Equal("2026-11-09 10:50", item.DueEnd);
        Assert.Equal("数学", item.DueName);
    }

    [Fact]
    public void RefreshPeriodDue_IgnoresCustomAndOldItems()
    {
        var engine = EngineWith(("第1节", "10:00", "10:40", "数学"));
        var now = new DateTime(2026, 11, 9, 10, 0, 0);

        var custom = new HomeworkItem { DueKind = HomeworkDueKinds.Custom, Due = "2026-11-09 10:40" };
        Assert.False(HomeworkDue.RefreshPeriodDue(custom, engine, now));

        var legacy = new HomeworkItem { DueKind = HomeworkDueKinds.Period, DuePeriod = -1, Due = "2026-11-09 10:40" };
        Assert.False(HomeworkDue.RefreshPeriodDue(legacy, engine, now));
    }

    // ——— 作业区行尾小字 ———

    [Fact]
    public void BoardHint_ShowsOwnDueTime_SoSameSubjectIsTellable()
    {
        // 同一科目两条作业（晚自习交 / 第二节晚自习交）：行尾时间必须不一样
        var a = HomeworkDue.BoardHint(Item("2026-11-07 19:50"), Now);
        var b = HomeworkDue.BoardHint(Item("2026-11-07 20:50"), Now);

        Assert.Equal("（19:50 交）", a);
        Assert.Equal("（20:50 交）", b);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BoardHint_MarksPastDueAsExpired()
    {
        Assert.Equal("（补交）", HomeworkDue.BoardHint(Item("", HomeworkDueKinds.Late), Now));
        Assert.Equal("（09:00 交 · 已过提交时间）", HomeworkDue.BoardHint(Item("2026-11-07 09:00"), Now));
        Assert.Equal("（11-06 交 · 已过提交时间）",
            HomeworkDue.BoardHint(Item("2026-11-06 09:00", fired: "2026-11-06 09:00"), Now));
        Assert.Equal("（10:41 交 · 已过提交时间）",
            HomeworkDue.BoardHint(Item("2026-11-07 10:41", dueEnd: "2026-11-07 10:55"), Now));
        Assert.Equal("", HomeworkDue.BoardHint(Item("", HomeworkDueKinds.None), Now));
    }

    [Fact]
    public void BoardHint_ShowsTimeAndCourseName()
    {
        var item = new HomeworkItem
        {
            Subject = "自习",
            Content = "自习(1)",
            Due = "2026-11-07 11:30",
            DueName = "自习(1)",
        };
        Assert.Equal("（11:30 自习(1)）", HomeworkDue.BoardHint(item, Now));

        item.Due = "2026-11-07 10:41";
        Assert.Equal("（10:41 自习(1) · 已过提交时间）", HomeworkDue.BoardHint(item, Now));
    }

    [Fact]
    public void BoardHint_FutureOtherDay_ShowsDate()
    {
        Assert.Equal("（11-09 交）", HomeworkDue.BoardHint(Item("2026-11-09 15:00"), Now));
    }
}
