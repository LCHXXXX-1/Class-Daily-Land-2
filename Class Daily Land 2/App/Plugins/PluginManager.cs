using ClassDailyLand.Core;
using ClassDailyLand.Core.Plugins;
using ClassDailyLand.Core.Storage;
using ClassDailyLand.Plugin;

namespace ClassDailyLand.App.Plugins;

/// <summary>设置中心「插件」页要显示的一行。</summary>
public sealed record PluginInfo(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    string Directory,
    bool Enabled,
    bool Loaded,
    string? Error,
    int ApiVersion,
    string? Homepage,
    int Registrations);

/// <summary>
/// 一个已加载插件的运行期状态：清单 + 上下文 + 实例 + 宿主门面。
/// 卸载时把这一整包丢掉，<see cref="AssemblyLoadContext"/> 才有机会真正被回收。
/// </summary>
internal sealed class PluginRuntime
{
    public PluginRuntime(PluginPackage package, PluginLoadContext context, IPlugin instance)
    {
        Package = package;
        Context = context;
        Instance = instance;
    }

    public PluginPackage Package { get; }
    public PluginLoadContext Context { get; }
    public IPlugin Instance { get; }
    public PluginHostContext? Host { get; set; }
    public string Id => Package.Id;
}

/// <summary>
/// 插件宿主的总入口：发现 → 加载 → 注册 → 求值 → 启停 / 重载 / 卸载。
///
/// <para>生命周期：<c>Startup</c> 里 <see cref="LoadAll"/>；运行期由
/// <see cref="RaiseTick"/> 每秒驱动插件回调；退出时 <see cref="ShutdownAll"/>。</para>
///
/// <para>所有对外暴露"当前状态"的东西（<see cref="Plugins"/>、<see cref="RecentLog"/>）
/// 都是快照，UI 线程随取随用。</para>
/// </summary>
public sealed class PluginManager
{
    /// <summary>本宿主实现的契约版本，与 <see cref="PluginManifest.HostApiVersion"/> 一致。</summary>
    public static readonly Version ApiVersion = new(1, 0);

    const int LogCapacity = 400;

    readonly AppController _ctx;
    readonly DataPaths _paths;
    readonly Logger _log;
    readonly PluginState _state;
    readonly PluginRegistry _registry;

    readonly Dictionary<string, PluginRuntime> _runtimes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    readonly List<PluginPackage> _packages = new();
    readonly LinkedList<string> _logLines = new();
    readonly object _logGate = new();

    public PluginManager(AppController ctx, DataPaths paths, Logger log)
    {
        _ctx = ctx;
        _paths = paths;
        _log = log;
        StatePath = Path.Combine(paths.Root, "plugins.json");
        _state = PluginState.Load(StatePath);
        _registry = new PluginRegistry(message => Append("registry", message));
    }

    public PluginRegistry Registry => _registry;
    public string PluginsRoot => _paths.PluginsRoot;
    public string StatePath { get; }

    /// <summary>插件门面通过它拿数据与窗口（同一个装配器，不额外造依赖）。</summary>
    internal AppController Owner => _ctx;

    /// <summary>插件列表 / 加载状态变化（UI 线程刷新用）。</summary>
    public event Action? Changed;

    /// <summary>有新日志（可能来自插件自己的线程，订阅方自行切回 UI 线程）。</summary>
    public event Action? LogAppended;

    // ————————————————————— 生命周期 —————————————————————

    public void LoadAll()
    {
        Append("host", $"开始扫描插件目录：{PluginsRoot}");
        Refresh();
        var loaded = _runtimes.Count;
        var failed = _errors.Count;
        var disabled = _packages.Count(p => p.Ok && !_state.IsEnabled(p.Id));
        Append("host", $"扫描完成：{_packages.Count} 个目录，加载 {loaded} 个" +
                       (failed > 0 ? $"，失败 {failed} 个" : "") +
                       (disabled > 0 ? $"，已禁用 {disabled} 个" : ""));
    }

    /// <summary>
    /// 重新扫描目录并让运行状态与磁盘对齐：
    /// 消失的卸载、新增的加载、已在跑的不动（不会打断正在工作的插件）。
    /// </summary>
    public void Refresh()
    {
        List<PluginPackage> scanned;
        try
        {
            scanned = PluginCatalog.Scan(PluginsRoot).ToList();
        }
        catch (Exception ex)
        {
            Append("host", "扫描插件目录失败：" + ex.Message);
            return;
        }

        var present = scanned.Where(p => p.Ok).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _runtimes.Keys.ToList())
            if (!present.Contains(id)) Unload(id, "目录已不存在");

        _packages.Clear();
        _packages.AddRange(scanned);
        _errors.Clear();

        foreach (var pkg in scanned)
        {
            if (!pkg.Ok)
            {
                _errors[pkg.Id] = pkg.Error ?? "插件目录不合法。";
                Append("host", $"跳过 {pkg.Name}：{pkg.Error}");
                continue;
            }
            if (_runtimes.ContainsKey(pkg.Id)) continue;
            if (!_state.IsEnabled(pkg.Id))
            {
                Append("host", $"{pkg.Name} 已禁用，不加载");
                continue;
            }
            Load(pkg);
        }

        Changed?.Invoke();
    }

    void Load(PluginPackage pkg)
    {
        var manifest = pkg.Manifest!;
        var context = new PluginLoadContext(pkg.Directory);
        try
        {
            var assembly = context.LoadEntry(manifest.EntryPath);
            var type = assembly.GetType(manifest.Type, throwOnError: false)
                       ?? assembly.GetTypes().FirstOrDefault(t =>
                           typeof(IPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });

            if (type is null)
                throw new InvalidOperationException(
                    $"在 {manifest.Entry} 里找不到类型 {manifest.Type}，也没有任何实现 IPlugin 的公开类型。");

            if (!typeof(IPlugin).IsAssignableFrom(type))
                throw new InvalidOperationException($"{type.FullName} 没有实现 IPlugin。");

            if (Activator.CreateInstance(type) is not IPlugin instance)
                throw new InvalidOperationException($"{type.FullName} 无法实例化（需要无参构造函数）。");

            var runtime = new PluginRuntime(pkg, context, instance);
            runtime.Host = new PluginHostContext(this, runtime);
            _runtimes[pkg.Id] = runtime;

            instance.Initialize(runtime.Host);

            var count = _registry.CountOf(pkg.Id);
            Append(pkg.Id, $"已加载 {manifest.Name} v{manifest.Version}，注册 {count} 个显示位");
        }
        catch (Exception ex)
        {
            // 半途失败要把已经注册的显示位一起撤掉，否则会留下指向"半死"插件的回调
            _registry.RemovePlugin(pkg.Id);
            _runtimes.Remove(pkg.Id);
            _errors[pkg.Id] = Describe(ex);
            Append(pkg.Id, $"加载失败：{Describe(ex)}");

            try { context.Unload(); } catch { /* 卸载失败也无所谓，反正是坏插件 */ }
        }
    }

    /// <summary>卸载一个插件：先保存状态、再 Shutdown、清注册、最后丢上下文。</summary>
    void Unload(string id, string reason)
    {
        if (!_runtimes.TryGetValue(id, out var runtime))
        {
            _registry.RemovePlugin(id);
            return;
        }
        _runtimes.Remove(id);

        try { runtime.Host?.FlushState(); } catch { /* 状态存不下也要继续卸 */ }

        try
        {
            runtime.Instance.Shutdown();
        }
        catch (Exception ex)
        {
            Append(id, "Shutdown 抛异常：" + Describe(ex));
        }

        var removed = _registry.RemovePlugin(id);
        try { runtime.Context.Unload(); } catch { /* 有残留引用时卸载会被推迟，属正常 */ }

        Append(id, $"已卸载（{reason}，清掉 {removed} 个显示位）");
    }

    public void ShutdownAll()
    {
        foreach (var id in _runtimes.Keys.ToList()) Unload(id, "应用退出");
        _state.Save();
    }

    // ————————————————————— 用户操作 —————————————————————

    /// <summary>启用/禁用。禁用后会立刻从界面上消失（无需重启）。</summary>
    public bool SetEnabled(string id, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var pkg = _packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (pkg is null) return false;

        _state.SetEnabled(id, enabled);
        _state.Save();

        if (enabled)
        {
            if (!pkg.Ok)
            {
                Append(id, "无法启用：" + (pkg.Error ?? "插件目录不合法"));
                Changed?.Invoke();
                return false;
            }
            if (!_runtimes.ContainsKey(id)) Load(pkg);
            else Append(id, "已经在运行，无需重复加载");
        }
        else
        {
            Unload(id, "用户禁用");
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>重载一个插件（改完 dll 后不用重启应用）。</summary>
    public bool Reload(string id)
    {
        var pkg = _packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (pkg is null) return false;

        Unload(id, "重载");
        _errors.Remove(id);
        if (pkg.Ok && _state.IsEnabled(id)) Load(pkg);
        Changed?.Invoke();
        return true;
    }

    /// <summary>从 .cblplugin / .zip / 文件夹安装；成功后立刻加载（除非用户之前禁用了它）。</summary>
    public InstallOutcome Install(string sourcePath, bool overwrite)
    {
        var outcome = PluginInstaller.Install(sourcePath, PluginsRoot, overwrite);
        if (!outcome.Ok)
        {
            Append("host", "安装失败：" + (outcome.Error ?? "未知原因"));
            return outcome;
        }

        Append("host", $"已安装 {outcome.PluginId} → {outcome.InstalledPath}");
        _state.SetEnabled(outcome.PluginId!, true);
        _state.Save();
        Refresh();

        // 装完还是没加载起来，把那次的错误原样抛回去，用户在装的时候就能看到
        if (_errors.TryGetValue(outcome.PluginId!, out var error))
            return new InstallOutcome(false, outcome.PluginId, "已复制到插件目录，但加载失败：" + error, outcome.InstalledPath);

        return outcome;
    }

    public InstallOutcome Uninstall(string id)
    {
        Unload(id, "卸载");
        var outcome = PluginInstaller.Uninstall(id, PluginsRoot);
        if (outcome.Ok)
        {
            _state.Forget(id);
            _state.Save();
            _packages.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            _errors.Remove(id);
            Append("host", "已卸载 " + id);
        }
        else
        {
            Append("host", "卸载失败：" + (outcome.Error ?? "未知原因"));
        }
        Changed?.Invoke();
        return outcome;
    }

    // ————————————————————— 运行期驱动 —————————————————————

    public void RaiseTick(HostSnapshot snapshot)
    {
        foreach (var runtime in _runtimes.Values.ToList())
        {
            try { runtime.Host?.RaiseTick(new HostTickEventArgs(snapshot)); }
            catch (Exception ex) { Append(runtime.Id, "Tick 事件抛异常：" + Describe(ex)); }
        }
    }

    public void RaiseSettingsChanged(IReadOnlyList<string> keys)
    {
        foreach (var runtime in _runtimes.Values.ToList())
        {
            try { runtime.Host?.RaiseSettingsChanged(new HostSettingsEventArgs(keys)); }
            catch (Exception ex) { Append(runtime.Id, "SettingsChanged 抛异常：" + Describe(ex)); }
        }
    }

    public void RaiseIslandClicked()
    {
        foreach (var runtime in _runtimes.Values.ToList())
        {
            try { runtime.Host?.RaiseIslandClicked(); }
            catch (Exception ex) { Append(runtime.Id, "IslandClicked 抛异常：" + Describe(ex)); }
        }
    }

    /// <summary>宿主里的空实现占位（插件主动 Invalidate 时用）。</summary>
    internal void RequestRefresh() => _ctx.RequestPluginRefresh();

    // ————————————————————— 快照与日志 —————————————————————

    public IReadOnlyList<PluginInfo> Plugins
    {
        get
        {
            var list = new List<PluginInfo>();
            foreach (var pkg in _packages)
            {
                var runtime = _runtimes.GetValueOrDefault(pkg.Id);
                _errors.TryGetValue(pkg.Id, out var error);
                list.Add(new PluginInfo(
                    Id: pkg.Id,
                    Name: pkg.Name,
                    Version: pkg.Version,
                    Author: pkg.Manifest?.Author ?? "",
                    Description: pkg.Manifest?.Description ?? "",
                    Directory: pkg.Directory,
                    Enabled: _state.IsEnabled(pkg.Id),
                    Loaded: runtime is not null,
                    Error: error ?? pkg.Error,
                    ApiVersion: pkg.Manifest?.ApiVersion ?? PluginManifest.HostApiVersion,
                    Homepage: pkg.Manifest?.Homepage,
                    Registrations: _registry.CountOf(pkg.Id)));
            }
            return list;
        }
    }

    public int LoadedCount => _runtimes.Count;

    public IReadOnlyList<string> RecentLog
    {
        get { lock (_logGate) return _logLines.ToArray(); }
    }

    public void ClearLog()
    {
        lock (_logGate) _logLines.Clear();
        LogAppended?.Invoke();
    }

    internal void Append(string source, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {source,-16}  {message}";
        lock (_logGate)
        {
            _logLines.AddLast(line);
            while (_logLines.Count > LogCapacity) _logLines.RemoveFirst();
        }
        // 插件日志同时进 app.log，出问题时不必让用户截图设置页
        try { _log.Info($"[plugin:{source}] {message}"); } catch { /* 日志失败不影响插件 */ }
        LogAppended?.Invoke();
    }

    static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        return ReferenceEquals(inner, ex)
            ? $"{ex.GetType().Name}: {ex.Message}"
            : $"{ex.GetType().Name}: {ex.Message} → {inner.GetType().Name}: {inner.Message}";
    }
}
