using ClassDailyLand.Plugin;

namespace ClassDailyLand.Core.Plugins;

/// <summary>下拉面板的一次求值结果。</summary>
public sealed record PanelRender(PanelSpec Spec, IReadOnlyList<string> Lines);

/// <summary>主窗区块的一次求值结果。</summary>
public sealed record BoardSectionRender(BoardSection Section);

/// <summary>
/// 显示位注册表：插件注册、宿主求值。
///
/// <para><b>竞争规则</b>：同一"单值"显示位（岛文案 / 岛徽标 / 副岛 / 主窗徽标…）
/// 只取一个 —— Priority 最大的胜出，平级时先注册的胜出。多值显示位
/// （下拉面板 / 主窗区块 / 菜单项）则全都要，按 Priority 与注册顺序排序。</para>
///
/// <para><b>异常隔离</b>：所有插件回调都在这里被接住。抛异常的注册项按"本次没有内容"
/// 处理，并通过 <c>onError</c> 记一条日志；连续炸也不会影响其它插件和宿主。</para>
///
/// 这个类不碰 UI，也不碰文件，单元测试可以直接构造它。
/// </summary>
public sealed class PluginRegistry
{
    public static class Slots
    {
        public const string IslandText = "island.text";
        public const string IslandExtra = "island.extra";
        public const string IslandBadge = "island.badge";
        public const string SubIsland = "subIsland.content";
        public const string Panel = "panel";
        public const string BoardSection = "board.section";
        public const string BoardBadge = "board.badge";
        public const string BoardAction = "board.action";
        public const string TrayAction = "tray.action";
    }

    public sealed record Entry(string Id, string PluginId, string Slot, object Spec, int Priority, long Sequence);

    readonly object _gate = new();
    readonly List<Entry> _entries = new();
    readonly Action<string>? _onError;
    long _seq;

    public PluginRegistry(Action<string>? onError = null) => _onError = onError;

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>某插件注册了多少个显示位（卸载/禁用时用于日志）。</summary>
    public int CountOf(string pluginId)
    {
        lock (_gate) return _entries.Count(e => e.PluginId == pluginId);
    }

    // ————————————————————— 注册 —————————————————————

    public string AddIslandText(string pluginId, IslandTextSpec spec) => Add(Slots.IslandText, pluginId, spec, spec.Priority);
    public string AddIslandExtra(string pluginId, IslandExtraSpec spec) => Add(Slots.IslandExtra, pluginId, spec, spec.Priority);
    public string AddIslandBadge(string pluginId, IslandBadgeSpec spec) => Add(Slots.IslandBadge, pluginId, spec, spec.Priority);
    public string AddSubIsland(string pluginId, SubIslandSpec spec) => Add(Slots.SubIsland, pluginId, spec, spec.Priority);
    public string AddPanel(string pluginId, PanelSpec spec) => Add(Slots.Panel, pluginId, spec, spec.Priority);
    public string AddBoardSection(string pluginId, BoardSectionSpec spec) => Add(Slots.BoardSection, pluginId, spec, spec.Priority);
    public string AddBoardBadge(string pluginId, BoardBadgeSpec spec) => Add(Slots.BoardBadge, pluginId, spec, spec.Priority);
    public string AddBoardAction(string pluginId, BoardActionSpec spec) => Add(Slots.BoardAction, pluginId, spec, spec.Order);
    public string AddTrayAction(string pluginId, TrayActionSpec spec) => Add(Slots.TrayAction, pluginId, spec, spec.Order);

    string Add(string slot, string pluginId, object spec, int priority)
    {
        Entry entry;
        lock (_gate)
        {
            var seq = ++_seq;
            entry = new Entry($"{pluginId}#{seq}", pluginId, slot, spec, priority, seq);
            _entries.Add(entry);
        }
        return entry.Id;
    }

    /// <summary>按注册 id 注销；不存在也不报错（插件重载时会重复注销）。</summary>
    public void Remove(string registrationId)
    {
        lock (_gate) _entries.RemoveAll(e => e.Id == registrationId);
    }

    /// <summary>禁用 / 重载 / 卸载插件时清掉它的全部注册。</summary>
    public int RemovePlugin(string pluginId)
    {
        lock (_gate) return _entries.RemoveAll(e => e.PluginId == pluginId);
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    // ————————————————————— 求值 —————————————————————

    /// <summary>单值位：Priority 最大者，平级取先注册的。</summary>
    Entry? Best(string slot)
    {
        lock (_gate)
        {
            Entry? best = null;
            foreach (var e in _entries)
            {
                if (e.Slot != slot) continue;
                if (best is null || e.Priority > best.Priority ||
                    (e.Priority == best.Priority && e.Sequence < best.Sequence))
                    best = e;
            }
            return best;
        }
    }

    /// <summary>多值位：全部返回，按 Priority 降序、注册顺序升序。</summary>
    List<Entry> AllOf(string slot)
    {
        lock (_gate)
        {
            return _entries.Where(e => e.Slot == slot)
                .OrderByDescending(e => e.Priority)
                .ThenBy(e => e.Sequence)
                .ToList();
        }
    }

    public string? IslandText() => FirstNonNull<string>(Slots.IslandText, spec =>
    {
        if (spec is not IslandTextSpec text) return null;
        var value = Safe(text.Text);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    });

    public int IslandExtraWidth()
    {
        var e = Best(Slots.IslandExtra);
        if (e?.Spec is not IslandExtraSpec spec) return 0;
        return Math.Clamp(spec.Width, 0, 400);
    }

    public IslandBadge? IslandBadgeValue() => FirstNonNull<IslandBadge>(Slots.IslandBadge, spec =>
    {
        if (spec is not IslandBadgeSpec badgeSpec) return null;
        var badge = Safe(badgeSpec.Badge);
        if (badge is null || string.IsNullOrWhiteSpace(badge.Text)) return null;
        return badge with { Text = badge.Text.Trim() };
    });

    public SubIslandInfo? SubIsland() => FirstNonNull<SubIslandInfo>(Slots.SubIsland, spec =>
    {
        if (spec is not SubIslandSpec subSpec) return null;
        var info = Safe(subSpec.Content);
        if (info is null) return null;
        if (string.IsNullOrWhiteSpace(info.Title) && string.IsNullOrWhiteSpace(info.Value)) return null;
        return info with { Progress = Math.Clamp(info.Progress, 0, 1) };
    });

    public IReadOnlyList<PanelRender> Panels()
    {
        var result = new List<PanelRender>();
        foreach (var e in AllOf(Slots.Panel))
        {
            if (e.Spec is not PanelSpec spec) continue;
            var lines = Safe(spec.Lines);
            if (lines is null) continue;
            var clean = lines.Where(l => l is not null).ToList();
            if (clean.Count == 0) continue;
            result.Add(new PanelRender(spec with { Width = Math.Clamp(spec.Width, 90, 640) }, clean));
        }
        return result;
    }

    public IReadOnlyList<BoardSectionRender> BoardSections()
    {
        var result = new List<BoardSectionRender>();
        foreach (var e in AllOf(Slots.BoardSection))
        {
            if (e.Spec is not BoardSectionSpec spec) continue;
            var section = Safe(spec.Section);
            if (section is null) continue;
            if (string.IsNullOrWhiteSpace(section.Title) && section.Lines.Count == 0) continue;
            result.Add(new BoardSectionRender(section));
        }
        return result;
    }

    public BoardBadge? BoardBadgeValue() => FirstNonNull<BoardBadge>(Slots.BoardBadge, spec =>
    {
        if (spec is not BoardBadgeSpec badgeSpec) return null;
        var badge = Safe(badgeSpec.Badge);
        if (badge is null || string.IsNullOrWhiteSpace(badge.Text)) return null;
        return badge with { Text = badge.Text.Trim() };
    });

    // 菜单项的 Order 语义是"越小越靠上"，与单值位的 Priority 相反，
    // 所以这里再按 Order 升序稳定排一次（同 Order 保持注册顺序）。
    public IReadOnlyList<BoardActionSpec> BoardActions()
    {
        var result = new List<BoardActionSpec>();
        foreach (var e in AllOf(Slots.BoardAction))
            if (e.Spec is BoardActionSpec spec && !string.IsNullOrWhiteSpace(spec.Text)) result.Add(spec);
        return result.OrderBy(x => x.Order).ToList();
    }

    public IReadOnlyList<TrayActionSpec> TrayActions()
    {
        var result = new List<TrayActionSpec>();
        foreach (var e in AllOf(Slots.TrayAction))
            if (e.Spec is TrayActionSpec spec && !string.IsNullOrWhiteSpace(spec.Text)) result.Add(spec);
        return result.OrderBy(x => x.Order).ToList();
    }

    /// <summary>把所有注册项按插件分组（日志与设置页展示用）。</summary>
    public IReadOnlyDictionary<string, int> CountByPlugin()
    {
        lock (_gate)
            return _entries.GroupBy(e => e.PluginId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 单值位的求值：按优先级顺序逐个问，**返回 null 视为"本秒没内容"，继续问下一个**。
    /// 这样高优先级插件临时没东西时，会自然落到次高优先级的插件上，而不是把显示位空着。
    /// </summary>
    T? FirstNonNull<T>(string slot, Func<object, T?> project) where T : class
    {
        foreach (var entry in AllOf(slot))
        {
            T? value;
            try
            {
                value = project(entry.Spec);
            }
            catch (Exception ex)
            {
                _onError?.Invoke($"插件回调异常（{typeof(T).Name}）：{ex.GetType().Name}: {ex.Message}");
                continue;
            }
            if (value is not null) return value;
        }
        return null;
    }

    // ————————————————————— 异常隔离 —————————————————————

    T? Safe<T>(Func<T?>? fn) where T : class
    {
        if (fn is null) return null;
        try
        {
            return fn();
        }
        catch (Exception ex)
        {
            _onError?.Invoke($"插件回调异常（{typeof(T).Name}）：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
