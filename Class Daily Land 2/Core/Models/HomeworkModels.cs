using System.Globalization;
using System.Text.Json.Serialization;
namespace ClassDailyLand2.Core.Models;

/// <summary>
/// homework.json 的元素：{"subject","content","date","due","due_end","due_name","due_kind","due_label"}。
///
/// due / due_end / due_name / due_period / due_kind / due_label 是「提交时间」：
///   due       "yyyy-MM-dd HH:mm"（提交时段开始 = 那节课下课；空 = 没设）
///   due_end   "yyyy-MM-dd HH:mm"（提交时段结束 = 下一节课上课；空 = 没有时段）
///   due_name  这节课的展示名（如「自习(1)」），作业区行尾显示成「10:00 自习(1)」
///   due_period 选的是第几节（0 起）；-1 = 自定义/补交/不交/老数据
///   due_kind  ""（没设）/ "period"（今天某一节的下课）/ "custom"（自定义文本）/ "late"（补交）
///   due_label 存给人看的一句话，如「第3节 数学 · 10:40 下课」，课表改了也不会失真
///
/// 提交时段 = [due, due_end)：这段时间里整块顶到作业区最前；
/// due_end 一过就沉到最底（见 <see cref="HomeworkDue"/>）。
/// 老 homework.json 没有这些键，读出来就是空串，行为与之前完全一样。
/// </summary>
public sealed class HomeworkItem
{
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("due")] public string Due { get; set; } = "";
    [JsonPropertyName("due_end")] public string DueEnd { get; set; } = "";
    [JsonPropertyName("due_name")] public string DueName { get; set; } = "";
    /// <summary>选的是"某节课"时记录节次下标；-1 = 自定义/补交/不交/老数据。</summary>
    [JsonPropertyName("due_period")] public int DuePeriod { get; set; } = -1;
    [JsonPropertyName("due_kind")] public string DueKind { get; set; } = "";
    [JsonPropertyName("due_label")] public string DueLabel { get; set; } = "";

    /// <summary>
    /// 到点的那一刻程序在场、已经弹过通知的时间（"yyyy-MM-dd HH:mm"）；空 = 没触发过。
    /// 它同时决定排位：有值且就是今天 → 顶到作业区第一位；没值又已经过点 → 沉到最底下。
    /// </summary>
    [JsonPropertyName("due_fired")] public string DueFired { get; set; } = "";

    [JsonIgnore]
    public string Display
    {
        get
        {
            var s = string.IsNullOrWhiteSpace(Subject) ? "" : Subject.Trim() + "：";
            return s + (Content ?? "").Trim();
        }
    }

    /// <summary>解析后的提交时段开始（那节课下课）；没设 / 解析不了返回 null。</summary>
    [JsonIgnore]
    public DateTime? DueTime => HomeworkDue.TryParse(Due, out var t) ? t : null;

    /// <summary>解析后的提交时段结束（下一节课上课）；没有时段 / 解析不了返回 null。</summary>
    [JsonIgnore]
    public DateTime? DueEndTime => HomeworkDue.TryParse(DueEnd, out var t) ? t : null;

    /// <summary>解析后的"已触发"时刻；没触发过返回 null。</summary>
    [JsonIgnore]
    public DateTime? DueFiredTime => HomeworkDue.TryParse(DueFired, out var t) ? t : null;

    [JsonIgnore]
    public bool IsLate => string.Equals((DueKind ?? "").Trim(), HomeworkDueKinds.Late, StringComparison.OrdinalIgnoreCase);
}

/// <summary>「提交时间」的种类常量（存进 due_kind）。</summary>
public static class HomeworkDueKinds
{
    /// <summary>没设提交时间（老数据 / 用户选「不交」）。</summary>
    public const string None = "";
    /// <summary>今天某一节课的下课时间。</summary>
    public const string Period = "period";
    /// <summary>自定义日期 + 时间。</summary>
    public const string Custom = "custom";
    /// <summary>补交：固定排在作业区最底下。</summary>
    public const string Late = "late";
}

/// <summary>
/// config.json：值日生游标 + 出勤人数。字段类型与 Python 版一致（should/actual 是字符串）。
/// </summary>
public sealed class DutyConfig
{
    [JsonPropertyName("current_index")] public int CurrentIndex { get; set; }
    [JsonPropertyName("last_date")] public string LastDate { get; set; } = "";
    [JsonPropertyName("should")] public string Should { get; set; } = "40";
    [JsonPropertyName("actual")] public string Actual { get; set; } = "0";

    [JsonIgnore] public int ShouldCount => ParseInt(Should, 0);
    [JsonIgnore] public int ActualCount => ParseInt(Actual, 0);

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
