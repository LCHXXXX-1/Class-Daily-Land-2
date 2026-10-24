using ClassDailyLand.Core.Models;
using ClassDailyLand.Core.Storage;

namespace ClassDailyLand.Core.Managers;

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
        var raw = AtomicJson.ReadFlat(_paths.Settings);
        Current = AppSettings.FromRaw(raw, out var repaired);
        RepairedKeys = repaired;
        if (raw.Count == 0) SaveCore();   // 首次运行写出一份完整默认配置
        else if (repaired.Count > 0) SaveCore();
    }

    void SaveCore() => AtomicJson.Write(_paths.Settings, Current.ToDictionary());

    public void Save() => SaveCore();

    // ——— 类型化读取 ———
    public int Int(string key) => Current.GetInt(key);
    public bool Bool(string key) => Current.GetBool(key);
    public string Str(string key) => Current.GetString(key);

    /// <summary>写入并（有变化时）立即保存 + 广播。</summary>
    public bool Set(string key, object? value, bool broadcast = true)
    {
        var before = Current.Get(key);
        if (!Current.Set(key, value)) return false;
        var after = Current.Get(key);
        if (Equals(before, after)) return false;

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
        }
        if (changed.Count == 0) return;
        SaveCore();
        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(changed));
    }

    public void ResetPage(string page)
    {
        if (!AppSettings.PageKeys.TryGetValue(page, out var keys)) return;
        Current.ResetPage(page);
        SaveCore();
        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(keys));
    }

    /// <summary>恢复全部默认（只广播一次）。</summary>
    public void ResetAll()
    {
        foreach (var meta in AppSettings.All) Current.ResetToDefault(meta.Key);
        SaveCore();
        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(AppSettings.All.Select(m => m.Key).ToList()));
    }
}
