namespace ClassDailyLand.Core;

/// <summary>
/// 全局版本号与部署配置的唯一真源：发版只改这一处。
/// 主程序、安装器、插件 IDE 都直接读这里的常量，
/// 不再依赖程序集属性或 MSBuild 的 &lt;Version&gt;。
/// </summary>
public static class VersionInfo
{
    public const string Version = "1.0";

    /// <summary>主更新源（version.json）。</summary>
    public const string VersionUrl =
        "https://gitee.com/lchxxxx/class-daily-land-2/raw/update/version.json";

    /// <summary>备用更新源（主源失败时使用）。</summary>
    public const string VersionUrlFallback =
        "https://raw.githubusercontent.com/LCHXXXX-1/Class-Daily-Land-2/update/version.json";

    /// <summary>主程序 exe 文件名。</summary>
    public const string ExeName = "Class Daily Land 2.exe";

    /// <summary>更新器所在子目录（程序目录下）。</summary>
    public const string UpdaterDir = "Update";

    /// <summary>资源包子目录（程序目录下）。</summary>
    public const string AssetsDir = "assets";
}
