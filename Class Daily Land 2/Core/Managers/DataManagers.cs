using ClassDailyLand2.Core.Models;
using ClassDailyLand2.Core.Storage;

namespace ClassDailyLand2.Core.Managers;

/// <summary>homework.json：[{"subject","content","date"}]</summary>
public sealed class HomeworkManager
{
    readonly DataPaths _paths;
    public List<HomeworkItem> Items { get; private set; } = new();

    public event EventHandler? Changed;

    public HomeworkManager(DataPaths paths) => _paths = paths;

    public void Load()
    {
        var existed = File.Exists(_paths.Homework);
        Items = AtomicJson.Read(_paths.Homework, Defaults.CreateHomework) ?? new List<HomeworkItem>();
        Logger.Current?.Trace($"[作业] 加载完成：{Items.Count} 条（首次运行：{!existed}）");
        if (!existed) Save();
    }

    public void Save()
    {
        Logger.Current?.Trace($"[作业] 保存 {Items.Count} 条作业并广播 Changed");
        AtomicJson.Write(_paths.Homework, Items);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Add(HomeworkItem item)
    {
        Items.Add(item);
        Logger.Current?.Trace($"[作业] 新增：{item.Subject} · {item.Content}（日期 {item.Date}），当前共 {Items.Count} 条");
        Save();
    }
    public void Insert(int index, HomeworkItem item)
    {
        index = Math.Clamp(index, 0, Items.Count);
        Items.Insert(index, item);
        Logger.Current?.Trace($"[作业] 在第 {index + 1} 位插入：{item.Subject} · {item.Content}");
        Save();
    }
    public void Update(int index, HomeworkItem item)
    {
        if (index < 0 || index >= Items.Count)
        {
            Logger.Current?.Warn($"[作业] 更新失败：索引 {index} 越界（共 {Items.Count} 条）");
            return;
        }
        Logger.Current?.Trace($"[作业] 修改第 {index + 1} 条：{Items[index].Subject} · {Items[index].Content} → {item.Subject} · {item.Content}");
        Items[index] = item;
        Save();
    }
    public void RemoveAt(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        Logger.Current?.Trace($"[作业] 删除第 {index + 1} 条：{Items[index].Subject} · {Items[index].Content}");
        Items.RemoveAt(index);
        Save();
    }
    public void Move(int from, int to)
    {
        if (from < 0 || from >= Items.Count) return;
        to = Math.Clamp(to, 0, Items.Count - 1);
        var item = Items[from];
        Items.RemoveAt(from);
        Items.Insert(to, item);
        Logger.Current?.Trace($"[作业] 移动「{item.Subject} · {item.Content}」：第 {from + 1} 位 → 第 {to + 1} 位");
        Save();
    }
    public void ReplaceAll(IEnumerable<HomeworkItem> items)
    {
        Items = items.ToList();
        Logger.Current?.Trace($"[作业] 整体替换为 {Items.Count} 条");
        Save();
    }
}

/// <summary>
/// name.json + config.json：值日生名单与游标、应到/实到人数。
/// 跨天自动顺延（last_date != 今天 → 游标 +1）。
/// </summary>
public sealed class StudentOnDuty
{
    readonly DataPaths _paths;

    public List<string> Names { get; private set; } = new();
    public DutyConfig Config { get; private set; } = new();

    public event EventHandler? Changed;

    public StudentOnDuty(DataPaths paths) => _paths = paths;

    public void Load()
    {
        var existed = File.Exists(_paths.Name);
        Names = AtomicJson.Read(_paths.Name, Defaults.CreateNames) ?? new List<string>();
        Config = AtomicJson.Read(_paths.Config, Defaults.CreateDuty) ?? new DutyConfig();
        RollOverIfNeeded(DateTime.Today);
        if (!existed) SaveNames();
    }

    /// <summary>新的一天自动换下一位值日生（与原版行为一致）。</summary>
    public bool RollOverIfNeeded(DateTime today)
    {
        var stamp = today.ToString("yyyy-MM-dd");
        if (string.Equals(Config.LastDate, stamp, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(Config.LastDate) && Names.Count > 0)
            Config.CurrentIndex = Wrap(Config.CurrentIndex + 1);
        Config.LastDate = stamp;
        Logger.Current?.Trace($"[值日] 跨天顺延：{stamp} 的值日生游标 → {Config.CurrentIndex}（{TodayName}），名单 {Names.Count} 人");
        SaveConfig();
        return true;
    }

    int Wrap(int index) => Names.Count == 0 ? 0 : ((index % Names.Count) + Names.Count) % Names.Count;

    public int CurrentIndex => Wrap(Config.CurrentIndex);

    /// <summary>今日值日生；名单为空返回 ""。</summary>
    public string TodayName => Names.Count == 0 ? "" : Names[CurrentIndex] ?? "";

    /// <summary>今日值日生的前后两位（用于右键菜单提示 / 面板展示）。</summary>
    public string NextName => Names.Count == 0 ? "" : Names[Wrap(CurrentIndex + 1)] ?? "";
    public string PrevName => Names.Count == 0 ? "" : Names[Wrap(CurrentIndex - 1)] ?? "";

    public void Next() { Config.CurrentIndex = Wrap(CurrentIndex + 1); Config.LastDate = DateTime.Today.ToString("yyyy-MM-dd"); SaveConfig(); }
    public void Prev() { Config.CurrentIndex = Wrap(CurrentIndex - 1); Config.LastDate = DateTime.Today.ToString("yyyy-MM-dd"); SaveConfig(); }

    public void SetAttendance(string should, string actual)
    {
        Config.Should = should ?? "0";
        Config.Actual = actual ?? "0";
        SaveConfig();
    }

    public void SetNames(IEnumerable<string> names)
    {
        Names = names.Select(n => (n ?? "").Trim()).Where(n => n.Length > 0).ToList();
        Config.CurrentIndex = Wrap(Config.CurrentIndex);
        AtomicJson.Write(_paths.Name, Names);
        SaveConfig();
    }

    public void SaveNames() => AtomicJson.Write(_paths.Name, Names);

    void SaveConfig()
    {
        AtomicJson.Write(_paths.Config, Config);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>agreement.json：{"agreed": true}</summary>
public sealed class AgreementManager
{
    readonly DataPaths _paths;
    public bool Agreed { get; private set; }

    public AgreementManager(DataPaths paths) => _paths = paths;

    public void Load()
    {
        var existed = File.Exists(_paths.Agreement);
        Agreed = AtomicJson.Read(_paths.Agreement, () => new AgreementFile()).Agreed;
        if (!existed) AtomicJson.Write(_paths.Agreement, new AgreementFile());
    }

    public void Agree()
    {
        Agreed = true;
        AtomicJson.Write(_paths.Agreement, new AgreementFile { Agreed = true });
    }
}

public sealed class AgreementFile
{
    [System.Text.Json.Serialization.JsonPropertyName("agreed")]
    public bool Agreed { get; set; }
}
