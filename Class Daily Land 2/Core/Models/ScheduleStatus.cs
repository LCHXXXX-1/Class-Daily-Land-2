namespace ClassDailyLand.Core.Models;

/// <summary>
/// 课表状态计算结果。字段与 Python 版逐字一致（见需求文档第四节）。
/// </summary>
public sealed record ScheduleStatus(
    string Status,      // "upcoming" / "ongoing" / "holiday" / "none" / "done"
    string Course,
    string Period,
    string Start,
    string End,
    int UntilSec,
    int UntilMin,
    int AdvanceSec,
    int RemainSec,
    int RemainMin,
    int DurationSec,
    int Index,
    string? Name)
{
    public static ScheduleStatus Empty { get; } =
        new("none", "", "", "", "", 0, 0, 0, 0, 0, 0, -1, null);

    public bool IsUpcoming => Status == "upcoming";
    public bool IsOngoing => Status == "ongoing";
}
