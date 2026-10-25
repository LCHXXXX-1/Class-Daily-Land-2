using System.Diagnostics;
using System.Windows;
using ClassDailyLand.Core;
using ClassDailyLand.UpdateContract;

namespace ClassDailyLand.Updater;

/// <summary>
/// 更新器入口与编排。
///
/// 它在主程序仍在后台运行时开始下载，只有替换文件前才强杀主程序；每一步都要能独立失败、
/// 并把原因落到 <c>last-update.json</c> 与日志里；失败时还会在屏幕上给红字 + 重试。
///
/// 主干：下载 → 校验 → 强杀主程序并等文件释放 → 清空目标目录（保留 settings / plugins）
/// → 解压到目标目录 → 写结果 → 等待用户点「完成」（按复选框决定是否启动主程序）。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var log = new JobLog();
        log.Write("===== 更新器启动 =====");
        log.Write("参数：" + string.Join(' ', args));

        var cli = CommandLine.Parse(args);

        // 一个参数都没有 = 用户直接双击了 Updater.exe。
        // 以前这里直接 return BadJob（双击后"什么都没发生"），现在弹一张提示卡片说清楚它是更新组件。
        if (!cli.HasJob && !cli.HasUrl)
        {
            log.Write("没有启动参数：按「请勿直接运行」提示模式启动");
            return RunWindow(null, null, directLaunch: true, log);
        }

        // 不再自我复制到 %Temp%：主程序在"检查更新"时已经把整个 Update\ 目录复制到
        // %Temp%\CDL_UpdateTemp 并从那里启动，更新器就地运行即可（少一次复制 + 一次进程启动）。

        var job = JobLoader.Load(cli, out var loadError);
        if (job is not null)
        {
            log.Attach(job.Paths.Log);
            log.Write($"任务 {job.JobId}：{job.App.CurrentVersion} → {job.App.TargetVersion}（{job.Channel}）");
            log.Write($"程序目录：{job.App.InstallDir}");
        }

        return RunWindow(job, loadError, directLaunch: false, log);
    }

    /// <summary>建 Application + 窗口并跑消息循环；返回更新器的退出码。</summary>
    private static int RunWindow(UpdateJob? job, string? loadError, bool directLaunch, JobLog log)
    {
        // 没有 App.xaml：入口就在 Main，Application 手动创建（就不会和 SDK 生成的 Main 撞车）
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        UpdaterTheme.Apply(UpdaterTheme.IsSystemDark());

        try
        {
            var window = new MainWindow(job, loadError, directLaunch, log);
            app.MainWindow = window;
            app.Run(window);
        }
        catch (Exception ex)
        {
            // 界面自己起不来（XAML 解析失败之类）时至少留下日志，别静默死掉
            log.Write("更新器界面启动失败：" + ex);
            return UpdateExitCode.ReplaceFailed;
        }

        return MainWindow.LastExitCode;
    }

    /// <summary>
    /// 界面调用的入口：把"意外异常"也翻译成界面上的红字 + 结果文件，而不是让 Task 带着异常跑掉。
    /// 取消照旧往上抛（界面自己会切到"已取消"）。
    /// </summary>
    internal static async Task<int> RunAsync(UpdateJob job, JobLog log, IUpdateUi ui, CancellationToken ct)
    {
        try
        {
            return await RunCoreAsync(job, log, ui, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return Fail(job, log, ui, UpdateExitCode.AccessDenied, "没有写入权限：" + ex.Message);
        }
        catch (IOException ex)
        {
            return Fail(job, log, ui, IsDiskFull(ex) ? UpdateExitCode.DiskFull : UpdateExitCode.ReplaceFailed,
                "文件操作失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            return Fail(job, log, ui, UpdateExitCode.ReplaceFailed, "更新器异常：" + ex);
        }
    }

    private static async Task<int> RunCoreAsync(UpdateJob job, JobLog log, IUpdateUi ui, CancellationToken ct)
    {
        Directory.CreateDirectory(job.Paths.WorkDir);

        // ① 下载（此时主程序还在跑：我们不碰它的目录，它也不挡下载）
        var zipPath = Path.Combine(job.Paths.WorkDir, "payload.zip");

        // 下载期间主程序仍在运行（窗口先亮"正在连接服务器…"，见 MainWindow.Window_Loaded）
        ui.Status("正在下载更新…");
        ui.SetProgress(0);

        var (downloaded, downloadCode, downloadError) =
            await Downloader.DownloadAsync(job.Download, zipPath, log, ui.Download, ct).ConfigureAwait(true);

        if (downloaded is null) return Fail(job, log, ui, downloadCode, downloadError ?? "下载失败");

        // ② 校验
        ui.Status("正在校验安装包…");

        if (!string.IsNullOrWhiteSpace(job.Download.Sha256))
        {
            var actual = await Task.Run(() => Hashing.ComputeFileSha256(downloaded), ct).ConfigureAwait(true);

            if (!Hashing.Sha256Equals(job.Download.Sha256, actual))
            {
                return Fail(job, log, ui, UpdateExitCode.HashMismatch,
                    $"安装包校验失败（期望 {Hashing.Normalize(job.Download.Sha256)}，实际 {actual}）");
            }

            log.Write("SHA-256 校验通过：" + actual);
        }
        else
        {
            log.Write("任务没有提供 sha256，跳过校验");
        }

        // ④ 关掉主程序：从这里开始独占程序目录里的文件（解压与替换是同一批文件）
        ui.Status("下载完成，正在准备安装…");
        ui.SetProgress(90);

        var appClosed = await Task.Run(() => KillRunningApp(job, log), ct).ConfigureAwait(true);
        if (!appClosed)
        {
            return Fail(job, log, ui, UpdateExitCode.AppStillRunning,
                "没能关闭主程序（可能权限不足）。请手动退出 Class Daily Land 2 后点「重试」。");
        }

        // ⑤ 先落一份「正在替换」：断电 / 被硬杀时，新程序启动能看出上次没做完
        WriteResult(job, ok: false, stage: "replacing", message: "正在替换文件…", backupDir: "");

        // ⑥ 最干净的环境覆盖：删除 --target 下除 settings / plugins 外的全部内容。
        //    更新器自己运行在 %Temp% 的副本里，所以目标目录的 Update 文件夹也可以删。
        ui.Status("正在清理旧文件…");
        ui.SetProgress(92);

        var targetDir = job.App.InstallDir;
        var cleaned = await Task.Run(() => ClearTarget(targetDir, log), ct).ConfigureAwait(true);
        if (!cleaned)
        {
            return Fail(job, log, ui, UpdateExitCode.AccessDenied,
                $"清理目标目录失败：{targetDir}。请确认没有其他程序占用文件后重试。");
        }

        // ⑦ 直接把下载的 ZIP 解压到目标目录；PayloadExtractor 仍然负责 Zip Slip、
        //    条目数 / 解压总量限制，以及 settings / plugins 的保留规则。
        ui.Status("正在解压替换文件…");
        ui.SetProgress(94);

        List<string>? payload = null;
        string? extractError = null;

        await Task.Run(() =>
        {
            payload = PayloadExtractor.Extract(downloaded, targetDir, job, log, out extractError,
                (done, total) => ui.SetProgress(94 + 5.0 * done / Math.Max(1, total)));
        }, ct).ConfigureAwait(true);

        if (payload is null) return Fail(job, log, ui, UpdateExitCode.ExtractFailed, extractError ?? "解压失败");

        log.Write($"解压完成：{payload.Count} 个文件 → {targetDir}");

        WriteResult(job, ok: true, stage: "done", message: "更新完成", backupDir: "");
        AppendAppLog(job, $"[update] 已更新到 {job.App.TargetVersion}（{job.Channel}）");

        // ⑧ 资源包：失败只记日志，主程序已经更新成功，不能反过来算失败
        if (job.Resources is not null && !string.IsNullOrWhiteSpace(job.Resources.Download.Url))
            await UpdateResourcesAsync(job, log, ui, ct).ConfigureAwait(true);

        // ⑨ 不自动重启：等用户在界面上点「完成」，再按复选框决定是否启动程序
        ui.Status("安装完成，请点击「完成」。");
        ui.SetProgress(100);

        log.Write("更新成功");
        return UpdateExitCode.Success;
    }

    /// <summary>资源包（<c>assets\</c>）单独更新：失败不回滚主程序，只记一笔。</summary>
    private static async Task UpdateResourcesAsync(UpdateJob job, JobLog log, IUpdateUi ui, CancellationToken ct)
    {
        var resources = job.Resources!;

        try
        {
            ui.Status($"正在更新资源包 {resources.Version}…");
            log.Write($"开始更新资源包 {resources.Version}");

            var zipPath = Path.Combine(job.Paths.WorkDir, "resources.zip");
            var (downloaded, _, error) =
                await Downloader.DownloadAsync(resources.Download, zipPath, log, null, ct).ConfigureAwait(true);

            if (downloaded is null)
            {
                log.Write("资源包下载失败（主程序已更新，本次跳过资源）：" + error);
                return;
            }

            if (!string.IsNullOrWhiteSpace(resources.Download.Sha256))
            {
                var actual = await Task.Run(() => Hashing.ComputeFileSha256(downloaded), ct).ConfigureAwait(true);
                if (!Hashing.Sha256Equals(resources.Download.Sha256, actual))
                {
                    log.Write("资源包校验失败，本次跳过资源更新");
                    return;
                }
            }

            var staging = Path.Combine(job.Paths.WorkDir, "resources");
            ResetDirectory(staging);

            List<string>? payload = null;
            string? extractError = null;

            await Task.Run(() =>
            {
                payload = PayloadExtractor.Extract(downloaded, staging, job, log, out extractError);
            }, ct).ConfigureAwait(true);

            if (payload is null)
            {
                log.Write("资源包解压失败，本次跳过资源更新：" + extractError);
                return;
            }

            ReplaceOutcome? outcome = null;
            await Task.Run(() => outcome = Replacer.Apply(job, staging, resources.InstallDir, payload!, log), ct)
                .ConfigureAwait(true);

            if (outcome is null || !outcome.Ok)
            {
                log.Write("资源包替换失败：" + (outcome?.Message ?? "未知原因"));
                return;
            }

            NotifyResources(resources.NotifyEvent, log);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Write("更新资源包时出错（忽略）：" + ex);
        }
    }

    /// <summary>资源替换完 Set 一下命名事件，让仍在运行的主程序热刷新（程序已退出时自然失败，忽略）。</summary>
    private static void NotifyResources(string? eventName, JobLog log)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return;

        try
        {
            if (EventWaitHandle.TryOpenExisting(eventName, out var handle))
            {
                using (handle) handle.Set();
                log.Write("已通知运行中的主程序热刷新资源");
            }
        }
        catch (Exception ex)
        {
            log.Write("通知资源刷新失败（忽略）：" + ex.Message);
        }
    }

    /// <summary>
    /// 清空 <c>--target</c>：只保留根目录下的 settings / plugins 两个文件夹，
    /// 其余文件和文件夹递归删除 —— 新包解压后就是一份完全干净的程序目录。
    /// </summary>
    private static bool ClearTarget(string targetDir, JobLog log)
    {
        var target = Path.GetFullPath(targetDir);
        if (!Directory.Exists(target))
        {
            log.Write("目标目录不存在：" + target);
            return false;
        }

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "settings", "plugins" };
        var deletedFiles = 0;
        var deletedDirs = 0;

        foreach (var entry in Directory.EnumerateFileSystemEntries(target))
        {
            var name = Path.GetFileName(entry);
            if (keep.Contains(name))
            {
                log.Write("保留用户数据：" + entry);
                continue;
            }

            try
            {
                if (Directory.Exists(entry))
                {
                    deletedFiles += DeleteDirectoryTree(entry);
                    deletedDirs++;
                }
                else
                {
                    DeleteFile(entry);
                    deletedFiles++;
                }
            }
            catch (Exception ex)
            {
                log.Write($"清理失败：{entry} → {ex.Message}");
                return false;
            }
        }

        log.Write($"已清理目标目录：删除 {deletedFiles} 个文件、{deletedDirs} 个文件夹（保留 settings / plugins）");
        return true;
    }

    private static int DeleteDirectoryTree(string dir)
    {
        var files = 0;

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            DeleteFile(file);
            files++;
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
            files += DeleteDirectoryTree(sub);

        Directory.Delete(dir, recursive: false);
        return files;
    }

    private static void DeleteFile(string path)
    {
        // 只读文件（例如从某些压缩包解出来的）先恢复属性，否则 Delete 会被拒绝
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    /// <summary>
    /// 覆盖 <c>--target</c> 之前强杀仍在运行的主程序。
    ///
    /// 下载和校验都在主程序**活着**的时候完成 —— 只有替换这一步需要独占程序目录里的文件，
    /// 所以"关程序"要留到最后一刻，而不是一开头就等它退出。
    ///
    /// 按进程名遍历 <see cref="Process.GetProcessesByName(string)"/> 并逐个 <see cref="Process.Kill()"/>，
    /// 杀完 <see cref="Thread.Sleep(int)"/> 1.5 秒等文件句柄释放，再进入解压 / 替换。
    /// </summary>
    private static bool KillRunningApp(UpdateJob job, JobLog log)
    {
        var names = new List<string>();

        var configured = Path.GetFileNameWithoutExtension(job.App.ExeName ?? "");
        if (!string.IsNullOrWhiteSpace(configured)) names.Add(configured);

        // 程序改名前后都覆盖，避免配置里的 exe 名与实际进程对不上
        names.Add("Class Daily Land");
        names.Add("Class Daily Land 2");

        var uniqueNames = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var killed = 0;

        foreach (var name in uniqueNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception ex)
            {
                log.Write($"按名字查找主程序 {name} 失败：{ex.Message}");
                continue;
            }

            foreach (var proc in processes)
            {
                using (proc)
                {
                    try
                    {
                        if (proc.HasExited) continue;

                        proc.Kill();
                        killed++;
                        log.Write($"已强杀主程序进程：{name}（PID {proc.Id}）");
                    }
                    catch (Exception ex)
                    {
                        log.Write($"强杀主程序 {name}（PID {proc.Id}）失败：{ex.Message}");
                    }
                }
            }
        }

        log.Write($"关闭主程序：共结束 {killed} 个进程");

        // 强杀返回不代表文件句柄已经释放，固定等 1.5 秒再解压 / 覆盖
        Thread.Sleep(1500);

        foreach (var name in uniqueNames)
        {
            try
            {
                var remaining = Process.GetProcessesByName(name);
                if (remaining.Length == 0) continue;

                foreach (var proc in remaining) proc.Dispose();
                log.Write($"仍有主程序进程在运行：{name}");
                return false;
            }
            catch (Exception ex)
            {
                log.Write($"复查主程序 {name} 失败（按已关闭处理）：{ex.Message}");
            }
        }

        return true;
    }

    /// <summary>
    /// 用户点「完成」且勾选了自动运行时调用：启动更新后的主程序。
    /// 路径只由 --target（job.App.InstallDir）与 <see cref="VersionInfo.ExeName"/> 拼接，
    /// 绝不能用 AppContext.BaseDirectory —— 更新器运行在 %Temp% 的副本里。
    /// </summary>
    internal static bool LaunchApp(UpdateJob job, JobLog log, out string error)
    {
        error = "";

        var target = job.App.InstallDir?.Trim() ?? "";
        if (target.Length == 0)
        {
            error = "更新任务里没有安装目录（--target）";
            log.Write("启动主程序失败：" + error);
            return false;
        }

        var exe = Path.Combine(target, VersionInfo.ExeName);
        if (!File.Exists(exe))
        {
            error = "主程序不存在：" + exe;
            log.Write("启动主程序失败：" + error);
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
            log.Write("已启动主程序：" + exe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log.Write("启动主程序失败：" + ex);
            return false;
        }
    }

    private static int Fail(UpdateJob job, JobLog log, IUpdateUi ui, int code, string message)
    {
        log.Write($"失败（{UpdateExitCode.Describe(code)}，退出码 {code}）：{message}");

        WriteResult(job, ok: false, stage: "failed",
            message: $"{UpdateExitCode.Describe(code)}：{message}", backupDir: "");

        AppendAppLog(job, $"[update] 失败：{UpdateExitCode.Describe(code)} —— {message}");
        ui.Fail($"{UpdateExitCode.Describe(code)}：{message}");

        return code;
    }

    private static void WriteResult(UpdateJob job, bool ok, string stage, string message, string backupDir)
    {
        try
        {
            UpdateJson.Write(job.Paths.Result, new LastUpdateResult
            {
                JobId = job.JobId,
                From = job.App.CurrentVersion,
                To = job.App.TargetVersion,
                Ok = ok,
                Stage = stage,
                Message = message,
                FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                BackupDir = backupDir,
                Consumed = false,
            });
        }
        catch (Exception)
        {
            // 结果文件写不进去也要继续：剩下的步骤比这份记录重要
        }
    }

    private static void AppendAppLog(UpdateJob job, string line)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(job.Paths.AppLog)) return;

            var dir = Path.GetDirectoryName(job.Paths.AppLog);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.AppendAllText(job.Paths.AppLog,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}",
                System.Text.Encoding.UTF8);
        }
        catch
        {
            // 见类注释：留痕失败不影响更新
        }
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
}
