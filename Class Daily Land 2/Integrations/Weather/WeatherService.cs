using ClassDailyLand2.Core;
using ClassDailyLand2.Core.Managers;
using ClassDailyLand2.Plugin;
using System.Windows.Threading;

namespace ClassDailyLand2.App.Integrations.Weather;

/// <summary>
/// 实时天气编排：定位 → 拉 Open-Meteo → 每秒只做纯内存计算。
///
/// <para>线程纪律与 <c>DisasterService</c> 完全一致：网络在 <c>Task.Run</c> 里跑，
/// 结果一律 <c>Post</c> 回 UI 线程再落字段；UI 线程每秒调一次 <see cref="Tick"/>，
/// 内部按 <c>weather_refresh_sec</c> 决定要不要真的发请求。</para>
///
/// <para>两个显示位由 <c>AppController</c> 按设置与灾害预警状态挑：
/// 副岛（默认）或主窗插件区块；**出现气象预警时天气自动让位到主窗**。</para>
/// </summary>
public sealed class WeatherService : IDisposable
{
    /// <summary>定位失败后的重试间隔（秒）。定位不通不该每秒打一次外网。</summary>
    const double RetryLocateSec = 60;

    /// <summary>in-flight 超过这么久没回来就判死（请求 10 秒必然超时返回）。</summary>
    const double StuckSec = 30;

    readonly Dispatcher _dispatcher;
    readonly Logger _log;
    readonly SettingsManager _settings;
    readonly string _cachePath;
    readonly string _locCachePath;

    public bool Enabled { get; private set; } = true;
    /// <summary>sub / main / off。</summary>
    public string ShowOn { get; private set; } = "sub";
    public bool AutoLocate { get; private set; } = true;
    public int RefreshSec { get; private set; } = 900;
    public int IdleCollapseSec { get; private set; } = 300;
    public bool Animations { get; private set; } = true;

    // —— 配置里的回退位置（IP 挂了就用它）——
    string _province = "四川省", _city = "", _district = "";
    string _locKey = "";

    public WeatherLocation? Location { get; private set; }
    public WeatherSnapshot? Snapshot { get; private set; }
    public string LastError { get; private set; } = "";

    /// <summary>定位结果变了（AppController 据此让灾害预警重新取坐标）。</summary>
    public event Action? LocationChanged;

    bool _locating, _fetching;
    double _locatingAt, _fetchingAt;
    double _nextLocateTs, _nextFetchTs;
    bool _cacheDirty;

    /// <summary>
    /// 启动时先信任磁盘缓存（界面立刻有内容），10 秒后再静默重定位一次。
    /// ★ 不做这一步的话，`weather-location.json` 里的旧位置会一直用到 24 小时 ——
    ///   换了网络 / 换了城市 / 之前定错过，界面就一直错着，用户只能手动点「重新定位」。
    /// </summary>
    bool _revalidate = true;
    double _revalidateAt;

    public WeatherService(Dispatcher dispatcher, SettingsManager settings, Logger log, string dataRoot)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _log = log;
        _cachePath = Path.Combine(dataRoot, "weather-cache.json");
        _locCachePath = Path.Combine(dataRoot, "weather-location.json");
        LoadCache();
        Location = WeatherLocator.LoadCache(_locCachePath);
        _revalidateAt = Now + 10;
    }

    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // ————————————————————— 设置 —————————————————————

    public void ApplySettings()
    {
        Enabled = _settings.Bool("weather_enabled");
        var showOn = _settings.Str("weather_show_on");
        ShowOn = showOn is "main" or "off" ? showOn : "sub";
        AutoLocate = _settings.Bool("auto_locate");
        RefreshSec = Math.Clamp(_settings.Int("weather_refresh_sec"), 300, 3600);
        IdleCollapseSec = Math.Clamp(_settings.Int("weather_idle_collapse_sec"), 0, 3600);
        Animations = _settings.Bool("weather_animations");

        _province = _settings.Str("disaster_province");
        _city = _settings.Str("disaster_city");
        _district = _settings.Str("disaster_district");

        // 位置相关的输入没变就不作废定位 —— 滑块/开关每次改动都会走到这里，
        // 无脑清 Location 会让用户每动一下设置就重新打一次外网。
        // ★ 第一次调用（_locKey 还是空串）必须直接接受现状：
        //   构造时刚从 weather-location.json 读出来的 24 小时缓存，不能被这次"发现键变了"清掉，
        //   否则每次启动都白打一遍 IP。
        var key = $"{AutoLocate}|{_province}|{_city}|{_district}";
        if (_locKey.Length == 0) _locKey = key;
        else if (key != _locKey)
        {
            _locKey = key;
            Location = null;
            _nextLocateTs = 0;
            _nextFetchTs = 0;
        }
        else if (Snapshot is not null && _nextFetchTs > 0)
        {
            // 刷新间隔改小了，下次该拉的时间点跟着往前挪（_nextFetchTs 为 0 表示"位置刚作废、待重拉"，别碰）
            _nextFetchTs = Snapshot.FetchedTs + RefreshSec;
        }

        _log.Trace($"[天气] 应用设置：开关={Enabled}，显示位置={ShowOn}，自动定位={AutoLocate}，" +
                   $"刷新间隔 {RefreshSec}s，无操作收起 {IdleCollapseSec}s，动画={Animations}，" +
                   $"回退位置={_province}/{_city}/{_district}");
    }

    // ————————————————————— 每秒 tick —————————————————————

    /// <summary>由 AppController 的 1 秒 tick 驱动。</summary>
    public void Tick()
    {
        if (!Enabled) return;
        var now = Now;

        // 启动 10 秒后静默重定位一次：缓存只用来"先有个内容"，不能当成锁死的答案
        if (_revalidate && now >= _revalidateAt)
        {
            _revalidate = false;
            if (AutoLocate && Location is not null && !_locating) StartLocate(now);
        }

        if (Location is null && !_locating && now >= _nextLocateTs)
            StartLocate(now);

        // in-flight 卡死兜底：请求 10 秒必然回来，30 秒还挂着说明异常逃出去了
        if (_locating && now - _locatingAt >= StuckSec)
        {
            _log.Warn("[天气] 定位请求未返回，强制复位");
            _locating = false;
            _nextLocateTs = now + RetryLocateSec;
        }
        if (_fetching && now - _fetchingAt >= StuckSec)
        {
            _log.Warn("[天气] 天气请求未返回，强制复位");
            _fetching = false;
            _nextFetchTs = now + RetryLocateSec;
        }

        if (Location is not null && !_fetching && now >= _nextFetchTs)
            StartFetch(now);
    }

    // ————————————————————— 定位 —————————————————————

    void StartLocate(double now)
    {
        if (!AutoLocate)
        {
            var local = WeatherLocator.FromCityTable(_province, _city, _district);
            if (local is null)
            {
                LastError = $"查不到 {_province}/{_city} 的坐标";
                _nextLocateTs = now + RetryLocateSec;
                return;
            }
            OnLocated(local);
            return;
        }

        _locating = true;
        _locatingAt = now;
        _log.Trace("[天气] 开始 IP 定位（四个源并行：ip-api / ipapi.co / ipip / pconline）");

        _ = Task.Run(async () =>
        {
            var (loc, error) = await WeatherLocator
                .LocateAsync(msg => _log.Trace(msg)).ConfigureAwait(false);

            // IP 三个源全挂 → 退回手选省市区查表（与手动模式同一口径）
            var fallback = loc ?? WeatherLocator.FromCityTable(_province, _city, _district);

            Post(() =>
            {
                _locating = false;
                if (fallback is null)
                {
                    LastError = string.IsNullOrWhiteSpace(error) ? "定位失败" : error;
                    _nextLocateTs = Now + RetryLocateSec;
                    _log.Warn($"[天气] 定位失败，且手选省市也查不到坐标：{LastError}");
                    return;
                }
                if (loc is null)
                    _log.Warn($"[天气] IP 定位失败（{error}），已回退到手选省市查表");
                OnLocated(fallback);
            });
        });
    }

    void OnLocated(WeatherLocation loc)
    {
        Location = loc;
        LastError = "";
        _nextFetchTs = 0;              // 位置变了，天气要立刻重拉
        _cacheDirty = true;
        WeatherLocator.SaveCache(_locCachePath, loc);
        _log.Trace($"[天气] 定位成功：{loc.Province}/{loc.City} {loc.Lat:0.####},{loc.Lon:0.####}（{loc.Source}）");
        LocationChanged?.Invoke();
    }

    // ————————————————————— 天气 —————————————————————

    void StartFetch(double now)
    {
        var loc = Location;
        if (loc is null) return;

        _fetching = true;
        _fetchingAt = now;
        var url = WeatherClient.ForecastUrl(loc.Lat, loc.Lon);
        _log.Trace($"[天气] 拉取实况：{url}");

        _ = Task.Run(async () =>
        {
            var (doc, error) = await WeatherHttp.FetchJsonAsync(url).ConfigureAwait(false);
            if (doc is null)
            {
                Post(() =>
                {
                    _fetching = false;
                    LastError = error;
                    _nextFetchTs = Now + RetryLocateSec;   // 失败退避，别每秒重试
                    _log.Warn($"[天气] 拉取失败：{error}");
                });
                return;
            }

            WeatherSnapshot? snap;
            using (doc) snap = WeatherClient.Parse(doc.RootElement, loc, Now);

            Post(() =>
            {
                _fetching = false;
                if (snap is null)
                {
                    LastError = "响应里没有 current 字段";
                    _nextFetchTs = Now + RetryLocateSec;
                    _log.Warn("[天气] 拉取成功但解析不出实况");
                    return;
                }
                Snapshot = snap;
                LastError = "";
                _nextFetchTs = snap.FetchedTs + RefreshSec;
                _cacheDirty = true;
                _log.Trace($"[天气] 实况更新：{snap.TempText} {snap.Text}（{snap.Place}）");
            });
        });
    }

    void Post(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    /// <summary>设置页的「重新定位」：丢掉当前坐标，下一秒重新走一遍 IP 源链。</summary>
    public void Relocate()
    {
        Location = null;
        _nextLocateTs = 0;
        _log.Trace("[天气] 用户触发重新定位");
    }

    /// <summary>设置页的「立即刷新」：不等 refresh 间隔，下一秒就拉一次实况。</summary>
    public void RefreshNow()
    {
        _nextFetchTs = 0;
        _log.Trace("[天气] 用户触发立即刷新");
    }

    // ————————————————————— 显示位 —————————————————————

    /// <summary>副岛内容（标题=温度，正文=天气词）。由 AppController 决定这一秒要不要用它。</summary>
    public (string Title, string Value)? SubIsland =>
        !Enabled || ShowOn != "sub" || Snapshot is null ? null : (Snapshot.TempText, Snapshot.Text);

    /// <summary>主窗插件区块：标题 + 两行详情 + 天气动画（BoardArt）。</summary>
    public BoardSection? BoardSection
    {
        get
        {
            if (!Enabled || Snapshot is null) return null;
            var s = Snapshot;
            return new BoardSection("天气", s.Lines,
                new BoardArt("weather", s.Code.ToString(), s.Temp, s.IsDay, Animations));
        }
    }

    /// <summary>主窗标题行徽标：「24° 晴」。</summary>
    public BoardBadge? BoardBadge =>
        !Enabled || Snapshot is null ? null : new BoardBadge($"{Snapshot.TempText} {Snapshot.Text}");

    /// <summary>设置页状态卡片。</summary>
    public string StatusText
    {
        get
        {
            var lines = new List<string>();
            if (Location is null)
                lines.Add(AutoLocate ? "正在定位…" : "正在按手选省市查坐标…");
            else
                lines.Add($"位置 {Location.Province}/{Location.City}/{Location.District} · " +
                          $"{Location.Lat:0.##},{Location.Lon:0.##}" +
                          $"（{(Location.Source == "city-table" ? "手选省市查表" : "IP 定位 " + Location.Source)}）");

            if (Snapshot is null)
                lines.Add(string.IsNullOrEmpty(LastError) ? "还没有天气数据" : $"天气获取失败：{LastError}");
            else
            {
                var age = (int)Math.Max(0, Now - Snapshot.FetchedTs);
                lines.Add($"{Snapshot.TempText} {Snapshot.Text} · 体感 {Math.Round(Snapshot.Feels):0}° · 湿度 {Snapshot.Humidity:0}%");
                lines.Add($"{Snapshot.Place} · {WeatherCodes.WindDirection(Snapshot.WindDegrees)} {WeatherCodes.WindScale(Snapshot.WindSpeedKmh)} 级" +
                          $" · {age}s 前更新");
                if (!string.IsNullOrEmpty(LastError)) lines.Add($"最近一次失败：{LastError}");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    // ————————————————————— 缓存 —————————————————————

    void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var dto = System.Text.Json.JsonSerializer.Deserialize<WeatherCacheDto>(File.ReadAllText(_cachePath));
            if (dto?.Snap is null) return;
            Snapshot = dto.Snap;
            _nextFetchTs = dto.Snap.FetchedTs + RefreshSec;
            _log.Trace($"[天气] 载入缓存：{dto.Snap.TempText} {dto.Snap.Text}（{dto.Snap.Place}）");
        }
        catch (Exception ex)
        {
            _log.Warn("读天气缓存失败：" + ex.Message);
        }
    }

    void SaveCacheIfDue()
    {
        if (!_cacheDirty) return;
        _cacheDirty = false;
        try
        {
            if (Snapshot is not null)
                Core.Storage.AtomicJson.Write(_cachePath, new WeatherCacheDto { Snap = Snapshot });
        }
        catch (Exception ex)
        {
            _log.Warn("写天气缓存失败：" + ex.Message);
        }
    }

    /// <summary>由 AppController 在每秒 Tick 末尾调，落盘频率跟着数据刷新走（不是每秒写）。</summary>
    public void Flush() => SaveCacheIfDue();

    sealed class WeatherCacheDto
    {
        public WeatherSnapshot? Snap { get; set; }
    }

    public void Dispose() { }
}
