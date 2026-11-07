using ClassDailyLand2.App.Integrations.Weather;
using ClassDailyLand2.Core;
using ClassDailyLand2.Core.Managers;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClassDailyLand2.App.Integrations.Disaster;

/// <summary>
/// 灾害预警集成：地震预警（ICL 主源 + Wolfx 备源）与气象灾害（中央气象台）。
///
/// <para>移植自 Python 版 <c>builtin/disaster/main.py</c>，显示位换成这里的形式：</para>
/// <list type="bullet">
///   <item><b>主岛</b>：地震时用「红色倒计时条 + 灾害预警文案」接管（复用上课倒计时那套渲染）；</item>
///   <item><b>副岛</b>：横波倒计时 / 气象预警摘要；</item>
///   <item><b>下拉面板</b>：震中、震级、发震时刻、距离、烈度、最近县，或气象预警清单；</item>
///   <item><b>提醒</b>：托盘气泡（同一事件按 uid 去重 + 节流，等级升级会再提醒一次）。</item>
/// </list>
///
/// 所有网络请求 10 秒超时、失败指数退避；数据留在内存 + cache.json，重启后先显示缓存。
/// </summary>
public sealed class DisasterService : IDisposable
{
    const double CacheSaveIntervalSec = 30;

    readonly Dispatcher _dispatcher;
    readonly Logger _log;
    readonly SettingsManager _settings;
    readonly string _cachePath;
    readonly Func<bool> _isInClass;
    readonly Action<string, string> _notify;

    /// <summary>
    /// 自动定位的坐标来源（由天气模块提供，惰性取 —— IP 定位是异步的，
    /// 构造时可能还没有结果）。null 表示天气模块没接上或开关是关的。
    /// </summary>
    readonly Func<WeatherLocation?>? _autoLocation;

    // ————————————————————— 配置 —————————————————————
    public bool Enabled { get; private set; } = true;
    public bool QuakeEnabled { get; private set; } = true;
    public bool WeatherEnabled { get; private set; } = true;
    public string Province { get; private set; } = "四川省";
    public string City { get; private set; } = "";
    public string District { get; private set; } = "";
    public double? Lat { get; private set; }
    public double? Lon { get; private set; }

    /// <summary>
    /// 当前坐标是哪来的：IP 定位 / 手选省市查表 / 手动经纬度。
    /// IP 是市级精度，拿它算地震倒计时会偏 —— 状态卡片必须一直把这个标出来。
    /// </summary>
    public string LocationSource { get; private set; } = "";

    /// <summary>是否走自动定位（与天气共用同一个开关 <c>auto_locate</c>）。</summary>
    public bool AutoLocate { get; private set; } = true;
    public double Threshold { get; private set; } = 2.0;
    public int QuakeIntervalSec { get; private set; } = 5;
    public int WeatherIntervalSec { get; private set; } = 60;
    public bool NotifyEnabled { get; private set; } = true;
    public bool BannerEnabled { get; private set; } = true;
    public int SmallHoldSec { get; private set; } = 5;
    public bool ShowStatusCard { get; private set; } = true;

    /// <summary>用户点过「确认」的事件（uid + revision），不再弹警报条。</summary>
    (string Uid, int Revision)? _dismissed;

    // ————————————————————— 状态 —————————————————————
    readonly object _gate = new();
    Dictionary<string, QuakeEvent> _quakes = new(StringComparer.Ordinal);
    Dictionary<string, WeatherEvent> _weather = new(StringComparer.Ordinal);
    readonly Dictionary<string, (double Ts, string Level)> _notified = new(StringComparer.Ordinal);
    readonly Dictionary<string, double> _firstSeen = new(StringComparer.Ordinal);

    /// <summary>已经补过一次 ICL 详情的 uid。详情接口对老事件常返回空 counties，
    /// 不记下来的话每轮轮询都会为同一条事件重发请求。</summary>
    readonly HashSet<string> _detailTried = new(StringComparer.Ordinal);

    /// <summary>"坐标没设置"只喊一次的记号（这个分支每秒都会走到）。</summary>
    bool _warnedNoCoords;

    readonly SourceStatus _icl = new("icl", "ICL 地震");
    readonly SourceStatus _wolfx = new("wolfx", "Wolfx 地震");
    readonly SourceStatus _nmc = new("nmc", "中央气象台");

    bool _iclInFlight, _wolfxInFlight, _nmcInFlight;
    double _iclStartedAt, _wolfxStartedAt, _nmcStartedAt;
    double _lastIclPoll, _lastWeatherPoll, _lastCacheSave;
    int _wolfxFailStreak;
    bool _cacheDirty;

    /// <summary>in-flight 超过这么久还没回来就判死（请求本身 10 秒必然超时返回）。</summary>
    const double StuckInFlightSec = 30;

    public QuakeEvaluation? Focus { get; private set; }
    public IReadOnlyList<object> Others { get; private set; } = Array.Empty<object>();
    public IslandEmergency? Emergency { get; private set; }

    public DisasterService(
        Dispatcher dispatcher,
        SettingsManager settings,
        Logger log,
        string cachePath,
        Func<bool> isInClass,
        Action<string, string> notify,
        Func<WeatherLocation?>? autoLocation = null)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _log = log;
        _cachePath = cachePath;
        _isInClass = isInClass;
        _notify = notify;
        _autoLocation = autoLocation;
        LoadCache();
    }

    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // ————————————————————— 设置 —————————————————————

    public void ApplySettings()
    {
        Enabled = _settings.Bool("disaster_enabled");
        QuakeEnabled = _settings.Bool("disaster_quake_enabled");
        WeatherEnabled = _settings.Bool("disaster_weather_enabled");
        NotifyEnabled = _settings.Bool("disaster_notify_enabled");
        BannerEnabled = _settings.Bool("disaster_banner_enabled");
        ShowStatusCard = _settings.Bool("disaster_show_status_card");
        Province = _settings.Str("disaster_province");
        City = _settings.Str("disaster_city");
        District = _settings.Str("disaster_district");
        Threshold = Math.Clamp(_settings.Int("disaster_threshold_x10"), 0, 120) / 10.0;
        QuakeIntervalSec = Math.Clamp(_settings.Int("disaster_quake_interval_sec"), 3, 120);
        WeatherIntervalSec = Math.Clamp(_settings.Int("disaster_weather_interval_sec"), 30, 3600);
        SmallHoldSec = Math.Clamp(_settings.Int("disaster_small_hold_sec"), 0, 120);

        // —— 位置：自动定位（IP）优先；开关关掉或还没定位出来时走下面原来那套 ——
        AutoLocate = _settings.Bool("auto_locate");
        var auto = AutoLocate ? _autoLocation?.Invoke() : null;
        if (auto is not null)
        {
            Lat = auto.Lat;
            Lon = auto.Lon;
            // 省名只做一道保险：英文省名没法用来过滤中央气象台，退回手选省
            Province = Cities.Provinces.Contains(auto.Province)
                ? auto.Province
                : _settings.Str("disaster_province");
            // 市/区县：定位器（WeatherLocator）已经把名字归一成城市表里的标准写法了，
            // 这里**不再按表校验** —— 只要差一个字就把市清空，用户看到的就是"连市都没有"。
            // 这道 Contains 只留着挡 ipapi.co 那种英文市名。
            City = string.IsNullOrEmpty(auto.City) || Cities.CitiesOf(Province).Contains(auto.City)
                ? auto.City
                : "";
            District = auto.District;
            LocationSource = auto.Source == "city-table"
                ? "手选省市查表"
                : $"IP 定位（{auto.Source}）";
        }
        else if (double.TryParse(_settings.Str("disaster_lat"), System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out var la) &&
                 double.TryParse(_settings.Str("disaster_lon"), System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out var lo) &&
                 la is >= -90 and <= 90 && lo is >= -180 and <= 180)
        {
            Lat = la;
            Lon = lo;
            LocationSource = "手动经纬度";
        }
        else
        {
            // 按 省/市/区县 查表；只选了省没选市时，退到省会（省的第一个地级市）。
            // 没有这一步，只选「四川省」的用户会拿不到坐标，倒计时永远算不出来。
            var coord = Cities.CoordOf(Province, City, District);
            if (coord is null && !string.IsNullOrWhiteSpace(Province))
            {
                var capital = Cities.CitiesOf(Province).FirstOrDefault();
                if (capital is not null) coord = Cities.CoordOf(Province, capital);
            }
            Lat = coord?.Lat;
            Lon = coord?.Lon;
            LocationSource = "手选省市查表";
        }

        _icl.Enabled = QuakeEnabled;
        _wolfx.Enabled = QuakeEnabled;
        _nmc.Enabled = WeatherEnabled && !string.IsNullOrEmpty(Province) && Province != Cities.NoWeather;

        // ★ 总开关关掉时必须当场清掉红条与焦点：Tick() 第一行是 if (!Enabled) return，
        //   之后再也不会跑到 UpdateEmergency 里那句 Emergency = null —— 不在这里清，
        //   正在显示的红色倒计时条会一直挂在主岛上直到重启（关红条/关地震分项都没这问题）。
        if (!Enabled)
        {
            Emergency = null;
            Focus = null;
            Others = Array.Empty<object>();
            _weatherFocus = null;
        }

        _log.Trace($"[灾害] 应用设置：总开关={Enabled}，地震={QuakeEnabled}（{QuakeIntervalSec}s），气象={WeatherEnabled}（{WeatherIntervalSec}s），" +
                   $"位置={Province}/{City}/{District}，" +
                   $"坐标={(Lat is null ? "未设置" : $"{Lat:0.####},{Lon:0.####}（{LocationSource}）")}，" +
                   $"自动定位={AutoLocate}，阈值={Threshold:0.#}，红条={BannerEnabled}，提醒={NotifyEnabled}");
    }

    // ————————————————————— 每秒 tick —————————————————————

    /// <summary>由 AppController 的 1 秒 tick 驱动。</summary>
    public void Tick()
    {
        if (!Enabled) return;
        var now = Now;

        PollIclIfDue(now);
        PollWolfxIfDue(now);
        PollWeatherIfDue(now);

        RefreshFocus(now);
        SaveCacheIfDue(now);
    }

    // ————————————————————— 轮询 —————————————————————

    /// <summary>
    /// in-flight 卡死兜底，返回 true 表示"可以继续往下走"（本来就没在飞，或已强制复位）。
    ///
    /// <para>请求本身 10 秒必然超时返回，所以 30 秒还挂着只可能是异常从 Task 里逃出去了
    /// （<see cref="DisasterSources.FetchJsonAsync"/> 只接了 HttpRequest / Json / IO / 取消几类）。
    /// 那种情况下 <c>_xxxInFlight</c> 永远停在 true，**该数据源再也不会被轮询，直到重启** ——
    /// 对预警应用来说，静默停摆比报错严重得多。</para>
    /// </summary>
    bool ReclaimStuck(ref bool flag, ref double startedAt, double now, SourceStatus status)
    {
        if (!flag) return true;
        if (now - startedAt < StuckInFlightSec) return false;

        _log.Warn($"[灾害] {status.Label} 上一次请求未返回（超过 {StuckInFlightSec:0}s），强制复位继续轮询");
        status.OnFail("上一次请求未返回", now);
        flag = false;
        return true;
    }

    void PollIclIfDue(double now)
    {
        if (!QuakeEnabled) return;
        if (!ReclaimStuck(ref _iclInFlight, ref _iclStartedAt, now, _icl)) return;
        if (now - _lastIclPoll < QuakeIntervalSec || !_icl.Ready(now)) return;

        _lastIclPoll = now;
        _iclInFlight = true;
        _iclStartedAt = now;
        var startMs = (long)((now - 7200) * 1000);
        var url = string.Format(DisasterSources.IclListUrl, startMs);
        _log.Trace($"[灾害] 轮询 ICL 主源（间隔 {QuakeIntervalSec}s）：{url}");

        _ = Task.Run(async () =>
        {
            var (doc, error) = await DisasterSources.FetchJsonAsync(url).ConfigureAwait(false);
            if (doc is null)
            {
                _log.Warn($"[灾害] ICL 主源请求失败：{error}");
                Post(() =>
                {
                    _icl.OnFail(error, Now);
                    _iclInFlight = false;
                });
                return;
            }

            using (doc)
            {
                var events = DisasterSources.ParseIclEvents(doc.RootElement);
                // 明细不在这儿打：每轮轮询都会把同一批事件原样推过来，5 秒一行足以把 app.log 冲爆。
                // 真正新增 / 更新的那几条由 IngestQuakes 用 MergeQuakes 已经算好却一直被丢掉的
                // changed 列表来记 —— 没变化时一个字都不写。
                _log.Trace($"[灾害] ICL 本轮解析出 {events.Count} 条事件");
                Post(() =>
                {
                    _icl.OnOk(Now);
                    _iclInFlight = false;
                    _wolfxFailStreak = 0;
                    IngestQuakes(events);
                });
            }
        });
    }

    void PollWolfxIfDue(double now)
    {
        // ICL 最近一次失败（_icl.Ok == false）就改用 Wolfx 顶上，与 Python 版一致的降级策略。
        // 注：_wolfxFailStreak 那半句实际是冗余的 —— 它 ≥2 时 ICL 必然还没成功过
        // （ICL 成功会把它清 0），此时 _icl.Ok 一定是 false。留着只为读起来顺，别当成
        // "Wolfx 失败两次也要继续查"来改。
        if (!QuakeEnabled) return;
        if (!ReclaimStuck(ref _wolfxInFlight, ref _wolfxStartedAt, now, _wolfx)) return;
        if (_wolfxFailStreak < 2 && _icl.Ok != false) return;
        if (now - _lastIclPoll < QuakeIntervalSec || !_wolfx.Ready(now)) return;

        _lastIclPoll = now;
        _wolfxInFlight = true;
        _wolfxStartedAt = now;
        _log.Trace($"[灾害] ICL 连续失败，改轮询 Wolfx 备源（{DisasterSources.WolfxEewUrls.Count} 个端点）");

        _ = Task.Run(async () =>
        {
            var all = new List<QuakeEvent>();
            var errors = new List<string>();
            foreach (var (key, url) in DisasterSources.WolfxEewUrls)
            {
                var (doc, error) = await DisasterSources.FetchJsonAsync(url).ConfigureAwait(false);
                if (doc is null)
                {
                    errors.Add($"{key}: {error}");
                    continue;
                }
                using (doc)
                {
                    all.AddRange(DisasterSources.ParseWolfxEvents(doc.RootElement, key));
                }
            }

            if (all.Count == 0 && errors.Count == DisasterSources.WolfxEewUrls.Count)
            {
                _log.Warn($"[灾害] Wolfx 四个端点全部失败（连续第 {_wolfxFailStreak + 1} 次）：{string.Join("；", errors)}");
                Post(() =>
                {
                    _wolfx.OnFail(string.Join("; ", errors), Now);
                    _wolfxFailStreak++;
                    _wolfxInFlight = false;
                });
                return;
            }

            Post(() =>
            {
                _wolfx.OnOk(Now);
                _wolfxInFlight = false;
                _wolfxFailStreak = 0;
                _log.Trace($"[灾害] Wolfx 本轮 {all.Count} 条事件（{errors.Count} 个端点失败）");
                IngestQuakes(all);
            });
        });
    }

    void PollWeatherIfDue(double now)
    {
        if (!WeatherEnabled) return;
        if (string.IsNullOrEmpty(Province) || Province == Cities.NoWeather) return;
        if (!ReclaimStuck(ref _nmcInFlight, ref _nmcStartedAt, now, _nmc)) return;
        if (now - _lastWeatherPoll < WeatherIntervalSec || !_nmc.Ready(now)) return;

        _lastWeatherPoll = now;
        _nmcInFlight = true;
        _nmcStartedAt = now;
        var url = DisasterSources.NmcUrl(Province);
        _log.Trace($"[灾害] 轮询中央气象台（{Province}，间隔 {WeatherIntervalSec}s）");

        _ = Task.Run(async () =>
        {
            var (doc, error) = await DisasterSources.FetchJsonAsync(url, DisasterSources.NmcHeaders)
                .ConfigureAwait(false);
            if (doc is null)
            {
                Post(() =>
                {
                    _nmc.OnFail(error, Now);
                    _nmcInFlight = false;
                });
                return;
            }

            using (doc)
            {
                var alerts = DisasterSources.ParseNmcAlerts(doc.RootElement, Province, strict: false);
                foreach (var a in alerts)
                    _log.Trace($"[灾害] 气象预警：{a.Title}（信号 {a.Signal}，等级 {a.Level}，地区 {a.County}，" +
                               $"发布 {QuakeCalc.FmtClockHm(a.IssuedTs)}，uid={a.Uid}）");
                _log.Trace($"[灾害] 中央气象台本轮 {alerts.Count} 条预警");
                Post(() =>
                {
                    _nmc.OnOk(Now);
                    _nmcInFlight = false;
                    lock (_gate)
                    {
                        var map = _weather.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                        foreach (var a in alerts) map[a.Uid] = a;
                        _weather = DisasterSources.PruneWeather(map, Now);
                        _cacheDirty = true;
                    }
                });
            }
        });
    }

    void IngestQuakes(IEnumerable<QuakeEvent> incoming)
    {
        var list = incoming.ToList();
        lock (_gate)
        {
            var before = _quakes.Count;
            var (merged, changed) = DisasterSources.MergeQuakes(_quakes, list);
            _quakes = DisasterSources.PruneQuakes(merged, Now);
            _cacheDirty = true;

            // 事件被裁掉，"详情已试过"的记号跟着丢，否则字典只增不减
            foreach (var key in _detailTried.Where(k => !_quakes.ContainsKey(k)).ToList())
                _detailTried.Remove(key);

            // 没有任何变化就一个字都不写：ICL 每 5 秒把同一批事件原样推过来，
            // 上一版在这里统计"其中 N 条全新"用的是合并**之后**的字典，恒等于 0。
            // 再按 _quakes 过一道：MergeQuakes 的 changed 里可能有刚加进去就被
            // maxItems 截掉的，日志不该记一条库里根本没有的事件。
            var changedLive = changed.Where(c => _quakes.ContainsKey(c.Uid)).ToList();
            if (changedLive.Count > 0 || _quakes.Count != before)
                _log.Trace($"[灾害] 合并地震事件：本次 {list.Count} 条（新增/更新 {changedLive.Count} 条），" +
                           $"事件库 {before} → {_quakes.Count} 条（超时 / 无修订的旧事件已被裁剪）");

            foreach (var ev in changedLive)
                _log.Trace($"[灾害] 事件更新：{ev.Source} {ev.Epicenter} M{ev.Magnitude:0.#}，深度 {ev.Depth:0}km，" +
                           $"震中 {ev.Lat:0.####},{ev.Lon:0.####}，发震 {QuakeCalc.FmtClock(ev.OriginTs)}，" +
                           $"uid={ev.Uid}，revision {ev.Revision}");
        }
        FetchDetailsIfNeeded();
    }

    /// <summary>新事件补一次 ICL 详情（取逐县距离，面板里显示"最近县"）。</summary>
    void FetchDetailsIfNeeded()
    {
        List<QuakeEvent> need;
        lock (_gate)
        {
            // 每条只试一次：详情接口对老事件常常返回空 counties，原条件 Counties is null
            // 会让它每轮轮询都重发，直到事件过期（默认 5 秒一轮 × 180 秒 ≈ 72 次无用请求）。
            need = _quakes.Values
                .Where(q => q.Source == "icl" && q.Counties is null && !_detailTried.Contains(q.Uid))
                .Take(2)
                .ToList();
            foreach (var ev in need) _detailTried.Add(ev.Uid);
        }
        if (need.Count == 0) return;
        _log.Trace($"[灾害] 为 {need.Count} 条 ICL 事件补逐县详情：{string.Join("、", need.Select(n => n.Epicenter))}");

        foreach (var ev in need)
        {
            var url = string.Format(DisasterSources.IclDetailUrl, ev.EventId);
            _ = Task.Run(async () =>
            {
                var (doc, _) = await DisasterSources.FetchJsonAsync(url).ConfigureAwait(false);
                if (doc is null)
                {
                    _log.Trace($"[灾害] 事件 {ev.Epicenter}（uid={ev.Uid}）的逐县详情请求失败");
                    return;
                }
                using (doc)
                {
                    var counties = DisasterSources.ParseIclDetail(doc.RootElement);
                    if (counties.Count == 0) return;
                    _log.Trace($"[灾害] 事件 {ev.Epicenter} 收到 {counties.Count} 个县的逐县数据");
                    Post(() =>
                    {
                        lock (_gate)
                        {
                            if (_quakes.TryGetValue(ev.Uid, out var old))
                                _quakes[ev.Uid] = old with { Counties = counties };
                        }
                    });
                }
            });
        }
    }

    void Post(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    // ————————————————————— 选主 + 展示 —————————————————————

    void RefreshFocus(double now)
    {
        var previousFocus = Focus;
        var evaluations = EvaluateQuakes(now);
        Focus = evaluations.FirstOrDefault();
        Others = evaluations.Skip(1).Take(4).Cast<object>().ToList();

        // 事件消失了就把"首次看到"的时间一起丢掉，否则字典会随运行时长无限增长
        if (_firstSeen.Count > 0)
        {
            var alive = new HashSet<string>(evaluations.Select(e => e.Uid), StringComparer.Ordinal);
            foreach (var key in _firstSeen.Keys.Where(k => !alive.Contains(k)).ToList())
                _firstSeen.Remove(key);
        }

        if (Focus is null)
        {
            var weather = WeatherForIsland();
            if (weather is not null)
            {
                Focus = null;
                _weatherFocus = weather;
            }
            else _weatherFocus = null;
        }
        else _weatherFocus = null;

        // 主焦点换了才记一行（每秒都在刷，没换时刷屏没意义）
        if (Focus?.Uid != previousFocus?.Uid || Focus?.Event.Revision != previousFocus?.Event.Revision)
        {
            if (Focus is { } f)
                _log.Trace($"[灾害] 主焦点：{f.Event.Epicenter} M{f.Event.Magnitude:0.#}（烈度 {f.LocalIntensity:0.#}，" +
                           $"距 {f.DistanceKm:0.#}km，" +
                           (f.Incoming ? $"横波 {Math.Max(0, f.ArriveTs - now):0} 秒后到达" : "横波已到达") +
                           $"，revision {f.Event.Revision}，另有 {Others.Count} 条次要事件）");
            else if (previousFocus is not null)
                _log.Trace("[灾害] 主焦点清空（事件已到达 / 过期 / 用户清除）");
        }

        UpdateEmergency(now);
        MaybeNotify(now);
        MaybeNotifyWeather(now);
        PruneNotified();
    }

    WeatherEvent? _weatherFocus;

    List<QuakeEvaluation> EvaluateQuakes(double now)
    {
        if (!QuakeEnabled) return new List<QuakeEvaluation>();
        if (Lat is null || Lon is null)
        {
            // 这个分支每秒都会走到：坐标缺失的告警只喊一次（坐标补上后才会再喊）
            lock (_gate)
            {
                if (!_warnedNoCoords && _quakes.Count > 0)
                {
                    _warnedNoCoords = true;
                    _log.Trace($"[灾害计算] 坐标未设置，跳过 {_quakes.Count} 条地震事件的评估（请在设置 → 灾害预警选位置）");
                }
            }
            return new List<QuakeEvaluation>();
        }
        _warnedNoCoords = false;

        List<QuakeEvent> events;
        lock (_gate) events = _quakes.Values.ToList();

        // ★ 这里原本每秒给每条可显示事件打一行 Trace（距离/烈度/倒计时）——
        //   一次地震 180 秒 × N 条 = 几百行，把 2000 行的内存环形日志冲得只剩灾害日志，
        //   插件、设置、报错全被挤掉。明细挪到 RefreshFocus 的"主焦点变了"那条里了。
        var list = new List<QuakeEvaluation>();
        foreach (var ev in events)
        {
            var r = QuakeCalc.Evaluate(ev, Lat, Lon, now, Threshold);
            if (r is { Displayable: true }) list.Add(r);
        }

        return list
            .OrderBy(e => e.Triggered ? 0 : 1)
            .ThenByDescending(e => e.LocalIntensity)
            .ThenBy(e => e.RemainSec)
            .ToList();
    }

    /// <summary>
    /// 上课中把低等级气象预警静默掉（显示与提醒一并处理）。
    /// 分界取橙色：黄色暴雨/大风在课上只是噪音，橙红才值得打断。
    /// </summary>
    public static bool WeatherMutedByClass(string level, bool inClass) =>
        inClass && QuakeCalc.Rank(level) < QuakeCalc.Rank(QuakeLevels.Orange);

    /// <summary>
    /// 本市（没选市则全省）≥ 黄色的气象预警，只做候选，不判断该不该显示。
    /// 拆出来是因为"面板里那句已静默"需要看到**被压掉的那几条**，否则永远不知道有没有东西被静默。
    /// </summary>
    List<WeatherEvent> LocalWeather()
    {
        if (!WeatherEnabled) return new List<WeatherEvent>();
        if (string.IsNullOrEmpty(Province) || Province == Cities.NoWeather) return new List<WeatherEvent>();

        List<WeatherEvent> all;
        lock (_gate) all = _weather.Values.ToList();

        var key = Cities.ShortName(City);
        return all.Where(w => QuakeCalc.LevelAtLeast(w.Level, "yellow") &&
                              (key.Length == 0 || w.Title.Contains(key, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>气象：设了城市只显示本市；否则显示全省。上课中低等级静默。</summary>
    WeatherEvent? WeatherForIsland()
    {
        var inClass = _isInClass();
        return LocalWeather()
            .Where(w => !WeatherMutedByClass(w.Level, inClass))
            .OrderByDescending(w => QuakeCalc.Rank(w.Level))
            .ThenByDescending(w => w.IssuedTs ?? 0)
            .FirstOrDefault();
    }

    /// <summary>
    /// 主岛的紧急倒计时：地震时接管。
    /// 倒计时条一律红色（用户要求），比例按「剩余 / 总倒计时」收缩，
    /// 直接复用上课倒计时那套蓝条的渲染。
    /// </summary>
    void UpdateEmergency(double now)
    {
        Emergency = null;
        if (!Enabled || !BannerEnabled) return;

        var focus = Focus;
        if (focus is null) return;
        if (!focus.Displayable) return;

        // ★ 蓝色（预估烈度低于「有震感」）**不接管主岛** —— 只发一条系统通知。
        //   红条是用来读秒的，蓝色既没有破坏性也没有紧迫感，弹上去只是打扰。
        if (QuakeCalc.Rank(focus.Level) <= QuakeCalc.Rank(QuakeLevels.Blue)) return;

        // 用户点过确认的事件不再弹（升级 revision 后会重新出现）
        if (_dismissed is { } d && d.Uid == focus.Uid && focus.Event.Revision <= d.Revision) return;

        var big = QuakeCalc.Rank(focus.Level) >= QuakeCalc.Rank(QuakeLevels.Red);

        if (!_firstSeen.TryGetValue(focus.Uid, out var seenAt))
        {
            seenAt = now;
            _firstSeen[focus.Uid] = now;
        }

        if (!ShouldTakeOverIsland(focus, big, now, seenAt, SmallHoldSec)) return;

        var window = Math.Max(1, focus.CountdownSec);
        var remain = Math.Max(0, focus.ArriveTs - now);
        var ratio = focus.Incoming && remain > 0 ? Math.Clamp(remain / window, 0, 1) : 0;

        Emergency = new IslandEmergency(
            Text: EmergencyText(focus),
            Ratio: ratio,
            BarColor: LevelBarColor(focus.Level),   // 底色跟着预估烈度：蓝/黄/橙/红
            Level: focus.Level);
        if (_lastEmergencyText != Emergency.Text)
        {
            _lastEmergencyText = Emergency.Text;
            _log.Warn($"[灾害] 红色倒计时接管主岛：{Emergency.Text}（等级 {focus.Level}）");
        }
    }

    string? _lastEmergencyText;

    /// <summary>
    /// 这条事件该不该占着主岛（红条 + 倒计时）。
    ///
    /// <para>规则分三种情况，最容易搞混的是前两种 —— 之前把它们合并成"非红级就只占 5 秒"，
    /// 结果**倒计时会在第 5 秒凭空消失**（用户报的 bug）：</para>
    ///
    /// <list type="bullet">
    ///   <item><b>横波还没到</b>（incoming）：整段倒计时都占着。预警的全部价值就是这段读秒，
    ///         哪怕只是黄色级别，也不能中途把红条收掉。</item>
    ///   <item><b>红级及以上</b>：一直占着，直到用户点胶囊确认。</item>
    ///   <item><b>其余（已到达的小震）</b>：占 <paramref name="smallHoldSec"/> 秒「已到达」提示后
    ///         把主岛交还课程，事件仍在副岛 / 下拉面板里。</item>
    /// </list>
    ///
    /// <para>注意第三种的计时起点取 <c>max(首次看到, 横波到达)</c>：否则远处地震从"开始预警"算起，
    /// 等横波真到达时 5 秒早就过了，连一句"已到达"都露不出来。</para>
    /// </summary>
    public static bool ShouldTakeOverIsland(
        QuakeEvaluation focus, bool big, double now, double seenAt, int smallHoldSec)
    {
        if (focus is null) return false;

        // 1) 还在倒计时：一直显示，直到横波到达
        if (focus.Incoming) return true;

        // 2) 红级及以上：一直显示，直到用户确认
        if (big) return true;

        // 3) 其余：到达后再留一小段"已到达"
        if (smallHoldSec <= 0) return false;
        var holdFrom = Math.Max(seenAt, focus.ArriveTs);
        return now - holdFrom <= smallHoldSec;
    }

    static string EmergencyText(QuakeEvaluation focus)
    {
        var ev = focus.Event;
        var place = string.IsNullOrWhiteSpace(ev.Epicenter) ? "未知震中" : ev.Epicenter.Trim();

        if (focus.Incoming)
        {
            var sec = Math.Max(0, (int)Math.Round(focus.ArriveTs - Now));
            return $"{place} · 距地震波到达还有 {sec} 秒";
        }
        return $"{place} · 地震波已到达";
    }

    /// <summary>
    /// 倒计时条的底色跟着**预估烈度**走：蓝 / 黄 / 橙 / 红，
    /// 与 <see cref="QuakeLevels.Colors"/> 同一套色值（也就是面板里那个等级色）。
    /// </summary>
    static Color LevelBarColor(string level)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(QuakeCalc.LevelColor(level));
        }
        catch
        {
            return Color.FromRgb(0xFF, 0x45, 0x3A);   // 解析不了就退回红
        }
    }

    public void DismissEmergency()
    {
        if (Focus is null) return;
        _dismissed = (Focus.Uid, Focus.Event.Revision);
        Emergency = null;
        _log.Info("灾害预警：用户已确认，收起倒计时");
    }

    // ————————————————————— 副岛 / 面板 —————————————————————

    /// <summary>
    /// 副岛上的**地震**部分（横波倒计时 / 已到达）。拆出来是因为
    /// 实时天气要能判断"副岛这一秒是不是被灾害占着"，好决定自己要不要让位到作业窗口。
    /// </summary>
    public (string Title, string Value, double Progress)? QuakeSubIsland
    {
        get
        {
            if (!Enabled) return null;
            var focus = Focus;
            if (focus is null) return null;

            var sec = Math.Max(0, (int)Math.Round(focus.ArriveTs - Now));
            var title = focus.Incoming ? "地震预警" : "地震";
            var value = focus.Incoming
                ? $"{sec} 秒 · 烈度{focus.LocalIntensity:0.#}"
                : $"已到达 · 烈度{focus.LocalIntensity:0.#}";
            var window = Math.Max(1, focus.CountdownSec);
            var ratio = focus.Incoming ? Math.Clamp((focus.ArriveTs - Now) / window, 0, 1) : 0;
            return (title, value, ratio);
        }
    }

    /// <summary>
    /// 副岛上的**气象预警**部分。有它的时候实时天气必须让位到作业窗口去（用户定的规则）。
    /// </summary>
    public (string Title, string Value, double Progress)? WeatherSubIsland
    {
        get
        {
            if (!Enabled) return null;
            var weather = _weatherFocus;
            if (weather is null) return null;
            return ("气象", $"{weather.Signal}{QuakeCalc.LevelWord(weather.Level)}", 0);
        }
    }

    /// <summary>副岛显示（地震优先，其次气象）。</summary>
    public (string Title, string Value, double Progress)? SubIsland => QuakeSubIsland ?? WeatherSubIsland;

    /// <summary>下拉面板（和插件面板并排显示）。</summary>
    public (string Title, IReadOnlyList<string> Lines)? Panel
    {
        get
        {
            if (!Enabled) return null;
            var lines = PanelLines();
            return lines.Count == 0 ? null : ("灾害预警", lines);
        }
    }

    List<string> PanelLines()
    {
        var lines = new List<string>();
        var focus = Focus;

        if (focus is not null)
        {
            var ev = focus.Event;
            lines.Add($"地震 · {(ev.Source == "drill" ? "演练" : ev.Source == "icl" ? "ICL" : "Wolfx")}");
            lines.Add($"M{ev.Magnitude:0.#} {ev.Epicenter}");
            lines.Add($"发震 {QuakeCalc.FmtClock(ev.OriginTs)} · 深 {ev.Depth:0}km");
            lines.Add($"本地 {focus.DistanceKm:0.#}km · 烈度 {focus.LocalIntensity:0.#}");
            lines.Add(focus.Incoming
                ? $"横波还有 {Math.Max(0, (int)Math.Round(focus.ArriveTs - Now))} 秒到达"
                : "横波已到达");

            var near = ev.Counties?.OrderBy(c => c.Distance ?? double.MaxValue).FirstOrDefault();
            if (near is not null)
                lines.Add($"最近县 {near.Name} {near.Distance:0}km 烈度{near.Intensity:0.#}");

            if (Others.Count > 0)
                lines.Add($"另有 {Others.Count} 条地震事件");
        }
        else if (_weatherFocus is { } w)
        {
            lines.Add($"气象灾害 · {Province}");
            if (!string.IsNullOrEmpty(City)) lines.Add($"本市 {City}");
            lines.Add($"{w.County} · {w.Signal}{QuakeCalc.LevelWord(w.Level)}");
            lines.Add($"发布 {QuakeCalc.FmtClockHm(w.IssuedTs)}");
        }
        else if (!ShowStatusCard)
        {
            return lines;
        }
        else
        {
            lines.Add("灾害预警 · 监测中");
        }

        lines.Add(_icl.Describe(Now));
        // 备源的健康状况也得看得见：ICL 挂了、Wolfx 顶着的时候，
        // 面板上只有一行"ICL 地震 失败×3"，用户会以为整条链路都断了
        if (QuakeEnabled) lines.Add(_wolfx.Describe(Now));
        if (WeatherEnabled && !string.IsNullOrEmpty(Province) && Province != Cities.NoWeather)
            lines.Add(_nmc.Describe(Now));

        if (Lat is null || Lon is null)
            lines.Add("未设置坐标 · 无法计算地震倒计时");
        else
            lines.Add($"坐标 {Lat:0.##}, {Lon:0.##} · 阈值 {Threshold:0.#} · {LocationSource}");

        // 只有真的有东西被压掉时才这么说：原来只要在上课就无条件写"已静默"，
        // 而静默逻辑当时并不存在（_isInClass 全项目只有这一处引用）—— 比不写更误导
        if (_isInClass() && LocalWeather().Any(w => WeatherMutedByClass(w.Level, true)))
            lines.Add("上课中 · 气象低等级已静默");
        return lines;
    }

    // ————————————————————— 提醒 —————————————————————

    void MaybeNotify(double now)
    {
        if (!Enabled || !NotifyEnabled || Focus is null) return;

        var focus = Focus!;
        var ev = focus.Event;
        if (!focus.Triggered) return;

        var throttle = Math.Max(5, _settings.Int("disaster_throttle_sec"));
        if (_notified.TryGetValue(ev.Uid, out var last))
        {
            // ★ 同一事件同一等级只提醒一次。原实现是"过 throttle 就再弹一次"，
            //   一个 120 秒的倒计时窗口会连弹 5 条气泡，而设置页写的是
            //   「同一事件按 uid 去重，等级升级会再提醒一次」—— 与文案对不上。
            if (QuakeCalc.Rank(focus.Level) <= QuakeCalc.Rank(last.Level)) return;
            if (now - last.Ts < throttle) return;   // 连续升级也别一秒钟弹好几条
        }

        // ★ 通知里**不写秒数**：Windows Toast 是静态快照，文本不会自己走秒。
        //   写上一个"还有 32 秒"，10 秒后还显示 32，比不写更误导。
        //   实时倒计时在主岛那条红条上（它本来就逐秒刷）。
        _notify("地震预警",
            $"{ev.Epicenter} M{ev.Magnitude:0.#} · 预估烈度 {focus.LocalIntensity:0.#} · 倒计时见主岛");
        _notified[ev.Uid] = (now, focus.Level);
    }

    /// <summary>
    /// 气象：**展示出来的那条**预警弹一次系统提醒，去重规则与地震共用
    /// （uid 不同前缀，互不干扰）。原来只有地震会弹，而「弹系统提醒」开关摆在总开关组里，
    /// 用户会以为暴雨橙警也会弹 —— 这里补齐。
    /// </summary>
    void MaybeNotifyWeather(double now)
    {
        if (!Enabled || !NotifyEnabled) return;

        var w = WeatherForIsland();   // 与副岛/面板同源：上课中被静默的自然也不会弹
        if (w is null) return;

        var throttle = Math.Max(5, _settings.Int("disaster_throttle_sec"));
        if (_notified.TryGetValue(w.Uid, out var last))
        {
            if (QuakeCalc.Rank(w.Level) <= QuakeCalc.Rank(last.Level)) return;
            if (now - last.Ts < throttle) return;
        }

        var where = string.IsNullOrWhiteSpace(w.County) ? Province : w.County;
        _notify("气象预警", $"{where} {w.Signal}{QuakeCalc.LevelWord(w.Level)}");
        _notified[w.Uid] = (now, w.Level);
    }

    /// <summary>
    /// 事件过期后把它的提醒记号一起丢掉。原来这段清理写在 MaybeNotify 里、
    /// 且只拿 <c>_quakes</c> 比对 —— 气象的 uid 永远不在地震表里，
    /// 一旦有地震提醒就会把气象的记号误删，下一秒同一条预警会重复弹。
    /// </summary>
    void PruneNotified()
    {
        if (_notified.Count == 0) return;

        HashSet<string> live;
        lock (_gate)
        {
            live = _quakes.Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var key in _weather.Keys) live.Add(key);
        }

        foreach (var key in _notified.Keys.Where(k => !live.Contains(k)).ToList())
            _notified.Remove(key);
    }

    // ————————————————————— 演练 / 状态 —————————————————————

    /// <summary>
    /// 注入一次演练地震（设置页的「模拟一次地震预警」用它）。
    ///
    /// <para>震中：传了坐标就用传的（设置页可以选省/市/区当震中）；
    /// 没传就沿用"当前位置往西约 120km" —— 这样不选也能看到一个像样的十几秒倒计时。
    /// 倒计时由 <see cref="QuakeCalc"/> 按震中到你位置的直线距离算，选得越远读秒越长。</para>
    /// </summary>
    public string StartDrill(double? magnitude = 6.4, double? depth = 10,
        double? epicenterLat = null, double? epicenterLon = null, string? epicenterName = null)
    {
        // 总开关关着时注入了也不显示（UpdateEmergency / SubIsland / Panel 都会挡掉），
        // 只会白白弹一条系统提醒 —— 直接说清楚，别让用户以为按钮坏了
        if (!Enabled)
            return "灾害预警总开关是关闭的，未注入演练（先把上面的「启用灾害预警」打开）";

        var lat = Lat ?? 30.6595;   // 成都
        var lon = Lon ?? 104.0658;
        var now = Now;

        var evLat = epicenterLat ?? lat + 0.6;
        var evLon = epicenterLon ?? lon - 1.1;
        var place = string.IsNullOrWhiteSpace(epicenterName) ? "演练震中" : epicenterName.Trim();

        var ev = new QuakeEvent(
            Uid: "drill:" + (long)now,
            Source: "drill",
            EventId: "drill",
            Revision: 1,
            OriginTs: now - 3,
            UpdatedTs: now,
            Lat: evLat,
            Lon: evLon,
            Depth: depth ?? 10,
            Magnitude: magnitude ?? 6.4,
            Epicenter: place);

        lock (_gate)
        {
            _quakes[ev.Uid] = ev;
        }
        RefreshFocus(now);

        var dist = Lat is null || Lon is null
            ? 0
            : QuakeCalc.HaversineKm(Lat.Value, Lon.Value, evLat, evLon);

        _log.Warn($"[灾害] 注入演练地震：M{ev.Magnitude:0.#}，深度 {ev.Depth:0}km，" +
                  $"震中 {place} {evLat:0.####},{evLon:0.####}" +
                  $"{(Lat is null || Lon is null ? "" : $"（距你 {dist:0}km）")}");

        if (Lat is null || Lon is null)
            return $"已注入演练地震 M{ev.Magnitude:0.#}（{place}），但你的位置没有坐标，倒计时算不出来 —— " +
                   "请先在上面「位置」里选省市";

        return Focus is null
            ? $"已注入演练地震 M{ev.Magnitude:0.#}（{place}，距你 {dist:0}km），但未达显示条件（预估烈度低于阈值）"
            : $"已注入演练地震 M{ev.Magnitude:0.#}（{place}，距你 {dist:0}km），" +
              $"横波还有 {Math.Max(0, (int)Math.Round(Focus.ArriveTs - now))} 秒";
    }

    /// <summary>
    /// 注入一次演练气象预警（设置页的「模拟一次气象预警」用它）。
    ///
    /// <para>事件塞进 <c>_weather</c>，走和真实预警**完全相同**的显示与提醒链路：
    /// 副岛被它占住 → 天气自动挤到作业窗口、并在天气后面写上预警内容。
    /// 这是验证位移规则最省事的办法，不用等真的下雨。</para>
    /// </summary>
    public string StartWeatherDrill(string signal = "暴雨", string level = QuakeLevels.Orange)
    {
        if (!Enabled)
            return "灾害预警总开关是关闭的，未注入演练（先把上面的「启用灾害预警」打开）";
        if (!WeatherEnabled)
            return "气象预警是关着的，未注入演练（先打开「启用气象预警」）";

        var now = Now;
        var county = string.IsNullOrWhiteSpace(City)
            ? (string.IsNullOrWhiteSpace(Province) ? "本地" : Province)
            : City;

        var ev = new WeatherEvent(
            Uid: "drill-weather:" + (long)now,
            AlertId: "drill",
            IssuedTs: now,
            // 标题里要带上当前市名：WeatherForIsland 就是靠它做"只看本市"的过滤
            Title: $"{county}气象台发布{signal}{QuakeCalc.LevelWord(level)}预警信号",
            Signal: signal,
            Level: level,
            Province: Province,
            County: county,
            Url: "");

        lock (_gate)
        {
            _weather[ev.Uid] = ev;
        }
        RefreshFocus(now);

        _log.Warn($"[灾害] 注入演练气象预警：{ev.Title}（uid={ev.Uid}，等级 {level}）");

        // 上课中「低于橙色」会被静默（WeatherMutedByClass）—— 这不是坏了，要说清楚
        return WeatherMutedByClass(level, _isInClass())
            ? $"已注入「{signal}{QuakeCalc.LevelWord(level)}预警」，但当前在上课中，低于橙色的气象预警会被静默 —— " +
              "换成橙色/红色才看得到"
            : $"已注入「{signal}{QuakeCalc.LevelWord(level)}预警」：副岛已被它占住，天气应该已经挤到作业窗口、并在后面带上预警内容了";
    }

    public void ClearDrill()
    {
        lock (_gate)
        {
            foreach (var key in _quakes.Keys.Where(k => k.StartsWith("drill:", StringComparison.Ordinal)).ToList())
                _quakes.Remove(key);
            foreach (var key in _weather.Keys.Where(k => k.StartsWith("drill-weather:", StringComparison.Ordinal)).ToList())
                _weather.Remove(key);
        }
        // 只删演练事件，再按剩下的真实事件重算。
        // 原来这里把 _firstSeen / _dismissed / Focus / Emergency 一并清零 ——
        // 清一次演练会顺手把同时在发生的真实地震的"已到达 5 秒"计时和用户已确认状态也复位，
        // 结果是刚被点掉的红条又自己弹回来。剩余记号由 RefreshFocus / PruneNotified 自己收敛。
        RefreshFocus(Now);
        _log.Trace("[灾害] 演练已清除（地震 + 气象），焦点交还真实事件");
    }

    /// <summary>
    /// 当前生效中的气象预警文案（如「暴雨橙色预警」）；没有就 null。
    /// 主窗的天气胶囊会把它接在天气后面 —— 有预警时天气被挤到作业窗口，得让人知道为什么。
    /// </summary>
    public string? WeatherWarningText =>
        Enabled && _weatherFocus is { } w ? $"{w.Signal}{QuakeCalc.LevelWord(w.Level)}预警" : null;

    /// <summary>设置页的状态说明（多行）。</summary>
    public string StatusText
    {
        get
        {
            var now = Now;
            var lines = new List<string>
            {
                Lat is null || Lon is null
                    ? (AutoLocate
                        ? "坐标未设置：自动定位还没拿到结果（会每分钟重试）。检查网络，" +
                          "或关掉「自动定位」后在下面手选省市。"
                        : "坐标未设置：请在下面的「位置」里选省市（或手填经纬度）。")
                    : $"{Province}/{City}/{District} · 坐标 {Lat:0.##}, {Lon:0.##} · {LocationSource} · 烈度阈值 {Threshold:0.#}",
                _icl.Describe(now),
                _wolfx.Describe(now),
                _nmc.Describe(now),
            };
            if (Focus is not null)
            {
                var sec = Math.Max(0, (int)Math.Round(Focus.ArriveTs - now));
                lines.Add($"当前震情：{Focus.Event.Epicenter} M{Focus.Event.Magnitude:0.#} · " +
                          (Focus.Incoming ? $"横波还有 {sec} 秒" : "横波已到达"));
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    // ————————————————————— 缓存 —————————————————————
    // 只是让重启后能先显示上一次的震情；坏了就丢掉，不影响运行。

    void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                _log.Trace("[灾害] 缓存文件不存在（disaster-cache.json），从零开始");
                return;
            }
            var dto = System.Text.Json.JsonSerializer.Deserialize<CacheDto>(File.ReadAllText(_cachePath));
            if (dto?.Quakes is null) return;
            foreach (var q in dto.Quakes) _quakes[q.Uid] = q;
            _log.Trace($"[灾害] 缓存载入 {dto.Quakes.Count} 条地震事件（重启后先按缓存显示）");
        }
        catch (Exception ex)
        {
            _log.Warn("读灾害缓存失败：" + ex.Message);
        }
    }

    void SaveCacheIfDue(double now)
    {
        if (!_cacheDirty) return;
        if (now - _lastCacheSave < CacheSaveIntervalSec) return;
        _lastCacheSave = now;
        _cacheDirty = false;

        try
        {
            var dto = new CacheDto { Quakes = _quakes.Values.TakeLast(8).ToList() };
            Core.Storage.AtomicJson.Write(_cachePath, dto);
            _log.Trace($"[灾害] 灾害缓存已落盘（{_cachePath}，{dto.Quakes.Count} 条）");
        }
        catch (Exception ex)
        {
            _log.Warn("写灾害缓存失败：" + ex.Message);
        }
    }

    sealed class CacheDto
    {
        public List<QuakeEvent>? Quakes { get; set; }
    }

    public void Dispose() { }
}
