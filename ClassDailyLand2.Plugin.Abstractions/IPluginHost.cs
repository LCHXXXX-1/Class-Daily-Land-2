namespace ClassDailyLand2.Plugin;

/// <summary>
/// 宿主交给插件的一切能力。共 48 个成员，分七组：
/// <list type="number">
///   <item>元信息与日志（8）</item>
///   <item>显示位注册（11）</item>
///   <item>只读数据（14）</item>
///   <item>插件私有存储（5）</item>
///   <item>宿主动作（7）</item>
///   <item>事件（3）</item>
///   <item>时间（见 <see cref="Now"/>）</item>
/// </list>
///
/// <para><b>线程纪律</b>：所有注册进去的回调（<c>Func&lt;…&gt;</c>）都由宿主在
/// <b>UI 线程</b>每秒调用一次。回调里禁止 IO、网络、锁等待与大数据计算 ——
/// 慢 1 毫秒就是整条界面卡 1 毫秒。需要重活请自己起线程，回调只读缓存。</para>
///
/// <para><b>异常纪律</b>：插件抛出的异常会被宿主接住并记进插件日志，
/// 该显示位这一秒按"没有内容"处理；连续抛错不会拖垮宿主，但会污染日志。</para>
/// </summary>
public interface IPluginHost
{
    // ═══════════════ 一、元信息与日志 ═══════════════

    /// <summary>契约版本。插件可在 Initialize 里比对，决定要不要降级运行。</summary>
    Version ApiVersion { get; }

    /// <summary>宿主版本（如 <c>1.0.0-mvp</c>）。</summary>
    string HostVersion { get; }

    /// <summary>本插件在 plugin.json 里声明的 id。</summary>
    string PluginId { get; }

    /// <summary>
    /// 本插件的私有数据目录（<c>plugins/&lt;id&gt;/data/</c>，已创建）。
    /// 用 <see cref="WriteJson{T}"/> / <see cref="ReadJson{T}"/> 时不必自己拼路径。
    /// </summary>
    string DataDirectory { get; }

    /// <summary>本插件所在的目录（<c>plugins/&lt;id&gt;/</c>）。</summary>
    string PluginDirectory { get; }

    /// <summary>普通日志（进插件页的"运行日志"）。</summary>
    void Log(string message);

    /// <summary>警告日志（黄色）。</summary>
    void Warn(string message);

    /// <summary>错误日志（红色）。</summary>
    void Error(string message);

    // ═══════════════ 二、显示位注册（都返回注册 id，可用于 Unregister） ═══════════════

    /// <summary>主岛文案补充位。</summary>
    string RegisterIslandText(IslandTextSpec spec);

    /// <summary>主岛加长段（预留宽度）。</summary>
    string RegisterIslandExtra(IslandExtraSpec spec);

    /// <summary>主岛右侧徽标位。</summary>
    string RegisterIslandBadge(IslandBadgeSpec spec);

    /// <summary>副岛内容位。</summary>
    string RegisterSubIsland(SubIslandSpec spec);

    /// <summary>下拉面板位（可注册多个，宿主并排显示）。</summary>
    string RegisterPanel(PanelSpec spec);

    /// <summary>班级日常主窗的追加区块位（可注册多个）。</summary>
    string RegisterBoardSection(BoardSectionSpec spec);

    /// <summary>班级日常标题行徽标位。</summary>
    string RegisterBoardBadge(BoardBadgeSpec spec);

    /// <summary>主窗右键菜单项。</summary>
    string RegisterBoardAction(BoardActionSpec spec);

    /// <summary>托盘右键菜单项。</summary>
    string RegisterTrayAction(TrayActionSpec spec);

    /// <summary>按注册 id 注销一个显示位。传不存在的 id 不报错。</summary>
    void Unregister(string registrationId);

    /// <summary>请求宿主立刻重算全部显示位（默认每秒一次，插件急需时可主动催）。</summary>
    void Invalidate();

    // ═══════════════ 三、只读数据 ═══════════════

    /// <summary>
    /// 应用内"现在"：已叠加设置里的时间偏移（<c>schedule_time_offset_seconds</c>）。
    /// 插件做时间判断请一律用它，不要用 <see cref="DateTime.Now"/>，否则测试/调时会错位。
    /// </summary>
    DateTime Now { get; }

    /// <summary>当前课表状态快照（每秒更新）。</summary>
    HostSnapshot Snapshot { get; }

    /// <summary>今天（按 <see cref="Now"/>）的全部课程，已叠加时间偏移。</summary>
    IReadOnlyList<CourseEntry> CoursesOfDay(DateTime day);

    /// <summary>此刻正在上的课；没课为 null。</summary>
    CourseEntry? CurrentCourse { get; }

    /// <summary>下一节课；没有下一节为 null。</summary>
    CourseEntry? NextCourse { get; }

    /// <summary>值日与出勤。</summary>
    DutySnapshot Duty { get; }

    /// <summary>全部作业条目（阶段 3 不区分日期，按录入顺序）。</summary>
    IReadOnlyList<HomeworkEntry> Homework { get; }

    /// <summary>读宿主设置项（settings.json）的原始值；不存在返回 null。</summary>
    string? GetSetting(string key);

    /// <summary>读整数设置项，缺失或类型不符时返回 <paramref name="fallback"/>。</summary>
    int GetSettingInt(string key, int fallback);

    /// <summary>读布尔设置项，缺失或类型不符时返回 <paramref name="fallback"/>。</summary>
    bool GetSettingBool(string key, bool fallback);

    /// <summary>主屏工作区（DIP）。</summary>
    HostWorkArea WorkArea { get; }

    /// <summary>当前 DPI 缩放（1.0 = 100%，1.5 = 150%）。</summary>
    double DpiScale { get; }

    /// <summary>班级日常主窗当前是否可见。</summary>
    bool IsMainWindowVisible { get; }

    /// <summary>灵动岛当前是否可见。</summary>
    bool IsIslandVisible { get; }

    // ═══════════════ 四、插件私有存储 ═══════════════

    /// <summary>
    /// 读本插件的键值状态（存在 <c>plugins/&lt;id&gt;/data/state.json</c>）。
    /// 写入后由宿主统一落盘，插件崩了也不会写坏文件。
    /// </summary>
    string? ReadState(string key);

    /// <summary>写键值状态（内存即时生效，落盘见 <see cref="FlushState"/>）。</summary>
    void WriteState(string key, string? value);

    /// <summary>把键值状态立刻落盘（退出/禁用前宿主也会调一次）。</summary>
    void FlushState();

    /// <summary>读一个本插件私有的 JSON 文件（<c>data/名字.json</c>）；不存在或损坏返回 default。</summary>
    T? ReadJson<T>(string name);

    /// <summary>原子写一个本插件私有的 JSON 文件。</summary>
    void WriteJson<T>(string name, T value);

    // ═══════════════ 五、宿主动作 ═══════════════

    /// <summary>弹一条托盘气泡（系统通知）。</summary>
    void Toast(string title, string message);

    /// <summary>打开设置中心；<paramref name="pageKey"/> 可指定页（general/appearance/island/…）。</summary>
    void OpenSettings(string? pageKey = null);

    /// <summary>用默认浏览器打开 http/https 链接（其它协议会被拒绝并记日志）。</summary>
    void OpenUrl(string url);

    /// <summary>在资源管理器里打开一个目录（只允许程序目录与插件目录内部）。</summary>
    void OpenFolder(string path);

    /// <summary>显示/隐藏班级日常主窗。</summary>
    void ShowMainWindow(bool show);

    /// <summary>显示/隐藏灵动岛。</summary>
    void ShowIsland(bool show);

    /// <summary>展开/收起副岛（收起是 40×40 的圆，展开是长条）。</summary>
    void SetSubIslandExpanded(bool expanded);

    // ═══════════════ 六、事件 ═══════════════

    /// <summary>每秒一次（与宿主的刷新同一个 tick，UI 线程）。</summary>
    event EventHandler<HostTickEventArgs>? Tick;

    /// <summary>宿主设置发生改动（UI 线程）。</summary>
    event EventHandler<HostSettingsEventArgs>? SettingsChanged;

    /// <summary>用户点了灵动岛（UI 线程）。</summary>
    event EventHandler? IslandClicked;
}
