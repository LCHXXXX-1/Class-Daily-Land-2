using ClassDailyLand2.Core;
using ClassDailyLand2.Core.Engine;
using ClassDailyLand2.Core.Models;

namespace ClassDailyLand2.App.Views;

/// <summary>下拉框里的一项：今天某一节的下课时间 / 自定义 / 补交 / 不交。</summary>
public sealed class HomeworkDueOption
{
    /// <summary>显示文案（也原样存进 due_label）。</summary>
    public string Text { get; init; } = "";

    /// <summary>写进 homework.json 的 due_kind（HomeworkDueKinds）。</summary>
    public string Kind { get; init; } = HomeworkDueKinds.None;

    /// <summary>提交时刻；补交 / 不交为 null。</summary>
    public DateTime? Due { get; init; }

    /// <summary>true = 选中它就去弹「自定义时间」窗口（本身不是一个可保存的状态）。</summary>
    public bool IsPicker { get; init; }

    public override string ToString() => Text;
}

/// <summary>
/// 「提交时间」下拉框的内容与回选。<b>独立「编辑作业」窗口</b>和<b>设置页的卡片编辑器</b>
/// 都用这一份，避免两处规则跑偏。
/// </summary>
public static class HomeworkDueOptions
{
    /// <summary>把「自定义时间…」那一项的文案（其他地方要判断"用户点了它"就看 IsPicker）。</summary>
    public const string PickerText = "自定义时间…";

    /// <summary>
    /// 生成整个下拉框：
    ///   今天每一节（含没排课的晚自习）→ 自定义 → 补交 → 不交；
    ///   如果当前状态是"自定义"且已有具体时间，会先在中间插一条显示出来。
    /// 当前选中的"某一节"如果今天已经不在课表里（隔天打开 / 临时调课），会补一条在最前面，避免值被弄丢。
    /// </summary>
    public static List<HomeworkDueOption> Build(ScheduleEngine? engine, DateTime now,
        string kind, DateTime? due, string label)
    {
        var list = new List<HomeworkDueOption>();

        var slots = engine?.DaySlots(now);
        if (slots is not null)
            foreach (var (text, at) in HomeworkDue.PeriodOptions(slots, now))
                list.Add(new HomeworkDueOption { Text = text, Kind = HomeworkDueKinds.Period, Due = at });

        if (kind == HomeworkDueKinds.Custom && due is not null)
            list.Add(new HomeworkDueOption
            {
                Text = $"自定义 · {HomeworkDue.Describe(due.Value, now)}",
                Kind = HomeworkDueKinds.Custom,
                Due = due,
            });

        list.Add(new HomeworkDueOption { Text = PickerText, Kind = HomeworkDueKinds.Custom, IsPicker = true });
        list.Add(new HomeworkDueOption { Text = "补交（排到最后）", Kind = HomeworkDueKinds.Late });
        list.Add(new HomeworkDueOption { Text = "不交（不设提交时间）", Kind = HomeworkDueKinds.None });

        if (Find(list, kind, due) is null && due is not null)
        {
            var text = string.IsNullOrWhiteSpace(label)
                ? $"{HomeworkDue.Describe(due.Value, now)} 截止"
                : label;
            list.Insert(0, new HomeworkDueOption { Text = text, Kind = kind, Due = due });
        }
        return list;
    }

    /// <summary>在一堆选项里找出与当前状态对应的那一项；找不到返回 null。</summary>
    public static HomeworkDueOption? Find(IEnumerable<HomeworkDueOption> options, string kind, DateTime? due)
    {
        foreach (var option in options)
        {
            if (option.IsPicker) continue;

            if (kind == HomeworkDueKinds.Period)
            {
                if (option.Kind == HomeworkDueKinds.Period && option.Due == due) return option;
                continue;
            }
            if (kind == HomeworkDueKinds.Custom)
            {
                if (option.Kind == HomeworkDueKinds.Custom && option.Due == due) return option;
                continue;
            }
            if (option.Kind == kind) return option;
        }
        return null;
    }

    /// <summary>下拉框旁边那句"这条作业会排在哪"。</summary>
    public static string HintFor(string kind, DateTime? due, DateTime now)
    {
        var probe = new HomeworkItem
        {
            Due = due is null ? "" : HomeworkDue.ToText(due.Value),
            DueKind = kind,
        };
        var slot = HomeworkDue.SlotOf(probe, now);

        if (kind == HomeworkDueKinds.Late) return "会排到作业区最底下";
        if (due is null) return "不设提交时间 · 按原顺序";
        if (slot == HomeworkSlot.Bottom) return "已过点 · 会排到最底下";
        if (slot == HomeworkSlot.Due) return "已到点 · 会顶到第一行";
        return $"{HomeworkDue.Describe(due.Value, now)} 到点后置顶并提醒";
    }

    // ——— 两个弹窗（警告 + 自定义时间）的文字只有这一份 ———

    public static bool WarnCustomTime(System.Windows.Window? owner) =>
        Dialogs.HomeworkDueDialogs.Warn(owner, "自定义提交时间？",
            "自定义时间可能让这条作业排到作业区最底下：\n" +
            "· 填的是今天、但时间已经过了 → 排到最底下\n" +
            "· 填的是以前的日子 → 同样排在最底下\n\n" +
            "想让它在到点时自动顶到第一行并弹通知，提交时间要晚于现在。",
            "继续");

    public static bool WarnLate(System.Windows.Window? owner) =>
        Dialogs.HomeworkDueDialogs.Warn(owner, "选择「补交」？",
            "补交的作业会固定排在作业区最底下，和其它过了提交时间的作业放在一起。\n" +
            "它到点也不会顶到第一行，也不会弹提醒。",
            "仍然补交");
}
