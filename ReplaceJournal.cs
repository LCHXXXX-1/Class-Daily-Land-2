using ClassDailyLand2.UpdateContract;
using System.Text;

namespace CDL2.Updater;

/// <summary>
/// 替换动作的落盘账本（备份目录里的 <c>journal.txt</c>）。
///
/// 旧版把"这轮创建/覆盖/删除了哪些文件"记在内存里，更新器进程被硬杀就全丢 ——
/// 用户只能得到一个损坏的程序目录。journal 让恢复变成**可离线重放**的操作：
/// <c>Updater.exe --rollback &lt;备份目录&gt;</c> 照着账本把旧文件搬回去。
///
/// 账本格式（一行一条，TAB 分隔）：
///   meta:  target / from / to / created_at
///   动作:  created|overwritten|deleted \t 相对路径
///   收尾:  completed
/// </summary>
public sealed class ReplaceJournal
{
    public string BackupDir { get; }
    public string FilesRoot => Path.Combine(BackupDir, "files");

    private readonly string _target;
    private readonly string _journalPath;
    private readonly List<string> _pending = new();
    private bool _completed;

    public ReplaceJournal(string backupDir, string target)
    {
        BackupDir = backupDir;
        _target = target;
        _journalPath = Path.Combine(backupDir, "journal.txt");
        Directory.CreateDirectory(FilesRoot);
    }

    public void WriteHeader(UpdateJob job)
    {
        try
        {
            var lines = new[]
            {
                $"target\t{job.App.InstallDir}",
                $"from\t{job.App.CurrentVersion}",
                $"to\t{job.App.TargetVersion}",
                $"created_at\t{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            };
            File.AppendAllLines(_journalPath, lines, Encoding.UTF8);
        }
        catch { /* 账本写不进去：回滚能力退化，但主流程继续 */ }
    }

    public void Record(string action, string relative) => _pending.Add($"{action}\t{relative}");

    public void Flush()
    {
        if (_pending.Count == 0) return;
        try
        {
            File.AppendAllLines(_journalPath, _pending, Encoding.UTF8);
            _pending.Clear();
        }
        catch { /* 留在内存里，下一次 Flush 再试 */ }
    }

    public void Complete()
    {
        if (_completed) return;
        _completed = true;
        Record("completed", "");
        Flush();
    }

    /// <summary>按账本恢复：删掉新建的，把覆盖 / 删除的从 files\ 搬回去。</summary>
    public void RestoreAll(UpdaterLog log) => Restore(BackupDir, _target, log);

    /// <summary>
    /// 离线回滚入口（<c>--rollback &lt;备份目录&gt;</c>）。
    /// 目标目录从账本的 target 行读；返回 null = 成功，否则人话原因。
    /// </summary>
    public static string? Restore(string backupDir, string? targetOverride, UpdaterLog log)
    {
        var journalPath = Path.Combine(backupDir, "journal.txt");
        if (!File.Exists(journalPath))
            return "备份目录里没有 journal.txt，无法回滚：" + backupDir;

        var filesRoot = Path.Combine(backupDir, "files");
        string? target = targetOverride;
        var actions = new List<(string Action, string Relative)>();

        try
        {
            foreach (var line in File.ReadAllLines(journalPath, Encoding.UTF8))
            {
                if (line.Length == 0) continue;
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;

                var key = line[..tab].Trim();
                var value = line[(tab + 1)..].Trim();

                if (key.Equals("target", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(target))
                    target = value;
                else if (key is "created" or "overwritten" or "deleted")
                    actions.Add((key, value));
            }
        }
        catch (Exception ex)
        {
            return "读回滚账本失败：" + ex.Message;
        }

        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
            return "备份账本里的程序目录不存在：" + (target ?? "（空）");

        var restored = 0;
        var deleted = 0;
        var failures = 0;

        foreach (var (action, relative) in actions)
        {
            if (string.IsNullOrWhiteSpace(relative)) continue;
            var dst = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                if (action == "created")
                {
                    // 这轮新建的：回滚 = 删掉
                    if (File.Exists(dst))
                    {
                        File.SetAttributes(dst, FileAttributes.Normal);
                        File.Delete(dst);
                        deleted++;
                    }
                }
                else
                {
                    var src = Path.Combine(filesRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(src))
                    {
                        log.Write($"回滚：备份里没有 {relative}，跳过");
                        failures++;
                        continue;
                    }

                    var dstDir = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                    File.Copy(src, dst, overwrite: true);
                    restored++;
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.Write($"回滚失败：{relative} → {ex.Message}");
            }
        }

        log.Write($"回滚完成：恢复 {restored} 个、删除 {deleted} 个、失败 {failures} 个（备份保留在 {backupDir}）");
        return failures > 0 ? $"回滚完成，但有 {failures} 个文件没恢复成功，详见日志" : null;
    }
}
