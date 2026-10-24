using System.Text;
using ClassDailyLand.UpdateContract;

namespace ClassDailyLand.Updater;

/// <summary>
/// 更新流程编排。每一步都写日志、都可取消、失败都回滚。
///
/// 顺序是**有意**的：
///   等旧进程退出 → 确认目标目录没有活动进程 → 下载 → 校验 → 解压 → 备份 → 替换 →
///   清扫 → 用户数据快照比对 →（失败则回滚）→ 写结果 → 拉起 app → 清理
///
/// ★ await 一律**不配** ConfigureAwait(false)：后续每个 _window.SetXxx 都是 UI 操作，
///   回到后台线程会直接抛"线程无法访问该对象"。窗口侧另有 Ui() 封送兜底。
/// </summary>
public sealed class UpdateEngine
{
    readonly UpdaterWindow _window;
    readonly CancellationTokenSource _cts = new();
    UpdateJob? _job;
    string? _appLog;

    public UpdateEngine(UpdaterWindow window) => _window = window;

    /// <summary>用户点取消（安全地停下，不影响已完成的步骤）。</summary>
    public void Cancel()
    {
        try { _cts.Cancel(); } catch { }
        UpdaterLog.Info("用户请求取消");
    }

    // ─────────────────────────── 主流程 ───────────────────────────

    public async Task<int> RunAsync(string jobPath)
    {
        var job = UpdateJson.Read<UpdateJob>(jobPath);
        if (job is null)
        {
            Fail(UpdateExitCode.BadJob, "任务文件读不出来（job.json 损坏或格式不对）");
            return UpdateExitCode.BadJob;
        }

        _job = job;
        job.Normalize();
        UpdaterLog.SetPath(job.Paths.Log);
        _appLog = job.Paths.AppLog;

        // job 里声明的外观偏好（主题 / 置顶 / 成功后几秒收起）
        ThemeBoot.Apply(job.Ui.Theme);
        _window.Topmost = job.Ui.Topmost;
        _window.AutoCloseSeconds = job.Ui.AutoCloseSeconds;

        UpdaterLog.Info($"开始更新：{job.App.CurrentVersion} → {job.App.TargetVersion}（{job.Channel}）");
        UpdaterLog.Info($"安装目录：{job.App.InstallDir}");
        UpdaterLog.Info($"工作目录：{job.Paths.WorkDir}");

        if (!job.IsSchemaSupported)
        {
            Fail(UpdateExitCode.BadJob, $"任务文件版本 {job.Schema} 太新，这个更新器只认 {UpdateJob.CurrentSchema}");
            return UpdateExitCode.BadJob;
        }

        if (!ValidateDownload(job.Download, out var why))
        {
            Fail(UpdateExitCode.BadJob, why);
            return UpdateExitCode.BadJob;
        }

        _window.SetVersion(job.App.CurrentVersion, job.App.TargetVersion);
        _window.SetCancellable(true);

        var exePath = Path.Combine(job.App.InstallDir, job.App.ExeName);

        // ── 1) 等旧进程退出 ──
        _window.SetStage("正在等待程序退出…", "保存设置与插件状态后会自动继续。");
        if (!ProcessGate.WaitForExit(job.App.Pid, 30_000, UpdaterLog.Info))
        {
            Fail(UpdateExitCode.AppStillRunning, "旧程序没有退出，请手动关闭后重试。");
            return UpdateExitCode.AppStillRunning;
        }

        // ── 2) 确认这个安装目录里没有活动进程 ──
        //    ★ 按完整路径匹配，不按进程名：装在别的目录的同名实例与本次替换无关。
        //    ★ 也不能用单实例 Mutex 判断 —— 那是整机全局的，机器上任何一份实例开着
        //      都会把这里误判成"仍在运行"（沙箱演练时真实踩到过这个坑）。
        var lingering = ProcessGate.FindByPath(exePath, UpdaterLog.Info);
        if (lingering.Count > 0)
        {
            UpdaterLog.Warn($"程序目录里还有 {lingering.Count} 个活动进程，等它们退出");
            _window.SetStage("正在等待程序退出…", "检测到程序还在运行，正在等它保存完数据。");
            if (!ProcessGate.WaitForAll(lingering, TimeSpan.FromSeconds(10), UpdaterLog.Info))
                return Fail(UpdateExitCode.AppStillRunning, "程序仍在运行，无法安全替换。请先完全关闭程序再更新。");
        }
        UpdaterLog.Info("程序目录没有活动进程，可以替换");

        // ── 3) 磁盘余量：解压 + 备份都要占地方 ──
        if (!CheckDiskSpace(job))
            return Fail(UpdateExitCode.DiskFull,
                $"磁盘剩余空间不足，无法完成更新（至少需要 {job.Limits.MinFreeBytesAfter / 1048576} MB）。");

        try
        {
            // ── 4) 下载 ──
            var zipPath = Path.Combine(job.Paths.WorkDir, "package.zip");
            var downloadError = await DownloadAsync(job.Download, zipPath);
            if (downloadError is not null) return Fail(UpdateExitCode.DownloadFailed, downloadError);
            var downloaded = zipPath;

            // ── 5) 校验 ──
            _window.SetStage("正在校验安装包…", null);
            _window.SetProgress(null);
            var actual = Hashing.ComputeFileSha256(downloaded);
            if (!Hashing.Sha256Equals(job.Download.Sha256, actual))
            {
                try { File.Delete(downloaded); } catch { }
                return Fail(UpdateExitCode.HashMismatch,
                    $"安装包校验失败（期望 {job.Download.Sha256[..12]}…，实际 {actual[..12]}…）。已删除该文件，请重新检查更新。");
            }
            UpdaterLog.Info("SHA256 校验通过：" + actual[..16] + "…");

            // ── 6) 解压 ──
            _window.SetStage("正在解压…", null);
            var extractDir = Path.Combine(job.Paths.WorkDir, "payload");
            var (files, extractError) = SafeZip.Extract(downloaded, extractDir,
                job.Limits.MaxEntries, job.Limits.MaxTotalBytes);
            if (extractError is not null)
                return Fail(UpdateExitCode.ExtractFailed, extractError);

            var payloadRoot = SafeZip.ResolvePayloadRoot(extractDir) ?? extractDir;
            UpdaterLog.Info($"解压完成：{files.Count} 个文件 → {payloadRoot}");

            // ── 7) 替换计划 + 用户数据快照 ──
            var existing = EnumerateRelative(job.App.InstallDir);
            var payload = EnumerateRelative(payloadRoot);
            var plan = ReplacePlanner.Build(existing, payload, job.Preserve, job.PreservePatterns);
            UpdaterLog.Info($"替换计划：新建 {plan.ToCreate.Count} / 覆盖 {plan.ToUpdate.Count} / 删除 {plan.ToDelete.Count} / 保留 {plan.Preserved.Count}");

            if (plan.Preserved.Count > 0)
                UpdaterLog.Info("受保护不碰：" + string.Join(", ", plan.Preserved.Take(12)));

            var preservedBefore = PreservedSnapshot.Capture(job.App.InstallDir, job.Preserve, job.PreservePatterns);

            // ── 8) 备份 + 替换（内部失败会自行回滚） ──
            var (journal, replaceError) = FileTransaction.Execute(
                job.App.InstallDir, payloadRoot, plan,
                job.Paths.BackupRoot, job.JobId,
                preservedBefore,
                job.Preserve, job.PreservePatterns,
                stage => _window.SetStage(stage, null));

            if (journal is null)
                return Fail(UpdateExitCode.ReplaceFailed, replaceError ?? "替换失败");

            UpdaterLog.Info($"替换完成，备份在 {journal.BackupDir}");

            // ── 9) 验证主程序在 ──
            if (!File.Exists(exePath))
            {
                FileTransaction.Rollback(journal);
                return Fail(UpdateExitCode.ReplaceFailed, "替换后找不到主程序，已回滚");
            }

            // ── 10) 资源包（可选，失败不影响本次程序更新） ──
            if (job.Resources is { } resources && _cts.Token.IsCancellationRequested == false)
            {
                var resourceError = await ApplyResourcesAsync(job, resources);
                if (resourceError is not null)
                    UpdaterLog.Warn("资源包更新失败（不影响本次程序更新）：" + resourceError);
            }

            // ── 11) 写结果 + 拉起 ──
            WriteResult(job, ok: true, message: $"已更新到 {job.App.TargetVersion}", journal.BackupDir);
            AppendAppLog($"更新完成：{job.App.CurrentVersion} → {job.App.TargetVersion}");

            _window.SetStage("更新完成", "正在启动程序…");
            _window.SetProgress(100);

            if (job.Relaunch)
            {
                await Task.Delay(job.RelaunchDelayMs);
                ProcessGate.Launch(exePath, job.App.InstallDir);
                UpdaterLog.Info("已启动：" + exePath);
            }

            // 清理临时目录（含下载的 zip 与解压内容）
            CleanupWorkDir(job.Paths.WorkDir);

            _window.SetStage("更新完成", $"{job.App.CurrentVersion} → {job.App.TargetVersion}");
            _window.SetHint("可以关掉这个窗口了。");
            return UpdateExitCode.Success;
        }
        catch (OperationCanceledException)
        {
            return Fail(UpdateExitCode.Cancelled, "已取消更新（已安装的内容没有改动）");
        }
        catch (Exception ex)
        {
            UpdaterLog.Error("更新异常：" + ex);
            return Fail(UpdateExitCode.ReplaceFailed, ex.Message);
        }
    }

    // ─────────────────────────── 回滚 ───────────────────────────

    public async Task<int> RollbackAsync(string backupDir)
    {
        await Task.Yield();
        _window.SetCancellable(false);
        _window.SetStage("正在恢复上一版本…", backupDir);

        if (!FileTransaction.RollbackFrom(backupDir, out var error))
        {
            Fail(UpdateExitCode.ReplaceFailed, error ?? "回滚失败");
            return UpdateExitCode.ReplaceFailed;
        }

        UpdaterLog.Info("已从备份恢复：" + backupDir);
        _window.SetStage("已恢复", "上一版本已经还原，正在启动程序…");
        return UpdateExitCode.Success;
    }

    // ─────────────────────────── 内部 ───────────────────────────

    async Task<string?> DownloadAsync(JobDownload download, string destPath)
    {
        var progress = new Progress<DownloadProgress>(p =>
        {
            _window.SetStage("正在下载…", null);
            _window.SetProgress(p.Total > 0 ? p.Received * 100.0 / p.Total : (double?)null);
            _window.SetBytes(p.Received, p.Total);
            _window.SetSpeed(p.BytesPerSecond);
        });

        UpdaterLog.Info($"开始下载：{download.Url}");
        var (path, error) = await Downloader.FetchAsync(
            download.Url, download.UrlFallback, destPath,
            download.Size, download.TimeoutSeconds, download.MaxAttempts,
            progress, _cts.Token);

        return path is null ? (error ?? "下载失败") : null;
    }

    async Task<string?> ApplyResourcesAsync(UpdateJob job, JobResources resources)
    {
        if (resources.Download is null) return "资源包缺少下载信息";

        _window.SetStage("正在下载资源包…", resources.Version);
        var zipPath = Path.Combine(job.Paths.WorkDir, "resources.zip");
        var (downloaded, error) = await Downloader.FetchAsync(
            resources.Download.Url, resources.Download.UrlFallback, zipPath,
            resources.Download.Size, resources.Download.TimeoutSeconds, resources.Download.MaxAttempts,
            null, _cts.Token);

        if (downloaded is null) return error;

        if (!Hashing.Sha256Equals(resources.Download.Sha256, Hashing.ComputeFileSha256(downloaded)))
            return "资源包校验失败";

        var extractDir = Path.Combine(job.Paths.WorkDir, "assets-payload");
        var (files, extractError) = SafeZip.Extract(downloaded, extractDir, 2000, 64L * 1024 * 1024);
        if (extractError is not null) return extractError;

        var payloadRoot = SafeZip.ResolvePayloadRoot(extractDir) ?? extractDir;
        var target = resources.InstallDir;
        if (string.IsNullOrWhiteSpace(target))
            target = Path.Combine(job.App.InstallDir, "assets");

        // 整目录替换：备份旧 → 删 → 移入新
        var backup = Path.Combine(job.Paths.BackupRoot, "assets-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        try
        {
            if (Directory.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                Directory.Move(target, backup);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(payloadRoot, target);
        }
        catch (Exception ex)
        {
            // 还原旧的
            try
            {
                if (Directory.Exists(backup) && !Directory.Exists(target)) Directory.Move(backup, target);
            }
            catch { }
            return "资源包替换失败：" + ex.Message;
        }

        // 通知正在运行的主程序热刷新
        if (!string.IsNullOrWhiteSpace(resources.NotifyEvent))
        {
            try
            {
                using var ev = new System.Threading.EventWaitHandle(
                    false, System.Threading.EventResetMode.AutoReset, resources.NotifyEvent);
                ev.Set();
            }
            catch { }
        }

        UpdaterLog.Info("资源包已更新到 " + resources.Version);
        return null;
    }

    static bool ValidateDownload(JobDownload download, out string error)
    {
        error = "";
        if (!download.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            error = "下载地址不是 http(s)";
            return false;
        }
        if (!Hashing.LooksLikeSha256(download.Sha256))
        {
            error = "任务里的 SHA256 格式不对（应为 64 位十六进制）";
            return false;
        }
        return true;
    }

    /// <summary>安装盘剩余空间够不够（解压 + 备份都要占地方）。取不到盘信息时不阻断。</summary>
    static bool CheckDiskSpace(UpdateJob job)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(job.App.InstallDir));
            if (string.IsNullOrEmpty(root)) return true;
            return new DriveInfo(root).AvailableFreeSpace >= job.Limits.MinFreeBytesAfter;
        }
        catch
        {
            return true;
        }
    }

    static List<string> EnumerateRelative(string root)
    {
        var list = new List<string>();
        if (!Directory.Exists(root)) return list;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            list.Add(SafeRelativePath.Normalize(rel));
        }
        return list;
    }

    void WriteResult(UpdateJob job, bool ok, string message, string? backupDir)
    {
        try
        {
            var result = new LastUpdateResult
            {
                JobId = job.JobId,
                From = job.App.CurrentVersion,
                To = job.App.TargetVersion,
                Ok = ok,
                Stage = ok ? "done" : "failed",
                Message = message,
                FinishedAt = DateTime.Now.ToString("o"),
                BackupDir = backupDir ?? "",
                Consumed = false,
            };
            UpdateJson.Write(job.Paths.Result, result);
        }
        catch (Exception ex)
        {
            UpdaterLog.Warn("写结果文件失败：" + ex.Message);
        }
    }

    /// <summary>往主程序的 app.log 追一行（用户能在老地方看到更新发生了什么事）。</summary>
    void AppendAppLog(string line)
    {
        var path = _appLog;
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [UPDATE] {line}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { /* 日志写不进去不影响更新结果 */ }
    }

    int Fail(int code, string message)
    {
        UpdaterLog.Error(message);
        _window.SetStage("更新失败", message);
        _window.SetHint($"代码 {code}：{UpdateExitCode.Describe(code)}");
        _window.SetProgress(null);

        // 失败也落一份结果：主程序下次启动会读它，给用户一个"上次更新出错了"的交待
        if (_job is not null)
        {
            WriteResult(_job, ok: false, message: message, backupDir: null);
            AppendAppLog("更新失败：" + message);
        }

        return code;
    }

    static void CleanupWorkDir(string workDir)
    {
        try
        {
            // 稍等一下，等文件句柄释放（尤其 zip 与 payload）
            Thread.Sleep(200);
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
        catch { /* 清不掉也无所谓，临时目录系统会自己收 */ }
    }
}
