using System.Diagnostics;
using System.Text.Json.Serialization;
using ClassDailyLand.Core.Managers;
using ClassDailyLand.Core.Storage;
using ClassDailyLand.UpdateContract;

namespace ClassDailyLand.App.Updates;

/// <summary>
/// 检查更新的编排：拉 version.json → 校验 → 比版本 → 产出 <see cref="UpdateOffer"/> → 写 job.json → 启动更新器。
///
/// 两个进程的分工：
///   · 本类（主程序）负责"发现 + 询问 + 起更新器"，全程在设置窗口里给反馈；
///   · 更新器负责"下载 + 解压 + 备份 + 替换 + 拉起"，主程序在那之前就退出了。
/// </summary>
public sealed class UpdateService
{
    readonly SettingsManager _settings;
    readonly DataPaths _paths;
    readonly AppConfig _config;
    readonly Action<string>? _log;

    int _inFlight;

    public UpdateService(SettingsManager settings, DataPaths paths, AppConfig config, Action<string>? log = null)
    {
        _settings = settings;
        _paths = paths;
        _config = config;
        _log = log;
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
            if (!silent) SetStatus(UpdateStage.Checking, "正在检查更新…", clear: true);

            var urls = _config.VersionUrls;
            var (file, source, error) = await UpdateSources.FetchAsync(urls, ct).ConfigureAwait(false);

            if (file is null)
            {
                var message = string.IsNullOrWhiteSpace(error) ? "无法连接更新源" : error;
                _log?.Invoke($"检查更新失败：{message}");
                if (!silent) SetStatus(UpdateStage.Failed, message);
                else Status = Status with { Stage = UpdateStage.Failed, Error = message };
                return Status;
            }

            var primaryUrl = urls.Count > 0 ? urls[0] : "";
            var fallbackUrl = urls.Count > 1 ? urls[1] : primaryUrl;

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

    // ─────────────────────────── 启动更新器 ───────────────────────────

    /// <summary>
    /// 用户点「立即更新」后调用：补下载兜底地址 → 写 job.json → 复制更新器到 temp → 启动。
    /// 成功返回 job 路径，主程序随后调 <c>AppController.Exit()</c> 退出。
    /// </summary>
    public async Task<UpdateLaunchResult> ApplyAsync(UpdateOffer offer, CancellationToken ct = default)
    {
        if (offer is null) return UpdateLaunchResult.Fail("没有要安装的更新");

        // 源码运行保护：直接替换开发目录会把源码弄乱
        if (UpdateEnvironment.IsSourceRun(UpdateEnvironment.InstallDir) && _settings.Bool("block_update_on_source"))
            return UpdateLaunchResult.Fail("检测到从源码运行，已按设置跳过自动更新（在「关于」页可关掉保护）");

        if (!UpdateEnvironment.IsDirectoryWritable(UpdateEnvironment.InstallDir, out var writeError) &&
            !UpdateEnvironment.IsElevated)
        {
            // 不可写但没关系 —— 上面会以 runas 启动更新器；这里只是把日志写清楚
            _log?.Invoke($"安装目录不可写（{writeError}），将以管理员权限运行更新器");
        }

        var jobId = DateTime.Now.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N")[..6];
        var workDir = Path.Combine(Path.GetTempPath(), "ClassDailyLand.Update", jobId);
        Directory.CreateDirectory(workDir);

        // 1) 更新器复制到 temp（避开"替换时锁住自己"）
        var updaterExe = UpdateEnvironment.StageUpdater(
            UpdateEnvironment.InstallDir, _config.UpdaterDir, workDir, out var stageError);
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
                ExeName = _config.ExeName,
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
                InstallDir = Path.Combine(UpdateEnvironment.InstallDir, _config.AssetsDir),
                NotifyEvent = "Local\\ClassDailyLand.Update.Resources.v1",
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
            pid = UpdateEnvironment.LaunchUpdater(updaterExe, jobPath, needElevate);
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
            "ClassDailyLand", "last-update.json");

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
