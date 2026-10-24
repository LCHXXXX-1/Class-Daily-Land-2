using ClassDailyLand.Core.Models;

namespace ClassDailyLand.Core.Engine;

/// <summary>灵动岛 / 主窗文案（格式与 Python 版一致，见需求文档第三节）。</summary>
public static class ScheduleText
{
    /// <summary>下节 语文 · 3分钟后</summary>
    public static string Upcoming(ScheduleStatus s) => $"下节 {s.Course} · {s.UntilMin}分钟后";

    /// <summary>本节：语文 还剩 12分钟</summary>
    public static string Ongoing(ScheduleStatus s) => $"本节：{s.Course} 还剩 {s.RemainMin}分钟";

    public static string Holiday(ScheduleStatus s) =>
        string.IsNullOrWhiteSpace(s.Name) ? "假期中" : $"假期中 · {s.Name}";

    public const string None = "今日无课";
    public const string Done = "今日课程已结束";

    /// <summary>主岛胶囊文案（不含插件后缀）。</summary>
    public static string Core(ScheduleStatus s) => s.Status switch
    {
        ScheduleEngine.StatusUpcoming => Upcoming(s),
        ScheduleEngine.StatusOngoing => Ongoing(s),
        ScheduleEngine.StatusHoliday => Holiday(s),
        ScheduleEngine.StatusDone => Done,
        _ => None,
    };

    /// <summary>若插件提供了 island_text，则追加 ` · {plugin_text}`。</summary>
    public static string WithPlugin(string core, string? pluginText) =>
        string.IsNullOrWhiteSpace(pluginText) ? core : core + " · " + pluginText.Trim();

    /// <summary>Alert 文案：上课了！/ 下课了！</summary>
    public static string AlertText(bool isStart) => isStart ? "上课了！" : "下课了！";

    /// <summary>「提前提醒」为 5 分钟时的示例文案自检用（下节 X · 5分钟后）。</summary>
    public static string CountdownLabel(ScheduleStatus s) => $"{s.UntilMin}";
}
