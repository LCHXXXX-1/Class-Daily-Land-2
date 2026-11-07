using ClassDailyLand2.Core.Engine;
using ClassDailyLand2.Core.Models;
using System.Globalization;

namespace ClassDailyLand2.Core;

/// <summary>作业在「作业区」里排在哪一段（数值越小越靠上）。</summary>
public enum HomeworkSlot
{
    /// <summary>正在提交时段内（本节下课 → 下节上课）：整个科目板块顶到最前。</summary>
    Top = 0,
    /// <summary>正常：没设提交时间，或者提交时间还没到。</summary>
    Normal = 1,
    /// <summary>补交：排在正常作业之后，但还在「已过提交时间」的上面。</summary>
    Late = 2,
    /// <summary>最底下：提交时段已经结束（下一节课上课了），也就是「已过提交时间」。</summary>
    Expired = 3,
}

/// <summary>
/// 「提交时间」的全部规则，纯逻辑、不碰 UI：
///
///   提交时段 = [due, due_end)：
///     due      = 选的那节课**下课**时间
///     due_end  = **下一节课上课**时间（最后一节到当天 23:59）
///
///   时段开始前              → 正常（按 homework.json 原顺序）
///   时段内                  → 顶到作业区最前 + 弹一条 Windows 通知（只在开始那一刻弹）
///   时段结束后              → 沉到最底下（「已过提交时间」），比补交/不交/自定义都低
///   补交                    → 排在正常作业之后、已过提交时间之前
///
/// 排位的单位是**整个科目板块**：同一科目有几条作业时，只要其中一条还在时段内，
/// 整个科目的板块都在最前面；所有条目都过完时段，整个板块一起沉到底。
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

    /// <summary>写盘用：整分仍然只存到分钟；带秒（测试课表）时保留秒，避免刷新时被截掉。</summary>
    public static string ToTextPrecise(DateTime value) =>
        value.Second == 0 && value.Millisecond == 0
            ? value.ToString(StorageFormat, CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>「今天 10:40」「11-06 15:30」这种给人看的时间。</summary>
    public static string Describe(DateTime value, DateTime now)
    {
        if (value.Date == now.Date) return $"今天 {value:HH:mm}";
        if (value.Date == now.Date.AddDays(1)) return $"明天 {value:HH:mm}";
        if (value.Date == now.Date.AddDays(-1)) return $"昨天 {value:HH:mm}";
        return value.Year == now.Year ? value.ToString("MM-dd HH:mm") : value.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>
    /// 把一个「今天的某节下课时间」（"HH:mm" / "HH:mm:ss"）拼成完整时刻；
    /// 解析失败返回当天 23:59。
    /// </summary>
    public static DateTime TimeOnDate(DateTime date, string hhmm)
    {
        if (!TimeUtil.TryParseSeconds(hhmm, out var seconds)) seconds = 23 * 60 * 60 + 59 * 60;
        return date.Date.AddSeconds(seconds);
    }

    /// <summary>
    /// 下拉框里每一节的文案（含没排课的晚自习）。
    /// 返回 <c>(文案, 提交时段开始, 提交时段结束)</c>：
    ///   开始 = 该节**下课**；结束 = **下一节上课**（最后一节到当天 23:59）。
    ///
    /// 三条文案规则，目的都是"同一个名字出现两次时必须能分辨"：
    ///   ① 课名唯一              → 「第3节 数学 · 10:40 下课」
    ///   ② 课名重复（下午三节数学）→ 「10:40 数学 · 第3节 下课」（时间打头）
    ///   ③ 这一节没排课（晚自习）  → 「晚自习 · 19:50 下课」；名字也重名时 → 「19:50 晚自习 · 第9节 下课」
    /// </summary>
    public static List<(string Text, DateTime Due, DateTime End, string Name, int Index)> PeriodOptions(
        IEnumerable<(int Index, Period Period, string Course)> slots, DateTime date)
    {
        var list = new List<(string, DateTime, DateTime, string, int)>();
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

        for (var i = 0; i < all.Count; i++)
        {
            var (index, period, rawCourse) = all[i];
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

            var due = TimeOnDate(date, end);

            // 提交时段 = [本节下课, 下一节上课)。最后一节没有"下一节"，给到当天 23:59。
            var windowEnd = date.Date.AddHours(23).AddMinutes(59);
            if (i + 1 < all.Count)
            {
                var nextStart = (all[i + 1].Period.Start ?? "").Trim();
                if (TimeUtil.TryParseSeconds(nextStart, out var nextSeconds))
                    windowEnd = date.Date.AddSeconds(nextSeconds);
            }

            // 课间没留空档 / 时间填反 → 至少留 1 分钟，否则永远进不了"提交中"
            if (windowEnd <= due) windowEnd = due.AddMinutes(1);

            // 展示名：有课程名用课程名（重名已经由引擎编号），否则用节次名
            var displayName = course.Length > 0 ? course : (name.Length > 0 ? name : ordinal);
            list.Add((text.Replace("  ", " ").Trim(), due, windowEnd, displayName, index));
        }
        return list;
    }

    /// <summary>这条作业该排在哪一段。</summary>
    public static HomeworkSlot SlotOf(HomeworkItem item, DateTime now)
    {
        if (item is null) return HomeworkSlot.Normal;
        if (item.IsLate) return HomeworkSlot.Late;              // 补交：正常作业之后

        var due = item.DueTime;
        if (due is null) return HomeworkSlot.Normal;            // 没设提交时间 = 老行为

        var end = item.DueEndTime;
        if (end is not null)
        {
            if (now >= end.Value) return HomeworkSlot.Expired;  // 下一节上课了 → 沉底
            if (now >= due.Value) return HomeworkSlot.Top;      // 下课到现在 → 顶到最前
            return HomeworkSlot.Normal;                         // 还没下课
        }

        // 没有时段终点（老数据 / 自定义时间）：沿用旧规则 ——
        // 程序亲眼看着它到点就置顶，否则过了点算过期。
        if (item.DueFiredTime is not null)
            return due.Value.Date == now.Date ? HomeworkSlot.Top : HomeworkSlot.Expired;

        return now < due.Value ? HomeworkSlot.Normal : HomeworkSlot.Expired;
    }

    /// <summary>
    /// 一个**科目板块**排在哪一段：取组内最靠上的那一条。
    /// 物理有两样作业时：第 1 样在时段内 → 整块 Top；第 1 样过完、第 2 样还没到 → 整块 Normal；
    /// 两样都过完 → 整块 Expired（沉底）。
    /// </summary>
    public static HomeworkSlot GroupSlot(IEnumerable<HomeworkItem> items, DateTime now)
    {
        var slot = HomeworkSlot.Normal;
        var first = true;
        foreach (var item in items)
        {
            var current = SlotOf(item, now);
            if (first || current < slot) slot = current;
            first = false;
        }
        return slot;
    }

    /// <summary>列表里那行小字（空串 = 不显示）。</summary>
    public static string Hint(HomeworkItem item, DateTime now)
    {
        if (item is null) return "";
        if (item.IsLate) return "补交 · 排最后";

        var due = item.DueTime;
        if (due is null) return "";

        // 提交时刻一到就标「已过提交时间」（这段时间里它会被顶到最上面）
        if (now >= due.Value) return "已过提交时间";

        return due.Value.Date == now.Date
            ? $"{due.Value:HH:mm} 截止"
            : $"{due.Value:MM-dd HH:mm} 截止";
    }

    /// <summary>
    /// 作业区那一行末尾的小字（空串 = 不显示）。
    /// 同一科目有两三条作业时（语文：晚自习交 / 第二节晚自习交），
    /// 只靠「语文」这个标题根本分不出是哪一条，所以每条都把自己的提交时间挂出来。
    ///
    /// 有 due_name 时显示成「时间 + 这节课的名字」（如「10:00 自习(1)」）；
    /// 提交时刻一到，再追加「· 已过提交时间」。
    /// </summary>
    public static string BoardHint(HomeworkItem item, DateTime now)
    {
        if (item is null) return "";
        if (item.IsLate) return "（补交）";

        var due = item.DueTime;
        if (due is null) return "";

        var name = (item.DueName ?? "").Trim();
        string label;
        if (due.Value.Date == now.Date)
            label = name.Length > 0 ? $"{Clock(due.Value)} {name}" : $"{Clock(due.Value)} 交";
        else
            label = name.Length > 0 ? $"{due.Value:MM-dd} {name}" : $"{due.Value:MM-dd} 交";

        // 提交时刻一到就挂「已过提交时间」。整块在这段时间里顶到最上面，
        // 下一节课上课（DueEnd）之后沉到最底 —— 见 SlotOf。
        return now >= due.Value ? $"（{label} · 已过提交时间）" : $"（{label}）";
    }

    /// <summary>整分显示 "HH:mm"，带秒（测试用的 10 秒课时）显示 "HH:mm:ss"。</summary>
    static string Clock(DateTime value) =>
        value.Second == 0 ? value.ToString("HH:mm") : value.ToString("HH:mm:ss");

    /// <summary>
    /// 如果这条作业选的是「某一节课」，就按**当前课表**重算它的提交时间。
    /// 课表里那节课的时间改了、或者换了活动课表，作业会跟着走。
    /// 返回 true 表示字段有变化（调用方负责落盘）。
    /// </summary>
    public static bool RefreshPeriodDue(HomeworkItem item, ScheduleEngine? engine, DateTime now)
    {
        if (item is null || engine is null) return false;
        if (!string.Equals((item.DueKind ?? "").Trim(), HomeworkDueKinds.Period, StringComparison.OrdinalIgnoreCase))
            return false;
        if (item.DuePeriod < 0) return false;       // 老数据 / 自定义 / 补交 / 不交

        var date = item.DueTime?.Date ?? now.Date;
        List<(int Index, Period Period, string Course)> slots;
        try { slots = engine.DaySlots(date); }
        catch { return false; }

        var match = PeriodOptions(slots, date).FirstOrDefault(o => o.Index == item.DuePeriod);
        if (match.Text is null) return false;       // 这节课被删了 / 下标越界：保持原样

        var due = ToTextPrecise(match.Due);
        var end = ToTextPrecise(match.End);
        var name = match.Name;
        var label = match.Text;

        if (item.Due == due && item.DueEnd == end && item.DueName == name && item.DueLabel == label)
            return false;

        item.Due = due;
        item.DueEnd = end;
        item.DueName = name;
        item.DueLabel = label;
        item.DueFired = "";                         // 时间变了，旧的"已到点"标记作废
        return true;
    }

    /// <summary>比较用的键：同一条作业改了内容/时间就算新的一条。</summary>
    public static string KeyOf(HomeworkItem item) =>
        $"{item.Subject}\u0001{item.Content}\u0001{item.Due}\u0001{item.DueEnd}\u0001{item.DuePeriod}\u0001{item.DueKind}";
}
