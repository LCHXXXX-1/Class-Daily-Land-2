using ClassDailyLand2.Core;
using ClassDailyLand2.Core.Models;
using Xunit;

namespace ClassDailyLand2.Core.Tests;

/// <summary>
/// 作业「提交时间」的三段排位 + 到点触发。
///
/// 规则（用户原话的落点）：
///   · 今天到点且程序在场 → 顶到第一行（due_fired 有值）
///   · 补交、已经过点但没赶上 → 沉到最底下
///   · 没设 / 还没到点     → 按原顺序
/// </summary>
public class HomeworkDueTests
{
    static readonly DateTime Now = new(2026, 11, 7, 10, 41, 0);   // 本地时间：周六 10:41

    static HomeworkItem Item(string due, string kind = HomeworkDueKinds.Period, string fired = "") =>
        new() { Subject = "数学", Content = "习题", Due = due, DueKind = kind, DueFired = fired };

    // ——— 排位 ———

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
    public void DueButNotFired_SinksToBottom()
    {
        // 建的时候时间就已经过去（或程序当时不在场）
        Assert.Equal(HomeworkSlot.Bottom, HomeworkDue.SlotOf(Item("2026-11-07 10:00"), Now));
    }

    [Fact]
    public void DueAndFired_MovesToTop()
    {
        Assert.Equal(HomeworkSlot.Due, HomeworkDue.SlotOf(Item("2026-11-07 10:40", fired: "2026-11-07 10:40"), Now));
    }

    [Fact]
    public void FiredYesterday_SinksToday()
    {
        Assert.Equal(HomeworkSlot.Bottom,
            HomeworkDue.SlotOf(Item("2026-11-06 15:00", fired: "2026-11-06 15:00"), Now));
    }

    [Fact]
    public void Late_AlwaysBottom()
    {
        Assert.Equal(HomeworkSlot.Bottom, HomeworkDue.SlotOf(Item("", HomeworkDueKinds.Late), Now));
        Assert.Equal(HomeworkSlot.Bottom, HomeworkDue.SlotOf(Item("2026-11-09 09:00", HomeworkDueKinds.Late), Now));
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
        var items = new List<HomeworkItem> { Item("2026-11-07 11:30") };

        watcher.Tick(items, Now);
        Assert.False(watcher.LayoutChanged);       // 第一拍只是建立基线

        watcher.Tick(items, new DateTime(2026, 11, 7, 11, 31, 0));
        Assert.True(watcher.LayoutChanged);        // 过点未触发 → 掉到最底下，排位变了
    }

    // ——— 小字提示 ———

    [Fact]
    public void Hint_CoversTheThreeStates()
    {
        Assert.Equal("补交 · 排最后", HomeworkDue.Hint(Item("", HomeworkDueKinds.Late), Now));
        Assert.Equal("11:30 截止", HomeworkDue.Hint(Item("2026-11-07 11:30"), Now));
        Assert.Equal("已过点 · 排最后", HomeworkDue.Hint(Item("2026-11-07 10:00"), Now));
        Assert.Equal("10:40 到点 · 已置顶",
            HomeworkDue.Hint(Item("2026-11-07 10:40", fired: "2026-11-07 10:40"), Now));
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

    // ——— 下拉框文案：晚自习 / 重名 ———

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

    // ——— 作业区行尾小字 ———

    [Fact]
    public void BoardHint_ShowsOwnDueTime_SoSameSubjectIsTellable()
    {
        // 同一科目两条作业（晚自习交 / 第二节晚自习交）：行尾时间必须不一样
        var a = HomeworkDue.BoardHint(Item("2026-11-07 19:50"), Now);
        var b = HomeworkDue.BoardHint(Item("2026-11-07 20:50"), Now);

        Assert.Equal("19:50 交", a);
        Assert.Equal("20:50 交", b);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BoardHint_CoversLatePastAndNoDue()
    {
        Assert.Equal("补交", HomeworkDue.BoardHint(Item("", HomeworkDueKinds.Late), Now));
        Assert.Equal("已过点", HomeworkDue.BoardHint(Item("2026-11-07 09:00"), Now));
        Assert.Equal("已过期", HomeworkDue.BoardHint(Item("2026-11-06 09:00", fired: "2026-11-06 09:00"), Now));
        Assert.Equal("10:40 到点", HomeworkDue.BoardHint(Item("2026-11-07 10:40", fired: "2026-11-07 10:40"), Now));
        Assert.Equal("", HomeworkDue.BoardHint(Item("", HomeworkDueKinds.None), Now));
    }

    [Fact]
    public void BoardHint_FutureOtherDay_ShowsDate()
    {
        Assert.Equal("11-09 交", HomeworkDue.BoardHint(Item("2026-11-09 15:00"), Now));
    }
}
