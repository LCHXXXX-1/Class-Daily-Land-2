using ClassDailyLand.Core.Engine;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Core.Storage;
using System.Text.Json;

namespace ClassDailyLand.Core.Managers;

/// <summary>
/// schedule.json 的读写 + 课表结构操作。引擎实例随文件更新重建，保证 UI 拿到的永远是最新状态。
/// </summary>
public sealed class ScheduleManager
{
    readonly DataPaths _paths;

    public ScheduleFile File { get; private set; } = Defaults.CreateSchedule();
    public ScheduleEngine Engine { get; private set; }
    public int AdvanceMinutes { get; private set; } = 2;
    public int OffsetSeconds { get; private set; }

    public event EventHandler? Changed;

    public ScheduleManager(DataPaths paths)
    {
        _paths = paths;
        Engine = new ScheduleEngine(File);
    }

    public void Load()
    {
        var existed = System.IO.File.Exists(_paths.Schedule);
        File = AtomicJson.Read(_paths.Schedule, Defaults.CreateSchedule).Normalize();
        AdvanceMinutes = Math.Clamp(File.AdvanceMinutes, 0, 60);
        OffsetSeconds = Math.Clamp(File.TimeOffsetSeconds, -3600, 3600);
        Engine = new ScheduleEngine(File);
        if (!existed) Save();   // 首次运行落盘默认课表，便于用户编辑
    }

    public void Save()
    {
        File.AdvanceMinutes = AdvanceMinutes;
        File.TimeOffsetSeconds = OffsetSeconds;
        File.Normalize();
        AtomicJson.Write(_paths.Schedule, File);
        Engine = new ScheduleEngine(File);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>提前提醒分钟（需求：改 5 → 文案变成「下节 X · 5分钟后」）。</summary>
    public void SetAdvance(int minutes)
    {
        var v = Math.Clamp(minutes, 0, 60);
        if (v == AdvanceMinutes) return;
        AdvanceMinutes = v;
        Save();
    }

    public void SetOffset(int seconds)
    {
        var v = Math.Clamp(seconds, -3600, 3600);
        if (v == OffsetSeconds) return;
        OffsetSeconds = v;
        Save();
    }

    public ScheduleStatus ComputeNow() => Engine.Compute(DateTime.Now, AdvanceMinutes, OffsetSeconds);
    public ScheduleStatus Compute(DateTime now) => Engine.Compute(now, AdvanceMinutes, OffsetSeconds);

    // ————————————————————— 课表管理 —————————————————————

    public IReadOnlyList<string> TimetableNames => File.Timetables.Keys.ToList();
    public Timetable Active => File.ActiveTimetable;
    public bool IsActive(string name) => string.Equals(File.Active, name, StringComparison.Ordinal);

    public void SetActive(string name)
    {
        if (!File.Timetables.ContainsKey(name)) return;
        File.Active = name;
        Save();
    }

    /// <summary>新建课表；copyFrom 非空时克隆其结构。</summary>
    public bool CreateTimetable(string name, string? copyFrom = null)
    {
        if (string.IsNullOrWhiteSpace(name) || File.Timetables.ContainsKey(name)) return false;
        if (copyFrom is not null && File.Timetables.TryGetValue(copyFrom, out var src))
            File.Timetables[name] = Clone(src);
        else
            File.Timetables[name] = Defaults.CreateTimetable();
        File.Active = name;
        Save();
        return true;
    }

    public bool DeleteTimetable(string name)
    {
        if (File.Timetables.Count <= 1) return false;      // 至少保留一张
        if (!File.Timetables.Remove(name)) return false;
        if (File.Active == name) File.Active = File.Timetables.Keys.First();
        Save();
        return true;
    }

    public bool RenameTimetable(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || oldName == newName) return false;
        if (!File.Timetables.TryGetValue(oldName, out var table)) return false;
        if (File.Timetables.ContainsKey(newName)) return false;
        File.Timetables.Remove(oldName);
        File.Timetables[newName] = table;
        if (File.Active == oldName) File.Active = newName;
        Save();
        return true;
    }

    static Timetable Clone(Timetable src)
    {
        var t = new Timetable { Periods = src.Periods.Select(p => new Period(p.Name, p.Start, p.End)).ToList() };
        foreach (var (key, row) in src.Days)
            t.Days[key] = row.Select(s => s ?? "").ToList();
        return t;
    }

    // ————————————————————— 临时调课 / 假期 / 调休 / 周末 —————————————————————

    public void SetTempAdjust(string date, IReadOnlyList<string> courses)
    {
        File.TempAdjust ??= new TempAdjust();
        File.TempAdjust.Date = date ?? "";
        File.TempAdjust.Courses = courses.Select(c => c ?? "").ToList();
        Save();
    }

    public void ClearTempAdjust()
    {
        File.TempAdjust = new TempAdjust();
        Save();
    }

    public bool AddHoliday(Holiday h)
    {
        if (!TempAdjust.TryParseDate(h.Start, out _)) return false;
        File.Holidays.Add(h);
        Save();
        return true;
    }

    public void RemoveHoliday(int index)
    {
        if (index < 0 || index >= File.Holidays.Count) return;
        File.Holidays.RemoveAt(index);
        Save();
    }

    public bool AddMakeup(MakeupDay m)
    {
        if (!TempAdjust.TryParseDate(m.Date, out _)) return false;
        m.AsWeekday = Math.Clamp(m.AsWeekday, 0, 6);
        File.MakeupDays.RemoveAll(x => string.Equals(x.Date, m.Date, StringComparison.Ordinal));
        File.MakeupDays.Add(m);
        Save();
        return true;
    }

    public void RemoveMakeup(int index)
    {
        if (index < 0 || index >= File.MakeupDays.Count) return;
        File.MakeupDays.RemoveAt(index);
        Save();
    }

    public void SetWeekend(WeekendConfig weekend)
    {
        File.Weekend = weekend.Normalize();
        Save();
    }

    /// <summary>导出/导入用的 JSON 文本。</summary>
    public string ExportJson() => System.Text.Json.JsonSerializer.Serialize(File, Storage.AtomicJson.Options);

    public bool ImportJson(string json)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            var file = node?.Deserialize<ScheduleFile>(Storage.AtomicJson.Options);
            if (file is null || file.Timetables.Count == 0) return false;
            File = file.Normalize();
            AdvanceMinutes = Math.Clamp(File.AdvanceMinutes, 0, 60);
            OffsetSeconds = Math.Clamp(File.TimeOffsetSeconds, -3600, 3600);
            Save();
            return true;
        }
        catch (Exception) { return false; }
    }
}

internal static class WeekendNormalizeExtensions
{
    public static WeekendConfig Normalize(this WeekendConfig w)
    {
        w.AfternoonPeriods ??= new();
        w.Courses ??= new();
        if (string.IsNullOrWhiteSpace(w.MorningSplit)) w.MorningSplit = "12:00";
        return w;
    }
}
