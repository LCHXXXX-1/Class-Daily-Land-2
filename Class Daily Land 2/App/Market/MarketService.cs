using ClassDailyLand2.App.Plugins;
using ClassDailyLand2.Core;
using ClassDailyLand2.Core.Market;
using ClassDailyLand2.Core.Storage;

namespace ClassDailyLand2.App.Market;

/// <summary>市场页的整体状态。</summary>
public enum MarketStage
{
    /// <summary>还没拉过（页面还没打开）。</summary>
    Idle = 0,
    /// <summary>正在拉索引 / 下载安装包。</summary>
    Loading = 1,
    /// <summary>有可用的索引（可能来自缓存）。</summary>
    Ready = 2,
    /// <summary>拉取失败；<see cref="MarketStatus.FromCache"/> 为 true 时列表仍可看。</summary>
    Failed = 3,
}

/// <summary>市场状态快照（界面只读它，不自己去碰网络）。</summary>
public sealed record MarketStatus(
    MarketStage Stage,
    string Message,
    DateTime? FetchedAt,
    string Source,
    bool FromCache,
    IReadOnlyList<string> Warnings)
{
    public static MarketStatus Idle { get; } =
        new(MarketStage.Idle, "", null, "", false, Array.Empty<string>());

    public string FreshnessText
    {
        get
        {
            if (FetchedAt is null) return "";
            var age = DateTime.Now - FetchedAt.Value;
            var when = age.TotalMinutes < 1 ? "刚刚"
                     : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} 分钟前"
                     : age.TotalDays < 1 ? $"{(int)age.TotalHours} 小时前"
                     : $"{(int)age.TotalDays} 天前";
            return $"{Source} · {when}";
        }
    }
}

/// <summary>
/// 插件市场的编排层：拉索引 → 算状态 → 下载校验 → 交给 PluginManager 安装。
///
/// <para><b>分工</b>：<c>Core/Market</c> 里是纯逻辑（解析 / 判定 / 下载校验，可单测），
/// 这里只做装配与状态通知，界面（<c>PluginsPage</c>）只读 <see cref="Status"/> 与
/// <see cref="Views"/>。安装本身仍然走 <see cref="PluginManager.Install"/>，
/// 不另写一套覆盖/回滚。</para>
/// </summary>
public sealed class MarketService
{
    readonly PluginManager _plugins;
    readonly Logger _log;
    readonly ClassDailyLand2.Core.Managers.SettingsManager _settings;

    int _inFlight;
    MarketIndex? _index;
    List<MarketView> _views = new();

    public MarketService(PluginManager plugins, DataPaths paths, Logger log, ClassDailyLand2.Core.Managers.SettingsManager settings)
    {
        _plugins = plugins;
        _log = log;
        _settings = settings;
        CachePath = Path.Combine(paths.Root, "market-cache.json");

        // 插件装/卸/启停之后，市场页的按钮状态要跟着变（"安装"→"已安装"）
        _plugins.Changed += OnInstalledPluginsChanged;

        // 上次崩溃留下的半截安装包：启动清一次，别一直占着 %TEMP%
        MarketDownloader.CleanupStale();
    }

    public string CachePath { get; }

    /// <summary>
    /// 索引地址，按「插件市场更新源」设置排序：auto = GitHub 优先、失败自动换 Gitee；
    /// github / gitee = 只走单源。每次取都重算，改完设置立刻生效。
    /// </summary>
    public IReadOnlyList<string> IndexUrls =>
        BuildUrls(VersionInfo.MarketIndexUrlFallback, VersionInfo.MarketIndexUrl);

    /// <summary>安装包的拼接根，与 <see cref="IndexUrls"/> 一一对应（顺序必须一致）。</summary>
    public IReadOnlyList<string> RawRoots =>
        BuildUrls(VersionInfo.MarketRawRootFallback, VersionInfo.MarketRawRoot);

    /// <summary>
    /// 把「GitHub / Gitee 两个地址」按设置排成有序列表。
    /// 两个参数的顺序固定为 (github, gitee) —— 与 <see cref="VersionInfo"/> 里谁叫主源谁叫备源无关，
    /// 免得读代码的人被 "Fallback" 这个词绕进去。
    /// </summary>
    IReadOnlyList<string> BuildUrls(string github, string gitee)
    {
        var preference = (_settings.Str("market_source") ?? "").Trim().ToLowerInvariant();
        var order = preference switch
        {
            "github" => new[] { github },
            "gitee" => new[] { gitee },
            _ => new[] { github, gitee },
        };

        return order
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public MarketStatus Status { get; private set; } = MarketStatus.Idle;

    /// <summary>当前视图（已按「可更新 → 未安装 → …」排好序）。</summary>
    public IReadOnlyList<MarketView> Views => _views;

    /// <summary>有几个可以更新（给"已安装"页和侧栏提示用）。</summary>
    public int UpdateCount => _views.Count(v => v.State == MarketState.Update);

    /// <summary>市场或本机插件变化后触发（UI 线程之外调用时订阅方自行切线程）。</summary>
    public event Action? Changed;

    // ————————————————————— 拉索引 —————————————————————

    /// <summary>
    /// 拉取市场索引。<paramref name="force"/> = false 且缓存未过期时直接用缓存（不发请求）。
    /// 失败但有缓存时：仍然显示缓存列表，状态标成"失败"并说明。
    /// </summary>
    /// <returns>
    /// 结束后是否处于"有可用索引"的状态（<see cref="MarketStage.Ready"/>）。
    /// 上一次还没跑完就再次调用会立刻返回 false —— 「立即同步」按钮据此给"正在同步中"。
    /// </returns>
    public async Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _inFlight, 1) == 1) return false;

        try
        {
            var cache = MarketCache.Load(CachePath);

            if (!force && MarketCache.IsFresh(cache))
            {
                Apply(cache!.Index!, new MarketStatus(
                    MarketStage.Ready, "已是最新", cache.FetchedAt, cache.Source, FromCache: true,
                    Array.Empty<string>()));
                _log.Info($"[市场] 使用缓存索引（{cache.Source}，{cache.FetchedAt:MM-dd HH:mm}，{cache.Index!.Plugins.Count} 个插件）");
                return true;
            }

            SetStatus(new MarketStatus(MarketStage.Loading, "正在获取插件市场…",
                Status.FetchedAt, Status.Source, Status.FromCache, Array.Empty<string>()));

            var fetch = await MarketClient.FetchAsync(IndexUrls, ct).ConfigureAwait(false);

            if (fetch.Index is not null)
            {
                MarketCache.Save(CachePath, fetch);
                Apply(fetch.Index, new MarketStatus(
                    MarketStage.Ready,
                    fetch.Warnings.Count == 0 ? "已是最新" : $"已更新（{fetch.Warnings.Count} 条索引有问题已跳过）",
                    fetch.FetchedAt, fetch.Source, FromCache: false, fetch.Warnings));

                _log.Info($"[市场] 索引来自 {fetch.Source}：{fetch.Index.Plugins.Count} 个插件" +
                          (fetch.Warnings.Count > 0 ? $"，跳过 {fetch.Warnings.Count} 条" : ""));
                foreach (var warning in fetch.Warnings) _log.Warn("[市场] " + warning);
                return true;
            }

            // 拿到索引失败：有缓存就先给用户看旧的，别给一个空列表
            if (cache is not null)
            {
                Apply(cache.Index!, new MarketStatus(
                    MarketStage.Failed,
                    $"无法连接市场（{fetch.Error}）。下面显示的是 {cache.FetchedAt:MM-dd HH:mm} 的缓存。",
                    cache.FetchedAt, cache.Source, FromCache: true, Array.Empty<string>()));
                _log.Warn($"[市场] 拉取失败，改用缓存：{fetch.Error}");
                return false;
            }

            SetStatus(new MarketStatus(MarketStage.Failed,
                string.IsNullOrWhiteSpace(fetch.Error) ? "无法连接插件市场" : fetch.Error,
                null, "", false, Array.Empty<string>()));
            _log.Warn("[市场] 拉取失败且没有缓存：" + fetch.Error);
            return false;
        }
        catch (OperationCanceledException)
        {
            SetStatus(Status with { Stage = MarketStage.Idle, Message = "" });
            return false;
        }
        catch (Exception ex)
        {
            SetStatus(new MarketStatus(MarketStage.Failed, "插件市场出错：" + ex.Message, null, "", false, Array.Empty<string>()));
            _log.Warn("[市场] 异常：" + ex);
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    void Apply(MarketIndex index, MarketStatus status)
    {
        _index = index;
        RebuildViews();
        Status = status;
        Changed?.Invoke();
    }

    void SetStatus(MarketStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }

    /// <summary>
    /// 重算视图。本机插件列表变了（装/卸载/禁用）也要调一次 ——
    /// 否则市场页会一直显示"安装"按钮。
    /// </summary>
    public void RebuildViews()
    {
        if (_index is null) return;

        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in _plugins.Plugins)
        {
            if (!string.IsNullOrWhiteSpace(info.Id)) installed[info.Id] = info.Version;
        }

        _views = MarketPolicy.Build(
            _index,
            installed,
            AppController.Version,
            PluginManager.SdkVersion,
            PluginManager.ApiVersion.Major).ToList();
    }

    /// <summary>插件装好/卸载后调用：重算视图并广播（不重新联网）。</summary>
    public void OnInstalledPluginsChanged()
    {
        RebuildViews();
        Changed?.Invoke();
    }

    // ————————————————————— 安装 / 更新 —————————————————————

    /// <summary>
    /// 下载并安装一个市场条目。<paramref name="overwrite"/> = false 时若已装会失败
    /// （由界面提示"覆盖安装"）。真正落盘走 <see cref="PluginManager.Install"/>。
    /// </summary>
    public async Task<(bool Ok, string Message)> InstallAsync(
        MarketView view,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (view is null) return (false, "没有选中插件。");
        if (!view.CanInstall) return (false, $"「{view.Entry.Name}」当前状态不能安装（{view.ActionText}）。");

        SetStatus(Status with { Stage = MarketStage.Loading, Message = $"正在下载「{view.Entry.Name}」…" });

        MarketDownload download;
        try
        {
            download = await MarketDownloader
                .DownloadAsync(view.Entry, RawRoots, progress, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetStatus(Status with { Stage = MarketStage.Ready, Message = "已取消" });
            return (false, "已取消");
        }

        if (!download.Ok || download.Path is null)
        {
            SetStatus(Status with { Stage = MarketStage.Ready, Message = "" });
            return (false, download.Error ?? "下载失败");
        }

        try
        {
            // 覆盖安装：更新/降级都必须 overwrite，否则 PluginInstaller 会以"已装过"失败
            var outcome = _plugins.Install(download.Path, overwrite: true);
            _log.Info($"[市场] 安装 {view.Entry.Id} v{view.Entry.Version}（来自 {download.Source}）：" +
                      (outcome.Ok ? "成功" : outcome.Error));

            if (!outcome.Ok)
            {
                SetStatus(Status with { Stage = MarketStage.Ready, Message = "" });
                return (false, outcome.Error ?? "安装失败");
            }

            OnInstalledPluginsChanged();
            SetStatus(Status with { Stage = MarketStage.Ready, Message = "" });
            return (true, $"「{view.Entry.Name}」v{view.Entry.Version} 已安装并启用。");
        }
        finally
        {
            // 装完（成功或失败）都要清临时包
            MarketDownloader.CleanupPackage(download.Path);
        }
    }
}
