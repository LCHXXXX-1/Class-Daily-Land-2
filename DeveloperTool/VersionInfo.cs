namespace DeveloperTool;

/// <summary>
/// 插件 IDE 自己的版本号与部署配置真源：发版只改这一处。
///
/// ★ 与主程序 <c>ClassDailyLand2.Core.VersionInfo</c> 完全解耦 —— IDE 不再链接
/// <c>..\Class Daily Land 2\Updates\VersionInfo.cs</c>，也不引用主程序命名空间。
/// 主程序升版不该带着 IDE 的版本号一起跳（两边发版节奏不同），
/// 更新器 <c>ClassDailyLand2.DeveloperTool.Updater</c> 反过来链接这一份。
/// </summary>
public static class VersionInfo
{
    public const string Version = "1.0.3";

    /// <summary>主更新源（version.json）。</summary>
    public const string VersionUrl =
        "https://gitee.com/lchxxxx/class-daily-land-2/raw/update/version.json";

    /// <summary>备用更新源（主源失败时使用）。</summary>
    public const string VersionUrlFallback =
        "https://raw.githubusercontent.com/LCHXXXX-1/Class-Daily-Land-2/update/version.json";

    /// <summary>IDE 的 exe 文件名（更新器拉起进程时用）。</summary>
    public const string ExeName = "DeveloperTool.exe";
}
