using System.Text.Json.Serialization;

namespace ClassDailyLand.UpdateContract;

/// <summary>
/// 主程序写给更新器的本地任务文件（<c>job.json</c>），schema 版本 1。
///
/// 这是**唯一的跨进程契约**：主程序写完就退出，更新器读它干活。
/// 因此这里每个字段都要有明确的默认值与容错 —— 更新器宁可少做一步，
/// 也不能因为某个字段缺失就半途崩掉（那时主程序已经退了，用户没有界面）。
///
/// ★ 谁都不能覆盖的两棵树：<see cref="Preserve"/> 里的 <c>settings</c> 与 <c>plugins</c>。
///   前者是用户的课表/作业/名单/设置，后者是用户装的插件。打包时排除、替换时跳过、
///   替换后再做一次清单比对（见 <see cref="PreservedSnapshot"/>）。
/// </summary>
public sealed class UpdateJob
{
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")] public int Schema { get; set; } = CurrentSchema;
    [JsonPropertyName("job_id")] public string JobId { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = JobAction.Update;
    [JsonPropertyName("channel")] public string Channel { get; set; } = "stable";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";

    [JsonPropertyName("app")] public JobApp App { get; set; } = new();
    [JsonPropertyName("download")] public JobDownload Download { get; set; } = new();

    /// <summary>资源包；null = 本次不更新资源。</summary>
    [JsonPropertyName("resources")] public JobResources? Resources { get; set; }

    [JsonPropertyName("paths")] public JobPaths Paths { get; set; } = new();

    /// <summary>整棵子树保留（相对路径的第一段，忽略大小写）。默认就是 settings 与 plugins。</summary>
    [JsonPropertyName("preserve")] public List<string> Preserve { get; set; } = DefaultPreserve();

    /// <summary>文件名模式保留（如 <c>*.log</c>、<c>*.pdb</c>）。</summary>
    [JsonPropertyName("preserve_patterns")] public List<string> PreservePatterns { get; set; } = DefaultPreservePatterns();

    [JsonPropertyName("relaunch")] public bool Relaunch { get; set; } = true;
    [JsonPropertyName("relaunch_delay_ms")] public int RelaunchDelayMs { get; set; } = 800;
    [JsonPropertyName("rollback_on_failure")] public bool RollbackOnFailure { get; set; } = true;

    [JsonPropertyName("limits")] public JobLimits Limits { get; set; } = new();
    [JsonPropertyName("ui")] public JobUi Ui { get; set; } = new();

    public static List<string> DefaultPreserve() => new() { "settings", "plugins" };
    public static List<string> DefaultPreservePatterns() => new() { "*.log", "*.pdb", "*.bak-*" };

    /// <summary>缺失按 1 处理；高于当前版本说明更新器太老，不能猜。</summary>
    [JsonIgnore] public bool IsSchemaSupported => Schema <= CurrentSchema;

    /// <summary>把所有缺失字段补成可用值（更新器读到 job 第一步就调它）。</summary>
    public void Normalize()
    {
        if (Schema <= 0) Schema = CurrentSchema;

        if (string.IsNullOrWhiteSpace(JobId))
            JobId = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        if (string.IsNullOrWhiteSpace(Action)) Action = JobAction.Update;
        if (string.IsNullOrWhiteSpace(Channel)) Channel = "stable";

        App ??= new JobApp();
        Download ??= new JobDownload();
        Paths ??= new JobPaths();
        Limits ??= new JobLimits();
        Ui ??= new JobUi();

        if (Preserve is null || Preserve.Count == 0) Preserve = DefaultPreserve();
        if (PreservePatterns is null || PreservePatterns.Count == 0) PreservePatterns = DefaultPreservePatterns();

        if (App.Pid < 0) App.Pid = 0;
        if (string.IsNullOrWhiteSpace(App.ExeName)) App.ExeName = "Class Daily Land 2.exe";
        if (RelaunchDelayMs < 0) RelaunchDelayMs = 0;

        if (Download.TimeoutSeconds <= 0) Download.TimeoutSeconds = 120;
        if (Download.MaxAttempts <= 0) Download.MaxAttempts = 3;
        if (Download.Size < 0) Download.Size = 0;

        if (Limits.MaxEntries <= 0) Limits.MaxEntries = 5000;
        if (Limits.MaxTotalBytes <= 0) Limits.MaxTotalBytes = 512L * 1024 * 1024;

        if (Limits.MinFreeBytesAfter <= 0) Limits.MinFreeBytesAfter = 64L * 1024 * 1024;

        if (string.IsNullOrWhiteSpace(Ui.Theme)) Ui.Theme = "system";
        if (Ui.AutoCloseSeconds <= 0) Ui.AutoCloseSeconds = 3;

        // 路径默认值：全部基于 job_id 与系统目录推导，保证更新器不需要问任何人
        var temp = Path.GetTempPath();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrWhiteSpace(Paths.WorkDir))
            Paths.WorkDir = Path.Combine(temp, "ClassDailyLand.Update", JobId);
        if (string.IsNullOrWhiteSpace(Paths.BackupRoot))
            Paths.BackupRoot = Path.Combine(localAppData, "ClassDailyLand", "UpdateBackup");
        if (string.IsNullOrWhiteSpace(Paths.Log))
            Paths.Log = Path.Combine(localAppData, "ClassDailyLand", "logs", $"updater-{JobId}.log");
        if (string.IsNullOrWhiteSpace(Paths.Result))
            Paths.Result = Path.Combine(localAppData, "ClassDailyLand", "last-update.json");

        App.InstallDir = App.InstallDir?.Trim() ?? "";
    }
}

/// <summary>动作类型。</summary>
public static class JobAction
{
    public const string Update = "update";
    public const string Resources = "resources";
    public const string Rollback = "rollback";
}

/// <summary>被更新的那个程序。</summary>
public sealed class JobApp
{
    [JsonPropertyName("exe_name")] public string ExeName { get; set; } = "Class Daily Land 2.exe";
    [JsonPropertyName("install_dir")] public string InstallDir { get; set; } = "";

    /// <summary>要等的旧进程；&lt;= 0 表示不用等。</summary>
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("current_version")] public string CurrentVersion { get; set; } = "";
    [JsonPropertyName("target_version")] public string TargetVersion { get; set; } = "";
    [JsonPropertyName("mandatory")] public bool Mandatory { get; set; }
}

/// <summary>下载描述。</summary>
public sealed class JobDownload
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("url_fallback")] public string UrlFallback { get; set; } = "";

    /// <summary>64 位十六进制（小写）；校验时忽略大小写。</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

    /// <summary>预期字节数；0 表示用响应的 Content-Length 当分母。</summary>
    [JsonPropertyName("size")] public long Size { get; set; }

    /// <summary>单次"没有任何字节到达"的超时（不是总时长）。</summary>
    [JsonPropertyName("timeout_seconds")] public int TimeoutSeconds { get; set; } = 120;

    [JsonPropertyName("max_attempts")] public int MaxAttempts { get; set; } = 3;
}

/// <summary>资源包更新描述。</summary>
public sealed class JobResources
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("download")] public JobDownload Download { get; set; } = new();

    /// <summary>资源写入根（通常是 <c>&lt;install_dir&gt;\assets</c>）。</summary>
    [JsonPropertyName("install_dir")] public string InstallDir { get; set; } = "";

    /// <summary>替换成功后 <c>Set()</c> 的命名事件，用于通知正在运行的主程序热刷新。</summary>
    [JsonPropertyName("notify_event")] public string NotifyEvent { get; set; } = "";
}

/// <summary>各类落点。</summary>
public sealed class JobPaths
{
    /// <summary>zip、payload、job.json 所在目录（更新器自己也从这里的 bin 跑）。</summary>
    [JsonPropertyName("work_dir")] public string WorkDir { get; set; } = "";

    [JsonPropertyName("backup_root")] public string BackupRoot { get; set; } = "";

    /// <summary>更新器自己的日志。</summary>
    [JsonPropertyName("log")] public string Log { get; set; } = "";

    /// <summary>主程序的 app.log，成功后追加一行给人看。</summary>
    [JsonPropertyName("app_log")] public string AppLog { get; set; } = "";

    /// <summary><c>last-update.json</c> 落点（新程序启动时消费）。</summary>
    [JsonPropertyName("result")] public string Result { get; set; } = "";
}

/// <summary>解压/磁盘防护上限。</summary>
public sealed class JobLimits
{
    [JsonPropertyName("max_entries")] public int MaxEntries { get; set; } = 5000;
    [JsonPropertyName("max_total_bytes")] public long MaxTotalBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>替换前要求的目标盘剩余空间（解压 + 备份都要占地方）。</summary>
    [JsonPropertyName("min_free_bytes_after")] public long MinFreeBytesAfter { get; set; } = 64L * 1024 * 1024;
}

/// <summary>更新器窗口的外观选项。</summary>
public sealed class JobUi
{
    [JsonPropertyName("theme")] public string Theme { get; set; } = "system";
    [JsonPropertyName("topmost")] public bool Topmost { get; set; } = true;
    [JsonPropertyName("auto_close_seconds")] public int AutoCloseSeconds { get; set; } = 3;
}

/// <summary>更新器退出码（主程序与 updater 共用同一套含义）。</summary>
public static class UpdateExitCode
{
    public const int Success = 0;
    public const int BadJob = 1;
    public const int Cancelled = 2;
    public const int DownloadFailed = 3;
    public const int HashMismatch = 4;
    public const int ExtractFailed = 5;
    public const int ReplaceFailed = 6;
    public const int AccessDenied = 7;
    public const int AppStillRunning = 8;
    public const int DiskFull = 9;

    public static string Describe(int code) => code switch
    {
        Success => "更新完成",
        BadJob => "更新任务文件无效",
        Cancelled => "已取消更新",
        DownloadFailed => "下载失败",
        HashMismatch => "安装包校验失败",
        ExtractFailed => "安装包解压失败",
        ReplaceFailed => "替换文件失败",
        AccessDenied => "没有写入权限",
        AppStillRunning => "程序还没有完全退出",
        DiskFull => "磁盘空间不足",
        _ => "未知错误",
    };
}

/// <summary>
/// 更新结果（<c>last-update.json</c>）。
///
/// 替换开始前先写一份 <c>{ ok:false, stage:"replacing" }</c>，
/// 新程序启动时若读到"没完成的 replacing"，就说明上次更新被硬杀或断电打断，
/// 关于页给出「恢复到上一版本」按钮 —— 这是唯一不要求用户懂技术就能自救的路径。
/// </summary>
public sealed class LastUpdateResult
{
    [JsonPropertyName("job_id")] public string JobId { get; set; } = "";
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }

    /// <summary>replacing / done / failed / rolledback。</summary>
    [JsonPropertyName("stage")] public string Stage { get; set; } = "";

    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("finished_at")] public string FinishedAt { get; set; } = "";
    [JsonPropertyName("backup_dir")] public string BackupDir { get; set; } = "";

    /// <summary>主程序是否已经把这次结果提示给用户（避免每次启动都弹）。</summary>
    [JsonPropertyName("consumed")] public bool Consumed { get; set; }
}
