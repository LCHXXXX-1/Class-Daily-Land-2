using ClassDailyLand2.Core.Models;
using System.Globalization;

namespace ClassDailyLand2.Core;

/// <summary>作业在「作业区」里排在哪一段。</summary>
public enum HomeworkSlot
{
    /// <summary>今天在程序里到点了（<c>due_fired</c> 有值）：顶到最前面。</summary>
    Due = 0,
    /// <summary>正常：没设提交时间，或者时间还没到。</summary>
    Normal = 1,
    /// <summary>最底下：补交，或者提交时间过去时程序不在场（错过了）。</summary>
    Bottom = 2,
}

/// <summary>
/// 「提交时间」的全部规则，纯逻辑、不碰 UI：
///
///   到点那一刻（程序在场）  → 打上 due_fired，顶到作业区第一位 + 弹一条 Windows 通知
///   没设 / 还没到点          → 按 homework.json 的原顺序
///   补交 / 已过点但没触发    → 沉到作业区最底下（"过了交作业的时间"就放底下）
///
/// 为什么用 due_fired 而不是"只要过了就算过期"：
///   用户想要的是「到点那一刻作业跳上来提醒我」，而「已经过去的时间」要沉底。
///   两者都是"时间在现在之前"，只能靠"是不是程序亲眼看着它到点"来区分。
/// </summary>
public static class HomeworkDue
{
    /// <summary>存储格式（本地时间，不带时区）。</summary>
    public const string StorageFormat = "yyyy-MM-dd HH:mm";

    /// <summary>
    /// 到点补触发的宽容窗口：程序打盹（睡眠/卡顿）不超过这个秒数，醒来后仍然算"刚过点"。
    /// 超过就当错过了 —— 直接沉底，不再补弹通知。
    /// </summary>
    public const int CatchUpSeconds = 120;

    /// <summary>能认的时间写法（自定义时间输入框也用这套）。</summary>
    public static readonly string[] ParseFormats =
    {
        "yyyy-M-d H:m", "yyyy/M/d H:m", "yyyy.M.d H:m",
        "yyyy-M-d H:m:s", "yyyy/M/d H:m:s", "yyyy.M.d H:m:s",
        "yyyy-M-d", "yyyy/M/d", "yyyy.M.d",
        "H:m",
    };

    public static bool TryParse(string? text, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (DateTime.TryParseExact(text.Trim(), ParseFormats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
            return true;

        // 兜底：系统区域格式（"2026/11/7 15:30" 之类）
        return DateTime.TryParse(text.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None, out value);
    }

    public static string ToText(DateTime value) =>
        value.ToString(StorageFormat, CultureInfo.InvariantCulture);

    /// <summary>「今天 10:40」「11-06 15:30」这种给人看的时间。</summary>
    public static string Describe(DateTime value, DateTime now)
    {
        if (value.Date == now.Date) return $"今天 {value:HH:mm}";
        if (value.Date == now.Date.AddDays(1)) return $"明天 {value:HH:mm}";
        if (value.Date == now.Date.AddDays(-1)) return $"昨天 {value:HH:mm}";
        return value.Year == now.Year ? value.ToString("MM-dd HH:mm") : value.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>
    /// 把一个「今天的某节下课时间」（"HH:mm"）拼成完整时刻；解析失败返回当天 23:59。
    /// </summary>
    public static DateTime TimeOnDate(DateTime date, string hhmm)
    {
        if (!TimeUtil.TryParse(hhmm, out var minutes)) minutes = 23 * 60 + 59;
        return date.Date.AddMinutes(minutes);
    }

    /// <summary>
    /// 下拉框里每一节的文案（含没排课的晚自习），以该节**下课**为提交时间。
    ///
    /// 三条规则，目的都是"同一个名字出现两次时必须能分辨"：
    ///   ① 课名唯一              → 「第3节 数学 · 10:40 下课」
    ///   ② 课名重复（下午三节数学）→ 「10:40 数学 · 第3节 下课」（时间打头）
    ///   ③ 这一节没排课（晚自习）  → 「晚自习 · 19:50 下课」；名字也重名时 → 「19:50 晚自习 · 第9节 下课」
    /// </summary>
    public static List<(string Text, DateTime Due)> PeriodOptions(
        IEnumerable<(int Index, Period Period, string Course)> slots, DateTime date)
    {
        var list = new List<(string, DateTime)>();
        var all = slots?.ToList() ?? new List<(int Index, Period Period, string Course)>();
        if (all.Count == 0) return list;

        var courseRepeats = all
            .Where(s => !string.IsNullOrWhiteSpace(s.Course))
            .GroupBy(s => s.Course.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var nameRepeats = all
            .GroupBy(s => (s.Period.Name ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (index, period, rawCourse) in all)
        {
            var course = (rawCourse ?? "").Trim();
            var name = (period.Name ?? "").Trim();
            var end = (period.End ?? "").Trim();
            var ordinal = $"第{index + 1}节";

            string text;
            if (course.Length > 0)
            {
                text = courseRepeats.Contains(course)
                    ? $"{end} {course} · {(name.Length > 0 && name != ordinal ? name : ordinal)} 下课"
                    : $"{(name.Length > 0 ? name : ordinal)} {course} · {end} 下课";
            }
            else
            {
                var title = name.Length > 0 ? name : ordinal;
                text = nameRepeats.Contains(name)
                    ? $"{end} {title} · {ordinal} 下课"
                    : $"{title} · {end} 下课";
            }

            list.Add((text.Replace("  ", " ").Trim(), TimeOnDate(date, end)));
        }
        return list;
    }

    /// <summary>这条作业该排在哪一段。</summary>
    public static HomeworkSlot SlotOf(HomeworkItem item, DateTime now)
    {
        if (item is null) return HomeworkSlot.Normal;
        if (item.IsLate) return HomeworkSlot.Bottom;            // 补交：无条件沉底

        var due = item.DueTime;
        if (due is null) return HomeworkSlot.Normal;            // 没设提交时间 = 老行为

        if (item.DueFiredTime is not null)
            return due.Value.Date == now.Date ? HomeworkSlot.Due : HomeworkSlot.Bottom;

        // 还没到点 → 正常；到点了但没触发（程序不在场 / 建的时候就已经过去）→ 沉底
        return due.Value > now ? HomeworkSlot.Normal : HomeworkSlot.Bottom;
    }

    /// <summary>列表里那行小字（空串 = 不显示）。</summary>
    public static string Hint(HomeworkItem item, DateTime now)
    {
        if (item is null) return "";
        if (item.IsLate) return "补交 · 排最后";

        var due = item.DueTime;
        if (due is null) return "";

        if (item.DueFiredTime is not null)
            return due.Value.Date == now.Date ? $"{due.Value:HH:mm} 到点 · 已置顶" : "已过期";

        if (due.Value <= now) return "已过点 · 排最后";

        return due.Value.Date == now.Date
            ? $"{due.Value:HH:mm} 截止"
            : $"{due.Value:MM-dd HH:mm} 截止";
    }

    /// <summary>
    /// 作业区那一行末尾的小字（空串 = 不显示）。
    /// 同一科目有两三条作业时（语文：晚自习交 / 第二节晚自习交），
    /// 只靠「语文」这个标题根本分不出是哪一条，所以每条都把自己的提交时间挂出来。
    /// </summary>
    public static string BoardHint(HomeworkItem item, DateTime now)
    {
        if (item is null) return "";
        if (item.IsLate) return "补交";

        var due = item.DueTime;
        if (due is null) return "";

        if (item.DueFiredTime is not null)
            return due.Value.Date == now.Date ? $"{due.Value:HH:mm} 到点" : "已过期";

        if (due.Value <= now) return "已过点";

        return due.Value.Date == now.Date
            ? $"{due.Value:HH:mm} 交"
            : $"{due.Value:MM-dd} 交";
    }

    /// <summary>比较用的键：同一条作业改了内容就算新的一条。</summary>
    public static string KeyOf(HomeworkItem item) =>
        $"{item.Subject}\u0001{item.Content}\u0001{item.Due}\u0001{item.DueKind}";
}
