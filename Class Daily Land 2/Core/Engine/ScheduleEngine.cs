using ClassDailyLand.Core.Models;

namespace ClassDailyLand.Core.Engine;

/// <summary>一节课在某天的展示条目（供主窗 / 下拉面板 / 导表使用）。</summary>
public sealed record DayEntry(
    int Index, string Period, string Course, string Start, string End,
    bool IsBreak, string State);   // State: "past" / "now" / "future" / "empty"

/// <summary>
/// 课表状态计算，纯逻辑、无 UI 依赖（需求文档第四节）。
/// weekday 约定与 Python date.weekday() 相同：周一=0 … 周日=6。
/// </summary>
public sealed class ScheduleEngine
{
    public const string StatusUpcoming = "upcoming";
    public const string StatusOngoing = "ongoing";
    public const string StatusHoliday = "holiday";
    public const string StatusNone = "none";
    public const string StatusDone = "done";

    readonly ScheduleFile _file;

    public ScheduleEngine(ScheduleFile file) => _file = file.Normalize();

    public ScheduleFile File => _file;

    // ————————————————————— 日期查询 —————————————————————

    public static bool TryParseDate(string? text, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return TempAdjust.TryParseDate(text, out date);
    }

    /// <summary>今天落在哪个假期里（含首尾日）；不在任何假期返回 null。</summary>
    public Holiday? HolidayFor(DateTime date)
    {
        foreach (var h in _file.Holidays)
        {
            if (!TryParseDate(h.Start, out var s)) continue;
            if (!TryParseDate(h.End, out var e)) e = s;
            if (e < s) (s, e) = (e, s);
            if (date.Date >= s.Date && date.Date <= e.Date) return h;
        }
        return null;
    }

    /// <summary>是否是调休上班日。</summary>
    public MakeupDay? MakeupFor(DateTime date) =>
        _file.MakeupDays.FirstOrDefault(m => TryParseDate(m.Date, out var d) && d.Date == date.Date);

    public static bool IsWeekend(DateTime date) => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    // ————————————————————— periods_for / courses_for —————————————————————

    /// <summary>某天要上的节次（时间）列表。</summary>
    public List<Period> PeriodsFor(DateTime date)
    {
        var main = _file.ActiveTimetable;

        // 1) 调休日：用主课表全部节次（不受周末配置影响）
        if (MakeupFor(date) is not null)
            return main.Periods.Where(p => p.IsValid).ToList();

        // 2) 假期：无课
        if (HolidayFor(date) is not null) return new List<Period>();

        // 3) 周末：上午沿用工作日（按 morning_split 切分），下午用自定义节次
        if (IsWeekend(date))
        {
            var split = _file.Weekend.MorningSplitMinutes;
            var list = main.Periods.Where(p => p.IsValid && StartMin(p) < split).ToList();
            list.AddRange(_file.Weekend.AfternoonPeriods.Where(p => p.IsValid));
            return list;
        }

        // 4) 普通工作日
        return main.Periods.Where(p => p.IsValid).ToList();
    }

    /// <summary>某天每节次的课程名，长度与 <see cref="PeriodsFor"/> 对齐，空串表示本节无课。</summary>
    public List<string> CoursesFor(DateTime date)
    {
        var main = _file.ActiveTimetable;
        var periods = PeriodsFor(date);
        var result = new List<string>(periods.Count);
        if (periods.Count == 0) return result;

        var makeup = MakeupFor(date);
        if (makeup is not null)
        {
            for (var i = 0; i < periods.Count; i++)
                result.Add(main.CourseAt(IndexOfMain(main, periods[i]), ClampWeekday(makeup.AsWeekday)));
            return Pad(result, periods.Count);
        }

        if (IsWeekend(date))
        {
            var split = _file.Weekend.MorningSplitMinutes;
            var wd = Weekday(date);
            var afternoonCount = _file.Weekend.AfternoonPeriods.Count(p => p.IsValid);
            var morningCount = periods.Count - afternoonCount;
            for (var i = 0; i < periods.Count; i++)
            {
                if (i < morningCount)
                {
                    // 上午同工作日：优先当天列，空则回落周一列
                    var idx = IndexOfMain(main, periods[i]);
                    var course = main.CourseAt(idx, wd);
                    if (string.IsNullOrWhiteSpace(course)) course = main.CourseAt(idx, 0);
                    result.Add(course);
                }
                else
                {
                    var k = i - morningCount;
                    result.Add(k < _file.Weekend.Courses.Count ? _file.Weekend.Courses[k] ?? "" : "");
                }
            }
            return Pad(result, periods.Count);
        }

        var weekday = Weekday(date);
        for (var i = 0; i < periods.Count; i++)
            result.Add(main.CourseAt(IndexOfMain(main, periods[i]), weekday));
        return Pad(result, periods.Count);
    }

    /// <summary>某天完整的 (节次, 课程) 列表，已剔除空课。</summary>
    public List<(int Index, Period Period, string Course)> CoursesOfDay(DateTime date)
    {
        var periods = PeriodsFor(date);
        var courses = CoursesFor(date);
        var list = new List<(int, Period, string)>();
        for (var i = 0; i < periods.Count; i++)
        {
            var course = i < courses.Count ? courses[i] : "";
            if (string.IsNullOrWhiteSpace(course)) continue;
            list.Add((i, periods[i], course.Trim()));
        }
        return list;
    }

    // ————————————————————— 状态计算 —————————————————————

    /// <summary>
    /// 计算当前课表状态。
    /// </summary>
    /// <param name="now">现在时间</param>
    /// <param name="advanceMinutes">提前提醒分钟（默认 2）</param>
    /// <param name="offsetSeconds">整体时间偏移秒（默认 0）</param>
    public ScheduleStatus Compute(DateTime now, int advanceMinutes = 2, int offsetSeconds = 0)
    {
        var today = now.Date;

        // 1) 临时调课优先（只替换课程，节次仍按当天规则取）
        List<(int Index, Period Period, string Course)> items;
        if (_file.TempAdjust.Matches(today))
        {
            var periods = PeriodsFor(today);
            var temp = _file.TempAdjust.Courses ?? new List<string>();
            items = new List<(int, Period, string)>();
            for (var i = 0; i < periods.Count; i++)
            {
                var course = i < temp.Count ? (temp[i] ?? "").Trim() : "";
                if (string.IsNullOrEmpty(course)) continue;
                items.Add((i, periods[i], course));
            }
        }
        else
        {
            var holiday = HolidayFor(today);
            // 2) 假期（调休日已在 PeriodsFor 中优先，此处 holiday 只含真假期）
            if (holiday is not null && MakeupFor(today) is null)
                return ScheduleStatus.Empty with
                {
                    Status = StatusHoliday,
                    Name = string.IsNullOrWhiteSpace(holiday.Name) ? "假期" : holiday.Name.Trim(),
                };
            items = CoursesOfDay(today);
        }

        // 3) 今天没有任何课
        if (items.Count == 0) return ScheduleStatus.Empty with { Status = StatusNone, Name = null };

        var advanceSec = Math.Max(0, advanceMinutes) * 60;
        foreach (var (index, period, course) in items)
        {
            var startMin = StartMin(period);
            var endMin = EndMin(period);
            if (endMin < startMin) endMin += 1440;   // 跨午夜

            var effStart = today.AddMinutes(startMin).AddSeconds(offsetSeconds).AddSeconds(-advanceSec);
            var effEnd = today.AddMinutes(endMin).AddSeconds(offsetSeconds);

            var startStr = TimeUtil.Format(startMin);
            var endStr = TimeUtil.Format(endMin);

            if (now >= effStart && now <= effEnd)
            {
                var remain = ClampSec((int)(effEnd - now).TotalSeconds);
                var duration = ClampSec((int)(effEnd - effStart).TotalSeconds);
                return new ScheduleStatus(
                    StatusOngoing, course, period.Name, startStr, endStr,
                    UntilSec: 0, UntilMin: 0, AdvanceSec: advanceSec,
                    RemainSec: remain, RemainMin: CeilMin(remain),
                    DurationSec: duration, Index: index, Name: null);
            }

            if (now < effStart)
            {
                var until = ClampSec((int)(effStart - now).TotalSeconds);
                var duration = ClampSec((int)(effEnd - effStart).TotalSeconds);
                return new ScheduleStatus(
                    StatusUpcoming, course, period.Name, startStr, endStr,
                    UntilSec: until, UntilMin: CeilMin(until), AdvanceSec: advanceSec,
                    RemainSec: 0, RemainMin: 0, DurationSec: duration,
                    Index: index, Name: null);
            }
        }

        // 4) 全部过完
        var last = items[^1];
        return new ScheduleStatus(
            StatusDone, last.Course, last.Period.Name,
            TimeUtil.Format(StartMin(last.Period)), TimeUtil.Format(EndMin(last.Period)),
            0, 0, advanceSec, 0, 0, 0, last.Index, null);
    }

    /// <summary>当天所有节次的展示条目（含空课占位），用于面板/导出。</summary>
    public List<DayEntry> DayTable(DateTime now, int advanceMinutes = 2, int offsetSeconds = 0)
    {
        var periods = PeriodsFor(now.Date);
        var courses = _file.TempAdjust.Matches(now.Date)
            ? Pad(_file.TempAdjust.Courses ?? new List<string>(), periods.Count)
            : CoursesFor(now.Date);

        var status = Compute(now, advanceMinutes, offsetSeconds);
        var currentIndex = status.IsOngoing ? status.Index : int.MinValue;
        var list = new List<DayEntry>(periods.Count);
        for (var i = 0; i < periods.Count; i++)
        {
            var course = i < courses.Count ? (courses[i] ?? "").Trim() : "";
            var endMin = EndMin(periods[i]);
            if (endMin < StartMin(periods[i])) endMin += 1440;
            var ended = now.Date.AddMinutes(endMin).AddSeconds(offsetSeconds) <= now;
            var state = string.IsNullOrEmpty(course) ? "empty"
                : i == currentIndex ? "now"
                : ended ? "past"
                : "future";
            list.Add(new DayEntry(i, periods[i].Name, course,
                TimeUtil.Format(StartMin(periods[i])), TimeUtil.Format(endMin), false, state));
        }
        return list;
    }

    // ————————————————————— 小工具 —————————————————————

    static int Weekday(DateTime d) => (int)d.DayOfWeek switch
    {
        0 => 6,   // Sunday -> 6
        _ => (int)d.DayOfWeek - 1,
    };

    static int ClampWeekday(int wd) => wd is >= 0 and <= 6 ? wd : 0;

    static int StartMin(Period p) => TimeUtil.TryParse(p.Start, out var m) ? m : 0;
    static int EndMin(Period p) => TimeUtil.TryParse(p.End, out var m) ? m : 0;

    /// <summary>该节次在主课表 periods 里的下标（周末下午的自定义节次返回 -1）。</summary>
    static int IndexOfMain(Timetable main, Period p)
    {
        var i = main.Periods.IndexOf(p);
        return i;
    }

    static int ClampSec(int v) => v < 0 ? 0 : v;
    static int CeilMin(int sec) => sec <= 0 ? 0 : (sec + 59) / 60;

    static List<string> Pad(List<string> list, int count)
    {
        while (list.Count < count) list.Add("");
        if (list.Count > count) list.RemoveRange(count, list.Count - count);
        return list;
    }

    internal static int ParseMinutes(string hhmm) => TimeUtil.TryParse(hhmm, out var m) ? m : 0;
}
