namespace CDL2.Updater;

/// <summary>
/// 更新器命令行。兼容主程序的 <c>--job &lt;job.json&gt;</c>、手工救援的
/// <c>--url/--hash/--target/--exe</c>，以及本版新增的 <c>--rollback &lt;备份目录&gt;</c>。
/// 未知参数一律忽略。
/// </summary>
public sealed class CommandLine
{
    public string? JobPath { get; private init; }
    public string? Url { get; private init; }
    public string? UrlFallback { get; private init; }
    public string? Sha256 { get; private init; }
    public string? TargetDir { get; private init; }
    public string? ExeName { get; private init; }
    public string? RollbackDir { get; private init; }

    public bool HasJob => !string.IsNullOrWhiteSpace(JobPath);
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
    public bool HasRollback => !string.IsNullOrWhiteSpace(RollbackDir);
    public bool DirectLaunch => !HasJob && !HasUrl && !HasRollback;

    public static CommandLine Parse(string[] args)
    {
        string? job = null, url = null, urlFallback = null, hash = null, target = null, exe = null, rollback = null;

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
                case "--rollback": rollback = value; break;
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
            RollbackDir = rollback,
        };
    }
}
