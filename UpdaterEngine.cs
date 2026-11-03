using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using ClassDailyLand2.UpdateContract;

namespace CDL2.Updater;

/// <summary>更新阶段（界面的勾选列表按这个顺序展示）。</summary>
public enum UpdateStage
{
    Prepare,
    Download,
    Verify,
    StopApp,
    Backup,
    Replace,
    Finish,
}

/// <summary>引擎对界面的回调。</summary>
public interface IUpdaterUi
{
    /// <summary>阶段变化 / 文本更新。progress 为 0~1。</summary>
    void OnStage(UpdateStage stage, string message, double progress);
    void OnDownloadProgress(long received, long? total);
    void OnSuccess(string message);
    void OnFailure(int exitCode, string message);
    void OnCancelled();
}

/// <summary>
/// 更新主干。与旧版的关键差异是**替换不再原地铺**：
///
///   下载 → 校验 → 结束主程序 → 解压到 staging → 算替换计划 → 备份（带 journal）
///   → 替换 → 校验保留区与关键文件 → 收尾。
///
/// 解压先落 staging，包坏在动程序目录之前就能整包拒绝；备份带 journal 文件，
/// 就算更新器进程被硬杀，<c>--rollback &lt;备份目录&gt;</c> 也能照着 journal 恢复。
/// </summary>
public sealed class UpdaterEngine
{
    private const string ExeNameFallback = "Class Daily Land 2.exe";

    private readonly UpdateJob _job;
    private readonly UpdaterLog _log;
    private readonly IUpdaterUi _ui;

    /// <summary>引擎回调可能在后台线程（解压/替换在 Task.Run 里跑），UI 调用一律封送回创建线程。</summary>
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    public UpdaterEngine(UpdateJob job, UpdaterLog log, IUpdaterUi ui)
    {
        _job = job;
        _log = log;
        _ui = ui;
    }

    private void Stage(UpdateStage stage, string message, double progress) =>
        Ui(u => u.OnStage(stage, message, progress));

    /// <summary>把 UI 回调封送回创建线程；没有同步上下文（无头测试）就原地调用。</summary>
    private void Ui(Action<IUpdaterUi> call)
    {
        var context = _uiContext;
        if (context is null) { call(_ui); return; }
        context.Post(_ => call(_ui), null);
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        try
        {
            return await RunCoreAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.Write("用户取消");
            Ui(u => u.OnCancelled());
            return UpdateExitCode.Cancelled;
        }
        catch (UnauthorizedAccessException ex)
        {
            return await FailAsync(UpdateExitCode.AccessDenied, "没有写入权限：" + ex.Message).ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            var code = IsDiskFull(ex) ? UpdateExitCode.DiskFull : UpdateExitCode.ReplaceFailed;
            return await FailAsync(code, "文件操作失败：" + ex.Message).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            return await FailAsync(UpdateExitCode.ReplaceFailed, "更新器异常：" + ex).ConfigureAwait(true);
        }
    }

    private async Task<int> RunCoreAsync(CancellationToken ct)
    {
        var job = _job;
        Directory.CreateDirectory(job.Paths.WorkDir);

        // ── Prepare ──
        Stage(UpdateStage.Prepare, "正在准备…", 0.02);

        if (!IsSafeInstallTarget(job.App.InstallDir, job.Paths.WorkDir, out var targetError))
            return await FailAsync(UpdateExitCode.BadJob, targetError).ConfigureAwait(true);

        if (!CheckFreeSpace(job.App.InstallDir, out var spaceError))
            return await FailAsync(UpdateExitCode.DiskFull, spaceError).ConfigureAwait(true);

        _log.Write($"任务 {job.JobId}：{job.App.CurrentVersion} → {job.App.TargetVersion}（{job.Channel}）");
        _log.Write($"程序目录：{job.App.InstallDir}");

        // ── Download（此时主程序还活着，不碰它的目录）──
        var zipPath = Path.Combine(job.Paths.WorkDir, "payload.zip");
        Stage(UpdateStage.Download, "正在下载更新…", 0.05);

        var progress = new Progress<DownloadProgress>(p =>
            _ui.OnDownloadProgress(p.Received, p.Total));

        var (downloaded, downloadCode, downloadError) =
            await Downloader.DownloadAsync(job.Download, zipPath, _log, progress, ct).ConfigureAwait(true);

        if (downloaded is null)
            return await FailAsync(downloadCode, downloadError ?? "下载失败", BeforeBackup: true).ConfigureAwait(true);

        // ── Verify ──
        Stage(UpdateStage.Verify, "正在校验安装包…", 0.35);

        if (!string.IsNullOrWhiteSpace(job.Download.Sha256))
        {
            var actual = await Task.Run(() => Hashing.ComputeFileSha256(downloaded), ct).ConfigureAwait(true);
            if (!Hashing.Sha256Equals(job.Download.Sha256, actual))
            {
                await TryDeleteDownloadedZip(downloaded).ConfigureAwait(true);
                return await FailAsync(UpdateExitCode.HashMismatch,
                    $"安装包校验失败（期望 {Hashing.Normalize(job.Download.Sha256)}，实际 {actual}）",
                    BeforeBackup: true).ConfigureAwait(true);
            }
            _log.Write("SHA-256 校验通过：" + actual);
        }
        else
        {
            _log.Write("任务没有提供 sha256，跳过校验");
        }

        // ── StopApp（只有替换需要独占程序目录，关程序留到最后一刻）──
        Stage(UpdateStage.StopApp, "正在关闭主程序…", 0.45);
        var appClosed = await Task.Run(() => KillRunningApp(), ct).ConfigureAwait(true);
        if (!appClosed)
        {
            return await FailAsync(UpdateExitCode.AppStillRunning,
                "没能关闭主程序（可能权限不足）。请手动退出 Class Daily Land 2 后重试。").ConfigureAwait(true);
        }
        await Task.Delay(1200, ct).ConfigureAwait(true);   // 等句柄释放

        // ── Backup + Replace（从这里开始任何失败都要回滚）──
        Stage(UpdateStage.Backup, "正在解压与备份…", 0.55);

        var staging = Path.Combine(job.Paths.WorkDir, "payload");
        List<string>? payload = null;
        string? extractError = null;

        await Task.Run(() =>
            payload = ExtractPayload(downloaded, staging, out extractError, ct), ct).ConfigureAwait(true);

        if (payload is null)
        {
            await TryDeleteDownloadedZip(downloaded).ConfigureAwait(true);
            return await FailAsync(UpdateExitCode.ExtractFailed, extractError ?? "解压失败").ConfigureAwait(true);
        }
        _log.Write($"解压完成：{payload.Count} 个文件 → {staging}");

        var existing = EnumerateRelative(job.App.InstallDir);
        var plan = ReplacePlanner.Build(existing, payload, job.Preserve, job.PreservePatterns);
        _log.Write($"替换计划：新增 {plan.ToCreate.Count}，覆盖 {plan.ToUpdate.Count}，" +
                   $"删除 {plan.ToDelete.Count}，保留 {plan.Preserved.Count}");

        var backupDir = Path.Combine(job.Paths.BackupRoot, job.JobId);
        var journal = new ReplaceJournal(backupDir, target: job.App.InstallDir);
        journal.WriteHeader(job);

        Stage(UpdateStage.Backup, "正在备份旧文件…", 0.65);
        await Task.Run(() => BackupOldFiles(plan, journal), ct).ConfigureAwait(true);

        // 「正在替换」先落盘：断电 / 硬杀后，新程序启动能看出上次没做完
        WriteResult(ok: false, stage: "replacing", message: "正在替换文件…", backupDir);

        Stage(UpdateStage.Replace, "正在替换文件…", 0.75);

        var before = PreservedSnapshot.Capture(job.App.InstallDir, job.Preserve, job.PreservePatterns);
        var failure = await Task.Run(() =>
            ApplyReplace(plan, staging, journal), ct).ConfigureAwait(true);

        if (failure is null)
        {
            // 第三重保护：证明用户数据一个字节都没动
            var after = PreservedSnapshot.Capture(job.App.InstallDir, job.Preserve, job.PreservePatterns);
            var difference = PreservedSnapshot.Compare(before, after);
            if (difference is not null)
                failure = "替换过程动到了用户数据（settings / plugins）：" + difference;
        }

        if (failure is null && !VerifyCriticalFiles())
            failure = "替换后缺少关键文件：" + job.App.ExeName;

        if (failure is not null)
        {
            Stage(UpdateStage.Replace, "正在回滚…", 0.9);
            await Task.Run(() => journal.RestoreAll(_log), ct).ConfigureAwait(true);
            await TryDeleteDownloadedZip(downloaded).ConfigureAwait(true);

            WriteResult(ok: false, stage: "rolledback",
                message: "替换失败，已回滚到上一版本：" + failure, backupDir);
            AppendAppLog($"[update] 失败（已回滚）：{failure}");

            _log.Write("已回滚到上一版本（备份保留在 " + backupDir + "）");
            Ui(u => u.OnFailure(UpdateExitCode.ReplaceFailed, "更新失败，已回滚到上一版本：" + failure));
            return UpdateExitCode.ReplaceFailed;
        }

        journal.Complete();
        await TryDeleteDownloadedZip(downloaded).ConfigureAwait(true);

        // ── Finish ──
        Stage(UpdateStage.Finish, "正在收尾…", 0.97);

        WriteResult(ok: true, stage: "done", message: "更新完成", backupDir);
        AppendAppLog($"[update] 已更新到 {job.App.TargetVersion}（{job.Channel}）");

        if (job.Resources is { } resources && !string.IsNullOrWhiteSpace(resources.Download.Url))
            await UpdateResourcesAsync(resources, ct).ConfigureAwait(true);

        Stage(UpdateStage.Finish, "更新完成", 1.0);
        _log.Write("更新成功");

        Ui(u => u.OnSuccess(job.Relaunch ? "更新完成" : "更新完成（任务要求不自动启动程序）"));
        return UpdateExitCode.Success;
    }

    // ————————————————————— 各阶段实现 —————————————————————

    /// <summary>解压到 staging：Zip Slip / 条目数 / 解压总量 / 保留规则，四道闸门全部来自契约。</summary>
    private List<string>? ExtractPayload(string zipPath, string stagingDir, out string? error, CancellationToken ct)
    {
        error = null;
        var payload = new List<string>();

        try
        {
            Directory.CreateDirectory(stagingDir);

            using var archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count > _job.Limits.MaxEntries)
            {
                error = $"安装包条目过多：{archive.Entries.Count} 个（上限 {_job.Limits.MaxEntries}）";
                return null;
            }

            long total = 0;
            var index = 0;
            // ★ 封送回调是延迟执行的：任何要进界面的值（包括 archive.Entries.Count）
            //   都必须在后台线程先算好，否则 ZipArchive 已释放，UI 线程上炸 ODE（踩过）
            var entryCount = archive.Entries.Count;

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();

                index++;
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                if (entry.Name.Length == 0) continue;

                if (!SafeRelativePath.TryNormalize(entry.FullName, out var relative, out var why))
                {
                    error = $"安装包里含有非法路径「{entry.FullName}」：{why}";
                    return null;
                }

                if (PreserveRules.IsPreserved(relative, _job.Preserve, _job.PreservePatterns))
                {
                    _log.Write($"跳过保留项（不进程序目录）：{relative}");
                    continue;
                }

                total += entry.Length;
                if (total > _job.Limits.MaxTotalBytes)
                {
                    error = $"安装包解压后超过上限（{_job.Limits.MaxTotalBytes} 字节），疑似异常包";
                    return null;
                }

                var dest = Path.Combine(stagingDir, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!SafeRelativePath.IsInside(stagingDir, dest))
                {
                    error = $"安装包条目越界：{entry.FullName}";
                    return null;
                }

                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                entry.ExtractToFile(dest, overwrite: true);
                payload.Add(relative);

                Ui(u => u.OnStage(UpdateStage.Backup, $"正在解压与备份…（{index}/{entryCount}）", 0.55));
            }
        }
        catch (InvalidDataException ex)
        {
            error = "安装包已损坏：" + ex.Message;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "解压失败：" + ex.Message;
            return null;
        }

        if (payload.Count == 0)
        {
            error = "安装包里没有任何可落地的文件";
            return null;
        }

        payload.Sort(StringComparer.OrdinalIgnoreCase);
        return payload;
    }

    /// <summary>备份会被覆盖 / 删除的旧文件，journal 里逐条记账（回滚与 --rollback 都靠它）。</summary>
    private void BackupOldFiles(ReplacePlan plan, ReplaceJournal journal)
    {
        var filesRoot = journal.FilesRoot;
        var count = 0;

        foreach (var relative in plan.ToUpdate.Concat(plan.ToDelete))
        {
            var src = Path.Combine(_job.App.InstallDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue;

            var dst = Path.Combine(filesRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

            File.Copy(src, dst, overwrite: true);
            journal.Record("overwritten", relative);
            count++;
        }

        journal.Flush();
        _log.Write($"已备份 {count} 个旧文件到 {journal.BackupDir}");
    }

    /// <summary>按计划落地。返回 null = 成功；否则返回人话原因（调用方负责回滚）。</summary>
    private string? ApplyReplace(ReplacePlan plan, string stagingDir, ReplaceJournal journal)
    {
        var target = _job.App.InstallDir;

        foreach (var relative in plan.ToCreate.Concat(plan.ToUpdate))
        {
            var src = Path.Combine(stagingDir, relative.Replace('/', Path.DirectorySeparatorChar));
            var dst = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));

            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

            var existed = File.Exists(dst);
            File.Copy(src, dst, overwrite: true);
            journal.Record(existed ? "overwritten" : "created", relative);
        }

        _log.Write($"已写入 {plan.ToCreate.Count + plan.ToUpdate.Count} 个文件");

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
                journal.Record("deleted", relative);
            }
            catch (Exception ex)
            {
                // 删不掉旧文件不致命（杀软占着之类），journal 不记，回滚时也不会漏
                _log.Write($"删除失败（忽略）：{relative} → {ex.Message}");
            }
        }

        journal.Flush();
        if (plan.ToDelete.Count > 0) _log.Write($"已清理 {plan.ToDelete.Count} 个旧文件");
        return null;
    }

    /// <summary>关键文件（主程序 exe）必须在，否则整轮视为失败并回滚。</summary>
    private bool VerifyCriticalFiles()
    {
        var exe = Path.Combine(_job.App.InstallDir, _job.App.ExeName ?? ExeNameFallback);
        var ok = File.Exists(exe);
        if (!ok) _log.Write("关键文件校验失败：" + exe);
        return ok;
    }

    /// <summary>资源包更新：失败只记日志，不能反过来把"主程序已更新成功"算成失败。</summary>
    private async Task UpdateResourcesAsync(JobResources resources, CancellationToken ct)
    {
        try
        {
            _log.Write($"开始更新资源包 {resources.Version}");
            var zipPath = Path.Combine(_job.Paths.WorkDir, "resources.zip");

            var (downloaded, _, error) =
                await Downloader.DownloadAsync(resources.Download, zipPath, _log, null, ct).ConfigureAwait(false);

            if (downloaded is null)
            {
                _log.Write("资源包下载失败（主程序已更新，本次跳过资源）：" + error);
                return;
            }

            if (!string.IsNullOrWhiteSpace(resources.Download.Sha256))
            {
                var actual = await Task.Run(() => Hashing.ComputeFileSha256(downloaded), ct).ConfigureAwait(false);
                if (!Hashing.Sha256Equals(resources.Download.Sha256, actual))
                {
                    _log.Write("资源包校验失败，本次跳过资源更新");
                    return;
                }
            }

            var staging = Path.Combine(_job.Paths.WorkDir, "resources");
            ResetDirectory(staging);

            List<string>? payload = null;
            string? extractError = null;
            payload = ExtractPayload(downloaded, staging, out extractError, ct);

            if (payload is null)
            {
                _log.Write("资源包解压失败，本次跳过资源更新：" + extractError);
                return;
            }

            // 资源目录同样走"备份 + journal + 回滚"
            var existing = EnumerateRelative(resources.InstallDir);
            var plan = ReplacePlanner.Build(existing, payload, _job.Preserve, _job.PreservePatterns);

            var backupDir = Path.Combine(_job.Paths.BackupRoot, _job.JobId + "-resources");
            var journal = new ReplaceJournal(backupDir, resources.InstallDir);
            journal.WriteHeader(_job);

            await Task.Run(() => BackupOldFilesFor(plan, resources.InstallDir, journal), ct).ConfigureAwait(false);
            var failure = await Task.Run(() => ApplyReplaceFor(plan, staging, resources.InstallDir, journal), ct)
                .ConfigureAwait(false);

            if (failure is not null)
            {
                await Task.Run(() => journal.RestoreAll(_log), ct).ConfigureAwait(false);
                _log.Write("资源包替换失败（已回滚）：" + failure);
                return;
            }

            journal.Complete();
            NotifyResources(resources.NotifyEvent);
            _log.Write($"资源包已更新到 {resources.Version}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Write("更新资源包时出错（忽略）：" + ex);
        }
    }

    private void BackupOldFilesFor(ReplacePlan plan, string targetDir, ReplaceJournal journal)
    {
        foreach (var relative in plan.ToUpdate.Concat(plan.ToDelete))
        {
            var src = Path.Combine(targetDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue;

            var dst = Path.Combine(journal.FilesRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

            File.Copy(src, dst, overwrite: true);
            journal.Record("overwritten", relative);
        }
        journal.Flush();
    }

    private string? ApplyReplaceFor(ReplacePlan plan, string stagingDir, string targetDir, ReplaceJournal journal)
    {
        foreach (var relative in plan.ToCreate.Concat(plan.ToUpdate))
        {
            var src = Path.Combine(stagingDir, relative.Replace('/', Path.DirectorySeparatorChar));
            var dst = Path.Combine(targetDir, relative.Replace('/', Path.DirectorySeparatorChar));

            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

            var existed = File.Exists(dst);
            File.Copy(src, dst, overwrite: true);
            journal.Record(existed ? "overwritten" : "created", relative);
        }
        journal.Flush();
        return null;
    }

    private void NotifyResources(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return;
        try
        {
            if (EventWaitHandle.TryOpenExisting(eventName, out var handle))
            {
                using (handle) handle.Set();
                _log.Write("已通知运行中的主程序热刷新资源");
            }
        }
        catch (Exception ex)
        {
            _log.Write("通知资源刷新失败（忽略）：" + ex.Message);
        }
    }

    // ————————————————————— 主程序进程 —————————————————————

    /// <summary>下载校验期间主程序活着；只有替换需要独占文件，按 PID + 名字逐个结束。</summary>
    private bool KillRunningApp()
    {
        var names = new List<string>();

        var configured = Path.GetFileNameWithoutExtension(_job.App.ExeName ?? "");
        if (!string.IsNullOrWhiteSpace(configured)) names.Add(configured);
        names.Add("Class Daily Land");
        names.Add("Class Daily Land 2");

        var uniqueNames = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var killed = 0;

        // 先按 PID 精确结束（主程序写 job 时留了 pid）
        if (_job.App.Pid > 0)
        {
            try
            {
                var proc = Process.GetProcessById(_job.App.Pid);
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                    killed++;
                    _log.Write($"已结束主程序进程（PID {proc.Id}，按任务里的 pid）");
                }
            }
            catch (Exception ex)
            {
                _log.Write($"按 PID 结束主程序失败（继续按名字找）：{ex.Message}");
            }
        }

        foreach (var name in uniqueNames)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    using (proc)
                    {
                        if (proc.Id == Environment.ProcessId) continue;   // 别杀自己
                        if (proc.HasExited) continue;

                        try
                        {
                            proc.Kill(entireProcessTree: true);
                            proc.WaitForExit(3000);
                            killed++;
                            _log.Write($"已结束主程序进程：{name}（PID {proc.Id}）");
                        }
                        catch (Exception ex)
                        {
                            _log.Write($"结束主程序 {name}（PID {proc.Id}）失败：{ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Write($"按名字查找主程序 {name} 失败：{ex.Message}");
            }
        }

        _log.Write($"关闭主程序：共结束 {killed} 个进程");

        foreach (var name in uniqueNames)
        {
            try
            {
                var remaining = Process.GetProcessesByName(name);
                var alive = remaining.Where(p => p.Id != Environment.ProcessId).ToArray();
                if (alive.Length == 0) continue;

                foreach (var proc in alive) proc.Dispose();
                _log.Write($"仍有主程序进程在运行：{name}");
                return false;
            }
            catch (Exception ex)
            {
                _log.Write($"复查主程序 {name} 失败（按已关闭处理）：{ex.Message}");
            }
        }

        return true;
    }

    /// <summary>更新完成后启动主程序（只由 InstallDir + ExeName 拼，绝不能用更新器自己的目录）。</summary>
    public bool LaunchApp(out string error)
    {
        error = "";
        var target = _job.App.InstallDir?.Trim() ?? "";
        if (target.Length == 0)
        {
            error = "更新任务里没有安装目录";
            return false;
        }

        var exe = Path.Combine(target, _job.App.ExeName ?? ExeNameFallback);
        if (!File.Exists(exe))
        {
            error = "主程序不存在：" + exe;
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = target,
                UseShellExecute = true,
            });
            _log.Write("已启动主程序：" + exe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ————————————————————— 结果与留痕 —————————————————————

    private void WriteResult(bool ok, string stage, string message, string backupDir)
    {
        try
        {
            UpdateJson.Write(_job.Paths.Result, new LastUpdateResult
            {
                JobId = _job.JobId,
                From = _job.App.CurrentVersion,
                To = _job.App.TargetVersion,
                Ok = ok,
                Stage = stage,
                Message = message,
                FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                BackupDir = backupDir,
                Consumed = false,
            });
        }
        catch { /* 结果文件写不进去也要继续 */ }
    }

    private void AppendAppLog(string line)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_job.Paths.AppLog)) return;
            var dir = Path.GetDirectoryName(_job.Paths.AppLog);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.AppendAllText(_job.Paths.AppLog,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { /* 留痕失败不影响更新 */ }
    }

    private async Task<int> FailAsync(int code, string message, bool BeforeBackup = false)
    {
        _log.Write($"失败（{UpdateExitCode.Describe(code)}，退出码 {code}）：{message}");

        WriteResult(ok: false,
            stage: BeforeBackup ? "failed" : "rolledback",
            message: $"{UpdateExitCode.Describe(code)}：{message}",
            backupDir: "");

        AppendAppLog($"[update] 失败：{UpdateExitCode.Describe(code)} —— {message}");
        Ui(u => u.OnFailure(code, $"{UpdateExitCode.Describe(code)}：{message}"));
        return code;
    }

    // ————————————————————— 工具 —————————————————————

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
        catch { /* 枚举不了按空算：替换计划退化成"全是新增"，不会误删 */ }
        return list;
    }

    private bool CheckFreeSpace(string targetDir, out string error)
    {
        error = "";
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(targetDir));
            if (string.IsNullOrEmpty(root)) return true;

            var drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace < _job.Limits.MinFreeBytesAfter)
            {
                error = $"磁盘空间不足：可用 {Bytes(drive.AvailableFreeSpace)}，" +
                        $"至少需要 {Bytes(_job.Limits.MinFreeBytesAfter)}（备份 + 解压都要占地方）";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.Write("检查磁盘空间失败（继续）：" + ex.Message);
            return true;
        }
    }

    private static bool IsSafeInstallTarget(string? installDir, string? workDir, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(installDir))
        {
            error = "更新任务里的安装目录为空。";
            return false;
        }

        string full;
        try { full = Path.GetFullPath(installDir); }
        catch (Exception ex)
        {
            error = "安装目录不合法：" + ex.Message;
            return false;
        }

        var temp = Path.GetTempPath();
        var work = string.IsNullOrWhiteSpace(workDir) ? "" : Path.GetFullPath(workDir);

        if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            (work.Length > 0 && full.StartsWith(work, StringComparison.OrdinalIgnoreCase)))
        {
            error = "解压目标不能是 %Temp% / 更新器工作目录：" + full;
            return false;
        }

        if (!Directory.Exists(full))
        {
            error = "安装目录不存在：" + full;
            return false;
        }

        return true;
    }

    private static void ResetDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* 删不掉就让后面的覆盖写兜住 */ }
        Directory.CreateDirectory(path);
    }

    private static bool IsDiskFull(IOException ex)
    {
        const int ErrorDiskFull = unchecked((int)0x80070070);
        const int ErrorHandleDiskFull = unchecked((int)0x80070027);
        return ex.HResult == ErrorDiskFull || ex.HResult == ErrorHandleDiskFull;
    }

    private static string Bytes(long value)
    {
        if (value >= 1024L * 1024 * 1024) return $"{value / 1024.0 / 1024 / 1024:0.0} GB";
        if (value >= 1024L * 1024) return $"{value / 1024.0 / 1024:0.0} MB";
        return $"{value / 1024.0:0} KB";
    }

    private async Task TryDeleteDownloadedZip(string zipPath)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(zipPath)) return;
                File.SetAttributes(zipPath, FileAttributes.Normal);
                File.Delete(zipPath);
                _log.Write("已删除下载的临时包");
                return;
            }
            catch (Exception ex)
            {
                _log.Write($"删除临时包失败（第 {attempt + 1} 次）：{ex.Message}");
                await Task.Delay(500).ConfigureAwait(true);
            }
        }
        _log.Write("临时包仍被占用，交给退出清理处理");
    }
}
