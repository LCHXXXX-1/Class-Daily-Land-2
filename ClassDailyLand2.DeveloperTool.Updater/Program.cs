using System.Diagnostics;
using System.Windows;
using ClassDailyLand2.Shared;
using ClassDailyLand2.UpdateContract;

namespace IDEUpdater;

/// <summary>
/// 更新器入口与编排。
///
/// 主干与主程序那份一致：它在 IDE 仍在运行时开始下载，替换文件前等 IDE 退出；
/// 每一步都要能独立失败、并把原因落到 <c>last-update.json</c> 与日志里；失败时还会在屏幕上给红字 + 重试。
///
/// 主干：下载 → 校验 → 等 IDE 退出并等文件释放 → 清空目标目录（保留 projects / plugins / Temp / WebView2Data 与三个 json）
/// → 解压到目标目录 → 写结果 → 等待用户点「完成」（按复选框决定是否启动 IDE）。
///
/// ★ 与主程序的差别就一处要紧的：主程序直接 Process.Kill，这里改用 <see cref="WaitForIdeExit"/> 只等不杀。
/// </summary>
internal static class Program
{
    /// <summary>IDE 主程序默认名：job 里没给 exe 时兜底（正式路径由 IDE 侧把 exe_name 填进 job.json）。</summary>
    private const string DefaultExeName = "DeveloperTool.exe";

    /// <summary>
    /// 等 IDE 退出的上限。90 秒是给「切回 IDE → 看提示 → 保存 → 关窗」留的余量：
    /// 比主程序的 60 秒长，因为这里不会替用户强杀，只能等他真的动手。
    /// </summary>
    private const int IdeExitTimeoutSeconds = 90;

    /// <summary>轮询间隔：300ms 足够跟手，又不至于把 CPU 拉起来。</summary>
    private const int IdeExitPollIntervalMs = 300;

    /// <summary>
    /// 契约 <c>UpdateJob.Normalize()</c> 给 exe 名填的默认值（主程序名）。
    /// 见 <see cref="ResolveExeName"/>：在 IDE 这儿它等同于「没填」。
    /// </summary>
    private const string ContractDefaultExeName = "Class Daily Land 2.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        var log = new JobLog();
        App.Attach(log);

        try
        {
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

            // 不做自我复制：更新器由 IDE 侧复制到 %Temp% 再从那里启动（对应主程序的
            // UpdateService.PrepareUpdaterAsync）。就地运行会锁住自己的 exe，
            // 后面的 ClearTarget 连"删掉 Update\ 里那份旧更新器"都做不到。
            // ★ 因此老代码里的 SelfRelocate 已废弃，别再加回来。

            var job = JobLoader.Load(cli, out var loadError);
            if (job is not null)
            {
                log.Attach(job.Paths.Log);
                log.Write($"任务 {job.JobId}：{job.App.CurrentVersion} → {job.App.TargetVersion}（{job.Channel}）");
                log.Write($"程序目录：{job.App.InstallDir}");
            }

            return RunWindow(job, loadError, directLaunch: false, log);
        }
        catch (Exception ex)
        {
            log.Write("更新器入口异常：" + ex);
            App.Report(ex, "更新器启动失败");
            return UpdateExitCode.ReplaceFailed;
        }
    }

    /// <summary>建 Application + 窗口并跑消息循环；返回更新器的退出码。</summary>
    private static int RunWindow(UpdateJob? job, string? loadError, bool directLaunch, JobLog log)
    {
        // 没有 App.xaml 内容：App 只负责挂全局异常；入口仍在 Main，避免和 SDK 生成的 Main 撞车。
        var app = new App { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            log.Write("UI 线程异常：" + e.Exception);
            App.Report(e.Exception, "UI 线程异常");
            e.Handled = true;
        };
        UpdaterTheme.Apply(UpdaterTheme.IsSystemDark());

        // 触屏：关掉"按住弹圆圈"的系统反馈（更新器卡片是全自绘的，圈圈会糊上去）
        System.Windows.EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new System.Windows.RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window) System.Windows.Input.Stylus.SetIsPressAndHoldEnabled(window, false);
            }));

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
            App.Report(ex, "更新器界面启动失败");
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

        // ① 先把路径钉死：--target 必须是安装目录，绝不能是 %Temp% / 更新器工作目录。
        if (!IsSafeInstallTarget(job.App.InstallDir, job.Paths.WorkDir, out var targetError))
            return Fail(job, log, ui, UpdateExitCode.BadJob, targetError);

        // ② 下载（此时 IDE 还在跑：我们不碰它的目录，它也不挡下载）
        var zipPath = Path.Combine(job.Paths.WorkDir, "payload.zip");

        // 下载期间 IDE 仍在运行（窗口先亮"正在连接服务器…"，见 MainWindow.Window_Loaded）
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

        // ③ 等 IDE 退出：从这里开始独占程序目录里的文件（解压与替换是同一批文件）
        ui.DownloadFinished();
        ui.Status("下载完成，正在准备安装…");
        ui.SetProgress(90);

        var ideExited = await Task.Run(() => WaitForIdeExit(job, log, ct), ct).ConfigureAwait(true);
        if (!ideExited)
        {
            return Fail(job, log, ui, UpdateExitCode.AppStillRunning,
                "请先在 DeveloperTool 里保存并退出（工具栏更新提示会引导保存），再点重试");
        }

        // 进程从列表里消失 ≠ 文件句柄立刻释放完。这里再等一小段，避免"进程还在收尾"
        // 和 "ZipFile.ExtractToDirectory" 抢同一批文件。
        await Task.Delay(1500, ct).ConfigureAwait(true);

        // ⑤ 先落一份「正在替换」：断电 / 被硬杀时，新程序启动能看出上次没做完
        WriteResult(job, ok: false, stage: "replacing", message: "正在替换文件…", backupDir: "");

        // ⑥ 最干净的环境覆盖：删除 --target 下除 settings / plugins 外的全部内容。
        //    更新器自己运行在 %Temp% 的副本里，所以目标目录的 Update 文件夹也可以删。
        ui.Status("正在清理旧文件…");
        ui.SetProgress(92);

        var targetDir = job.App.InstallDir;

        // out 变量必须在 lambda 外面声明：写在 lambda 里就只作用于 lambda 本身，
        // 下面 !cleaned 分支拿不到（CS0103）。
        string clearError = "";
        var cleaned = await Task.Run(() => ClearTarget(job, targetDir, log, out clearError), ct).ConfigureAwait(true);
        if (!cleaned)
        {
            return Fail(job, log, ui, UpdateExitCode.AccessDenied,
                $"清理目标目录失败：{targetDir}\n\n原因：{clearError}\n\n请确认没有其他程序（如终端、杀毒软件、VS调试器）占用该目录后重试。");
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

        if (payload is null)
        {
            await Task.Run(() => TryDeleteDownloadedZip(downloaded, log), ct).ConfigureAwait(true);
            return Fail(job, log, ui, UpdateExitCode.ExtractFailed, extractError ?? "解压失败");
        }

        log.Write($"解压完成：{payload.Count} 个文件 → {targetDir}");
        await Task.Run(() => TryDeleteDownloadedZip(downloaded, log), ct).ConfigureAwait(true);

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
                await Task.Run(() => TryDeleteDownloadedZip(downloaded, log), ct).ConfigureAwait(true);
                log.Write("资源包解压失败，本次跳过资源更新：" + extractError);
                return;
            }

            await Task.Run(() => TryDeleteDownloadedZip(downloaded, log), ct).ConfigureAwait(true);

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
    /// 清空 <c>--target</c>：只保留 IDE 的用户数据（<see cref="IdePreserve"/> 里的目录与 json），
    /// 其余文件和文件夹递归删除 —— 新包解压后就是一份完全干净的程序目录。
    ///
    /// 主程序这边是 settings / plugins 两棵树，IDE 换成自己的落点；job.Preserve 一并并进来，
    /// 这样 job.json 里临时加了什么保留项也不会被这一层删掉。
    ///
    /// ★ 每个条目都重试 5 次、间隔 500ms：进程刚退出时句柄、杀软扫描、资源管理器的缩略图缓存
    ///   都会让 Delete 短暂失败，一次失败就整体放弃会把「稍等一下就能过」变成「更新失败」。
    ///   最终仍失败时通过 <paramref name="error"/> 带回具体是哪个文件 —— 只报"没有写入权限"
    ///   等于让人对着一个目录猜，日志里也留了 5 次重试的痕迹。
    /// </summary>
    private static bool ClearTarget(UpdateJob job, string targetDir, JobLog log, out string error)
    {
        error = "";
        var target = Path.GetFullPath(targetDir);
        if (!Directory.Exists(target))
        {
            log.Write("目标目录不存在：" + target);
            error = "目标目录不存在：" + target;
            return false;
        }

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in IdePreserve.Dirs) keep.Add(name);
        foreach (var name in IdePreserve.Files) keep.Add(name);
        foreach (var name in job.Preserve) keep.Add(name.Trim());

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

            var success = false;
            Exception? lastEx = null;

            // 重试 5 次，每次间隔 500ms（应对杀软扫描或句柄延迟释放）
            for (var attempt = 1; attempt <= 5; attempt++)
            {
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
                    success = true;
                    break;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    log.Write($"清理失败（第 {attempt}/5 次）：{entry} → {ex.Message}");
                    Thread.Sleep(500);
                }
            }

            if (!success)
            {
                error = $"文件被占用或拒绝访问：{entry}（{lastEx?.Message}）";
                return false;
            }
        }

        log.Write($"已清理目标目录：删除 {deletedFiles} 个文件、{deletedDirs} 个文件夹（保留 {string.Join("/", keep)}）");
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
    /// ★ IDE 特有：只等 IDE 自己退出，绝不 Kill。
    ///
    /// 主程序那边可以直接 <c>Process.Kill()</c> —— 用户数据都在 settings 里，强杀最多丢一次自动保存。
    /// IDE 不一样：用户手上可能是写了一半的插件代码，强杀等于把没保存的编辑整段吃掉，
    /// 所以这里到点就交回界面，让用户自己保存、退出、再点「重试」。
    ///
    /// 正常路径其实轮不到超时：IDE 侧的 ConfirmBeforeUpdate 已经在拉起更新器前让用户保存并关窗，
    /// 这段只是兜底（比如用户点完提示又从别处开了一个 IDE 窗口）。
    ///
    /// 用 <see cref="NativeProcess.AnyRunning"/> 走 Toolhelp 快照，不打开进程句柄：
    /// 进程刚退出 / 无权限时 <c>Process.GetProcessesByName</c> 这类 API 会抛 Win32Exception，
    /// 而这里恰好只在「东西正在退出」的时候跑。
    /// </summary>
    /// <summary>
    /// 取 IDE 的 exe / 进程名。
    ///
    /// 为什么要夹这一层：契约的 <c>Normalize()</c> 会在 exe 名为空时填主程序的名字
    /// （Class Daily Land 2.exe）。IDE 侧组装 job.json 时若漏填 exe_name，
    /// 更新器就会去等一个根本不存在的进程，白等满 90 秒再报"请先保存并退出" —— 排查成本极高。
    /// 所以把"等于契约默认值"当成"没填"，换成 IDE 的默认名，并留一行日志说明换过。
    /// </summary>
    private static string ResolveExeName(UpdateJob job, JobLog? log)
    {
        var name = job.App.ExeName?.Trim() ?? "";

        if (name.Length == 0)
        {
            return DefaultExeName;
        }

        if (string.Equals(name, ContractDefaultExeName, StringComparison.OrdinalIgnoreCase))
        {
            log?.Write($"job 里的 exe 名是主程序的默认值（{name}），按未填处理，改用 {DefaultExeName}");
            return DefaultExeName;
        }

        return name;
    }

    private static bool WaitForIdeExit(UpdateJob job, JobLog log, CancellationToken ct)
    {
        var name = Path.GetFileName(ResolveExeName(job, log));   // job 里可能给相对路径，进程名只比文件名

        var started = DateTime.UtcNow;
        var deadline = started.AddSeconds(IdeExitTimeoutSeconds);

        while (NativeProcess.AnyRunning(name))
        {
            // 用户点了取消就按"取消"往上抛，别伪装成"没退出"—— 那会弹一条让人去保存退出的错提示
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

            if (DateTime.UtcNow >= deadline)
            {
                log.Write($"等待 {name} 退出超时（{IdeExitTimeoutSeconds} 秒），放弃本次更新（不强制结束进程）");
                return false;
            }

            Thread.Sleep(IdeExitPollIntervalMs);
        }

        log.Write($"{name} 已退出（等待 {DateTime.UtcNow.Subtract(started).TotalSeconds:F1} 秒）");
        return true;
    }

    /// <summary>
    /// 用户点「完成」且勾选了自动运行时调用：启动更新后的 IDE。
    ///
    /// exe 名取自 <c>job.App.ExeName</c>（IDE 侧组装 job.json 时填 DeveloperTool.exe），空了才用常量兜底。
    /// ★ 不能照主程序那样用 <c>VersionInfo.ExeName</c> —— 那是共享常量里的 "Class Daily Land 2.exe"，
    ///   拼出来会把主程序拉起来，正好是最危险的一种错误（用户以为在开 IDE）。
    /// 路径只由 --target（job.App.InstallDir）与 exe 名拼接，绝不能用 AppContext.BaseDirectory：
    /// 更新器运行在 %Temp% 的副本里。
    /// </summary>
    internal static bool LaunchApp(UpdateJob job, JobLog log, out string error)
    {
        error = "";

        var target = job.App.InstallDir?.Trim() ?? "";
        if (target.Length == 0)
        {
            error = "更新任务里没有安装目录（--target）";
            log.Write("启动 IDE 失败：" + error);
            return false;
        }

        var name = ResolveExeName(job, log);
        var exe = Path.IsPathRooted(name) ? name : Path.Combine(target, name);

        if (!File.Exists(exe))
        {
            error = "IDE 不存在：" + exe;
            log.Write("启动 IDE 失败：" + error);
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
            log.Write("已启动 IDE：" + exe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log.Write("启动 IDE 失败：" + ex);
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

    private static bool IsSafeInstallTarget(string? installDir, string? workDir, out string error)
    {
        error = "";

        if (string.IsNullOrWhiteSpace(installDir))
        {
            error = "更新任务里的安装目录为空（--target）。";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(installDir);
        }
        catch (Exception ex)
        {
            error = "安装目录不合法：" + ex.Message;
            return false;
        }

        var temp = Path.GetTempPath();
        var work = string.IsNullOrWhiteSpace(workDir) ? "" : Path.GetFullPath(workDir!);

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

    private static void TryDeleteDownloadedZip(string zipPath, JobLog log)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(zipPath)) return;

                File.SetAttributes(zipPath, FileAttributes.Normal);
                File.Delete(zipPath);
                log.Write("已删除下载的临时包：" + zipPath);
                return;
            }
            catch (Exception ex)
            {
                log.Write($"删除临时包失败（第 {attempt + 1} 次）：{ex.Message}");
                Thread.Sleep(500);
            }
        }

        log.Write("临时包仍然被占用，交给退出清理流程处理：" + zipPath);
    }

    private static bool IsDiskFull(IOException ex)
    {
        const int ErrorDiskFull = unchecked((int)0x80070070);
        const int ErrorHandleDiskFull = unchecked((int)0x80070027);
        return ex.HResult == ErrorDiskFull || ex.HResult == ErrorHandleDiskFull;
    }
}
