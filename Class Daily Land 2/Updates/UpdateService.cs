using ClassDailyLand2.Core;
using ClassDailyLand2.Core.Managers;
using ClassDailyLand2.Core.Storage;
using ClassDailyLand2.UpdateContract;

namespace ClassDailyLand2.App.Updates;

/// <summary>
/// 检查更新的编排：拉 version.json → 校验 → 比版本 → 产出 <see cref="UpdateOffer"/>；
/// 用户点「立即更新」时启动**预复制**到 %Temp% 的更新器（检查阶段就并行复制好了）。
///
/// 两个进程的分工：
///   · 本类（主程序）负责"发现 + 询问 + 起更新器"，全程在设置窗口里给反馈；
///   · 更新器负责"下载 + 解压 + 备份 + 替换 + 拉起"，替换前才强杀主程序。
/// </summary>
public sealed class UpdateService
{
    readonly SettingsManager _settings;
    readonly DataPaths _paths;
    readonly Action<string>? _log;

    int _inFlight;

    /// <summary>检查更新时预复制好的更新器 exe（%Temp%\CDL_UpdateTemp\Updater.exe）。</summary>
    string? _preparedUpdaterExe;

    /// <summary>预复制目录名（%Temp% 下）。更新器收尾时会把整个目录删掉。</summary>
    const string PreparedUpdaterFolderName = "CDL_UpdateTemp";

    public UpdateService(SettingsManager settings, DataPaths paths, Action<string>? log = null)
    {
        _settings = settings;
        _paths = paths;
        _log = log;
    }

    /// <summary>主源 + 备用源（来自 <see cref="VersionInfo"/>），去重去空。</summary>
    static readonly string[] VersionUrls = BuildVersionUrls();

    static string[] BuildVersionUrls()
    {
        var list = new List<string>();
        foreach (var url in new[] { VersionInfo.VersionUrl, VersionInfo.VersionUrlFallback })
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (list.Any(x => string.Equals(x, url, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(url.Trim());
        }
        return list.ToArray();
    }

    /// <summary>最近一次检查结果（关于页据此渲染）。</summary>
    public UpdateStatus Status { get; private set; } = UpdateStatus.Idle;

    /// <summary>状态变化（UI 订阅它刷新按钮与 InfoBar）。</summary>
    public event Action<UpdateStatus>? StatusChanged;

    /// <summary>当前要跟踪的通道（stable / beta）。</summary>
    public string Channel =>
        string.Equals(_settings.Str("update_channel"), "beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "stable";

    // ─────────────────────────── 检查 ───────────────────────────

    /// <summary>
    /// 检查更新。<paramref name="silent"/> = true 时"已最新/失败"只写日志不改 UI 状态（启动检查用）。
    /// 并发调用会直接返回上一次的结果（按钮连点 5 次只发 1 个请求）。
    /// </summary>
    public async Task<UpdateStatus> CheckAsync(bool silent = false, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _inFlight, 1) == 1)
        {
            _log?.Invoke("检查更新正在进行中，忽略本次调用");
            return Status;
        }

        try
        {
            // 用户主动检查时：网络请求与"把 Update\ 整目录复制到 %Temp%"并行做。
            // 两边都完成才亮出「立即更新」，这样点下去只是启动 temp 里的副本 —— 界面秒开。
            var prepareTask = silent ? null : PrepareUpdaterAsync(ct);

            if (!silent) SetStatus(UpdateStage.Checking, "正在检查更新…", clear: true);

            var urls = VersionUrls;
            var (file, source, error) = await UpdateSources.FetchAsync(urls, ct).ConfigureAwait(false);

            if (file is null)
            {
                var message = string.IsNullOrWhiteSpace(error) ? "无法连接更新源" : error;
                _log?.Invoke($"检查更新失败：{message}");
                if (!silent) SetStatus(UpdateStage.Failed, message);
                else Status = Status with { Stage = UpdateStage.Failed, Error = message };
                return Status;
            }

            var primaryUrl = urls.Count() > 0 ? urls[0] : "";
            var fallbackUrl = urls.Count() > 1 ? urls[1] : primaryUrl;

            var channel = Channel;
            var local = AppVersion.Value;

            // ── 程序本体 ──
            var appBlock = string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase)
                ? file.Beta
                : file.Stable;
            var appOffer = BuildOffer(appBlock, channel, UpdateKind.App, source, primaryUrl, fallbackUrl, local);

            // 被「稍后再说」跳过的版本不再提示，除非它是强制更新
            if (appOffer is not null && !appOffer.IsMandatory &&
                string.Equals(_settings.Str("update_skipped_version"), appOffer.Version.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                _log?.Invoke($"跳过版本 {appOffer.Version}（用户点过稍后再说）");
                appOffer = null;
            }

            // ── 资源包（独立判定：没装过就当作 0.0.0，只要远端有就提示） ──
            var installedResources = SemVer.ParseOrZero(_settings.Str("resources_version"));
            var resourcesOffer = BuildOffer(file.Resources, "resources", UpdateKind.Resources, source, primaryUrl, fallbackUrl, installedResources);

            var now = DateTime.Now;
            var hasAny = appOffer is not null || resourcesOffer is not null;

            if (hasAny)
            {
                // 等预复制收尾：复制失败就别亮「立即更新」，否则点下去只会失败
                if (prepareTask is not null && await prepareTask.ConfigureAwait(false) is null)
                {
                    const string prepareError = "更新器准备失败：无法把 Update 目录复制到临时目录，暂时不能更新。";
                    _log?.Invoke(prepareError);
                    SetStatus(UpdateStage.Failed, prepareError);
                    return Status;
                }

                var text = appOffer is not null
                    ? $"发现新版本 {appOffer.Version}（{appOffer.ReleaseDate}）"
                    : $"已是最新 {local}；资源包 {resourcesOffer!.Version} 可更新";
                if (appOffer is not null && resourcesOffer is not null)
                    text += $" · 资源包 {resourcesOffer.Version}";
                if (appOffer?.IsMandatory == true) text += " · 强制更新";

                SetStatus(UpdateStage.Available, text, appOffer, resourcesOffer);
            }
            else
            {
                if (!silent) SetStatus(UpdateStage.UpToDate, $"已是最新 {local}（检查于 {now:HH:mm}）");
                else Status = Status with { Stage = UpdateStage.UpToDate, CheckedAt = now };
            }

            // last_update_check 只在成功时写：网络抖动不能把下次检查推迟 6 小时
            try
            {
                _settings.Set("last_update_check", now.ToString("yyyy-MM-dd HH:mm:ss"), broadcast: false);
            }
            catch { /* 落盘失败不影响本次结果 */ }

            return Status;
        }
        catch (OperationCanceledException)
        {
            if (!silent) SetStatus(UpdateStage.Idle, "");
            return Status;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"检查更新异常：{ex}");
            if (!silent) SetStatus(UpdateStage.Failed, ex.Message);
            return Status;
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    /// <summary>校验一块更新信息并组装 <see cref="UpdateOffer"/>；不满足条件返回 null。</summary>
    static UpdateOffer? BuildOffer(object? block, string channel, UpdateKind kind, string source,
                                   string primaryUrl, string fallbackUrl, SemVer baseline)
    {
        string version, releaseDate, downloadUrl, sha256;
        List<string> changelog;
        bool mandatory;
        long size = 0;

        switch (block)
        {
            case RemoteChannel app:
                if (string.IsNullOrWhiteSpace(app.Version)) return null;
                version = app.Version.Trim();
                releaseDate = app.ReleaseDate ?? "";
                downloadUrl = app.DownloadUrl ?? "";
                sha256 = app.Sha256 ?? "";
                changelog = app.Changelog;
                mandatory = app.IsMandatory;
                break;

            case RemoteResourceChannel res:
                if (string.IsNullOrWhiteSpace(res.Version)) return null;
                version = res.Version.Trim();
                releaseDate = res.ReleaseDate ?? "";
                downloadUrl = res.DownloadUrl ?? "";
                sha256 = res.Sha256 ?? "";
                changelog = res.Changelog;
                mandatory = res.IsMandatory ?? false;
                break;

            default:
                return null;
        }

        if (!SemVer.TryParse(version, out var ver)) return null;

        if (!downloadUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !downloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;

        // 哈希缺失/格式错 = 这条更新没法安全安装，直接不当作"有更新"
        if (!Hashing.LooksLikeSha256(sha256)) return null;

        if (ver <= baseline) return null;

        return new UpdateOffer(kind, channel, ver, releaseDate, changelog, mandatory,
            downloadUrl, sha256, size, source, primaryUrl, "", fallbackUrl);
    }

    /// <summary>用户点「稍后再说」：记住这个版本，下次检查不再提示（除非它是强制更新）。</summary>
    public void SkipVersion(UpdateOffer offer)
    {
        if (offer is null) return;
        try
        {
            _settings.Set("update_skipped_version", offer.Version.ToString(), broadcast: true);
            _log?.Invoke($"已跳过版本 {offer.Version}");
        }
        catch (Exception ex)
        {
            _log?.Invoke("记录跳过版本失败：" + ex.Message);
        }
    }

    void SetStatus(UpdateStage stage, string message, UpdateOffer? app = null, UpdateOffer? resources = null, bool clear = false)
    {
        var status = new UpdateStatus(stage, message, DateTime.Now, app, resources, null);
        if (clear) status = status with { App = null, Resources = null };
        Status = status;
        StatusChanged?.Invoke(status);
    }

    // ─────────────────────────── 预复制更新器 ───────────────────────────

    /// <summary>
    /// 把 <c>Update\</c> 整目录复制到 <c>%Temp%\CDL_UpdateTemp</c>，返回 Updater.exe 的完整路径。
    ///
    /// 检查更新时与网络请求**并行**执行；点「立即更新」时直接启动这个副本，
    /// 省掉"临时目录 + 复制 + 启动"那几百毫秒，窗口就是秒开的。
    /// 失败返回 null（调用方据此压住「立即更新」按钮）。
    /// </summary>
    public async Task<string?> PrepareUpdaterAsync(CancellationToken ct = default)
    {
        try
        {
            var prepared = await Task.Run(() => CopyUpdaterToTemp(ct), ct).ConfigureAwait(false);
            _preparedUpdaterExe = prepared;
            return prepared;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke("准备更新器失败：" + ex.Message);
            _preparedUpdaterExe = null;
            return null;
        }
    }

    string? CopyUpdaterToTemp(CancellationToken ct)
    {
        var sourceDir = Path.Combine(UpdateEnvironment.InstallDir, UpdateEnvironment.NewUpdaterDir);
        var sourceExe = Path.Combine(sourceDir, UpdateEnvironment.UpdaterExeName);

        if (!File.Exists(sourceExe))
        {
            // 老安装可能只有 updater\ 或根目录单文件：交给 StageUpdater 那条慢路径兜底
            _log?.Invoke(@"没有找到 Update\Updater.exe，跳过预复制");
            return null;
        }

        var targetDir = Path.Combine(Path.GetTempPath(), PreparedUpdaterFolderName);

        // 复制前强制清空：新旧文件混在一起 = 拿半个旧更新器去替换程序。
        // 上次的更新器可能还在运行 / 文件被占用，所以删除失败时逐个文件再清一遍。
        ForceResetDirectory(targetDir);

        try
        {
            CopyUpdaterFiles(sourceDir, targetDir, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 固定目录仍被占用（例如上一个更新器没退干净）：换一个全新的临时目录再复制，
            // 保证「立即更新」不会因为一个删不掉的 temp 文件而永远不可用。
            _log?.Invoke("覆盖临时更新器失败，改用独立临时目录：" + ex.Message);

            targetDir = Path.Combine(Path.GetTempPath(),
                PreparedUpdaterFolderName + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(targetDir);
            CopyUpdaterFiles(sourceDir, targetDir, ct);
        }

        var preparedExe = Path.Combine(targetDir, UpdateEnvironment.UpdaterExeName);
        if (!File.Exists(preparedExe)) return null;

        _log?.Invoke("更新器已预复制：" + preparedExe);
        return preparedExe;
    }

    /// <summary>
    /// 强制清空目标目录：先整目录删除，失败就逐个文件（去掉只读属性）再删；
    /// 最后无论如何都保证目录存在，让后面的覆盖复制至少有落点。
    /// </summary>
    void ForceResetDirectory(string targetDir)
    {
        try
        {
            if (File.Exists(targetDir))
            {
                File.SetAttributes(targetDir, FileAttributes.Normal);
                File.Delete(targetDir);
            }
            else if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke("清理旧的临时更新器失败，改为逐个删除：" + ex.Message);

            try
            {
                if (Directory.Exists(targetDir))
                {
                    foreach (var file in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                        }
                        catch (Exception fileEx)
                        {
                            _log?.Invoke("删除临时文件失败（稍后覆盖）：" + file + " → " + fileEx.Message);
                        }
                    }
                }
            }
            catch (Exception walkEx)
            {
                _log?.Invoke("遍历旧临时目录失败（稍后覆盖）：" + walkEx.Message);
            }
        }

        Directory.CreateDirectory(targetDir);
    }

    /// <summary>把整个 Update 目录复制到临时目录；目标文件被占用时稍等重试一次。</summary>
    static void CopyUpdaterFiles(string sourceDir, string targetDir, CancellationToken ct)
    {
        // 框架依赖的更新器是一整坨文件（exe + 依赖 dll + deps.json + runtimeconfig.json），必须整目录搬
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceDir, file);
            var destination = Path.Combine(targetDir, relative);

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            if (File.Exists(destination))
            {
                try { File.SetAttributes(destination, FileAttributes.Normal); }
                catch { /* 覆盖失败时下面会给出真正原因 */ }
            }

            try
            {
                File.Copy(file, destination, overwrite: true);
            }
            catch (IOException)
            {
                // 旧的更新器进程可能刚刚退出，给它一点时间释放文件句柄
                Thread.Sleep(300);
                File.Copy(file, destination, overwrite: true);
            }
        }
    }

    // ─────────────────────────── 启动更新器 ───────────────────────────

    /// <summary>
    /// 用户点「立即更新」后调用：优先启动预复制好的副本（四个参数，秒开）；
    /// 没准备好就退回"补兜底地址 → 写 job.json → 复制更新器 → 启动"这条慢路径。
    /// 启动成功后主程序继续在后台运行，下载与解压由更新器完成，替换前才由更新器关闭主程序。
    /// </summary>
    public async Task<UpdateLaunchResult> ApplyAsync(UpdateOffer offer, CancellationToken ct = default)
    {
        if (offer is null) return UpdateLaunchResult.Fail("没有要安装的更新");

        if (!UpdateEnvironment.IsDirectoryWritable(UpdateEnvironment.InstallDir, out var writeError) &&
            !UpdateEnvironment.IsElevated)
        {
            // 不可写但没关系 —— 上面会以 runas 启动更新器；这里只是把日志写清楚
            _log?.Invoke($"安装目录不可写（{writeError}），将以管理员权限运行更新器");
        }

        // ── 快速路径：检查更新时已经预复制到 %Temp%\CDL_UpdateTemp ──
        var preparedExe = _preparedUpdaterExe;
        if (preparedExe is not null && File.Exists(preparedExe))
        {
            var elevate = !UpdateEnvironment.IsElevated &&
                          !UpdateEnvironment.IsDirectoryWritable(UpdateEnvironment.InstallDir, out _);

            try
            {
                var startedPid = UpdateEnvironment.LaunchUpdater(
                    preparedExe, offer.DownloadUrl, offer.Sha256,
                    UpdateEnvironment.InstallDir, VersionInfo.ExeName, elevate);

                if (startedPid is null) return UpdateLaunchResult.Fail("更新器未能启动");

                _log?.Invoke($"更新器已启动（快速路径，PID {startedPid}）：{preparedExe}");
                UpdateEnvironment.AppendAppLog(Path.Combine(_paths.Root, "app.log"),
                    $"[update] 开始更新到 {offer.Version}（{offer.Channel}），更新器 PID {startedPid}");

                return UpdateLaunchResult.Success(preparedExe);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return UpdateLaunchResult.Fail("已取消（用户拒绝了管理员授权）");
            }
            catch (Exception ex)
            {
                _log?.Invoke("快速路径启动更新器失败，改用 job.json 路径：" + ex.Message);
            }
        }

        var jobId = DateTime.Now.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N")[..6];
        var workDir = Path.Combine(Path.GetTempPath(), "ClassDailyLand2.Update", jobId);
        Directory.CreateDirectory(workDir);

        // 1) 更新器复制到 temp（避开"替换时锁住自己"）
        var updaterExe = UpdateEnvironment.StageUpdater(
            UpdateEnvironment.InstallDir, VersionInfo.UpdaterDir, workDir, out var stageError);
        if (updaterExe is null)
            return UpdateLaunchResult.Fail(stageError ?? "更新器准备失败");

        // 2) 补下载兜底地址：另一源的 version.json 里可能写的是 GitHub 的包地址
        var download = offer.DownloadUrl;
        var downloadFallback = offer.DownloadUrlFallback;
        if (string.IsNullOrWhiteSpace(downloadFallback) || downloadFallback == download)
        {
            var (other, _) = await UpdateSources
                .TryGetDownloadUrlAsync(offer.FallbackVersionJsonUrl, offer.Channel, ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(other) && !string.Equals(other, download, StringComparison.OrdinalIgnoreCase))
                downloadFallback = other;
        }

        // 3) 组装 job.json
        var job = new UpdateJob
        {
            Schema = UpdateJob.CurrentSchema,
            JobId = jobId,
            Action = JobAction.Update,
            Channel = offer.Channel,
            CreatedAt = DateTime.Now.ToString("o"),
            App = new JobApp
            {
                ExeName = VersionInfo.ExeName,
                InstallDir = UpdateEnvironment.InstallDir,
                Pid = Environment.ProcessId,
                CurrentVersion = AppVersion.Current,
                TargetVersion = offer.Version.ToString(),
                Mandatory = offer.IsMandatory,
            },
            Download = new JobDownload
            {
                Url = download,
                UrlFallback = downloadFallback,
                Sha256 = offer.Sha256,
                Size = offer.Size,
            },
            Paths = new JobPaths
            {
                WorkDir = workDir,
                AppLog = Path.Combine(_paths.Root, "app.log"),
            },
        };

        // 资源包一起带上（若有）—— 更新器按需处理，没有就跳过
        if (Status.Resources is { } res && res.Version > SemVer.ParseOrZero(_settings.Str("resources_version")))
        {
            job.Resources = new JobResources
            {
                Version = res.Version.ToString(),
                Download = new JobDownload
                {
                    Url = res.DownloadUrl,
                    UrlFallback = res.DownloadUrlFallback,
                    Sha256 = res.Sha256,
                    Size = res.Size,
                },
                InstallDir = Path.Combine(UpdateEnvironment.InstallDir, VersionInfo.AssetsDir),
                NotifyEvent = "Local\\ClassDailyLand2.Update.Resources.v1",
            };
        }

        job.Normalize();

        var jobPath = Path.Combine(workDir, "job.json");
        try
        {
            UpdateJson.Write(jobPath, job);
        }
        catch (Exception ex)
        {
            return UpdateLaunchResult.Fail("写更新任务失败：" + ex.Message);
        }

        // 4) 启动（装在 Program Files 时要提权）
        var needElevate = !UpdateEnvironment.IsElevated &&
                          !UpdateEnvironment.IsDirectoryWritable(UpdateEnvironment.InstallDir, out _);

        int? pid;
        try
        {
            pid = UpdateEnvironment.LaunchUpdater(updaterExe, jobPath, job, needElevate);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return UpdateLaunchResult.Fail("已取消（用户拒绝了管理员授权）");
        }
        catch (Exception ex)
        {
            return UpdateLaunchResult.Fail("启动更新器失败：" + ex.Message);
        }

        if (pid is null) return UpdateLaunchResult.Fail("更新器未能启动");

        _log?.Invoke($"更新已开始（目标 {offer.Version}），更新器 PID {pid}，任务文件 {jobPath}");
        UpdateEnvironment.AppendAppLog(Path.Combine(_paths.Root, "app.log"),
            $"[update] 开始更新到 {offer.Version}（{offer.Channel}），更新器 PID {pid}");

        return UpdateLaunchResult.Success(jobPath);
    }
}

/// <summary>更新结果文件（<c>last-update.json</c>）的读取与消费入口。</summary>
public static class LastUpdate
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassDailyLand2", "last-update.json");

    public static LastUpdateResult? Read(string? path = null)
    {
        try
        {
            return UpdateJson.Read<LastUpdateResult>(path ?? DefaultPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>消费一次结果（标记 consumed=true 写回），返回给用户看的那句话；没有待消费的返回 null。</summary>
    public static string? Consume(out LastUpdateResult? result, string? path = null)
    {
        result = Read(path);
        if (result is null || result.Consumed) return null;

        var message = result.Ok
            ? $"已更新到 {result.To}（{result.FinishedAt}）"
            : (string.IsNullOrWhiteSpace(result.Message) ? "上次更新未完成" : result.Message);

        try
        {
            result.Consumed = true;
            UpdateJson.Write(path ?? DefaultPath, result);
        }
        catch { /* 只是标记，失败也不影响提示 */ }

        return message;
    }
}
