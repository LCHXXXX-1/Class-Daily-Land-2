using System.Globalization;
using System.Text.Json.Serialization;

namespace ClassDailyLand2.Core.Models;

/// <summary>一节课的时间段（早读/第1节…）。start/end 为 "HH:mm"。</summary>
public sealed class Period
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("start")] public string Start { get; set; } = "";
    [JsonPropertyName("end")] public string End { get; set; } = "";

    public Period() { }
    public Period(string name, string start, string end) { Name = name; Start = start; End = end; }

    [JsonIgnore] public bool IsValid => TimeUtil.TryParse(Start, out _) && TimeUtil.TryParse(End, out _);
}

/// <summary>
/// 单张课表。periods 为行（节次），timetable 的 key 是节次下标的字符串（"0".."n"），
/// value 是 7 个元素，依次为 周一..周日 的课程名（空串表示本节无课）。
/// </summary>
public sealed class Timetable
{
    [JsonPropertyName("periods")] public List<Period> Periods { get; set; } = new();

    [JsonPropertyName("timetable")] public Dictionary<string, List<string>> Days { get; set; } = new();

    public string CourseAt(int periodIndex, int weekday)
    {
        if (periodIndex < 0 || weekday < 0 || weekday > 6) return "";
        if (!Days.TryGetValue(periodIndex.ToString(CultureInfo.InvariantCulture), out var row)) return "";
        if (row is null || weekday >= row.Count) return "";
        return row[weekday] ?? "";
    }

    public void SetCourse(int periodIndex, int weekday, string course)
    {
        var key = periodIndex.ToString(CultureInfo.InvariantCulture);
        if (!Days.TryGetValue(key, out var row) || row is null) Days[key] = row = new List<string>();
        while (row.Count < 7) row.Add("");
        row[weekday] = course ?? "";
    }

    /// <summary>把某节次的 7 天数组整体写入（越界自动补齐）。</summary>
    public void SetRow(int periodIndex, IEnumerable<string>? courses)
    {
        var list = (courses ?? Array.Empty<string>()).Select(c => c ?? "").ToList();
        while (list.Count < 7) list.Add("");
        if (list.Count > 7) list = list.Take(7).ToList();
        Days[periodIndex.ToString(CultureInfo.InvariantCulture)] = list;
    }

    /// <summary>删除节次（下标 >= count），并同步 timetable 里的行。</summary>
    public void TrimPeriods(int count)
    {
        if (count < 0) count = 0;
        while (Periods.Count > count) Periods.RemoveAt(Periods.Count - 1);
        foreach (var key in Days.Keys.ToList())
            if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) || i >= count)
                Days.Remove(key);
    }
}

/// <summary>假期区间（含首尾日）。</summary>
public sealed class Holiday
{
    [JsonPropertyName("start")] public string Start { get; set; } = "";
    [JsonPropertyName("end")] public string End { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

/// <summary>调休上班日：date 当天按 as_weekday（周一=0…周日=6）的课表上课。</summary>
public sealed class MakeupDay
{
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("as_weekday")] public int AsWeekday { get; set; }
}

/// <summary>临时调课：date 当天用 courses（与主课表节次对齐）替换课程。</summary>
public sealed class TempAdjust
{
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("courses")] public List<string> Courses { get; set; } = new();

    public bool Matches(DateTime date) =>
        !string.IsNullOrWhiteSpace(Date) && TryParseDate(Date, out var d) && d.Date == date.Date;

    internal static bool TryParseDate(string s, out DateTime date) => DateTime.TryParseExact(
        s?.Trim(), new[] { "yyyy-M-d", "yyyy/MM/dd", "yyyy-MM-dd", "yyyy.M.d" },
        CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}

/// <summary>
/// 周末配置：上午与工作日相同（按 morning_split 切分），下午用自定义节次与课程。
/// </summary>
public sealed class WeekendConfig
{
    [JsonPropertyName("morning_split")] public string MorningSplit { get; set; } = "12:00";
    [JsonPropertyName("afternoon_periods")] public List<Period> AfternoonPeriods { get; set; } = new();
    [JsonPropertyName("courses")] public List<string> Courses { get; set; } = new();

    public int MorningSplitMinutes => TimeUtil.TryParse(MorningSplit, out var m) ? m : 12 * 60;
}

/// <summary>schedule.json 的完整结构。</summary>
public sealed class ScheduleFile
{
    [JsonPropertyName("timetables")] public Dictionary<string, Timetable> Timetables { get; set; } = new();
    [JsonPropertyName("active")] public string Active { get; set; } = "";
    [JsonPropertyName("temp_adjust")] public TempAdjust TempAdjust { get; set; } = new();
    [JsonPropertyName("advance_minutes")] public int AdvanceMinutes { get; set; } = 2;
    [JsonPropertyName("time_offset_seconds")] public int TimeOffsetSeconds { get; set; }
    [JsonPropertyName("holidays")] public List<Holiday> Holidays { get; set; } = new();
    [JsonPropertyName("makeup_days")] public List<MakeupDay> MakeupDays { get; set; } = new();
    [JsonPropertyName("weekend")] public WeekendConfig Weekend { get; set; } = new();

    /// <summary>当前启用课表；不存在时自动取第一张，并保证字典非空。</summary>
    public Timetable ActiveTimetable
    {
        get
        {
            if (Timetables.Count == 0) Timetables[Defaults.DefaultTimetableName] = Defaults.CreateTimetable();
            if (string.IsNullOrWhiteSpace(Active) || !Timetables.ContainsKey(Active))
                Active = Timetables.Keys.First();
            return Timetables[Active];
        }
    }

    /// <summary>读入后清洗：把 null 集合换空集合，避免引擎到处判空。</summary>
    public ScheduleFile Normalize()
    {
        Timetables ??= new();
        foreach (var t in Timetables.Values)
        {
            t.Periods ??= new();
            t.Days ??= new();
            for (var i = 0; i < t.Periods.Count; i++)
            {
                t.Periods[i].Name ??= "";
                t.Periods[i].Start ??= "";
                t.Periods[i].End ??= "";
            }
        }
        TempAdjust ??= new();
        TempAdjust.Courses ??= new();
        Holidays ??= new();
        MakeupDays ??= new();
        Weekend ??= new();
        Weekend.AfternoonPeriods ??= new();
        Weekend.Courses ??= new();
        Active ??= "";
        return this;
    }
}

internal static class TimeUtil
{
    /// <summary>解析 "H:mm" / "HH:mm"（也接受 "HH:mm:ss"），返回从 0 点起的分钟数。</summary>
    public static bool TryParse(string? text, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        var parts = s.Split(':');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)) return false;
        if (h < 0 || h > 47 || m < 0 || m > 59) return false;
        minutes = h * 60 + m;
        return true;
    }

    public static string Format(int minutesOfDay)
    {
        var m = ((minutesOfDay % 1440) + 1440) % 1440;
        return $"{m / 60:D2}:{m % 60:D2}";
    }
}
