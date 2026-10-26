using ClassDailyLand2.App.Win32;
using ClassDailyLand2.Core.Storage;
using ClassDailyLand2.Plugin;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ClassDailyLand2.App.Plugins;

/// <summary>
/// 一个插件的宿主门面：把 <see cref="IPluginHost"/> 的 48 个成员接到真实的
/// 应用装配器（<see cref="AppController"/>）与注册表上。
///
/// 每个已加载插件一份，<c>PluginId</c> / 数据目录都跟着各自的清单走 ——
/// 插件之间看不到彼此的数据，也不会互相污染注册。
/// </summary>
internal sealed class PluginHostContext : IPluginHost
{
    readonly PluginManager _manager;
    readonly PluginRuntime _runtime;
    readonly string _dataDir;
    readonly Dictionary<string, string?> _state;
    readonly string _statePath;

    public PluginHostContext(PluginManager manager, PluginRuntime runtime)
    {
        _manager = manager;
        _runtime = runtime;

        _dataDir = Path.Combine(runtime.Package.Directory, "data");
        try { Directory.CreateDirectory(_dataDir); } catch { /* 目录建不出来时读写会各自失败 */ }

        _statePath = Path.Combine(_dataDir, "state.json");
        _state = AtomicJson.Read(_statePath, () => new Dictionary<string, string?>(StringComparer.Ordinal));
    }

    AppController Ctx => _manager.Owner;
    string Id => _runtime.Id;

    // ═══════════════ 一、元信息与日志 ═══════════════

    public Version ApiVersion => PluginManager.ApiVersion;
    public string HostVersion => AppController.Version;
    public string PluginId => Id;
    public string DataDirectory => _dataDir;
    public string PluginDirectory => _runtime.Package.Directory;

    public void Log(string message) => _manager.Append(Id, message ?? "");
    public void Warn(string message) => _manager.Append(Id, "⚠ " + (message ?? ""));
    public void Error(string message) => _manager.Append(Id, "✖ " + (message ?? ""));

    // ═══════════════ 二、显示位注册 ═══════════════

    public string RegisterIslandText(IslandTextSpec spec) => _manager.Registry.AddIslandText(Id, spec);
    public string RegisterIslandExtra(IslandExtraSpec spec) => _manager.Registry.AddIslandExtra(Id, spec);
    public string RegisterIslandBadge(IslandBadgeSpec spec) => _manager.Registry.AddIslandBadge(Id, spec);
    public string RegisterSubIsland(SubIslandSpec spec) => _manager.Registry.AddSubIsland(Id, spec);
    public string RegisterPanel(PanelSpec spec) => _manager.Registry.AddPanel(Id, spec);
    public string RegisterBoardSection(BoardSectionSpec spec) => _manager.Registry.AddBoardSection(Id, spec);
    public string RegisterBoardBadge(BoardBadgeSpec spec) => _manager.Registry.AddBoardBadge(Id, spec);
    public string RegisterBoardAction(BoardActionSpec spec) => _manager.Registry.AddBoardAction(Id, spec);
    public string RegisterTrayAction(TrayActionSpec spec) => _manager.Registry.AddTrayAction(Id, spec);

    public void Unregister(string registrationId)
    {
        if (!string.IsNullOrEmpty(registrationId)) _manager.Registry.Remove(registrationId);
    }

    public void Invalidate()
    {
        try { _manager.RequestRefresh(); }
        catch (Exception ex) { Warn("Invalidate 失败：" + ex.Message); }
    }

    // ═══════════════ 三、只读数据 ═══════════════

    /// <summary>已叠加时间偏移的"现在"（与界面上显示的时间一致）。</summary>
    public DateTime Now => DateTime.Now.AddSeconds(Ctx.Schedule.OffsetSeconds);

    public HostSnapshot Snapshot
    {
        get
        {
            var s = Ctx.CurrentStatus;
            return new HostSnapshot(
                Status: s.Status,
                StatusText: Ctx.Schedule is null ? "" : Core.Engine.ScheduleText.Core(s),
                Course: s.Course,
                Index: s.Index,
                Start: s.Start,
                End: s.End,
                RemainMinutes: s.RemainMin,
                UntilMinutes: s.UntilMin,
                HolidayName: s.Name,
                Now: Now);
        }
    }

    public IReadOnlyList<CourseEntry> CoursesOfDay(DateTime day)
    {
        var list = new List<CourseEntry>();
        try
        {
            var status = Ctx.CurrentStatus;
            var isToday = day.Date == Now.Date;
            foreach (var (index, period, course) in Ctx.Schedule.Engine.CoursesOfDay(day))
            {
                list.Add(new CourseEntry(
                    index,
                    course,
                    Shift(period.Start),
                    Shift(period.End),
                    isToday && status.IsOngoing && status.Index == index));
            }
        }
        catch (Exception ex)
        {
            Warn("CoursesOfDay 失败：" + ex.Message);
        }
        return list;
    }

    public CourseEntry? CurrentCourse
    {
        get
        {
            var s = Ctx.CurrentStatus;
            if (!s.IsOngoing || s.Index < 0) return null;
            return CoursesOfDay(Now).FirstOrDefault(c => c.Index == s.Index);
        }
    }

    public CourseEntry? NextCourse
    {
        get
        {
            var s = Ctx.CurrentStatus;
            if (!s.IsUpcoming || s.Index < 0) return null;
            return CoursesOfDay(Now).FirstOrDefault(c => c.Index == s.Index);
        }
    }

    public DutySnapshot Duty
    {
        get
        {
            try { return new DutySnapshot(Ctx.Duty.TodayName, Ctx.Duty.Config.ShouldCount, Ctx.Duty.Config.ActualCount); }
            catch { return new DutySnapshot("", 0, 0); }
        }
    }

    public IReadOnlyList<HomeworkEntry> Homework
    {
        get
        {
            try
            {
                return Ctx.Homework.Items
                    .Select(i => new HomeworkEntry(i.Subject ?? "", i.Content ?? "", i.Date ?? ""))
                    .ToList();
            }
            catch { return Array.Empty<HomeworkEntry>(); }
        }
    }

    public string? GetSetting(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var raw = Ctx.Settings.Current.Get(key);
        return raw switch
        {
            null => null,
            bool b => b ? "true" : "false",
            _ => Convert.ToString(raw, CultureInfo.InvariantCulture),
        };
    }

    public int GetSettingInt(string key, int fallback)
    {
        var raw = GetSetting(key);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public bool GetSettingBool(string key, bool fallback)
    {
        var raw = GetSetting(key);
        return bool.TryParse(raw, out var v) ? v : fallback;
    }

    public HostWorkArea WorkArea
    {
        get
        {
            var rect = WindowUtil.WorkAreaDip();
            return new HostWorkArea(rect.X, rect.Y, rect.Width, rect.Height, DpiScale);
        }
    }

    public double DpiScale
    {
        get
        {
            try
            {
                var window = Ctx.Main;
                return window is null ? 1.0 : Math.Round(WindowUtil.DpiScale(window), 2);
            }
            catch { return 1.0; }
        }
    }

    public bool IsMainWindowVisible => Ctx.Main?.IsVisible == true;
    public bool IsIslandVisible => Ctx.Island?.IsVisible == true;

    // ═══════════════ 四、插件私有存储 ═══════════════

    public string? ReadState(string key) =>
        !string.IsNullOrEmpty(key) && _state.TryGetValue(key, out var v) ? v : null;

    public void WriteState(string key, string? value)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (value is null) _state.Remove(key);
        else _state[key] = value;
    }

    public void FlushState()
    {
        try { AtomicJson.Write(_statePath, new Dictionary<string, string?>(_state, StringComparer.Ordinal)); }
        catch (Exception ex) { Warn("状态落盘失败：" + ex.Message); }
    }

    public T? ReadJson<T>(string name)
    {
        var path = StateFilePath(name);
        if (path is null || !File.Exists(path)) return default;
        try
        {
            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            Warn($"读 {name}.json 失败：{ex.Message}");
            return default;
        }
    }

    public void WriteJson<T>(string name, T value)
    {
        var path = StateFilePath(name);
        if (path is null)
        {
            Warn($"文件名「{name}」不合法，已忽略写入。");
            return;
        }
        try { AtomicJson.Write(path, value!); }
        catch (Exception ex) { Warn($"写 {name}.json 失败：{ex.Message}"); }
    }

    /// <summary>把插件给的名字规范成 data/&lt;名字&gt;.json，挡掉路径穿越。</summary>
    string? StateFilePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var clean = name.Trim();
        foreach (var bad in new[] { "/", "\\", "..", ":" })
            if (clean.Contains(bad, StringComparison.Ordinal)) return null;
        if (clean.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) clean = clean[..^5];
        if (clean.Length == 0) return null;
        return Path.Combine(_dataDir, clean + ".json");
    }

    // ═══════════════ 五、宿主动作 ═══════════════

    public void Toast(string title, string message)
    {
        try { Ctx.Tray?.Balloon(string.IsNullOrWhiteSpace(title) ? "班级日常" : title, message ?? ""); }
        catch (Exception ex) { Warn("Toast 失败：" + ex.Message); }
    }

    public void OpenSettings(string? pageKey = null)
    {
        try { Ctx.ShowSettings(pageKey); }
        catch (Exception ex) { Warn("OpenSettings 失败：" + ex.Message); }
    }

    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Warn($"只允许打开 http/https 链接，已拒绝：{url}");
            return;
        }
        try { Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Warn("打开链接失败：" + ex.Message); }
    }

    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { Warn("路径不合法：" + path); return; }

        // 只允许程序目录内部（插件目录就在这里），避免插件变成任意目录浏览器
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            Warn($"只允许打开程序目录内的路径，已拒绝：{full}");
            return;
        }
        if (!Directory.Exists(full))
        {
            Warn("目录不存在：" + full);
            return;
        }
        try { Ctx.OpenDirectory(full); }
        catch (Exception ex) { Warn("打开目录失败：" + ex.Message); }
    }

    public void ShowMainWindow(bool show)
    {
        try { Ctx.Settings.Set("show_main_window", show); }
        catch (Exception ex) { Warn("ShowMainWindow 失败：" + ex.Message); }
    }

    public void ShowIsland(bool show)
    {
        try { Ctx.Settings.Set("show_island", show); }
        catch (Exception ex) { Warn("ShowIsland 失败：" + ex.Message); }
    }

    public void SetSubIslandExpanded(bool expanded)
    {
        try { Ctx.SubIsland?.SetExpanded(expanded); }
        catch (Exception ex) { Warn("SetSubIslandExpanded 失败：" + ex.Message); }
    }

    // ═══════════════ 六、事件 ═══════════════

    public event EventHandler<HostTickEventArgs>? Tick;
    public event EventHandler<HostSettingsEventArgs>? SettingsChanged;
    public event EventHandler? IslandClicked;

    internal void RaiseTick(HostTickEventArgs e) => Tick?.Invoke(this, e);
    internal void RaiseSettingsChanged(HostSettingsEventArgs e) => SettingsChanged?.Invoke(this, e);
    internal void RaiseIslandClicked() => IslandClicked?.Invoke(this, EventArgs.Empty);

    /// <summary>界面显示的时间 = 原时间 + 全局偏移（与下拉面板的算法一致）。</summary>
    string Shift(string hhm)
    {
        var offset = Ctx.Schedule.OffsetSeconds;
        if (string.IsNullOrWhiteSpace(hhm)) return "";
        var parts = hhm.Trim().Split(':');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m))
        {
            var total = Math.Clamp(h * 60 + m + offset / 60, 0, 1440);
            return $"{total / 60:D2}:{total % 60:D2}";
        }
        return hhm;
    }
}
