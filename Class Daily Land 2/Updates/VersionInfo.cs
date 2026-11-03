namespace ClassDailyLand2.Core;

/// <summary>
/// 全局版本号与部署配置的唯一真源：发版只改这一处。
/// 主程序、安装器、插件 IDE 都直接读这里的常量，
/// 不再依赖程序集属性或 MSBuild 的 &lt;Version&gt;。
/// </summary>
public static class VersionInfo
{
    public const string Version = "1.3.3";

    /// <summary>主更新源（version.json）。</summary>
    public const string VersionUrl =
        "https://gitee.com/lchxxxx/class-daily-land-2/raw/update/version.json";

    /// <summary>备用更新源（主源失败时使用）。</summary>
    public const string VersionUrlFallback =
        "https://raw.githubusercontent.com/LCHXXXX-1/Class-Daily-Land-2/update/version.json";

    // ——————————————— 插件市场 ———————————————
    //
    // 索引与安装包都放在 plugins 分支上，不依赖任何服务器：
    //   · 主源 Gitee raw（国内快），备源 GitHub raw（Gitee 挂了才用）
    //   · 索引里的 path 是相对路径，主备源共用同一条记录 —— 换域名不用改索引
    //
    // ★ Gitee 的 /raw/ 现在会 302 到 raw.giteeusercontent.com 的签名地址，
    //   HttpClient 必须 AllowAutoRedirect（MarketClient 里已开）。

    /// <summary>插件市场索引（主源）。</summary>
    public const string MarketIndexUrl =
        "https://gitee.com/lchxxxx/class-daily-land-2/raw/plugins/index.json";

    /// <summary>插件市场索引（备用源）。</summary>
    public const string MarketIndexUrlFallback =
        "https://raw.githubusercontent.com/LCHXXXX-1/Class-Daily-Land-2/plugins/index.json";

    /// <summary>安装包相对路径的拼接根（与主源对应）。</summary>
    public const string MarketRawRoot =
        "https://gitee.com/lchxxxx/class-daily-land-2/raw/plugins/";

    /// <summary>安装包相对路径的拼接根（与备用源对应）。</summary>
    public const string MarketRawRootFallback =
        "https://raw.githubusercontent.com/LCHXXXX-1/Class-Daily-Land-2/plugins/";

    /// <summary>主程序 exe 文件名。</summary>
    public const string ExeName = "Class Daily Land 2.exe";

    /// <summary>更新器所在子目录（程序目录下）。</summary>
    public const string UpdaterDir = "Update";

    /// <summary>资源包子目录（程序目录下）。</summary>
    public const string AssetsDir = "assets";
}
