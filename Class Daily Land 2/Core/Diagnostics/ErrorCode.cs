namespace ClassDailyLand2.Core.Diagnostics;

/// <summary>
/// 一个错误码的完整定义。code 与文案分离，便于以后接 i18n：
/// 显示文案永远查表拿 <see cref="Name"/> / <see cref="Meaning"/>，不写死在业务代码里。
///
/// <para><b>一个码 = 一类失败，不是一次失败。</b>
/// 同一个码可以在多处抛出（例如 <c>EPL3001</c> 可能是解压失败、校验失败或写盘失败），
/// 所以 <see cref="Meaning"/> 描述的是**这一类**的共同特征；
/// 「这一次到底为什么」写在异常消息里，界面上单独一行「本次原因」。
/// 如果某处的失败语义和现有码都不贴（用户看到"安装失败"但其实是版本冲突），
/// **新增一个码**，不要套用相近的码 —— 套用是让错误码失去意义的最快方式。</para>
///
/// <para><b>编码规则</b>：<c>E + 模块码(2字母) + 类型(1位数字) + 序号(3位)</c>，固定 7 位。</para>
/// <list type="bullet">
///   <item>模块码：SY 系统 / UI 界面 / PL 插件 / MK 插件市场 / UP 更新 / DT 数据 / IF 外部依赖</item>
///   <item>类型：1 参数校验 / 2 业务规则 / 3 外部依赖或 IO / 4 未预期</item>
///   <item>序号：0001 起，按模块递增，**不复用**（下线的码留着，避免旧日志对不上）</item>
/// </list>
/// 例：<c>EPL2001</c> = 插件模块 + 业务规则 + 第 1 条。
/// </summary>
public sealed record ErrorCode(
    string Code,
    string Name,
    string Meaning,
    string Level = "ERROR")
{
    /// <summary>模块码（code 的第 2~3 位），列表页按它分组。</summary>
    public string Module => Code.Length >= 3 ? Code.Substring(1, 2) : "??";

    /// <summary>模块的中文名，给错误码清单用。</summary>
    public string ModuleName => ErrorCodes.ModuleNameOf(Module);

    public override string ToString() => $"{Code}（{Name}）";
}

/// <summary>
/// 错误码总表。**所有错误码只在这里定义**，业务代码一律引用 <see cref="ErrorCodes"/> 里的常量，
/// 不允许散落字符串（改文案、补说明都只动这一个文件）。
/// </summary>
public static class ErrorCodes
{
    // ————————————————————— 系统 SY —————————————————————
    public static readonly ErrorCode Unknown = new("ESY4001", "未预期异常",
        "没有专门处理的异常，走兜底错误码。看到它说明还有一条没被归类的崩溃路径。");

    public static readonly ErrorCode InvalidArgument = new("ESY1001", "参数不合法",
        "调用方传进来的值不满足前置条件（空、越界、格式不对）。", "WARN");

    public static readonly ErrorCode StateConflict = new("ESY2001", "状态冲突",
        "当前状态不允许这个操作，比如重复安装、在没有更新可用时点更新。", "WARN");

    public static readonly ErrorCode FileMissing = new("ESY3001", "文件不存在",
        "程序期望的数据文件或目录缺失（首次运行属正常，其余情况需要排查）。", "WARN");

    public static readonly ErrorCode FileIoFailed = new("ESY3002", "文件读写失败",
        "磁盘写入、替换或删除失败，常见原因是文件被占用或目录无权限。");

    public static readonly ErrorCode ProcessTerminated = new("ESY4002", "关键进程异常退出",
        "一个关键进程在任务结束前就退出了（可能是主窗口进程被强制结束，也可能是主程序依赖的子进程），" +
        "对应的功能会立刻不可用，重启程序可恢复。");

    public static readonly ErrorCode UnexpectedExit = new("ESY4003", "上次异常退出",
        "上一次运行没有走正常关闭流程就结束了（崩溃、断电或被强制结束）。" +
        "这条是下次启动时补报的，日志里的最后几行是当时的现场。");

    // ————————————————————— 界面 UI —————————————————————
    public static readonly ErrorCode UiActionFailed = new("EUI2001", "界面操作失败",
        "用户操作触发的界面逻辑出错（列表渲染、对话框、页面切换等）。");

    // ————————————————————— 插件 PL —————————————————————
    public static readonly ErrorCode PluginLoadFailed = new("EPL2001", "插件加载失败",
        "插件程序集能读到但初始化失败：类型找不到、构造函数抛异常、契约版本不匹配。");

    public static readonly ErrorCode PluginManifestInvalid = new("EPL1001", "插件清单无效",
        "plugin.json 缺失、字段缺失或格式不合法，插件无法被识别。", "WARN");

    public static readonly ErrorCode PluginInstallFailed = new("EPL3001", "插件安装失败",
        "解压、校验或写入插件目录的过程中失败，安装目录可能已回滚。");

    public static readonly ErrorCode PluginUninstallFailed = new("EPL3002", "插件卸载失败",
        "插件目录删除不干净（通常是 dll 句柄未释放），可能残留 _trash- 目录。", "WARN");

    // ————————————————————— 插件市场 MK —————————————————————
    public static readonly ErrorCode MarketFetchFailed = new("EMK3001", "市场索引拉取失败",
        "GitHub / Gitee 两个源都没能拿到可用的 index.json，页面会退回缓存。");

    public static readonly ErrorCode MarketIndexInvalid = new("EMK1001", "市场索引格式不支持",
        "远端 index.json 的 schema 比本程序支持的版本新，需要升级主程序。", "WARN");

    public static readonly ErrorCode MarketPackageFailed = new("EMK3002", "安装包下载或校验失败",
        "下载中断、哈希与索引不一致、包结构不合法。");

    // ————————————————————— 更新 UP —————————————————————
    public static readonly ErrorCode UpdateCheckFailed = new("EUP3001", "检查更新失败",
        "所有更新源都不可达或返回内容不合法。", "WARN");

    public static readonly ErrorCode UpdatePrepareFailed = new("EUP2001", "更新准备失败",
        "无法把更新器暂存到临时目录，或写 job.json 失败，因此不提供「立即更新」。");

    // ————————————————————— 数据 DT —————————————————————
    public static readonly ErrorCode DataCorrupted = new("EDT3001", "数据文件损坏",
        "JSON 解析失败，原文件已隔离为 .bad 并回退默认值，用户的旧数据需要人工恢复。", "WARN");

    /// <summary>全部错误码（错误码清单页按这个顺序展示）。</summary>
    public static readonly IReadOnlyList<ErrorCode> All = new[]
    {
        Unknown, InvalidArgument, StateConflict, FileMissing, FileIoFailed,
        ProcessTerminated, UnexpectedExit,
        UiActionFailed,
        PluginManifestInvalid, PluginLoadFailed, PluginInstallFailed, PluginUninstallFailed,
        MarketIndexInvalid, MarketFetchFailed, MarketPackageFailed,
        UpdateCheckFailed, UpdatePrepareFailed,
        DataCorrupted,
    };

    static readonly Dictionary<string, ErrorCode> ByCode =
        All.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>按错误码查定义；查不到返回 null（展示层据此显示"未登记的错误码"）。</summary>
    public static ErrorCode? Find(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : ByCode.GetValueOrDefault(code.Trim());

    /// <summary>模块码 → 中文名。</summary>
    public static string ModuleNameOf(string module) => module switch
    {
        "SY" => "系统",
        "UI" => "界面",
        "PL" => "插件",
        "MK" => "插件市场",
        "UP" => "更新",
        "DT" => "数据",
        "IF" => "外部依赖",
        _ => "其他",
    };
}
