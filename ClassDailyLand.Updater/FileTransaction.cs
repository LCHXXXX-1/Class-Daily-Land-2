using System.Text.Json.Serialization;
using ClassDailyLand.UpdateContract;

namespace ClassDailyLand.Updater;

/// <summary>替换日志（落在备份目录里，回滚靠它）。</summary>
public sealed class ReplaceJournal
{
    [JsonPropertyName("job_id")] public string JobId { get; set; } = "";
    [JsonPropertyName("install_dir")] public string InstallDir { get; set; } = "";
    [JsonPropertyName("backup_dir")] public string BackupDir { get; set; } = "";
    [JsonPropertyName("started_at")] public string StartedAt { get; set; } = "";
    [JsonPropertyName("finished_at")] public string FinishedAt { get; set; } = "";

    /// <summary>新建的文件（回滚时删掉）。</summary>
    [JsonPropertyName("created")] public List<string> Created { get; set; } = new();

    /// <summary>被覆盖的文件（回滚时从备份还原）。</summary>
    [JsonPropertyName("updated")] public List<string> Updated { get; set; } = new();

    /// <summary>被删除的文件（回滚时从备份还原）。</summary>
    [JsonPropertyName("deleted")] public List<string> Deleted { get; set; } = new();

    /// <summary>白名单保留的文件（绝不该被碰；快照比对用）。</summary>
    [JsonPropertyName("preserved")] public List<string> Preserved { get; set; } = new();
}

/// <summary>
/// 备份 → 替换 → 回滚。这是更新器里唯一"改磁盘"的地方。
///
/// ★ **用户数据（settings\ 与 plugins\）的三重保护**：
///   1. <see cref="ReplacePlanner.Build"/> 把它们算进 <see cref="ReplacePlan.Preserved"/>，
///      既不覆盖也不删除；
///   2. 备份时把它们单独复制一份到 <c>backup\preserved\</c>（万一后续逻辑出 bug 能救回来）；
///   3. 替换完做**快照比对**（<see cref="PreservedSnapshot"/>），对不上立即回滚并报错。
/// </summary>
public static class FileTransaction
{
    /// <summary>
    /// 执行替换。成功返回 journal（已写盘），失败返回 error 并已自动回滚。
    /// </summary>
    public static (ReplaceJournal? Journal, string? Error) Execute(
        string installDir, string payloadDir, ReplacePlan plan,
        string backupRoot, string jobId,
        IReadOnlyList<FileFingerprint> preservedBefore,
        IReadOnlyCollection<string>? preserveDirs,
        IReadOnlyCollection<string>? preservePatterns,
        Action<string>? onStage = null)
    {
        var journal = new ReplaceJournal
        {
            JobId = jobId,
            InstallDir = installDir,
            StartedAt = DateTime.Now.ToString("o"),
            Preserved = plan.Preserved.ToList(),
        };

        var backupDir = Path.Combine(backupRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}");
        journal.BackupDir = backupDir;

        try
        {
            Directory.CreateDirectory(backupDir);

            // ── 1) 备份将被覆盖 / 删除的文件（回滚的原料） ──
            onStage?.Invoke("正在备份…");
            var filesRoot = Path.Combine(backupDir, "files");
            foreach (var rel in plan.ToUpdate.Concat(plan.ToDelete))
            {
                var src = Path.Combine(installDir, rel);
                if (!File.Exists(src)) continue;

                var dest = Path.Combine(filesRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, overwrite: true);
            }

            // 用户数据单独存一份（第三重保险的"抢救原料"）
            var preservedRoot = Path.Combine(backupDir, "preserved");
            foreach (var rel in plan.Preserved)
            {
                var src = Path.Combine(installDir, rel);
                if (!File.Exists(src)) continue;

                var dest = Path.Combine(preservedRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, overwrite: true);
            }

            // ── 2) 覆盖（exe 最后：它是最热的文件） ──
            onStage?.Invoke("正在替换文件…");
            foreach (var rel in Order(plan.ToUpdate))
            {
                CopyWithRetry(Path.Combine(payloadDir, rel), Path.Combine(installDir, rel));
                journal.Updated.Add(rel);
            }

            foreach (var rel in plan.ToCreate)
            {
                CopyWithRetry(Path.Combine(payloadDir, rel), Path.Combine(installDir, rel));
                journal.Created.Add(rel);
            }

            // ── 3) 删除陈旧文件（已在备份里） ──
            onStage?.Invoke("正在清理旧版本文件…");
            foreach (var rel in plan.ToDelete)
            {
                var target = Path.Combine(installDir, rel);
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    journal.Deleted.Add(rel);
                }
                catch (IOException)
                {
                    // 删不掉的陈旧文件不致命（可能被杀软抓着），记日志继续
                }
            }

            // 删掉因此空出来的目录（自底向上；保留子树与安装根绝不碰）
            foreach (var rel in plan.ToDelete)
                RemoveEmptyParents(installDir, rel, preserveDirs, preservePatterns,
                    msg => UpdaterLog.Info(msg));

            // ── 4) 用户数据快照比对（第三重保险） ──
            //    ★ 前后两次必须用**同一套保留规则**。曾经这里写成 Capture(installDir, null, null)，
            //      规则是 null 时一个文件都匹配不到，"after"永远是空列表 → 每次更新都误判
            //      "动到了用户数据"然后回滚。沙箱演练时真实踩到过这个坑。
            onStage?.Invoke("正在校验用户数据…");
            var preservedAfter = PreservedSnapshot.Capture(installDir, preserveDirs, preservePatterns);
            var diff = PreservedSnapshot.Compare(preservedBefore, preservedAfter);
            if (diff is not null)
            {
                Rollback(journal);
                return (null, $"更新过程动到了用户数据，已回滚：{diff}");
            }

            journal.FinishedAt = DateTime.Now.ToString("o");
            WriteJournal(backupDir, journal);
            PruneOldBackups(backupRoot, keep: 3);
            return (journal, null);
        }
        catch (Exception ex)
        {
            try { Rollback(journal); } catch { /* 回滚自身失败也要把原始错误报出去 */ }
            return (null, "替换失败：" + ex.Message);
        }
    }

    /// <summary>回滚：删掉新建的，还原被覆盖与被删除的。</summary>
    public static void Rollback(ReplaceJournal journal)
    {
        if (string.IsNullOrWhiteSpace(journal.BackupDir)) return;

        var filesRoot = Path.Combine(journal.BackupDir, "files");

        // 先删新建的（逆序）
        foreach (var rel in Enumerable.Reverse(journal.Created))
        {
            try { File.Delete(Path.Combine(journal.InstallDir, rel)); } catch { }
        }

        // 再还原被改/被删的（从备份）
        foreach (var rel in journal.Updated.Concat(journal.Deleted))
        {
            var backup = Path.Combine(filesRoot, rel);
            if (!File.Exists(backup)) continue;

            var dest = Path.Combine(journal.InstallDir, rel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(backup, dest, overwrite: true);
            }
            catch { }
        }
    }

    /// <summary>从备份目录回滚（供 <c>--rollback</c> 命令用）。</summary>
    public static bool RollbackFrom(string backupDir, out string? error)
    {
        error = null;
        var journalPath = Path.Combine(backupDir, "journal.json");
        if (!File.Exists(journalPath))
        {
            error = "备份目录里没有 journal.json，无法自动回滚";
            return false;
        }

        var journal = UpdateJson.Read<ReplaceJournal>(journalPath);
        if (journal is null)
        {
            error = "journal.json 读不出来";
            return false;
        }

        Rollback(journal);
        return true;
    }

    // ─────────────────────────── 内部 ───────────────────────────

    /// <summary>
    /// 文件删掉后，把它上面空出来的目录也删了（自底向上）。
    /// 保留子树（settings / plugins）与安装根绝不碰。
    /// 删不掉只记日志 —— 清理是锦上添花，不是更新的必要条件。
    /// </summary>
    static void RemoveEmptyParents(string installDir, string rel,
                                   IReadOnlyCollection<string>? preserveDirs,
                                   IReadOnlyCollection<string>? preservePatterns,
                                   Action<string>? log = null)
    {
        var dir = Path.GetDirectoryName(Path.Combine(installDir, rel));

        while (!string.IsNullOrEmpty(dir))
        {
            var relDir = SafeRelativePath.Normalize(Path.GetRelativePath(installDir, dir));
            if (relDir.Length == 0) break;                       // 到安装根了

            // 目录本身在保留范围里就不碰（判定借第一段规则：拼一个假文件名）
            if (PreserveRules.IsPreserved(relDir + "/_", preserveDirs, preservePatterns)) break;

            if (Directory.Exists(dir))
            {
                var entries = CountEntriesWithRetry(dir);
                if (entries != 0) break;                          // 不空 / 列不出来 → 停

                try
                {
                    Directory.Delete(dir);
                    log?.Invoke($"清理空目录：{relDir}");
                }
                catch (Exception ex)
                {
                    // 删不掉不阻断更新（OneDrive / 杀软可能占着），记下来给人看
                    log?.Invoke($"空目录 {relDir} 没删掉（{ex.Message}），跳过");
                    break;
                }
            }

            dir = Path.GetDirectoryName(dir);
        }
    }

    /// <summary>
    /// 数目录里的条目。文件删除可能还没落盘（或被杀软占着），短暂"不空"是正常的，
    /// 重试几次再下结论；列目录本身失败返回 -1（调用方按"不空"处理，绝不瞎删）。
    /// </summary>
    static int CountEntriesWithRetry(string dir)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var count = Directory.EnumerateFileSystemEntries(dir).Count();
                if (count == 0 || attempt == 2) return count;
            }
            catch (Exception ex)
            {
                UpdaterLog.Warn($"列目录 {dir} 失败：{ex.Message}");
                return -1;
            }

            Thread.Sleep(150);
        }

        return -1;
    }

    /// <summary>热文件最后换：*.dll → *.deps.json / *.runtimeconfig.json → exe。</summary>
    static IEnumerable<string> Order(IEnumerable<string> items)    {
        static int Rank(string rel)
        {
            var name = Path.GetFileName(rel).ToLowerInvariant();
            if (name.EndsWith(".exe")) return 3;
            if (name.EndsWith(".deps.json") || name.EndsWith(".runtimeconfig.json")) return 2;
            return 1;
        }

        return items.OrderBy(Rank).ThenBy(x => x, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>复制一份文件，失败重试 5 次（杀软 / 索引 / OneDrive 常抓着文件几百毫秒）。</summary>
    static void CopyWithRetry(string src, string dest)
    {
        IOException? last = null;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, overwrite: true);
                return;
            }
            catch (IOException ex) when (attempt < 4)
            {
                last = ex;
                Thread.Sleep(200 * (attempt + 1));
            }
            catch (UnauthorizedAccessException ex) when (attempt < 4)
            {
                last = new IOException(ex.Message, ex);
                Thread.Sleep(200 * (attempt + 1));
            }
        }

        throw last ?? new IOException("复制失败：" + src);
    }

    static void WriteJournal(string backupDir, ReplaceJournal journal)
    {
        try { UpdateJson.Write(Path.Combine(backupDir, "journal.json"), journal); }
        catch { /* journal 写不出来不影响本次更新，回滚时会提示 */ }
    }

    /// <summary>只保留最近 N 份备份（2MB 载荷，留 3 份足够回退）。</summary>
    static void PruneOldBackups(string backupRoot, int keep)
    {
        try
        {
            var dirs = Directory.GetDirectories(backupRoot)
                .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var dir in dirs.Skip(keep))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
        catch { }
    }
}
