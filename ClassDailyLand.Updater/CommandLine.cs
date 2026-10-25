namespace ClassDailyLand.Updater;

/// <summary>
/// 更新器的命令行。
///
/// 两种入口并存：
///   1. <c>--job &lt;job.json&gt;</c> —— 主程序走这条（契约文件里带着保留名单、上限、日志/结果落点）；
///   2. <c>--url / --hash / --target / --exe</c> —— 手工执行或救援时走这条，缺的字段用契约默认值补。
///
/// 未知参数一律忽略：宁可多一个不认识的开关，也不要因为某个拼写差异把更新整个拒掉。
/// </summary>
internal sealed class CommandLine
{
    public string? JobPath { get; private init; }
    public string? Url { get; private init; }
    public string? UrlFallback { get; private init; }
    public string? Sha256 { get; private init; }
    public string? TargetDir { get; private init; }
    public string? ExeName { get; private init; }

    public bool HasJob => !string.IsNullOrWhiteSpace(JobPath);
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);

    public static CommandLine Parse(string[] args)
    {
        string? job = null, url = null, urlFallback = null, hash = null, target = null, exe = null;

        for (var i = 0; i < args.Length; i++)
        {
            var name = (args[i] ?? "").Trim().ToLowerInvariant();
            if (name.Length == 0) continue;

            string? value = i + 1 < args.Length ? args[++i] : null;

            switch (name)
            {
                case "--job": job = value; break;
                case "--url": url = value; break;
                case "--url-fallback": urlFallback = value; break;
                case "--hash":
                case "--sha256": hash = value; break;
                case "--target": target = value; break;
                case "--exe": exe = value; break;
                default: break;
            }
        }

        return new CommandLine
        {
            JobPath = job,
            Url = url,
            UrlFallback = urlFallback,
            Sha256 = hash,
            TargetDir = target,
            ExeName = exe,
        };
    }
}
