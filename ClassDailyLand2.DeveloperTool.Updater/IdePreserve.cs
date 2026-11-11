namespace IDEUpdater;

/// <summary>
/// IDE 的用户数据落点 —— 与主程序的 <c>settings</c> / <c>plugins</c> 完全是两套东西。
///
/// 为什么更新器里还要留一份：权威来源是 job.json（IDE 侧组装时填 <c>job.Preserve</c>），
/// 但 <c>--url/--hash/--target/--exe</c> 这条救援路径合成的 job 会退回契约默认的 settings / plugins，
/// 照那个值跑会把 IDE 的插件工程和三个 json 一起冲掉。所以：契约字段优先，
/// 这里只是「契约没带 / 带的是默认值」时的兜底，<see cref="Program"/> 的 ClearTarget 也跟这里对齐。
///
/// 匹配语义沿用 <c>PreserveRules</c>：按相对路径的第一段比较，
/// 所以根目录下的 <c>ide_config.json</c> 直接写名字就能命中。
/// </summary>
internal static class IdePreserve
{
    /// <summary>目录：插件工程、已装插件、WPF 临时目录（App 构造函数改写 TEMP 指过去的）、WebView2 缓存降级落点。</summary>
    internal static readonly string[] Dirs = { "projects", "plugins", "Temp", "WebView2Data" };

    /// <summary>文件：IDE 配置、市场 Token、最近项目 —— 覆盖掉就是重装级的体验损失。</summary>
    internal static readonly string[] Files = { "ide_config.json", "ide_auth.json", "recent_projects.json" };

    /// <summary>目录 + 文件，合起来就是 job.Preserve 的形状。</summary>
    internal static List<string> All()
    {
        var all = new List<string>(Dirs.Length + Files.Length);
        all.AddRange(Dirs);
        all.AddRange(Files);
        return all;
    }
}
