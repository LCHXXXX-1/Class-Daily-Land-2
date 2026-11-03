using ClassDailyLand2.Core.Models;
using ClassDailyLand2.Core.Storage;

namespace ClassDailyLand2.Core.Managers;

/// <summary>设置项变更事件参数：给出被改动的键，便于 UI 精确响应。</summary>
public sealed class SettingsChangedEventArgs : EventArgs
{
    public IReadOnlyList<string> Keys { get; }
    public SettingsChangedEventArgs(IReadOnlyList<string> keys) => Keys = keys;
}

/// <summary>
/// settings.json 的读写 + 广播。所有修改即时落盘（原子写），改完发 SettingsChanged。
/// </summary>
public sealed class SettingsManager
{
    readonly DataPaths _paths;
    public AppSettings Current { get; private set; } = new();

    /// <summary>某键被读取/写回时做过类型修正（回退默认值）。</summary>
    public IReadOnlyList<string> RepairedKeys { get; private set; } = Array.Empty<string>();

    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    public SettingsManager(DataPaths paths) => _paths = paths;

    public void Load()
    {
        Logger.Current?.Trace("[设置] 开始加载 settings.json");
        var raw = AtomicJson.ReadFlat(_paths.Settings);

        // 一次性迁移：更新源默认已改为 gitee、插件源默认已改为 github。
        // 老用户 settings.json 里存的还是历史默认值（update_source: auto / github，
        // market_source: auto），不迁移的话改默认值对存量用户就是空操作。
        var migratedSource = false;
        foreach (var key in new[] { "update_source", "market_source" })
        {
            if (!raw.TryGetValue(key, out var value)) continue;
            var current = (value as string)?.Trim().ToLowerInvariant();

            var shouldMigrate = key == "update_source"
                ? current is "auto" or "github"      // 更新源 → gitee
                : current == "auto";                 // 插件源 → github
            if (!shouldMigrate) continue;

            raw[key] = key == "update_source" ? "gitee" : "github";
            migratedSource = true;
            Logger.Current?.Info($"[设置] 迁移：{key} 由 {current} 改为 {raw[key]}（默认源切换）");
        }

        Current = AppSettings.FromRaw(raw, out var repaired);
        RepairedKeys = repaired;
        Logger.Current?.Trace($"[设置] 加载完成：文件 {raw.Count} 键，模型 {AppSettings.All.Count()} 项，类型修正 {repaired.Count} 项");
        if (raw.Count == 0)
        {
            Logger.Current?.Trace("[设置] 首次运行：写出完整默认配置");
            SaveCore();
        }
        else if (repaired.Count > 0)
        {
            Logger.Current?.Trace($"[设置] 有 {repaired.Count} 项被修正，回写配置文件");
            SaveCore();
        }

        if (migratedSource)
        {
            // 迁移结果立刻落盘：否则每次启动都会重新迁移一遍
            Logger.Current?.Info("[设置] 默认源迁移完成，回写配置文件");
            SaveCore();
        }
    }

    void SaveCore() => AtomicJson.Write(_paths.Settings, Current.ToDictionary());

    public void Save()
    {
        Logger.Current?.Trace("[设置] 手动保存（退出前）");
        SaveCore();
    }

    // ——— 类型化读取 ———
    public int Int(string key) => Current.GetInt(key);
    public bool Bool(string key) => Current.GetBool(key);
    public string Str(string key) => Current.GetString(key);

    /// <summary>写入并（有变化时）立即保存 + 广播。</summary>
    public bool Set(string key, object? value, bool broadcast = true)
    {
        var before = Current.Get(key);
        if (!Current.Set(key, value))
        {
            Logger.Current?.Trace($"[设置] 写入 {key} 被模型拒绝（未知键或类型不符），输入值：{value ?? "null"}");
            return false;
        }
        var after = Current.Get(key);
        if (Equals(before, after))
        {
            Logger.Current?.Trace($"[设置] {key} 值未变（{before ?? "null"}），跳过落盘");
            return false;
        }

        Logger.Current?.Trace($"[设置] 变更 {key}：{before ?? "null"} → {after ?? "null"}（广播：{broadcast}）");
        SaveCore();
        if (broadcast) SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(new[] { key }));
        return true;
    }

    /// <summary>批量写入（只广播一次）。</summary>
    public void SetMany(IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        var changed = new List<string>();
        foreach (var kv in pairs)
        {
            var before = Current.Get(kv.Key);
            if (!Current.Set(kv.Key, kv.Value)) continue;
            if (Equals(before, Current.Get(kv.Key))) continue;
            changed.Add(kv.Key);
            Logger.Current?.Trace($"[设置] 批量变更 {kv.Key}：{before ?? "null"} → {Current.Get(kv.Key) ?? "null"}");
        }
        if (changed.Count == 0) return;
        Logger.Current?.Trace($"[设置] 批量落盘，共 {changed.Count} 键：{string.Join(", ", changed)}");
        SaveCore();
        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(changed));
    }

    public void ResetPage(string page)
    {
        if (!AppSettings.PageKeys.TryGetValue(page, out var keys)) return;
        Logger.Current?.Trace($"[设置] 恢复本页默认：{page}（{keys.Count()} 键）");
        Current.ResetPage(page);
        SaveCore();
        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(keys));
    }

    /// <summary>恢复全部默认（只广播一次）。</summary>
    public void ResetAll()
    {
        Logger.Current?.Warn($"[设置] 恢复全部默认（{AppSettings.All.Count()} 项）");
        foreach (var meta in AppSettings.All) Current.ResetToDefault(meta.Key);
        SaveCore();
        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(AppSettings.All.Select(m => m.Key).ToList()));
    }
}
