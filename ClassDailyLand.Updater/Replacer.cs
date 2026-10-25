using ClassDailyLand.UpdateContract;

namespace ClassDailyLand.Updater;

/// <summary>替换结果。<see cref="BackupDir"/> 是这一轮备份留在哪儿（回滚与事后排查都靠它）。</summary>
internal sealed record ReplaceOutcome(bool Ok, string Message, string BackupDir);

/// <summary>
/// 把 staging 里的 payload 铺到程序目录上。
///
/// 顺序刻意这样排：
///   1. 算计划（覆盖 / 新增 / 删除 / 保留）—— 纯逻辑，在 <see cref="ReplacePlanner"/> 里，可单测；
///   2. 检查磁盘空间；
///   3. 备份被覆盖与被删除的**旧文件**；
///   4. 抓保留区（settings / plugins）快照，再写入 / 删除；
///   5. 再抓一次快照比对 —— 这是「用户数据没被动过」的证明，不是假设；
///   6. 任何一步炸掉就按备份回滚。
/// </summary>
internal static class Replacer
{
    public static ReplaceOutcome Apply(UpdateJob job, string stagingDir, string targetDir,
                                       IReadOnlyList<string> payload, JobLog log)
    {
        string target;
        try { target = Path.GetFullPath(targetDir); }
        catch (Exception ex) { return new ReplaceOutcome(false, $"程序目录不合法：{ex.Message}", ""); }

        if (!Directory.Exists(target))
            return new ReplaceOutcome(false, $"程序目录不存在：{target}", "");

        var existing = EnumerateRelative(target);
        var plan = ReplacePlanner.Build(existing, payload, job.Preserve, job.PreservePatterns);

        log.Write($"替换计划：新增 {plan.ToCreate.Count}，覆盖 {plan.ToUpdate.Count}，" +
                  $"删除 {plan.ToDelete.Count}，保留 {plan.Preserved.Count}");

        var spaceError = CheckFreeSpace(job, stagingDir, payload, target, log);
        if (spaceError is not null) return new ReplaceOutcome(false, spaceError, "");

        var backupDir = Path.Combine(job.Paths.BackupRoot, job.JobId);
        var created = new List<string>();
        var overwritten = new List<string>();
        var deleted = new List<string>();

        try
        {
            Backup(target, backupDir, plan.ToUpdate, plan.ToDelete, log);

            var before = PreservedSnapshot.Capture(target, job.Preserve, job.PreservePatterns);

            foreach (var relative in plan.ToCreate.Concat(plan.ToUpdate))
            {
                var src = Path.Combine(stagingDir, relative.Replace('/', Path.DirectorySeparatorChar));
                var dst = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));

                var dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

                var existed = File.Exists(dst);
                File.Copy(src, dst, overwrite: true);

                if (existed) overwritten.Add(relative);
                else created.Add(relative);
            }

            log.Write($"已写入 {plan.ToCreate.Count + plan.ToUpdate.Count} 个文件");

            foreach (var relative in plan.ToDelete)
            {
                var dst = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    if (File.Exists(dst))
                    {
                        File.SetAttributes(dst, FileAttributes.Normal);
                        File.Delete(dst);
                    }
                    deleted.Add(relative);
                }
                catch (Exception ex)
                {
                    // 删不掉旧文件不致命（比如被杀软占着），记一笔就算了
                    log.Write($"删除失败（忽略）：{relative} → {ex.Message}");
                }
            }

            if (plan.ToDelete.Count > 0)
                log.Write($"已清理 {deleted.Count} 个旧文件");

            // 第三重保护：写完再证明一次用户数据一个字节都没动
            var after = PreservedSnapshot.Capture(target, job.Preserve, job.PreservePatterns);
            var difference = PreservedSnapshot.Compare(before, after);
            if (difference is not null)
            {
                log.Write("保留区被改动，开始回滚：" + difference);
                Rollback(target, backupDir, created, overwritten, deleted, log);
                return new ReplaceOutcome(false, "替换过程动到了用户数据（settings / plugins），已回滚并保留旧版本：" + difference, backupDir);
            }

            log.Write("替换完成；用户数据（settings / plugins）未被改动");
            return new ReplaceOutcome(true, "", backupDir);
        }
        catch (Exception ex)
        {
            log.Write("替换失败，开始回滚：" + ex.Message);
            Rollback(target, backupDir, created, overwritten, deleted, log);
            return new ReplaceOutcome(false, "替换文件失败：" + ex.Message, backupDir);
        }
    }

    /// <summary>空间不够就**在动手之前**失败：替换到一半没地方放备份是最难收拾的状态。</summary>
    private static string? CheckFreeSpace(UpdateJob job, string stagingDir, IReadOnlyList<string> payload,
                                          string target, JobLog log)
    {
        try
        {
            var payloadBytes = 0L;
            foreach (var relative in payload)
            {
                var full = Path.Combine(stagingDir, relative.Replace('/', Path.DirectorySeparatorChar));
                var info = new FileInfo(full);
                if (info.Exists) payloadBytes += info.Length;
            }

            var root = Path.GetPathRoot(target);
            if (string.IsNullOrEmpty(root)) return null;

            var drive = new DriveInfo(root);
            // 备份与被覆盖的旧文件大小相当，粗略按 payload 的两倍估
            var need = payloadBytes * 2 + job.Limits.MinFreeBytesAfter;
            if (drive.AvailableFreeSpace < need)
            {
                return $"磁盘空间不足：可用 {Bytes(drive.AvailableFreeSpace)}，至少需要 {Bytes(need)}";
            }

            return null;
        }
        catch (Exception ex)
        {
            log.Write("检查磁盘空间失败（继续替换）：" + ex.Message);
            return null;
        }
    }

    private static void Backup(string target, string backupDir, IReadOnlyCollection<string> toUpdate,
                               IReadOnlyCollection<string> toDelete, JobLog log)
    {
        var filesRoot = Path.Combine(backupDir, "files");
        var count = 0;

        foreach (var relative in toUpdate.Concat(toDelete))
        {
            var src = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue;

            var dst = Path.Combine(filesRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

            File.Copy(src, dst, overwrite: true);
            count++;
        }

        // 备份清单：出问题时一眼能看出这一轮动过哪些文件
        try
        {
            var manifest = Path.Combine(backupDir, "manifest.txt");
            var lines = new List<string>
            {
                $"job_id: {Path.GetFileName(backupDir)}",
                $"created_at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"target: {target}",
                "",
                "[备份的文件]",
            };
            lines.AddRange(toUpdate.Concat(toDelete).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            File.WriteAllLines(manifest, lines, System.Text.Encoding.UTF8);
        }
        catch { /* 清单只是给人看的 */ }

        log.Write($"已备份 {count} 个旧文件到 {backupDir}");
    }

    private static void Rollback(string target, string backupDir, List<string> created,
                                 List<string> overwritten, List<string> deleted, JobLog log)
    {
        var filesRoot = Path.Combine(backupDir, "files");

        // 1) 删掉这一轮新建出来的
        foreach (var relative in created)
        {
            try
            {
                var dst = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(dst)) File.Delete(dst);
            }
            catch (Exception ex) { log.Write($"回滚：删不掉新文件 {relative} → {ex.Message}"); }
        }

        // 2) 把覆盖掉的、删掉的从备份拷回去
        foreach (var relative in overwritten.Concat(deleted))
        {
            try
            {
                var src = Path.Combine(filesRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(src))
                {
                    log.Write($"回滚：备份里没有 {relative}，跳过");
                    continue;
                }

                var dst = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
                var dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

                File.Copy(src, dst, overwrite: true);
            }
            catch (Exception ex) { log.Write($"回滚：恢复 {relative} 失败 → {ex.Message}"); }
        }

        log.Write("回滚结束（备份仍保留在 " + backupDir + "）");
    }

    private static List<string> EnumerateRelative(string root)
    {
        var list = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = SafeRelativePath.Normalize(Path.GetRelativePath(root, file));
                if (relative.Length > 0) list.Add(relative);
            }
        }
        catch (Exception)
        {
            // 枚举不了就当作空：替换计划会退化成"全是新增"，不会误删
        }
        return list;
    }

    private static string Bytes(long value)
    {
        if (value >= 1024L * 1024 * 1024) return $"{value / 1024.0 / 1024 / 1024:0.0} GB";
        if (value >= 1024L * 1024) return $"{value / 1024.0 / 1024:0.0} MB";
        return $"{value / 1024.0:0} KB";
    }
}
