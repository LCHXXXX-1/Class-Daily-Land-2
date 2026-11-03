namespace ClassDailyLand2.Core.Diagnostics;

/// <summary>
/// 一个错误码的完整定义。code 与文案分离，便于以后接 i18n：
/// 显示文案永远查表拿 <see cref="Name"/> / <see cref="Meaning"/>，不写死在业务代码里。
///
/// <para><b>一个码 = 一类失败，不是一次失败。</b>
/// 同一个码可以在多处抛出（例如 <c>EUP3004</c> 可能是解压失败、条目非法或写盘失败），
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

    /// <summary>常见原因（按发生频率排序，给用户看的排查清单）。</summary>
    public string[] Causes { get; init; } = Array.Empty<string>();

    /// <summary>用户能自己做的处理（对应报错窗上的恢复操作）。</summary>
    public string[] Suggestions { get; init; } = Array.Empty<string>();

    /// <summary>技术排查要点：日志里搜什么关键字、看哪个文件。</summary>
    public string Technical { get; init; } = "";

    /// <summary>常见原因（清单页展示用，一行）。</summary>
    public string CausesText => Causes.Length == 0 ? "" : "原因：" + string.Join("；", Causes);

    /// <summary>处理建议（清单页展示用，一行）。</summary>
    public string SuggestionsText => Suggestions.Length == 0 ? "" : "建议：" + string.Join("；", Suggestions);

    /// <summary>技术排查要点（清单页展示用）。</summary>
    public string TechnicalText => string.IsNullOrWhiteSpace(Technical) ? "" : "排查：" + Technical;

    /// <summary>明细合并成一段（清单页一次绑定，省模板元素）。</summary>
    public string DetailText
    {
        get
        {
            var lines = new System.Collections.Generic.List<string>();
            if (Causes.Length > 0) lines.Add("原因：" + string.Join("；", Causes));
            if (Suggestions.Length > 0) lines.Add("建议：" + string.Join("；", Suggestions));
            if (!string.IsNullOrWhiteSpace(Technical)) lines.Add("排查：" + Technical);
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>明细是否为空（模板据此折叠空行）。</summary>
    public bool HasDetails => Causes.Length > 0 || Suggestions.Length > 0 || !string.IsNullOrWhiteSpace(Technical);

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
        "没有专门处理的异常，走兜底错误码。看到它说明还有一条没被归类的崩溃路径。")
    {
        Causes = new[] { "某个 catch 分支里没有归类", "第三方库抛出非约定异常", "线程/异步上下文的边缘情况" },
        Suggestions = new[] { "重启软件", "导出日志反馈（这条码出现时日志尾部就是现场）" },
        Technical = "搜日志里的 ERROR 与堆栈；对照 ErrorCodes.All 看是否缺少专用码",
    };

    public static readonly ErrorCode InvalidArgument = new("ESY1001", "参数不合法",
        "调用方传进来的值不满足前置条件（空、越界、格式不对）。", "WARN")
    {
        Causes = new[] { "配置文件被手工改坏", "插件回传了非法值", "界面输入未做校验" },
        Suggestions = new[] { "检查最近改过的设置项", "如与插件有关，先禁用该插件" },
        Technical = "搜日志里的 [参数] 前缀；核对 settings.json 对应键的类型与取值范围",
    };

    public static readonly ErrorCode StateConflict = new("ESY2001", "状态冲突",
        "当前状态不允许这个操作，比如重复安装、在没有更新可用时点更新。", "WARN")
    {
        Causes = new[] { "重复点击（前一次还没结束）", "更新器/安装器已在运行", "插件正在被占用" },
        Suggestions = new[] { "等当前任务结束后再试", "必要时重启软件" },
        Technical = "搜 [状态] 与 [更新] 标记；确认是否残留 Updater 进程",
    };

    public static readonly ErrorCode SingleInstanceConflict = new("ESY2002", "单实例冲突",
        "已有一个实例在运行，第二个实例本应唤醒前者后退出；这条码表示唤醒流程出了问题。", "WARN")
    {
        Causes = new[] { "命名事件创建失败", "上一个实例处于僵尸状态" },
        Suggestions = new[] { "在任务管理器结束残留进程后重新启动" },
        Technical = "搜 [单实例]；核对是否同时存在两个同名进程",
    };

    public static readonly ErrorCode FileMissing = new("ESY3001", "文件不存在",
        "程序期望的数据文件或目录缺失（首次运行属正常，其余情况需要排查）。", "WARN")
    {
        Causes = new[] { "首次运行尚未生成", "被清理软件/同步盘删除", "插件包内缺文件" },
        Suggestions = new[] { "重启软件让程序重建默认文件", "从备份恢复数据目录" },
        Technical = "搜 [文件] 缺失关键字；核对数据根目录下的实际文件清单",
    };

    public static readonly ErrorCode FileIoFailed = new("ESY3002", "文件读写失败",
        "磁盘写入、替换或删除失败，常见原因是文件被占用或目录无权限。")
    {
        Causes = new[] { "文件被杀毒软件/同步盘占用", "目录只读或无权限", "磁盘已满", "路径过长" },
        Suggestions = new[] { "换一个本地目录（避免同步盘）", "关掉占用该目录的程序后重试", "以管理员身份运行" },
        Technical = "搜 [文件] 与 IOException/UnauthorizedAccessException；核对 HResult",
    };

    public static readonly ErrorCode PermissionDenied = new("ESY3003", "权限不足",
        "操作被系统拒绝，通常是路径需要管理员权限或文件受保护。", "WARN")
    {
        Causes = new[] { "安装在 Program Files 等受保护目录", "文件属性为只读/系统", "UAC 未通过" },
        Suggestions = new[] { "以管理员身份运行", "把程序放到用户目录下" },
        Technical = "搜 UnauthorizedAccessException；确认进程是否提权（启动横幅里有 elevated）",
    };

    public static readonly ErrorCode DiskFull = new("ESY3004", "磁盘空间不足",
        "写入失败且系统返回磁盘已满，常见于更新解压与日志暴涨。")
    {
        Causes = new[] { "更新包解压需要额外空间", "日志长时间未清理", "磁盘本身剩余很少" },
        Suggestions = new[] { "清理磁盘后重试", "设置里清理日志" },
        Technical = "搜 [磁盘] 与 FreeSpace；核对 HResult 0x80070070 / 0x80070027",
    };

    public static readonly ErrorCode ShutdownFailed = new("ESY3005", "关闭流程异常",
        "退出时的保存/清理步骤抛错，程序仍会退出但可能留下未落盘的数据。", "WARN")
    {
        Causes = new[] { "保存设置时磁盘异常", "插件卸载抛错", "运行标记删除失败" },
        Suggestions = new[] { "下次启动确认设置是否生效", "必要时重新配置" },
        Technical = "搜 [退出] 前缀；核对 settings.json 是否完整",
    };

    public static readonly ErrorCode ProcessTerminated = new("ESY4002", "关键进程异常退出",
        "一个关键进程在任务结束前就退出了（可能是主窗口进程被强制结束，也可能是主程序依赖的子进程）。")
    {
        Causes = new[] { "主程序被任务管理器/更新器强制结束", "看门狗检测到主进程消失", "子进程崩溃" },
        Suggestions = new[] { "重启软件", "更新时看到此码属正常（更新器会关闭主程序）" },
        Technical = "搜 [看门狗] 与 [进程] 关键字；核对事件时间与更新任务时间是否重合",
    };

    public static readonly ErrorCode UnexpectedExit = new("ESY4003", "上次异常退出",
        "上一次运行没有走正常关闭流程就结束了（崩溃、断电或被强制结束）。")
    {
        Causes = new[] { "崩溃（未捕获异常）", "断电/强制关机", "被强制结束进程", "旧版更新器未清运行标记" },
        Suggestions = new[] { "看日志尾部确认当时在做什么", "若与更新有关，升级到最新版即可" },
        Technical = "对比 .running.json 的 started_at 与 app.log 最后一行时间",
    };

    public static readonly ErrorCode WatchdogRestart = new("ESY4004", "看门狗干预",
        "看门狗检测到关键进程缺失并尝试恢复，可能是误报也可能是真崩溃。", "WARN")
    {
        Causes = new[] { "进程名被改动/被其它程序占用", "主进程正在退出时的竞态" },
        Suggestions = new[] { "若频繁出现，导出日志反馈" },
        Technical = "搜 [看门狗]；核对进程快照与时间点",
    };

    // ————————————————————— 界面 UI —————————————————————

    public static readonly ErrorCode UiActionFailed = new("EUI2001", "界面操作失败",
        "用户操作触发的界面逻辑出错（列表渲染、对话框、页面切换等）。")
    {
        Causes = new[] { "绑定数据为空/类型不符", "窗口在动画中查询布局", "插件往界面塞了非法元素" },
        Suggestions = new[] { "重试该操作", "重启软件；若与插件有关先禁用插件" },
        Technical = "搜 [界面] 与堆栈；确认是否有插件参与该页面",
    };

    public static readonly ErrorCode WindowCreateFailed = new("EUI4001", "窗口创建失败",
        "某个窗口/XAML 初始化时抛错，通常是资源缺失或 XAML 解析问题。")
    {
        Causes = new[] { "资源字典缺失", "XAML 引用了不存在的元素", "显卡驱动导致渲染初始化失败" },
        Suggestions = new[] { "重启软件", "更新显卡驱动", "导出日志反馈" },
        Technical = "搜 XamlParseException / 缺失的资源 Key 名",
    };

    public static readonly ErrorCode ThemeApplyFailed = new("EUI2002", "主题应用失败",
        "切换深浅色时资源字典或画笔更新失败，界面可能显示为默认配色。", "WARN")
    {
        Causes = new[] { "资源键被插件覆盖", "系统主题接口异常" },
        Suggestions = new[] { "在设置里再切一次主题" },
        Technical = "搜 [主题]；核对 _Bg/_Text 等键是否被覆盖",
    };

    // ————————————————————— 插件 PL —————————————————————

    public static readonly ErrorCode PluginManifestInvalid = new("EPL1001", "插件清单无效",
        "plugin.json 缺失、字段缺失或格式不合法，插件无法被识别。", "WARN")
    {
        Causes = new[] { "作者漏写必填字段", "JSON 语法错误", "schema 版本不匹配" },
        Suggestions = new[] { "联系插件作者更新", "卸载该插件" },
        Technical = "搜 [插件] 清单；核对 id/version/entry 字段",
    };

    public static readonly ErrorCode PluginLoadFailed = new("EPL2001", "插件加载失败",
        "插件程序集能读到但初始化失败：类型找不到、构造函数抛异常、契约版本不匹配。")
    {
        Causes = new[] { "依赖 dll 缺失", "契约版本与主程序不匹配", "插件构造函数抛异常" },
        Suggestions = new[] { "更新插件到最新版", "禁用该插件" },
        Technical = "搜 [插件] 加载 与 ReflectionTypeLoadException；核对契约版本号",
    };

    public static readonly ErrorCode PluginContractMismatch = new("EPL2002", "插件契约不匹配",
        "插件依赖的主程序契约版本与当前版本不一致，加载会被拒绝。", "WARN")
    {
        Causes = new[] { "插件过旧", "主程序升级后契约有破坏性变更" },
        Suggestions = new[] { "更新插件", "暂时禁用该插件" },
        Technical = "搜契约版本号；对照 Shared/UpdateContract 的兼容规则",
    };

    public static readonly ErrorCode PluginCrash = new("EPL4001", "插件运行时崩溃",
        "插件代码在回调中抛出未捕获异常，主程序已隔离该回调。")
    {
        Causes = new[] { "插件内部空引用/越界", "访问了已释放的宿主资源", "长时间阻塞导致超时" },
        Suggestions = new[] { "禁用该插件并向作者反馈", "重启软件" },
        Technical = "搜 [插件] 异常 与插件 id；堆栈中会标注宿主调用点",
    };

    public static readonly ErrorCode PluginInstallFailed = new("EPL3001", "插件安装失败",
        "解压、校验或写入插件目录的过程中失败，安装目录可能已回滚。")
    {
        Causes = new[] { "下载包不完整/哈希不符", "目标目录被占用", "磁盘空间不足" },
        Suggestions = new[] { "重试安装", "换一个市场源", "清理插件目录后重试" },
        Technical = "搜 [插件] 安装 与 sha256；核对 _trash- 目录是否残留",
    };

    public static readonly ErrorCode PluginUninstallFailed = new("EPL3002", "插件卸载失败",
        "插件目录删除不干净（通常是 dll 句柄未释放），可能残留 _trash- 目录。", "WARN")
    {
        Causes = new[] { "插件仍在运行", "文件被同步盘锁定" },
        Suggestions = new[] { "重启软件后再卸载", "手动删除 _trash- 目录" },
        Technical = "搜 [插件] 卸载；核对 _trash- 目录",
    };

    // ————————————————————— 插件市场 MK —————————————————————

    public static readonly ErrorCode MarketIndexInvalid = new("EMK1001", "市场索引格式不支持",
        "远端 index.json 的 schema 比本程序支持的版本新，需要升级主程序。", "WARN")
    {
        Causes = new[] { "主程序过旧", "远端索引被改坏" },
        Suggestions = new[] { "升级主程序", "稍后重试" },
        Technical = "搜 [市场] schema；对照本程序支持的版本常量",
    };

    public static readonly ErrorCode MarketAuthFailed = new("EMK2001", "市场身份校验失败",
        "开发者工具上传/审核需要身份，令牌无效或过期。", "WARN")
    {
        Causes = new[] { "令牌过期", "账号无权限", "系统时间偏差过大" },
        Suggestions = new[] { "重新登录", "校准系统时间" },
        Technical = "搜 [市场] 401/403；核对系统时间与令牌有效期",
    };

    public static readonly ErrorCode MarketFetchFailed = new("EMK3001", "市场索引拉取失败",
        "GitHub / Gitee 两个源都没能拿到可用的 index.json，页面会退回缓存。")
    {
        Causes = new[] { "网络不可达/被劫持", "两个源都限流", "返回内容不是合法 JSON" },
        Suggestions = new[] { "稍后重试", "切换市场源（设置 → 插件）", "检查代理设置" },
        Technical = "搜 [市场] 拉取 与 HTTP 状态码；核对 raw.githubusercontent/gitee 的可达性",
    };

    public static readonly ErrorCode MarketPackageFailed = new("EMK3002", "安装包下载或校验失败",
        "下载中断、哈希与索引不一致、包结构不合法。")
    {
        Causes = new[] { "网络中断", "索引里的 sha256 过期", "包内缺 plugin.json" },
        Suggestions = new[] { "重试", "换源", "向作者反馈" },
        Technical = "搜 [市场] 下载 与 sha256 对比结果",
    };

    // ————————————————————— 更新 UP —————————————————————

    public static readonly ErrorCode UpdateSchemaUnsupported = new("EUP1001", "更新任务版本不支持",
        "job.json 的 schema 高于当前更新器支持版本。", "WARN")
    {
        Causes = new[] { "主程序比更新器新（更新器未同步）" },
        Suggestions = new[] { "重新安装程序（会带上匹配的更新器）" },
        Technical = "搜 JobLoader 里的 schema 判定与 CurrentSchema 常量",
    };

    public static readonly ErrorCode UpdatePrepareFailed = new("EUP2001", "更新准备失败",
        "无法把更新器暂存到临时目录，或写 job.json 失败，因此不提供「立即更新」。")
    {
        Causes = new[] { "临时目录不可写", "磁盘空间不足", "Update\\Updater.exe 缺失" },
        Suggestions = new[] { "清理临时目录/磁盘", "重新安装程序", "手动下载安装包" },
        Technical = "搜 [更新] 准备；核对 %Temp%\\ClassDailyLand2.Update 与安装目录的 Update 子目录",
    };

    public static readonly ErrorCode UpdateAppStillRunning = new("EUP2002", "无法关闭主程序",
        "更新器尝试关闭主程序失败，替换无法开始。", "WARN")
    {
        Causes = new[] { "主程序以管理员身份运行而更新器没有", "进程卡死无法结束", "其它同名进程占用" },
        Suggestions = new[] { "手动退出主程序后重试", "以管理员身份运行更新器" },
        Technical = "搜 [关闭主程序] 与卡住的 PID；确认进程所有者与提权状态",
    };

    public static readonly ErrorCode UpdateCheckFailed = new("EUP3001", "检查更新失败",
        "所有更新源都不可达或返回内容不合法。")
    {
        Causes = new[] { "网络不可达/GitHub 与 Gitee 都超时", "version.json 格式错误" },
        Suggestions = new[] { "稍后重试", "在设置里切换更新源", "手动下载安装包" },
        Technical = "搜 [更新] 检查 与两个源的失败原因；核对 version.json 的 schema/version 字段",
    };

    public static readonly ErrorCode UpdateDownloadFailed = new("EUP3002", "更新包下载失败",
        "所有下载源都失败或长时间无进度。")
    {
        Causes = new[] { "网络中断", "Gitee 资产限速严重", "代理拦截" },
        Suggestions = new[] { "重试（会自动换源）", "手动下载安装包覆盖" },
        Technical = "搜 [下载] 与 换源记录；核对实际速率与停滞检测触发点",
    };

    public static readonly ErrorCode UpdateHashMismatch = new("EUP3003", "更新包校验失败",
        "下载包的 SHA-256 与 version.json 不一致（包损坏或版本信息过期）。")
    {
        Causes = new[] { "下载被截断/被中间设备改写", "version.json 的 sha256 与包不匹配" },
        Suggestions = new[] { "重试下载", "手动下载覆盖安装" },
        Technical = "搜 [校验] 期望/实际；对比 version.json 与线上资产的实际哈希",
    };

    public static readonly ErrorCode UpdateExtractFailed = new("EUP3004", "更新包解压失败",
        "包结构非法、条目越界、解压总量超限或磁盘写入失败。")
    {
        Causes = new[] { "zip 损坏", "包含非法路径（Zip Slip）", "磁盘空间不足" },
        Suggestions = new[] { "重新下载", "手动下载覆盖安装" },
        Technical = "搜 [解压] 与非法条目路径；核对 Limits 配置",
    };

    public static readonly ErrorCode UpdateReplaceFailed = new("EUP3005", "更新替换失败",
        "写入文件阶段失败，已按备份账本回滚到上一版本。")
    {
        Causes = new[] { "文件被占用（杀软/同步盘）", "权限不足", "磁盘错误" },
        Suggestions = new[] { "关闭占用程序后重试", "改用本地目录", "手动下载覆盖安装" },
        Technical = "搜 [替换] 与 journal.txt；备份目录里保留替换前的全部文件",
    };

    public static readonly ErrorCode UpdateRollbackFailed = new("EUP3006", "回滚失败",
        "替换失败后尝试按账本恢复，但有文件没能还原，程序目录可能处于半新半旧状态。")
    {
        Causes = new[] { "备份文件缺失", "还原时目标文件仍被占用" },
        Suggestions = new[] { "手动下载最新安装包覆盖安装", "从备份目录手动复制文件" },
        Technical = "搜 [回滚] 与 journal.txt 中未成功的条目",
    };

    public static readonly ErrorCode UpdateLaunchFailed = new("EUP3007", "启动更新器失败",
        "更新器进程没能拉起来（被拦截、缺依赖或路径不可执行）。")
    {
        Causes = new[] { "杀软拦截", "缺少 .NET 运行时", "Update\\Updater.exe 被删除" },
        Suggestions = new[] { "加入杀软白名单", "重装程序", "手动下载覆盖" },
        Technical = "搜 [更新] 启动；核对 Win32Exception 的错误码",
    };

    // ————————————————————— 数据 DT —————————————————————

    public static readonly ErrorCode DataCorrupted = new("EDT3001", "数据文件损坏",
        "JSON 解析失败，原文件已隔离为 .bad 并回退默认值，用户的旧数据需要人工恢复。", "WARN")
    {
        Causes = new[] { "写入时断电/崩溃", "同步盘冲突覆盖", "手工编辑出错" },
        Suggestions = new[] { "程序已回退默认值，可继续使用", "需要找回数据时用 .bad 文件修复" },
        Technical = "搜 [数据] 损坏 与 .bad 文件名；对比文件大小与末尾内容是否被截断",
    };

    public static readonly ErrorCode DataWriteFailed = new("EDT3002", "数据保存失败",
        "原子写入失败（先写临时文件再替换），本次改动没有落盘。")
    {
        Causes = new[] { "磁盘满", "目录被占用", "文件只读" },
        Suggestions = new[] { "清理磁盘后重试", "检查目录权限" },
        Technical = "搜 [数据] 写入；核对 .tmp 残留文件",
    };

    public static readonly ErrorCode DataMigrationFailed = new("EDT3003", "数据迁移失败",
        "旧版本数据升级到新结构时失败，已保留原文件。", "WARN")
    {
        Causes = new[] { "旧数据字段缺失", "迁移逻辑边界未覆盖" },
        Suggestions = new[] { "导出反馈旧数据文件", "必要时重装后重新配置" },
        Technical = "搜 [迁移]；保留的原始文件路径会写进日志",
    };

    public static readonly ErrorCode BackupFailed = new("EDT3004", "备份创建失败",
        "更新/重装前的备份没有完整生成，替换阶段的风险保护会降级。", "WARN")
    {
        Causes = new[] { "磁盘空间不足", "备份目录不可写" },
        Suggestions = new[] { "清理磁盘后重试", "手动备份 settings.json 与 plugins" },
        Technical = "搜 [备份]；核对 UpdateBackup 目录下的文件数",
    };

    // ————————————————————— 外部依赖 IF —————————————————————

    public static readonly ErrorCode NetworkUnreachable = new("EIF3001", "网络不可达",
        "连接根本没建立（DNS 失败、断网、代理拒绝）。", "WARN")
    {
        Causes = new[] { "断网/校园网未登录", "代理配置错误", "DNS 污染" },
        Suggestions = new[] { "检查网络与代理", "稍后重试", "切换源" },
        Technical = "搜 [HTTP] 与 SocketException/HttpRequestException；核对目标域名解析",
    };

    public static readonly ErrorCode HttpError = new("EIF3002", "服务返回错误状态",
        "连接建立了但对方返回 4xx/5xx。", "WARN")
    {
        Causes = new[] { "接口变更/资源被删", "触发限流", "需要鉴权" },
        Suggestions = new[] { "稍后重试", "升级到最新版（接口可能已调整）" },
        Technical = "搜 [HTTP] 状态码；核对响应体头部",
    };

    public static readonly ErrorCode NetTimeout = new("EIF3003", "网络超时",
        "请求发出后长时间没有响应，已按超时中止。", "WARN")
    {
        Causes = new[] { "网络抖动", "对方服务慢", "前置代理卡住" },
        Suggestions = new[] { "重试", "换源" },
        Technical = "搜 [HTTP] 超时 与实际耗时",
    };

    public static readonly ErrorCode TlsFailed = new("EIF3004", "HTTPS 握手失败",
        "TLS 握手不成功（证书、协议版本、被中间设备拦截）。", "WARN")
    {
        Causes = new[] { "系统证书过期", "被抓包工具拦截", "系统时间错误" },
        Suggestions = new[] { "校准系统时间", "关闭抓包/代理软件" },
        Technical = "搜 AuthenticationException；核对系统证书链",
    };

    public static readonly ErrorCode JsonInvalid = new("EIF3005", "响应不是合法 JSON",
        "对方返回的内容无法解析（被劫持、返回了 HTML 错误页）。", "WARN")
    {
        Causes = new[] { "被认证网关劫持", "接口返回错误页", "编码异常" },
        Suggestions = new[] { "换网络/关代理重试", "稍后再试" },
        Technical = "搜 [JSON] 解析失败；日志会截取响应前 200 字符",
    };

    /// <summary>全部错误码（错误码清单页按这个顺序展示）。</summary>
    public static readonly IReadOnlyList<ErrorCode> All = new[]
    {
        // 系统
        Unknown, InvalidArgument, StateConflict, SingleInstanceConflict,
        FileMissing, FileIoFailed, PermissionDenied, DiskFull, ShutdownFailed,
        ProcessTerminated, UnexpectedExit, WatchdogRestart,
        // 界面
        UiActionFailed, WindowCreateFailed, ThemeApplyFailed,
        // 插件
        PluginManifestInvalid, PluginLoadFailed, PluginContractMismatch, PluginCrash,
        PluginInstallFailed, PluginUninstallFailed,
        // 市场
        MarketIndexInvalid, MarketAuthFailed, MarketFetchFailed, MarketPackageFailed,
        // 更新
        UpdateSchemaUnsupported, UpdatePrepareFailed, UpdateAppStillRunning,
        UpdateCheckFailed, UpdateDownloadFailed, UpdateHashMismatch, UpdateExtractFailed,
        UpdateReplaceFailed, UpdateRollbackFailed, UpdateLaunchFailed,
        // 数据
        DataCorrupted, DataWriteFailed, DataMigrationFailed, BackupFailed,
        // 外部依赖
        NetworkUnreachable, HttpError, NetTimeout, TlsFailed, JsonInvalid,
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

    /// <summary>模块展示顺序（错误码清单页按它分组）。</summary>
    public static readonly string[] ModuleOrder = { "SY", "UI", "PL", "MK", "UP", "DT", "IF" };
}
