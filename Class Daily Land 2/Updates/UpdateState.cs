using ClassDailyLand.UpdateContract;

namespace ClassDailyLand.App.Updates;

/// <summary>这次更新装的是什么。</summary>
public enum UpdateKind
{
    /// <summary>程序本体（exe / dll / 资源），替换完重启。</summary>
    App = 0,

    /// <summary>资源包（图标 / 节假日 / 课表模板），解压到 assets\，不用重启。</summary>
    Resources = 1,
}

/// <summary>关于页「检查更新」的状态机。</summary>
public enum UpdateStage
{
    /// <summary>还没检查过（按钮叫「检查更新」）。</summary>
    Idle = 0,

    /// <summary>正在拉 version.json。</summary>
    Checking = 1,

    /// <summary>已经是最新的（按钮恢复可用）。</summary>
    UpToDate = 2,

    /// <summary>发现新版本（按钮变成「重新检查」，另出「查看更新」）。</summary>
    Available = 3,

    /// <summary>两个源都拉不到（InfoBar 显示失败原因）。</summary>
    Failed = 4,
}

/// <summary>
/// 一次「可装的更新」。主程序判定完成后持有它，用户点「立即更新」时
/// 把它翻译成 <see cref="UpdateJob"/> 写给更新器 —— 所以这里必须包含
/// 更新器干活所需的全部信息（地址、哈希、大小、目标版本）。
/// </summary>
public sealed record UpdateOffer(
    UpdateKind Kind,
    string Channel,
    SemVer Version,
    string ReleaseDate,
    IReadOnlyList<string> Changelog,
    bool IsMandatory,

    string DownloadUrl,
    string Sha256,
    long Size,

    /// <summary>来自哪个源（Gitee / GitHub），只用于日志与失败提示。</summary>
    string Source,
    string VersionJsonUrl,

    /// <summary>兑底下载地址。点「立即更新」时会再拉一次另一源的 version.json 补上。</summary>
    string DownloadUrlFallback,
    string FallbackVersionJsonUrl);

/// <summary>
/// 一次检查的完整结果。两个包（程序本体 / 资源包）互不干扰 ——
/// 资源包挂了不应该让用户看到"检查更新失败"。
/// </summary>
public sealed record UpdateStatus(
    UpdateStage Stage,
    string Message,
    DateTime CheckedAt,
    UpdateOffer? App,
    UpdateOffer? Resources,
    string? Error)
{
    public static UpdateStatus Idle => new(UpdateStage.Idle, "", DateTime.MinValue, null, null, null);

    /// <summary>只要有任一包有更新就算"有更新"（供托盘菜单/提示判断）。</summary>
    public bool HasAnyUpdate => App is not null || Resources is not null;
}

/// <summary>用户点「立即更新」后，启动更新器的结果。</summary>
public sealed record UpdateLaunchResult(bool Ok, string Message, string? JobPath = null, int ExitCode = 0)
{
    public static UpdateLaunchResult Success(string jobPath) => new(true, "", jobPath);
    public static UpdateLaunchResult Fail(string message, int exitCode = 0) => new(false, message, null, exitCode);
}
